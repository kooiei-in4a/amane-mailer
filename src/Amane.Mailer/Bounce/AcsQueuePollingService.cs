using Azure;
using Amane.Mailer.Configuration;
using Amane.Mailer.Data.Sqlite;
using Amane.Mailer.Data.Sqlite.Models;
using Amane.Mailer.Delivery;
using Amane.Mailer.Operations;

namespace Amane.Mailer.Bounce;

/// <summary>
/// Pull transport: polls Azure Storage Queue and inserts ACS delivery reports into the inbox (#305).
/// Deletes queue messages only after durable inbox acceptance (insert or UNIQUE conflict). ADR 0020 D-09.
/// Poison envelopes (decode/parse) are retained until DequeueCount reaches an internal threshold, then
/// recorded in provider_queue_dead_letters before delete (#461).
/// </summary>
public sealed class AcsQueuePollingService(
    IAcsEventQueueClient queueClient,
    ProviderEventInboxRepository inboxRepository,
    ProviderQueueDeadLetterRepository queueDeadLetterRepository,
    MailerBounceIngestionOptions options,
    IBounceIngestionQueue bounceQueue,
    MailerRuntimeMetrics runtimeMetrics,
    TimeProvider timeProvider,
    ILogger<AcsQueuePollingService> logger) : BackgroundService
{
    /// <summary>Internal poison threshold; not public configuration (#461).</summary>
    internal const long PoisonDequeueThreshold = 5;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(options.QueuePollInterval);
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await PollOnceAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                runtimeMetrics.RecordProviderQueuePollFailed();
                LogPollFailure(ex);
            }

            try
            {
                await timer.WaitForNextTickAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
        }
    }

    /// <summary>Single poll cycle for tests.</summary>
    internal Task PollOnceAsync(CancellationToken cancellationToken) =>
        PollOnceCoreAsync(cancellationToken);

    private async Task PollOnceCoreAsync(CancellationToken cancellationToken)
    {
        IReadOnlyList<AcsQueueReceivedMessage> messages;
        try
        {
            messages = await queueClient.ReceiveMessagesAsync(
                options.QueueBatchSize,
                options.QueueVisibilityTimeout,
                cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            runtimeMetrics.RecordProviderQueuePollFailed();
            LogPollFailure(ex);
            return;
        }

        foreach (var message in messages)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await ProcessMessageAsync(message, cancellationToken);
        }
    }

    private async Task ProcessMessageAsync(
        AcsQueueReceivedMessage message,
        CancellationToken cancellationToken)
    {
        string body;
        try
        {
            body = AcsQueueMessageBodyDecoder.Decode(message.Body);
        }
        catch (Exception)
        {
            await HandlePayloadInvalidAsync(
                message,
                ProviderQueueDeadLetterRepository.FailureStageDecode,
                ProviderQueueDeadLetterRepository.BodyInvalidErrorCode,
                "ACS queue message body could not be decoded.",
                cancellationToken);
            return;
        }

        IReadOnlyList<AcsEventParseResult> parseResults;
        try
        {
            parseResults = AcsEventParser.ParseMany(body);
        }
        catch (Exception)
        {
            await HandlePayloadInvalidAsync(
                message,
                ProviderQueueDeadLetterRepository.FailureStageParse,
                ProviderQueueDeadLetterRepository.EventInvalidErrorCode,
                "ACS queue message parse threw.",
                cancellationToken);
            return;
        }

        var now = timeProvider.GetUtcNow();
        var sawDeliveryReport = false;
        var sawUnparseable = false;

        foreach (var parseResult in parseResults)
        {
            if (parseResult.Outcome == AcsEventParseOutcome.Ignored)
            {
                continue;
            }

            if (parseResult.Outcome == AcsEventParseOutcome.Unparseable)
            {
                sawUnparseable = true;
                continue;
            }

            if (parseResult.Outcome != AcsEventParseOutcome.DeliveryReport || parseResult.Report is null)
            {
                continue;
            }

            sawDeliveryReport = true;

            try
            {
                var inserted = await inboxRepository.TryInsertAsync(
                    new ProviderEventInboxInsert
                    {
                        Id = Guid.CreateVersion7(now),
                        Provider = MailerBounceIngestionOptions.ProviderAcs,
                        EventId = parseResult.Report.EventId,
                        ProviderMessageId = parseResult.Report.MessageId,
                        DeliveryStatus = parseResult.Report.Status,
                        RecipientEmail = parseResult.Report.Recipient,
                        StatusMessage = SanitizeStatusMessage(parseResult.Report.StatusMessage),
                        OccurredAt = parseResult.Report.OccurredAt,
                        MaxAttempts = options.MaxAttempts,
                        CreatedAt = now,
                    },
                    cancellationToken);

                // Inserted or UNIQUE conflict both mean durable acceptance (ADR 0020 D-01 / D-09).
                if (inserted)
                {
                    bounceQueue.TrySignalWorkAvailable();
                }
            }
            catch (Exception ex)
            {
                // Leave the queue message for visibility-timeout redelivery.
                runtimeMetrics.RecordProviderQueuePollFailed();
                LogIngestFailure(ex);
                return;
            }
        }

        if (sawUnparseable)
        {
            // Valid sibling DeliveryReports were already inserted (UNIQUE absorbs redelivery).
            await HandlePayloadInvalidAsync(
                message,
                ProviderQueueDeadLetterRepository.FailureStageParse,
                ProviderQueueDeadLetterRepository.EventInvalidErrorCode,
                "ACS queue message contained an unparseable event.",
                cancellationToken);
            return;
        }

        if (sawDeliveryReport)
        {
            // All delivery reports were durably accepted (else we returned above).
            await TryDeleteAsync(message, cancellationToken);
            return;
        }

        // Only Ignored events (e.g. Delivered / non-delivery-report types): safe to delete.
        await TryDeleteAsync(message, cancellationToken);
    }

    private async Task HandlePayloadInvalidAsync(
        AcsQueueReceivedMessage message,
        string failureStage,
        string errorCode,
        string warningMessage,
        CancellationToken cancellationToken)
    {
        runtimeMetrics.RecordProviderQueuePayloadInvalid();

        if (message.DequeueCount < PoisonDequeueThreshold)
        {
            logger.LogWarning(
                "{WarningMessage} DequeueCount={DequeueCount}; leaving message for redelivery.",
                warningMessage,
                message.DequeueCount);
            return;
        }

        var now = timeProvider.GetUtcNow();
        bool inserted;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            inserted = await queueDeadLetterRepository.TryInsertAsync(
                new ProviderQueueDeadLetterInsert
                {
                    Id = Guid.CreateVersion7(now),
                    Provider = MailerBounceIngestionOptions.ProviderAcs,
                    QueueMessageId = message.MessageId,
                    FailureStage = failureStage,
                    LastErrorCode = errorCode,
                    DequeueCount = message.DequeueCount,
                    CreatedAt = now,
                },
                cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // Durable record failed: keep the queue message (do not delete).
            runtimeMetrics.RecordProviderQueuePollFailed();
            logger.LogError(
                "ACS queue poison dead-letter insert failed; leaving message for redelivery. Detail={Detail}",
                ProviderErrorSanitizer.Sanitize(ex.Message));
            return;
        }

        if (inserted)
        {
            runtimeMetrics.RecordProviderQueuePoisoned();
        }

        logger.LogWarning(
            "{WarningMessage} DequeueCount={DequeueCount}; recorded local dead-letter and deleting queue message.",
            warningMessage,
            message.DequeueCount);

        await TryDeleteAsync(message, cancellationToken);
    }

    private async Task TryDeleteAsync(
        AcsQueueReceivedMessage message,
        CancellationToken cancellationToken)
    {
        try
        {
            await queueClient.DeleteMessageAsync(message.MessageId, message.PopReceipt, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            runtimeMetrics.RecordProviderQueuePollFailed();
            LogPollFailure(ex);
        }
    }

    private void LogPollFailure(Exception ex)
    {
        if (ex is RequestFailedException requestFailed)
        {
            // Omit Azure ErrorCode: provider-supplied strings are not allowlisted (#26 / #305 review).
            logger.LogError(
                "ACS Storage Queue poll failed. Status={Status}; Detail={Detail}",
                requestFailed.Status,
                ProviderErrorSanitizer.Sanitize(requestFailed.Message));
            return;
        }

        logger.LogError(
            "ACS Storage Queue poll failed. Detail={Detail}",
            ProviderErrorSanitizer.Sanitize(ex.Message));
    }

    private void LogIngestFailure(Exception ex)
    {
        logger.LogError(
            "ACS queue inbox insert failed; leaving message for visibility redelivery. Detail={Detail}",
            ProviderErrorSanitizer.Sanitize(ex.Message));
    }

    /// <summary>
    /// Sanitize before the first DB write (#460 / ADR 0020 D-08). Missing/blank stays null.
    /// </summary>
    internal static string? SanitizeStatusMessage(string? raw) =>
        string.IsNullOrWhiteSpace(raw) ? null : ProviderErrorSanitizer.Sanitize(raw);
}
