using Amane.Mailer.Data.Sqlite;
using Amane.Mailer.Operations;
using Amane.Mailer.Worker;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Amane.Mailer.Tests.Fixtures;

public sealed class ReadyzObservabilityHarness : IAsyncDisposable
{
    private readonly string _root;
    private readonly WebApplicationFactory<global::Program> _factory;

    private ReadyzObservabilityHarness(
        string root,
        string databasePath,
        WebApplicationFactory<global::Program> factory,
        HttpClient client,
        MailRequestRepository repository,
        WorkerServiceStatus serviceStatus,
        MailerRuntimeMetrics runtimeMetrics,
        CapturingLoggerProvider logCapture)
    {
        _root = root;
        DatabasePath = databasePath;
        _factory = factory;
        Client = client;
        Repository = repository;
        ServiceStatus = serviceStatus;
        RuntimeMetrics = runtimeMetrics;
        LogCapture = logCapture;
    }

    public HttpClient Client { get; }

    public string DatabasePath { get; }

    public MailRequestRepository Repository { get; }

    public WorkerServiceStatus ServiceStatus { get; }

    public MailerRuntimeMetrics RuntimeMetrics { get; }

    public CapturingLoggerProvider LogCapture { get; }

    public static Task<ReadyzObservabilityHarness> CreateAsync(
        CancellationToken cancellationToken,
        bool workerEnabled = true) =>
        CreateCoreAsync(
            cancellationToken,
            workerEnabled,
            migrateFully: true,
            throughMigrationFileName: null);

    public static Task<ReadyzObservabilityHarness> CreateWithPartialMigrationsAsync(
        string throughMigrationFileName,
        CancellationToken cancellationToken) =>
        CreateCoreAsync(
            cancellationToken,
            workerEnabled: false,
            migrateFully: false,
            throughMigrationFileName: throughMigrationFileName);

    public static async Task<ReadyzObservabilityHarness> CreateWithChecksumMismatchAsync(
        CancellationToken cancellationToken)
    {
        var harness = await CreateCoreAsync(
            cancellationToken,
            workerEnabled: false,
            migrateFully: true,
            throughMigrationFileName: null);
        await CorruptAppliedChecksumAsync(harness.DatabasePath, cancellationToken);
        return harness;
    }

    public static async Task<ReadyzObservabilityHarness> CreateWithMissingDatabaseAsync(
        CancellationToken cancellationToken)
    {
        var harness = await CreateCoreAsync(
            cancellationToken,
            workerEnabled: false,
            migrateFully: true,
            throughMigrationFileName: null);
        SqliteConnection.ClearAllPools();
        File.Delete(harness.DatabasePath);
        TryDeleteFile(harness.DatabasePath + "-wal");
        TryDeleteFile(harness.DatabasePath + "-shm");
        return harness;
    }

    private static async Task CorruptAppliedChecksumAsync(
        string databasePath,
        CancellationToken cancellationToken)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:Mailer"] = $"Data Source={databasePath}",
            })
            .Build();
        await using var connection = await new SqliteConnectionFactory(configuration)
            .OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE schema_migrations
            SET checksum = '0000000000000000000000000000000000000000000000000000000000000000'
            WHERE version = (
                SELECT version FROM schema_migrations ORDER BY version DESC LIMIT 1);
            """;
        var updated = await command.ExecuteNonQueryAsync(cancellationToken);
        Assert.True(updated >= 1);
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch (IOException)
        {
            // Best-effort cleanup beside the primary DB delete.
        }
    }

    private static async Task<ReadyzObservabilityHarness> CreateCoreAsync(
        CancellationToken cancellationToken,
        bool workerEnabled,
        bool migrateFully,
        string? throughMigrationFileName)
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            "amane-mailer-readyz-observability",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var databasePath = Path.Combine(root, "mailer.db");
        var tenantConfigDirectory = Path.Combine(root, "config");
        Directory.CreateDirectory(tenantConfigDirectory);
        var tenantConfigPath = Path.Combine(tenantConfigDirectory, "tenants.json");
        await File.WriteAllTextAsync(tenantConfigPath, TenantConfigJson, cancellationToken);

        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:Mailer"] = $"Data Source={databasePath}",
            })
            .Build();
        var factoryForMigrate = new SqliteConnectionFactory(configuration);
        if (migrateFully)
        {
            await new SqlMigrationRunner(factoryForMigrate).ApplyPendingAsync(cancellationToken);
        }
        else
        {
            var migrationDirectory = Path.Combine(root, "migrations");
            ApplyMigrationsThrough(migrationDirectory, throughMigrationFileName!);
            await new SqlMigrationRunner(factoryForMigrate, migrationDirectory)
                .ApplyPendingAsync(cancellationToken);
        }

        var logCapture = new CapturingLoggerProvider();
        var factory = new WebApplicationFactory<global::Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Testing");
            builder.ConfigureLogging(logging =>
            {
                logging.ClearProviders();
                logging.AddProvider(logCapture);
            });
            builder.ConfigureAppConfiguration((_, config) =>
            {
                config.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["ConnectionStrings:Mailer"] = $"Data Source={databasePath}",
                    ["MAILER_TENANTS_PATH"] = tenantConfigPath,
                    ["Mailer:Worker:Enabled"] = workerEnabled ? "true" : "false",
                    ["MAIL_SERVICE_TOKEN"] = MailerWebApplicationFixtureBase.Token,
                });
            });
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<IHostedService>();
            });
        });

        var client = factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
        });
        return new ReadyzObservabilityHarness(
            root,
            databasePath,
            factory,
            client,
            factory.Services.GetRequiredService<MailRequestRepository>(),
            factory.Services.GetRequiredService<WorkerServiceStatus>(),
            factory.Services.GetRequiredService<MailerRuntimeMetrics>(),
            logCapture);
    }

    public async ValueTask DisposeAsync()
    {
        Client.Dispose();
        await _factory.DisposeAsync();
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(_root))
            MailerWebApplicationFixtureBase.DeleteDirectoryWithRetry(_root);
    }

    private static void ApplyMigrationsThrough(string migrationDirectory, string throughMigrationFileName)
    {
        Directory.CreateDirectory(migrationDirectory);
        var source = Path.Combine(AppContext.BaseDirectory, "Data", "Migrations");
        if (!Directory.Exists(source))
        {
            source = Path.GetFullPath(Path.Combine(
                AppContext.BaseDirectory,
                "..",
                "..",
                "..",
                "..",
                "..",
                "src",
                "Amane.Mailer",
                "Data",
                "Migrations"));
        }

        var keep = true;
        foreach (var file in Directory.GetFiles(source, "*.sql")
                     .OrderBy(Path.GetFileName, StringComparer.Ordinal))
        {
            var fileName = Path.GetFileName(file);
            if (!keep)
                continue;

            File.Copy(file, Path.Combine(migrationDirectory, fileName), overwrite: true);
            if (string.Equals(fileName, throughMigrationFileName, StringComparison.Ordinal))
                keep = false;
        }
    }

    private static string TenantConfigJson =>
        $$"""
        {
          "version": 1,
          "environment": "develop",
          "tenants": [
            {
              "tenant_id": "{{MailerWebApplicationFixtureBase.TenantId}}",
              "name": "example-develop",
              "source_services": ["{{MailerWebApplicationFixtureBase.SourceService}}"],
              "default_from": {
                "email": "noreply@example.com",
                "display_name": "Example Service"
              },
              "token_env": "MAIL_SERVICE_TOKEN",
              "provider": "mailpit",
              "live_sending": false,
              "metadata_max_bytes": 4096,
              "retry": {
                "max_attempts": 3,
                "initial_delay_seconds": 1,
                "max_delay_seconds": 2
              }
            }
          ]
        }
        """;
}
