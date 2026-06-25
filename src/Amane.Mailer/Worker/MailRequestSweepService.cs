using Amane.Mailer.Configuration;
using Amane.Mailer.Data.Sqlite;
using Amane.Mailer.Queue;

namespace Amane.Mailer.Worker;

public sealed class MailRequestSweepService(
    MailRequestRepository repository,
    IMailRequestQueue queue,
    MailerSweepOptions sweepOptions,
    ExpiredProcessingReaper expiredProcessingReaper,
    WorkerServiceStatus serviceStatus,
    TimeProvider timeProvider,
    ILogger<MailRequestSweepService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await WriteHeartbeatAsync(stoppingToken);
        serviceStatus.SetSweepRunning(true);
        try
        {
            using var timer = new PeriodicTimer(sweepOptions.Interval);

            try
            {
                while (await timer.WaitForNextTickAsync(stoppingToken))
                {
                    try
                    {
                        await SweepOnceAsync(stoppingToken);
                    }
                    catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                    {
                        return;
                    }
                    catch (Exception ex)
                    {
                        logger.LogError(ex, "Mail request sweep failed.");
                    }
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
            }
        }
        finally
        {
            serviceStatus.SetSweepRunning(false);
        }
    }

    private async Task SweepOnceAsync(CancellationToken cancellationToken)
    {
        await WriteHeartbeatAsync(cancellationToken);

        var now = timeProvider.GetUtcNow();
        await expiredProcessingReaper.DeadLetterExpiredProcessingAtMaxAttemptsAsync(now, cancellationToken);

        if (!await repository.HasDispatchableWorkAsync(now, cancellationToken))
        {
            return;
        }

        if (!queue.TrySignalWorkAvailable())
        {
            logger.LogWarning(
                "WorkAvailable channel is full during sweep; worker drain will eventually catch up.");
        }
    }

    private async Task WriteHeartbeatAsync(CancellationToken cancellationToken)
    {
        try
        {
            var now = timeProvider.GetUtcNow();
            await repository.UpsertHeartbeatAsync("sweep", now, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Failed to update sweep heartbeat.");
        }
    }
}
