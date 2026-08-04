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
/// affect the lease row (proof the row's owner_token no longer matches ours, i.e. it already
/// expired long enough for someone else to reclaim it) and never recovers for this instance --
/// callers must treat that as "abort, do not publish as a successful backup."
/// </summary>
public sealed class MaintenanceLeaseHeartbeat : IAsyncDisposable
{
    private readonly MailerMaintenanceLeaseStore _leaseStore;
    private readonly string _leaseName;
    private readonly Guid _ownerToken;
    private readonly TimeSpan _leaseDuration;
    private readonly TimeProvider _timeProvider;
    private readonly CancellationTokenSource _stopCts = new();
    private readonly Task _loopTask;
    private volatile bool _isHealthy = true;

    public MaintenanceLeaseHeartbeat(
        MailerMaintenanceLeaseStore leaseStore,
        string leaseName,
        Guid ownerToken,
        TimeSpan leaseDuration,
        TimeSpan renewInterval,
        TimeProvider timeProvider)
    {
        _leaseStore = leaseStore;
        _leaseName = leaseName;
        _ownerToken = ownerToken;
        _leaseDuration = leaseDuration;
        _timeProvider = timeProvider;
        _loopTask = RunAsync(renewInterval, _stopCts.Token);
    }

    public bool IsHealthy => _isHealthy;

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

                var renewed = await _leaseStore.RenewAsync(
                    _leaseName,
                    _ownerToken,
                    _leaseDuration,
                    _timeProvider.GetUtcNow(),
                    cancellationToken);
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
