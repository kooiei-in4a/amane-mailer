using Amane.Mailer.Data.Sqlite;
using Amane.Mailer.Operations;
using Amane.Mailer.Worker;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Amane.Mailer.Tests;

public sealed class MailerShutdownLifecycleTests
{
    [Fact]
    public async Task Wal_checkpoint_runs_after_hosted_services_stop()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var root = Path.Combine(Path.GetTempPath(), "amane-mailer-shutdown", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);

        try
        {
            var connectionString = $"Data Source={Path.Combine(root, "mailer.db")}";
            var events = new ShutdownEventRecorder();

            await ApplyMigrationsAsync(connectionString, cancellationToken);

            var builder = Host.CreateApplicationBuilder();
            builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:Mailer"] = connectionString,
            });
            builder.Logging.ClearProviders();
            builder.Logging.AddProvider(new ShutdownEventLoggerProvider(events));
            builder.Services.AddSingleton(events);
            builder.Services.AddSingleton<SqliteConnectionFactory>();
            builder.Services.AddHostedService<ProbeHostedService>();
            builder.Services.AddHostedService<MailerWalCheckpointShutdownService>();

            using var host = builder.Build();
            await host.StartAsync(cancellationToken);
            await host.StopAsync(cancellationToken);

            events.AssertRecordedAfter("probe.stop.completed", "wal.checkpoint.completed");
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    private static async Task ApplyMigrationsAsync(string connectionString, CancellationToken cancellationToken)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:Mailer"] = connectionString,
            })
            .Build();
        var runner = new SqlMigrationRunner(new SqliteConnectionFactory(configuration));
        await runner.ApplyPendingAsync(cancellationToken);
    }

    private sealed class ProbeHostedService(ShutdownEventRecorder events) : IHostedService
    {
        public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public Task StopAsync(CancellationToken cancellationToken)
        {
            events.Record("probe.stop.completed");
            return Task.CompletedTask;
        }
    }

    private sealed class ShutdownEventRecorder
    {
        private readonly object _gate = new();
        private readonly List<string> _events = [];

        public void Record(string eventName)
        {
            lock (_gate)
            {
                _events.Add(eventName);
            }
        }

        public void AssertRecordedAfter(string before, string after)
        {
            List<string> snapshot;
            lock (_gate)
            {
                snapshot = [.. _events];
            }

            var beforeIndex = snapshot.IndexOf(before);
            var afterIndex = snapshot.IndexOf(after);
            var recorded = string.Join(" -> ", snapshot);

            Assert.True(beforeIndex >= 0, $"Missing event '{before}'. Recorded: {recorded}");
            Assert.True(afterIndex >= 0, $"Missing event '{after}'. Recorded: {recorded}");
            Assert.True(beforeIndex < afterIndex, $"Expected '{after}' after '{before}'. Recorded: {recorded}");
        }
    }

    private sealed class ShutdownEventLoggerProvider(ShutdownEventRecorder events) : ILoggerProvider
    {
        public ILogger CreateLogger(string categoryName) => new ShutdownEventLogger(events);

        public void Dispose()
        {
        }
    }

    private sealed class ShutdownEventLogger(ShutdownEventRecorder events) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull =>
            NullScope.Instance;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (eventId.Id == MailerWalCheckpointShutdownService.WalCheckpointCompletedEvent.Id
                && string.Equals(
                    eventId.Name,
                    MailerWalCheckpointShutdownService.WalCheckpointCompletedEvent.Name,
                    StringComparison.Ordinal))
            {
                events.Record("wal.checkpoint.completed");
            }
        }
    }

    private sealed class NullScope : IDisposable
    {
        public static readonly NullScope Instance = new();

        public void Dispose()
        {
        }
    }
}
