namespace Amane.Mailer.Data.Sqlite;

/// <summary>
/// Periodically renews a durable maintenance lease while a long-running operation (a backup
/// snapshot) is in progress (ADR 0022 D-09). Holding the lease for the operation's full
/// duration is what keeps concurrent attachment acceptance blocked at the acceptance-time gate
/// (<c>expires_at &gt; now</c>); without renewal, a snapshot that runs longer than the lease
/// duration would let <c>expires_at</c> lapse mid-flight and reopen that race even though the
/// original holder never stopped working.
///
/// <see cref="IsHealthy"/> latches to <see langword="false"/> the first time a renewal fails to
/// affect the lease row (proof the owner/fencing token no longer matches, i.e. it already
/// expired long enough for someone else to reclaim it) or throws any exception other than
/// <see cref="OperationCanceledException"/> (e.g. SQLite busy, an I/O error) -- a transient DB
/// failure must never look like a live heartbeat. It never recovers for this instance -- callers
/// must treat that as "abort, do not publish as a successful backup." <see cref="IsHealthy"/>
/// also fails closed if the renewal loop task itself faulted, as defense-in-depth against any
/// failure path that manages to escape the loop's own try/catch.
/// </summary>
public sealed class MaintenanceLeaseHeartbeat : IAsyncDisposable
{
    private readonly MailerMaintenanceLeaseStore _leaseStore;
    private readonly string _leaseName;
    private readonly Guid _ownerToken;
    private readonly long _fencingToken;
    private readonly TimeSpan _leaseDuration;
    private readonly TimeProvider _timeProvider;
    private readonly CancellationTokenSource _stopCts = new();
    private readonly Task _loopTask;
    private volatile bool _isHealthy = true;

    public MaintenanceLeaseHeartbeat(
        MailerMaintenanceLeaseStore leaseStore,
        string leaseName,
        Guid ownerToken,
        long fencingToken,
        TimeSpan leaseDuration,
        TimeSpan renewInterval,
        TimeProvider timeProvider)
    {
        _leaseStore = leaseStore;
        _leaseName = leaseName;
        _ownerToken = ownerToken;
        _fencingToken = fencingToken;
        _leaseDuration = leaseDuration;
        _timeProvider = timeProvider;
        _loopTask = RunAsync(renewInterval, _stopCts.Token);
    }

    public bool IsHealthy => _isHealthy && !_loopTask.IsFaulted;

    private async Task RunAsync(TimeSpan renewInterval, CancellationToken cancellationToken)
    {
        try
        {
            using var timer = new PeriodicTimer(renewInterval, _timeProvider);
            while (await timer.WaitForNextTickAsync(cancellationToken))
            {
                if (!_isHealthy)
                {
                    return;
                }

                bool renewed;
                try
                {
                    renewed = await _leaseStore.RenewAsync(
                        _leaseName,
                        _ownerToken,
                        _fencingToken,
                        _leaseDuration,
                        _timeProvider.GetUtcNow(),
                        cancellationToken);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception)
                {
                    // SQLite busy, an I/O error, or any other renewal failure: never let a
                    // transient DB failure look like a live heartbeat.
                    _isHealthy = false;
                    return;
                }

                if (!renewed)
                {
                    _isHealthy = false;
                    return;
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown via DisposeAsync.
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _stopCts.CancelAsync();
        try
        {
            await _loopTask;
        }
        catch (OperationCanceledException)
        {
        }

        _stopCts.Dispose();
    }
}
