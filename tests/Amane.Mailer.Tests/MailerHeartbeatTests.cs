using Amane.Mailer.Data.Sqlite;
using Amane.Mailer.Operations;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;

namespace Amane.Mailer.Tests;

public sealed class MailerHeartbeatTests
{
    [Fact]
    public async Task heartbeat_upsert_inserts_and_updates()
    {
        var ct = TestContext.Current.CancellationToken;
        var root = Path.Combine(Path.GetTempPath(), "amane-mailer-heartbeat-upsert", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var databasePath = Path.Combine(root, "mailer.db");

        try
        {
            var configuration = BuildConfiguration(databasePath);
            var factory = new SqliteConnectionFactory(configuration);
            await MigrateAsync(factory, ct);

            var repository = new MailRequestRepository(factory);
            var t1 = new DateTimeOffset(2026, 6, 24, 10, 0, 0, TimeSpan.Zero);
            await repository.UpsertHeartbeatAsync("worker", t1, ct);

            var heartbeats = await repository.GetHeartbeatsAsync(ct);
            var worker = Assert.Single(heartbeats);
            Assert.Equal("worker", worker.Name);
            Assert.Equal(t1, worker.LastHeartbeatAt);

            var t2 = t1.AddMinutes(1);
            await repository.UpsertHeartbeatAsync("worker", t2, ct);

            heartbeats = await repository.GetHeartbeatsAsync(ct);
            worker = Assert.Single(heartbeats);
            Assert.Equal(t2, worker.LastHeartbeatAt);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task get_heartbeats_returns_empty_when_no_rows()
    {
        var ct = TestContext.Current.CancellationToken;
        var root = Path.Combine(Path.GetTempPath(), "amane-mailer-heartbeat-empty", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var databasePath = Path.Combine(root, "mailer.db");

        try
        {
            var configuration = BuildConfiguration(databasePath);
            var factory = new SqliteConnectionFactory(configuration);
            await MigrateAsync(factory, ct);

            var repository = new MailRequestRepository(factory);
            var heartbeats = await repository.GetHeartbeatsAsync(ct);

            Assert.Empty(heartbeats);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task get_heartbeats_returns_all_rows()
    {
        var ct = TestContext.Current.CancellationToken;
        var root = Path.Combine(Path.GetTempPath(), "amane-mailer-heartbeat-all", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var databasePath = Path.Combine(root, "mailer.db");

        try
        {
            var configuration = BuildConfiguration(databasePath);
            var factory = new SqliteConnectionFactory(configuration);
            await MigrateAsync(factory, ct);

            var repository = new MailRequestRepository(factory);
            var now = new DateTimeOffset(2026, 6, 24, 10, 0, 0, TimeSpan.Zero);
            await repository.UpsertHeartbeatAsync("worker", now, ct);
            await repository.UpsertHeartbeatAsync("sweep", now.AddSeconds(5), ct);

            var heartbeats = await repository.GetHeartbeatsAsync(ct);

            Assert.Equal(2, heartbeats.Count);
            Assert.Contains(heartbeats, h => h.Name == "worker");
            Assert.Contains(heartbeats, h => h.Name == "sweep");
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task healthcheck_returns_healthy_with_fresh_heartbeats()
    {
        var ct = TestContext.Current.CancellationToken;
        var root = Path.Combine(Path.GetTempPath(), "amane-mailer-heartbeat-fresh", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var databasePath = Path.Combine(root, "mailer.db");

        try
        {
            var configuration = BuildConfiguration(databasePath);
            var factory = new SqliteConnectionFactory(configuration);
            await MigrateAsync(factory, ct);

            var repository = new MailRequestRepository(factory);
            var now = DateTimeOffset.UtcNow;
            await repository.UpsertHeartbeatAsync("worker", now, ct);
            await repository.UpsertHeartbeatAsync("sweep", now, ct);

            var exitCode = await MailerCliHost.RunHealthCheckAsync(configuration, ct);

            Assert.Equal(DbMigrateCommand.SuccessExitCode, exitCode);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task healthcheck_returns_unhealthy_when_worker_heartbeat_stale()
    {
        var ct = TestContext.Current.CancellationToken;
        var root = Path.Combine(Path.GetTempPath(), "amane-mailer-heartbeat-worker-stale", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var databasePath = Path.Combine(root, "mailer.db");

        try
        {
            var configuration = BuildConfiguration(databasePath);
            var factory = new SqliteConnectionFactory(configuration);
            await MigrateAsync(factory, ct);

            var repository = new MailRequestRepository(factory);
            var now = DateTimeOffset.UtcNow;
            await repository.UpsertHeartbeatAsync("worker", now.AddMinutes(-10), ct);
            await repository.UpsertHeartbeatAsync("sweep", now, ct);

            var exitCode = await MailerCliHost.RunHealthCheckAsync(configuration, ct);

            Assert.Equal(1, exitCode);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task healthcheck_returns_unhealthy_when_sweep_heartbeat_stale()
    {
        var ct = TestContext.Current.CancellationToken;
        var root = Path.Combine(Path.GetTempPath(), "amane-mailer-heartbeat-sweep-stale", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var databasePath = Path.Combine(root, "mailer.db");

        try
        {
            var configuration = BuildConfiguration(databasePath);
            var factory = new SqliteConnectionFactory(configuration);
            await MigrateAsync(factory, ct);

            var repository = new MailRequestRepository(factory);
            var now = DateTimeOffset.UtcNow;
            await repository.UpsertHeartbeatAsync("worker", now, ct);
            await repository.UpsertHeartbeatAsync("sweep", now.AddMinutes(-10), ct);

            var exitCode = await MailerCliHost.RunHealthCheckAsync(configuration, ct);

            Assert.Equal(1, exitCode);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task healthcheck_returns_unhealthy_when_no_heartbeats_exist()
    {
        var ct = TestContext.Current.CancellationToken;
        var root = Path.Combine(Path.GetTempPath(), "amane-mailer-heartbeat-none", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var databasePath = Path.Combine(root, "mailer.db");

        try
        {
            var configuration = BuildConfiguration(databasePath);
            var factory = new SqliteConnectionFactory(configuration);
            await MigrateAsync(factory, ct);

            var exitCode = await MailerCliHost.RunHealthCheckAsync(configuration, ct);

            Assert.Equal(1, exitCode);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task healthcheck_returns_unhealthy_when_only_worker_heartbeat_exists()
    {
        var ct = TestContext.Current.CancellationToken;
        var root = Path.Combine(Path.GetTempPath(), "amane-mailer-heartbeat-worker-only", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var databasePath = Path.Combine(root, "mailer.db");

        try
        {
            var configuration = BuildConfiguration(databasePath);
            var factory = new SqliteConnectionFactory(configuration);
            await MigrateAsync(factory, ct);

            var repository = new MailRequestRepository(factory);
            await repository.UpsertHeartbeatAsync("worker", DateTimeOffset.UtcNow, ct);

            var exitCode = await MailerCliHost.RunHealthCheckAsync(configuration, ct);

            Assert.Equal(1, exitCode);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task healthcheck_returns_unhealthy_when_only_sweep_heartbeat_exists()
    {
        var ct = TestContext.Current.CancellationToken;
        var root = Path.Combine(Path.GetTempPath(), "amane-mailer-heartbeat-sweep-only", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var databasePath = Path.Combine(root, "mailer.db");

        try
        {
            var configuration = BuildConfiguration(databasePath);
            var factory = new SqliteConnectionFactory(configuration);
            await MigrateAsync(factory, ct);

            var repository = new MailRequestRepository(factory);
            await repository.UpsertHeartbeatAsync("sweep", DateTimeOffset.UtcNow, ct);

            var exitCode = await MailerCliHost.RunHealthCheckAsync(configuration, ct);

            Assert.Equal(1, exitCode);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task healthcheck_skips_heartbeat_when_worker_disabled()
    {
        var ct = TestContext.Current.CancellationToken;
        var root = Path.Combine(Path.GetTempPath(), "amane-mailer-heartbeat-disabled", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var databasePath = Path.Combine(root, "mailer.db");

        try
        {
            var configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["ConnectionStrings:Mailer"] = $"Data Source={databasePath}",
                    ["Mailer:Worker:Enabled"] = "false",
                })
                .Build();

            var factory = new SqliteConnectionFactory(configuration);
            await MigrateAsync(factory, ct);

            var exitCode = await MailerCliHost.RunHealthCheckAsync(configuration, ct);

            Assert.Equal(DbMigrateCommand.SuccessExitCode, exitCode);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task db_stats_reports_heartbeat_age_when_heartbeats_exist()
    {
        var ct = TestContext.Current.CancellationToken;
        var root = Path.Combine(Path.GetTempPath(), "amane-mailer-stats-heartbeat", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var databasePath = Path.Combine(root, "mailer.db");
        var now = new DateTimeOffset(2026, 6, 24, 12, 0, 0, TimeSpan.Zero);

        try
        {
            var configuration = BuildConfiguration(databasePath);
            var factory = new SqliteConnectionFactory(configuration);
            await MigrateAsync(factory, ct);

            var repository = new MailRequestRepository(factory);
            await repository.UpsertHeartbeatAsync("worker", now.AddSeconds(-30), ct);
            await repository.UpsertHeartbeatAsync("sweep", now.AddSeconds(-45), ct);

            var output = new StringWriter();
            var error = new StringWriter();
            var command = new DbStatsCommand(factory, () => now);
            var exitCode = await command.ExecuteAsync(["db", "stats"], output, error, ct);

            Assert.Equal(DbStatsCommand.SuccessExitCode, exitCode);

            var stats = ParseStats(output.ToString());
            Assert.Equal("30", stats["worker_heartbeat_age_seconds"]);
            Assert.Equal("45", stats["sweep_heartbeat_age_seconds"]);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(root, recursive: true);
        }
    }

    private static IConfiguration BuildConfiguration(string databasePath) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:Mailer"] = $"Data Source={databasePath}",
            })
            .Build();

    private static async Task MigrateAsync(SqliteConnectionFactory factory, CancellationToken ct)
    {
        var runner = new SqlMigrationRunner(factory);
        await runner.ApplyPendingAsync(ct);
    }

    private static IReadOnlyDictionary<string, string> ParseStats(string stats) =>
        stats
            .Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries)
            .Select(line => line.Split('=', count: 2))
            .ToDictionary(parts => parts[0], parts => parts[1], StringComparer.Ordinal);
}
