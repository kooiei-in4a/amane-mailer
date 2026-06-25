using Amane.Mailer.Data.Sqlite;

namespace Amane.Mailer.Worker;

public sealed class MailerWalCheckpointShutdownService(
    SqliteConnectionFactory connections,
    ILogger<MailerWalCheckpointShutdownService> logger) : IHostedLifecycleService
{
    public static readonly EventId WalCheckpointCompletedEvent = new(1001, "WalCheckpointCompleted");
    public static readonly EventId WalCheckpointCanceledEvent = new(1002, "WalCheckpointCanceled");
    public static readonly EventId WalCheckpointFailedEvent = new(1003, "WalCheckpointFailed");

    public Task StartingAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task StartedAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task StoppingAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public async Task StoppedAsync(CancellationToken cancellationToken)
    {
        try
        {
            await connections.RunWalCheckpointTruncateAsync(cancellationToken);
            logger.LogInformation(
                WalCheckpointCompletedEvent,
                "WAL checkpoint (TRUNCATE) completed during StoppedAsync after hosted services stopped.");
        }
        catch (OperationCanceledException ex)
        {
            logger.LogWarning(
                WalCheckpointCanceledEvent,
                ex,
                "WAL checkpoint (TRUNCATE) was canceled during StoppedAsync after hosted services stopped.");
        }
        catch (Exception ex)
        {
            logger.LogError(
                WalCheckpointFailedEvent,
                ex,
                "WAL checkpoint (TRUNCATE) failed during StoppedAsync after hosted services stopped.");
        }
    }
}
