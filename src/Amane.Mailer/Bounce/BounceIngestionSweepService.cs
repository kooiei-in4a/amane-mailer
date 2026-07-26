using Amane.Mailer.Configuration;
using Amane.Mailer.Data.Sqlite;
using Amane.Mailer.Data.Sqlite.Models;

namespace Amane.Mailer.Bounce;

/// <summary>
/// Reaps expired inbox leases at max_attempts (#388) and wakes the bounce worker (#402).
/// </summary>
public sealed class BounceIngestionSweepService(
    ProviderEventInboxRepository repository,
    MailerBounceIngestionOptions options,
    IBounceIngestionQueue queue,
    TimeProvider timeProvider,
    ILogger<BounceIngestionSweepService> logger) : BackgroundService
{
    private static readonly TimeSpan SweepInterval = TimeSpan.FromSeconds(15);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(SweepInterval);
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var now = timeProvider.GetUtcNow();
                await DeadLetterExpiredProcessingAtMaxAttemptsAsync(now, stoppingToken);

                if (await repository.HasPendingWorkAsync(now, stoppingToken)
                    && !queue.TrySignalWorkAvailable())
                {
                    logger.LogDebug("Bounce ingestion queue is full during sweep; worker will catch up.");
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Bounce ingestion sweep failed.");
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

    internal async Task DeadLetterExpiredProcessingAtMaxAttemptsAsync(
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            var deadLettered = await repository.DeadLetterExpiredProcessingAtMaxAttemptsAsync(
                now,
                options.ReconcileBatchSize,
                cancellationToken);

            foreach (var inboxEvent in deadLettered)
            {
                LogExpiredProcessingDeadLetter(inboxEvent);
            }

            if (deadLettered.Count < options.ReconcileBatchSize)
            {
                return;
            }
        }
    }

    private void LogExpiredProcessingDeadLetter(ExpiredProcessingDeadLetteredInboxEvent inboxEvent)
    {
        logger.LogError(
            "Provider event inbox row {InboxId} was dead-lettered after its processing lease expired at attempt {AttemptCount}. Provider={Provider}; EventId={EventId}; ErrorCode={ErrorCode}",
            inboxEvent.Id,
            inboxEvent.AttemptCount,
            inboxEvent.Provider,
            inboxEvent.EventId,
            inboxEvent.ErrorCode);
    }
}
