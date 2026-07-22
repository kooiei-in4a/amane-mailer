using Amane.Mailer.Configuration;
using Amane.Mailer.Data.Sqlite;

namespace Amane.Mailer.Worker;

public sealed class AdminAuditRetentionService(
    AdminAuditRepository repository,
    MailerAdminAuditRetentionOptions retentionOptions,
    TimeProvider timeProvider,
    ILogger<AdminAuditRetentionService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await PurgeExpiredAsync(retentionOptions.RetentionDays, stoppingToken);
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
                "Admin audit retention sweep failed during startup due to SQLite storage full (SQLITE_FULL).",
                "Admin audit retention sweep failed during startup.");
        }

        using var timer = new PeriodicTimer(retentionOptions.SweepInterval);

        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                try
                {
                    await PurgeExpiredAsync(retentionOptions.RetentionDays, stoppingToken);
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
                        "Admin audit retention sweep failed due to SQLite storage full (SQLITE_FULL).",
                        "Admin audit retention sweep failed.");
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
    }

    private async Task PurgeExpiredAsync(int retentionDays, CancellationToken cancellationToken)
    {
        var cutoff = timeProvider.GetUtcNow().AddDays(-retentionDays);
        var totalDeleted = 0;

        while (!cancellationToken.IsCancellationRequested)
        {
            var deleted = await repository.DeleteOlderThanAsync(
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
                "Admin audit retention sweep removed {DeletedCount} audit events older than {RetentionDays} days.",
                totalDeleted,
                retentionDays);
        }
    }
}
