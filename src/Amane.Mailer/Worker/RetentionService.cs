using Amane.Mailer.Configuration;
using Amane.Mailer.Data.Sqlite;

namespace Amane.Mailer.Worker;

public sealed class RetentionService(
    MailRequestRepository repository,
    ProviderEventInboxRepository providerEventInboxRepository,
    MailSuppressionRepository mailSuppressionRepository,
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
                    SqliteDatabaseExceptionLogging.LogError(
                        logger,
                        ex,
                        "Retention sweep failed due to SQLite storage full (SQLITE_FULL).",
                        "Retention sweep failed.");
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
        var totalInboxDeleted = 0;
        var totalSuppressionsDeleted = 0;

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

        while (!cancellationToken.IsCancellationRequested)
        {
            var deleted = await providerEventInboxRepository.DeleteExpiredTerminalAsync(
                cutoff,
                retentionOptions.BatchSize,
                cancellationToken);

            if (deleted == 0)
            {
                break;
            }

            totalInboxDeleted += deleted;
        }

        if (retentionOptions.PurgeMailSuppressions)
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var deleted = await mailSuppressionRepository.DeleteExpiredAsync(
                    cutoff,
                    retentionOptions.BatchSize,
                    cancellationToken);

                if (deleted == 0)
                {
                    break;
                }

                totalSuppressionsDeleted += deleted;
            }
        }

        if (totalDeleted > 0)
        {
            logger.LogInformation(
                "Retention purge removed {DeletedCount} completed mail requests older than {RetentionDays} days.",
                totalDeleted,
                retentionOptions.RetentionDays);
        }

        if (totalInboxDeleted > 0)
        {
            logger.LogInformation(
                "Retention purge removed {DeletedCount} terminal provider_event_inbox rows older than {RetentionDays} days.",
                totalInboxDeleted,
                retentionOptions.RetentionDays);
        }

        if (totalSuppressionsDeleted > 0)
        {
            logger.LogInformation(
                "Retention purge removed {DeletedCount} mail_suppressions rows older than {RetentionDays} days.",
                totalSuppressionsDeleted,
                retentionOptions.RetentionDays);
        }
    }
}
