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
/// </summary>
public sealed class AcsQueuePollingService(
    IAcsEventQueueClient queueClient,
    ProviderEventInboxRepository inboxRepository,
    MailerBounceIngestionOptions options,
    IBounceIngestionQueue bounceQueue,
    MailerRuntimeMetrics runtimeMetrics,
    TimeProvider timeProvider,
    ILogger<AcsQueuePollingService> logger) : BackgroundService
{
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
            // Treat decode failure like Unparseable: retain for visibility redelivery (ADR 0020 D-09).
            runtimeMetrics.RecordProviderQueuePollFailed();
            logger.LogWarning("ACS queue message body could not be decoded; leaving message for redelivery.");
            return;
        }

        IReadOnlyList<AcsEventParseResult> parseResults;
        try
        {
            parseResults = AcsEventParser.ParseMany(body);
        }
        catch (Exception)
        {
            runtimeMetrics.RecordProviderQueuePollFailed();
            logger.LogWarning("ACS queue message parse threw; leaving message for redelivery.");
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

        if (sawDeliveryReport)
        {
            // All delivery reports were durably accepted (else we returned above).
            await TryDeleteAsync(message, cancellationToken);
            return;
        }

        if (sawUnparseable)
        {
            // Do not delete: Unparseable is an ingestion failure, not "nothing to ingest" (D-09).
            runtimeMetrics.RecordProviderQueuePollFailed();
            logger.LogWarning("ACS queue message was unparseable; leaving message for redelivery.");
            return;
        }

        // Only Ignored events (e.g. Delivered / non-delivery-report types): safe to delete.
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
}
