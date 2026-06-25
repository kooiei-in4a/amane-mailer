using Amane.Mailer.Configuration;
using Amane.Mailer.Data.Sqlite;

namespace Amane.Mailer.Worker;

public sealed class RetentionService(
    MailRequestRepository repository,
    MailerRetentionOptions retentionOptions,
    TimeProvider timeProvider,
    ILogger<RetentionService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(retentionOptions.SweepInterval);

        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                try
                {
                    await PurgeExpiredAsync(stoppingToken);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    return;
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "Retention sweep failed.");
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
    }

    private async Task PurgeExpiredAsync(CancellationToken cancellationToken)
    {
        var cutoff = timeProvider.GetUtcNow().AddDays(-retentionOptions.RetentionDays);
        var totalDeleted = 0;

        while (!cancellationToken.IsCancellationRequested)
        {
            var deleted = await repository.DeleteExpiredCompletedAsync(
                cutoff,
                retentionOptions.BatchSize,
                cancellationToken);

            if (deleted == 0)
            {
                break;
            }

            totalDeleted += deleted;
        }

        if (totalDeleted > 0)
        {
            logger.LogInformation(
                "Retention purge removed {DeletedCount} completed mail requests older than {RetentionDays} days.",
                totalDeleted,
                retentionOptions.RetentionDays);
        }
    }
}
