using Amane.Mailer.Data.Sqlite;
using Amane.Mailer.Operations;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;

namespace Amane.Mailer.Tests;

/// <summary>
/// ADR 0022 D-09: a long-running backup must keep renewing the maintenance lease for its full
/// duration, or expires_at can lapse mid-flight and reopen the acceptance race the lease exists
/// to close. Uses short real durations/intervals (not a fake clock) so these run fast without
/// depending on PeriodicTimer honoring a custom TimeProvider's virtual time.
/// </summary>
public sealed class MaintenanceLeaseHeartbeatTests : IAsyncLifetime
{
    private string? _root;
    private string? _databasePath;
    private MailerMaintenanceLeaseStore? _leaseStore;

    public async ValueTask InitializeAsync()
    {
        _root = Path.Combine(Path.GetTempPath(), "amane-mailer-lease-heartbeat-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
        _databasePath = Path.Combine(_root, "mailer.db");
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:Mailer"] = $"Data Source={_databasePath}",
            })
            .Build();
        var factory = new SqliteConnectionFactory(configuration);
        await new SqlMigrationRunner(factory).ApplyPendingAsync(TestContext.Current.CancellationToken);
        _leaseStore = new MailerMaintenanceLeaseStore(factory);
    }

    public ValueTask DisposeAsync()
    {
        SqliteConnection.ClearAllPools();
        if (_root is not null && Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }

        return ValueTask.CompletedTask;
    }

    [Fact]
    public async Task Keeps_the_lease_alive_past_its_original_duration_while_healthy()
    {
        var ct = TestContext.Current.CancellationToken;
        const string leaseName = "test-lease";
        var owner = Guid.NewGuid();
        var leaseDuration = TimeSpan.FromMilliseconds(300);
        var renewInterval = TimeSpan.FromMilliseconds(60);
        var now = DateTimeOffset.UtcNow;

        var acquired = await _leaseStore!.TryAcquireAsync(leaseName, owner, leaseDuration, now, ct);
        Assert.True(acquired.Acquired);

        await using (new MaintenanceLeaseHeartbeat(
            _leaseStore, leaseName, owner, acquired.FencingToken, leaseDuration, renewInterval, TimeProvider.System))
        {
            // Longer than the original 300ms duration: without renewal this would have expired.
            await Task.Delay(TimeSpan.FromMilliseconds(500), ct);
            Assert.True(await _leaseStore.IsHeldAsync(leaseName, DateTimeOffset.UtcNow, ct));
        }
    }

    [Fact]
    public async Task IsHealthy_latches_false_once_a_renewal_finds_the_lease_reclaimed_by_another_owner()
    {
        var ct = TestContext.Current.CancellationToken;
        const string leaseName = "test-lease";
        var ownerA = Guid.NewGuid();
        var ownerB = Guid.NewGuid();
        var leaseDuration = TimeSpan.FromMilliseconds(150);
        var renewInterval = TimeSpan.FromMilliseconds(400);
        var now = DateTimeOffset.UtcNow;

        var acquiredA = await _leaseStore!.TryAcquireAsync(leaseName, ownerA, leaseDuration, now, ct);
        Assert.True(acquiredA.Acquired);

        await using var heartbeat = new MaintenanceLeaseHeartbeat(
            _leaseStore, leaseName, ownerA, acquiredA.FencingToken, leaseDuration, renewInterval, TimeProvider.System);

        // Let ownerA's lease actually expire (renewInterval is deliberately longer than
        // leaseDuration here), then have a different owner reclaim it -- simulating a real
        // heartbeat failure/delay long enough for someone else to legitimately step in.
        await Task.Delay(TimeSpan.FromMilliseconds(200), ct);
        var acquiredB = await _leaseStore.TryAcquireAsync(
            leaseName, ownerB, TimeSpan.FromMinutes(5), DateTimeOffset.UtcNow, ct);
        Assert.True(acquiredB.Acquired);

        // Give the heartbeat's next tick a chance to observe the reclaim.
        await Task.Delay(TimeSpan.FromMilliseconds(500), ct);
        Assert.False(heartbeat.IsHealthy);
    }

    [Fact]
    public async Task IsHealthy_latches_false_when_a_renewal_throws()
    {
        // Simulates a transient DB failure (SQLite busy / I/O error) during renewal by dropping
        // the lease table out from under the heartbeat -- the next RenewAsync call throws
        // instead of returning false, and the heartbeat must still fail closed rather than
        // leaving IsHealthy at its last-known-good value (post-merge review of #533/PR #537).
        var ct = TestContext.Current.CancellationToken;
        const string leaseName = "test-lease";
        var owner = Guid.NewGuid();
        var leaseDuration = TimeSpan.FromMilliseconds(300);
        var renewInterval = TimeSpan.FromMilliseconds(60);
        var now = DateTimeOffset.UtcNow;

        var acquired = await _leaseStore!.TryAcquireAsync(leaseName, owner, leaseDuration, now, ct);
        Assert.True(acquired.Acquired);

        await using var heartbeat = new MaintenanceLeaseHeartbeat(
            _leaseStore, leaseName, owner, acquired.FencingToken, leaseDuration, renewInterval, TimeProvider.System);

        SqliteConnection.ClearAllPools();
        await using (var connection = new SqliteConnection($"Data Source={_databasePath}"))
        {
            await connection.OpenAsync(ct);
            await using var command = connection.CreateCommand();
            command.CommandText = "DROP TABLE mailer_maintenance_leases;";
            await command.ExecuteNonQueryAsync(ct);
        }

        await Task.Delay(TimeSpan.FromMilliseconds(400), ct);
        Assert.False(heartbeat.IsHealthy);
    }

    [Fact]
    public async Task Stops_renewing_after_dispose()
    {
        var ct = TestContext.Current.CancellationToken;
        const string leaseName = "test-lease";
        var owner = Guid.NewGuid();
        var leaseDuration = TimeSpan.FromMilliseconds(300);
        var renewInterval = TimeSpan.FromMilliseconds(50);
        var now = DateTimeOffset.UtcNow;

        var acquired = await _leaseStore!.TryAcquireAsync(leaseName, owner, leaseDuration, now, ct);
        Assert.True(acquired.Acquired);

        var heartbeat = new MaintenanceLeaseHeartbeat(
            _leaseStore, leaseName, owner, acquired.FencingToken, leaseDuration, renewInterval, TimeProvider.System);
        await Task.Delay(TimeSpan.FromMilliseconds(120), ct);
        await heartbeat.DisposeAsync();

        // No more renewals after dispose: past the original duration, the lease lapses on its own.
        await Task.Delay(TimeSpan.FromMilliseconds(400), ct);
        Assert.False(await _leaseStore.IsHeldAsync(leaseName, DateTimeOffset.UtcNow, ct));
    }
}
