using System.Net;
using Amane.Mailer.Data.Sqlite;
using Amane.Mailer.Data.Sqlite.Models;
using Amane.Mailer.Operations;
using Amane.Mailer.Tests.Fixtures;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace Amane.Mailer.Tests;

/// <summary>
/// Regression suite for #342 / #355 — schema mismatch vs DB/I/O/cancellation
/// classification for readiness probes.
/// </summary>
public sealed class ReadinessFailureClassificationTests
{
    [Fact]
    public async Task Readyz_records_schema_not_ready_on_partial_migrations()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var harness = await ReadyzObservabilityHarness.CreateWithPartialMigrationsAsync(
            throughMigrationFileName: "002_worker_heartbeats.sql",
            cancellationToken: ct);

        using var response = await harness.Client.GetAsync("/readyz", ct);
        var body = await response.Content.ReadAsStringAsync(ct);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        ReadyzAssertionHelpers.AssertReadyBody(body, ready: false);
        ReadyzAssertionHelpers.AssertPrimaryReason(harness, MailerReadinessReasons.SchemaNotReady);
    }

    [Fact]
    public async Task Readyz_records_schema_not_ready_on_checksum_mismatch()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var harness = await ReadyzObservabilityHarness.CreateWithChecksumMismatchAsync(ct);

        using var response = await harness.Client.GetAsync("/readyz", ct);
        var body = await response.Content.ReadAsStringAsync(ct);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        ReadyzAssertionHelpers.AssertReadyBody(body, ready: false);
        ReadyzAssertionHelpers.AssertPrimaryReason(harness, MailerReadinessReasons.SchemaNotReady);
    }

    [Fact]
    public async Task Readyz_records_database_error_when_schema_probe_hits_missing_database()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var harness = await ReadyzObservabilityHarness.CreateWithMissingDatabaseAsync(ct);

        using var response = await harness.Client.GetAsync("/readyz", ct);
        var body = await response.Content.ReadAsStringAsync(ct);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        ReadyzAssertionHelpers.AssertReadyBody(body, ready: false);
        ReadyzAssertionHelpers.AssertPrimaryReason(harness, MailerReadinessReasons.DatabaseError);
        Assert.DoesNotContain("SQLite Error", body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("unable to open", body, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task IsCurrentSchemaReady_propagates_sqlite_exception_for_missing_database()
    {
        var ct = TestContext.Current.CancellationToken;
        var root = Path.Combine(
            Path.GetTempPath(),
            "amane-mailer-readyz-missing-db",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var databasePath = Path.Combine(root, "missing.db");
            var configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["ConnectionStrings:Mailer"] = $"Data Source={databasePath}",
                })
                .Build();
            var runner = new SqlMigrationRunner(new SqliteConnectionFactory(configuration));

            var exception = await Assert.ThrowsAsync<SqliteException>(
                () => runner.IsCurrentSchemaReadyAsync(ct));
            Assert.DoesNotContain("schema_not_ready", exception.Message, StringComparison.Ordinal);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(root))
                MailerWebApplicationFixtureBase.DeleteDirectoryWithRetry(root);
        }
    }

    [Fact]
    public async Task IsCurrentSchemaReady_propagates_io_exception_when_migration_file_locked()
    {
        var ct = TestContext.Current.CancellationToken;
        var root = Path.Combine(
            Path.GetTempPath(),
            "amane-mailer-readyz-io",
            Guid.NewGuid().ToString("N"));
        var migrationDirectory = Path.Combine(root, "migrations");
        Directory.CreateDirectory(migrationDirectory);
        var lockedPath = Path.Combine(migrationDirectory, "001_locked.sql");
        await File.WriteAllTextAsync(lockedPath, "-- locked for readiness probe\n", ct);

        await using var locked = new FileStream(
            lockedPath,
            FileMode.Open,
            FileAccess.ReadWrite,
            FileShare.None);
        try
        {
            var databasePath = Path.Combine(root, "mailer.db");
            var configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["ConnectionStrings:Mailer"] = $"Data Source={databasePath}",
                })
                .Build();
            var runner = new SqlMigrationRunner(
                new SqliteConnectionFactory(configuration),
                migrationDirectory);

            await Assert.ThrowsAsync<IOException>(() => runner.IsCurrentSchemaReadyAsync(ct));
        }
        finally
        {
            await locked.DisposeAsync();
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(root))
                MailerWebApplicationFixtureBase.DeleteDirectoryWithRetry(root);
        }
    }

    [Fact]
    public async Task EvaluateCore_maps_migration_io_exception_to_unexpected_error()
    {
        var metrics = new MailerRuntimeMetrics();
        var logCapture = new CapturingLoggerProvider();
        using var loggerFactory = LoggerFactory.Create(builder => builder.AddProvider(logCapture));
        var evaluator = new MailerReadinessEvaluator(
            metrics,
            loggerFactory.CreateLogger<MailerReadinessEvaluator>());

        var result = await evaluator.EvaluateCoreAsync(
            isSchemaReadyAsync: _ => throw new IOException("disk read failed path=/secret/migrations"),
            isWorkerRunning: static () => true,
            isSweepRunning: static () => true,
            getHeartbeatsAsync: static _ => Task.FromResult<IReadOnlyList<WorkerHeartbeat>>([]),
            maxHeartbeatStaleness: TimeSpan.FromSeconds(300),
            workerEnabled: true,
            cancellationToken: CancellationToken.None);

        Assert.False(result.IsReady);
        Assert.Equal(MailerReadinessReasons.UnexpectedError, result.FailureReason);
        ReadyzAssertionHelpers.AssertPrimaryReason(metrics, MailerReadinessReasons.UnexpectedError);

        var joined = logCapture.JoinedOutput();
        Assert.DoesNotContain("disk read failed", joined, StringComparison.Ordinal);
        Assert.DoesNotContain("/secret/migrations", joined, StringComparison.Ordinal);
    }

    [Fact]
    public async Task EvaluateCore_maps_wrapped_sqlite_exception_to_database_error()
    {
        var metrics = new MailerRuntimeMetrics();
        var logCapture = new CapturingLoggerProvider();
        using var loggerFactory = LoggerFactory.Create(builder => builder.AddProvider(logCapture));
        var evaluator = new MailerReadinessEvaluator(
            metrics,
            loggerFactory.CreateLogger<MailerReadinessEvaluator>());

        var result = await evaluator.EvaluateCoreAsync(
            isSchemaReadyAsync: _ => throw new InvalidOperationException(
                "probe wrap secret=s3cret",
                new SqliteException("locked", 6)),
            isWorkerRunning: static () => true,
            isSweepRunning: static () => true,
            getHeartbeatsAsync: static _ => Task.FromResult<IReadOnlyList<WorkerHeartbeat>>([]),
            maxHeartbeatStaleness: TimeSpan.FromSeconds(300),
            workerEnabled: true,
            cancellationToken: CancellationToken.None);

        Assert.False(result.IsReady);
        Assert.Equal(MailerReadinessReasons.DatabaseError, result.FailureReason);
        ReadyzAssertionHelpers.AssertPrimaryReason(metrics, MailerReadinessReasons.DatabaseError);

        var joined = logCapture.JoinedOutput();
        Assert.DoesNotContain("s3cret", joined, StringComparison.Ordinal);
        Assert.DoesNotContain("locked", joined, StringComparison.Ordinal);
    }

    [Fact]
    public async Task EvaluateCore_recovers_from_schema_not_ready_to_ready()
    {
        var metrics = new MailerRuntimeMetrics();
        var logCapture = new CapturingLoggerProvider();
        using var loggerFactory = LoggerFactory.Create(builder => builder.AddProvider(logCapture));
        var evaluator = new MailerReadinessEvaluator(
            metrics,
            loggerFactory.CreateLogger<MailerReadinessEvaluator>());

        var schemaReady = false;
        var notReady = await evaluator.EvaluateCoreAsync(
            isSchemaReadyAsync: _ => Task.FromResult(schemaReady),
            isWorkerRunning: static () => true,
            isSweepRunning: static () => true,
            getHeartbeatsAsync: static _ => Task.FromResult<IReadOnlyList<WorkerHeartbeat>>(
            [
                new WorkerHeartbeat("worker", DateTimeOffset.UtcNow),
                new WorkerHeartbeat("sweep", DateTimeOffset.UtcNow),
            ]),
            maxHeartbeatStaleness: TimeSpan.FromSeconds(300),
            workerEnabled: true,
            cancellationToken: CancellationToken.None);

        Assert.False(notReady.IsReady);
        Assert.Equal(MailerReadinessReasons.SchemaNotReady, notReady.FailureReason);
        ReadyzAssertionHelpers.AssertPrimaryReason(metrics, MailerReadinessReasons.SchemaNotReady);

        logCapture.Clear();
        schemaReady = true;
        var ready = await evaluator.EvaluateCoreAsync(
            isSchemaReadyAsync: _ => Task.FromResult(schemaReady),
            isWorkerRunning: static () => true,
            isSweepRunning: static () => true,
            getHeartbeatsAsync: static _ => Task.FromResult<IReadOnlyList<WorkerHeartbeat>>(
            [
                new WorkerHeartbeat("worker", DateTimeOffset.UtcNow),
                new WorkerHeartbeat("sweep", DateTimeOffset.UtcNow),
            ]),
            maxHeartbeatStaleness: TimeSpan.FromSeconds(300),
            workerEnabled: true,
            cancellationToken: CancellationToken.None);

        Assert.True(ready.IsReady);
        ReadyzAssertionHelpers.AssertReadyMetrics(metrics);
        Assert.Contains(
            logCapture.Snapshot(),
            static entry =>
                entry.Level == LogLevel.Information &&
                entry.FormattedMessage.Contains("Mailer readiness recovered", StringComparison.Ordinal));
    }

    [Fact]
    public async Task EvaluateCore_recovers_from_database_error_to_ready()
    {
        var metrics = new MailerRuntimeMetrics();
        var logCapture = new CapturingLoggerProvider();
        using var loggerFactory = LoggerFactory.Create(builder => builder.AddProvider(logCapture));
        var evaluator = new MailerReadinessEvaluator(
            metrics,
            loggerFactory.CreateLogger<MailerReadinessEvaluator>());

        var fail = true;
        var notReady = await evaluator.EvaluateCoreAsync(
            isSchemaReadyAsync: _ => fail
                ? throw new SqliteException("db busy", 5)
                : Task.FromResult(true),
            isWorkerRunning: static () => true,
            isSweepRunning: static () => true,
            getHeartbeatsAsync: static _ => Task.FromResult<IReadOnlyList<WorkerHeartbeat>>(
            [
                new WorkerHeartbeat("worker", DateTimeOffset.UtcNow),
                new WorkerHeartbeat("sweep", DateTimeOffset.UtcNow),
            ]),
            maxHeartbeatStaleness: TimeSpan.FromSeconds(300),
            workerEnabled: true,
            cancellationToken: CancellationToken.None);

        Assert.False(notReady.IsReady);
        Assert.Equal(MailerReadinessReasons.DatabaseError, notReady.FailureReason);
        ReadyzAssertionHelpers.AssertPrimaryReason(metrics, MailerReadinessReasons.DatabaseError);

        logCapture.Clear();
        fail = false;
        var ready = await evaluator.EvaluateCoreAsync(
            isSchemaReadyAsync: _ => fail
                ? throw new SqliteException("db busy", 5)
                : Task.FromResult(true),
            isWorkerRunning: static () => true,
            isSweepRunning: static () => true,
            getHeartbeatsAsync: static _ => Task.FromResult<IReadOnlyList<WorkerHeartbeat>>(
            [
                new WorkerHeartbeat("worker", DateTimeOffset.UtcNow),
                new WorkerHeartbeat("sweep", DateTimeOffset.UtcNow),
            ]),
            maxHeartbeatStaleness: TimeSpan.FromSeconds(300),
            workerEnabled: true,
            cancellationToken: CancellationToken.None);

        Assert.True(ready.IsReady);
        ReadyzAssertionHelpers.AssertReadyMetrics(metrics);
        Assert.Contains(
            logCapture.Snapshot(),
            static entry =>
                entry.Level == LogLevel.Information &&
                entry.FormattedMessage.Contains("Mailer readiness recovered", StringComparison.Ordinal));
        Assert.DoesNotContain("db busy", logCapture.JoinedOutput(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task IsCurrentSchemaReady_propagates_operation_canceled()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var root = Path.Combine(
            Path.GetTempPath(),
            "amane-mailer-readyz-cancel",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var migrationDirectory = Path.Combine(root, "migrations");
            Directory.CreateDirectory(migrationDirectory);
            await File.WriteAllTextAsync(
                Path.Combine(migrationDirectory, "001_stub.sql"),
                "-- stub\n",
                CancellationToken.None);

            var databasePath = Path.Combine(root, "mailer.db");
            var configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["ConnectionStrings:Mailer"] = $"Data Source={databasePath}",
                })
                .Build();
            var factory = new SqliteConnectionFactory(configuration);
            await new SqlMigrationRunner(factory).ApplyPendingAsync(CancellationToken.None);
            var runner = new SqlMigrationRunner(factory, migrationDirectory);

            await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => runner.IsCurrentSchemaReadyAsync(cts.Token));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(root))
                MailerWebApplicationFixtureBase.DeleteDirectoryWithRetry(root);
        }
    }

    [Fact]
    public async Task EvaluateCore_cancelled_probe_does_not_overwrite_readiness_observation()
    {
        var metrics = new MailerRuntimeMetrics();
        var logCapture = new CapturingLoggerProvider();
        using var loggerFactory = LoggerFactory.Create(builder => builder.AddProvider(logCapture));
        var evaluator = new MailerReadinessEvaluator(
            metrics,
            loggerFactory.CreateLogger<MailerReadinessEvaluator>());

        evaluator.Observe(MailerReadinessResult.Ready());
        Assert.True(metrics.CaptureSnapshot().Ready);
        logCapture.Clear();

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var cancelled = await evaluator.EvaluateCoreAsync(
            isSchemaReadyAsync: _ => throw new OperationCanceledException(cts.Token),
            isWorkerRunning: static () => true,
            isSweepRunning: static () => true,
            getHeartbeatsAsync: static _ => Task.FromResult<IReadOnlyList<WorkerHeartbeat>>([]),
            maxHeartbeatStaleness: TimeSpan.FromSeconds(300),
            workerEnabled: true,
            cancellationToken: cts.Token);

        Assert.False(cancelled.IsReady);
        var snapshot = metrics.CaptureSnapshot();
        Assert.True(snapshot.ReadinessObserved);
        Assert.True(snapshot.Ready);
        Assert.Null(snapshot.ReadinessFailureReason);
        Assert.DoesNotContain(
            logCapture.Snapshot(),
            static entry => entry.FormattedMessage.Contains("Mailer readiness not ready", StringComparison.Ordinal));
    }

    [Fact]
    public async Task EvaluateCore_maps_database_and_unexpected_exceptions()
    {
        var metrics = new MailerRuntimeMetrics();
        var logCapture = new CapturingLoggerProvider();
        using var loggerFactory = LoggerFactory.Create(builder => builder.AddProvider(logCapture));
        var evaluator = new MailerReadinessEvaluator(
            metrics,
            loggerFactory.CreateLogger<MailerReadinessEvaluator>());

        var databaseResult = await evaluator.EvaluateCoreAsync(
            isSchemaReadyAsync: _ => throw new SqliteException("db busy", 5),
            isWorkerRunning: static () => true,
            isSweepRunning: static () => true,
            getHeartbeatsAsync: static _ => Task.FromResult<IReadOnlyList<WorkerHeartbeat>>([]),
            maxHeartbeatStaleness: TimeSpan.FromSeconds(300),
            workerEnabled: true,
            cancellationToken: CancellationToken.None);

        Assert.False(databaseResult.IsReady);
        Assert.Equal(MailerReadinessReasons.DatabaseError, databaseResult.FailureReason);
        ReadyzAssertionHelpers.AssertPrimaryReason(metrics, MailerReadinessReasons.DatabaseError);

        evaluator.ClearForTests();
        logCapture.Clear();

        var unexpectedResult = await evaluator.EvaluateCoreAsync(
            isSchemaReadyAsync: _ => throw new InvalidOperationException("boom with secret=s3cret"),
            isWorkerRunning: static () => true,
            isSweepRunning: static () => true,
            getHeartbeatsAsync: static _ => Task.FromResult<IReadOnlyList<WorkerHeartbeat>>([]),
            maxHeartbeatStaleness: TimeSpan.FromSeconds(300),
            workerEnabled: true,
            cancellationToken: CancellationToken.None);

        Assert.False(unexpectedResult.IsReady);
        Assert.Equal(MailerReadinessReasons.UnexpectedError, unexpectedResult.FailureReason);
        ReadyzAssertionHelpers.AssertPrimaryReason(metrics, MailerReadinessReasons.UnexpectedError);

        var joined = logCapture.JoinedOutput();
        Assert.DoesNotContain("boom", joined, StringComparison.Ordinal);
        Assert.DoesNotContain("s3cret", joined, StringComparison.Ordinal);
        Assert.DoesNotContain("db busy", joined, StringComparison.Ordinal);
    }

    [Fact]
    public void ClassifyException_maps_sqlite_and_unexpected()
    {
        Assert.Equal(
            MailerReadinessReasons.DatabaseError,
            MailerReadinessEvaluator.ClassifyException(new SqliteException("db busy", 5)));
        Assert.Equal(
            MailerReadinessReasons.DatabaseError,
            MailerReadinessEvaluator.ClassifyException(
                new InvalidOperationException("wrap", new SqliteException("locked", 6))));
        Assert.Equal(
            MailerReadinessReasons.UnexpectedError,
            MailerReadinessEvaluator.ClassifyException(new InvalidOperationException("boom")));
    }

    [Fact]
    public void Observe_records_database_and_unexpected_errors_without_exception_message()
    {
        var metrics = new MailerRuntimeMetrics();
        var logCapture = new CapturingLoggerProvider();
        using var loggerFactory = LoggerFactory.Create(builder => builder.AddProvider(logCapture));
        var evaluator = new MailerReadinessEvaluator(
            metrics,
            loggerFactory.CreateLogger<MailerReadinessEvaluator>());

        evaluator.Observe(MailerReadinessResult.NotReady(MailerReadinessReasons.DatabaseError));
        ReadyzAssertionHelpers.AssertPrimaryReason(metrics, MailerReadinessReasons.DatabaseError);

        evaluator.Observe(MailerReadinessResult.NotReady(MailerReadinessReasons.UnexpectedError));
        ReadyzAssertionHelpers.AssertPrimaryReason(metrics, MailerReadinessReasons.UnexpectedError);

        var joined = logCapture.JoinedOutput();
        Assert.DoesNotContain("boom", joined, StringComparison.Ordinal);
        Assert.DoesNotContain("db busy", joined, StringComparison.Ordinal);
        Assert.DoesNotContain("password", joined, StringComparison.OrdinalIgnoreCase);
    }
}
