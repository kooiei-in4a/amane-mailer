using System.Net;
using Amane.Mailer.Data.Sqlite;
using Amane.Mailer.Operations;
using Amane.Mailer.Tests.Fixtures;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;

namespace Amane.Mailer.Tests;

public sealed class SchemaReadinessTests
{
    [Fact]
    public async Task Healthcheck_fails_on_migration_002_era_schema()
    {
        var ct = TestContext.Current.CancellationToken;
        var root = Path.Combine(Path.GetTempPath(), "amane-mailer-schema-ready-old", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var databasePath = Path.Combine(root, "mailer.db");
        var migrationDirectory = Path.Combine(root, "migrations");

        try
        {
            await ApplyMigrationsThroughAsync(databasePath, migrationDirectory, "002_worker_heartbeats.sql", ct);

            var configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["ConnectionStrings:Mailer"] = $"Data Source={databasePath}",
                    ["Mailer:Worker:Enabled"] = "false",
                })
                .Build();

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
    public async Task Healthcheck_succeeds_on_current_schema_when_worker_disabled()
    {
        var ct = TestContext.Current.CancellationToken;
        var root = Path.Combine(Path.GetTempPath(), "amane-mailer-schema-ready-current", Guid.NewGuid().ToString("N"));
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
            await new SqlMigrationRunner(factory).ApplyPendingAsync(ct);

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
    public async Task IsCurrentSchemaReady_fails_when_checksum_mismatches()
    {
        var ct = TestContext.Current.CancellationToken;
        var root = Path.Combine(Path.GetTempPath(), "amane-mailer-schema-ready-checksum", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var databasePath = Path.Combine(root, "mailer.db");
        var migrationDirectory = Path.Combine(root, "migrations");

        try
        {
            CopyCurrentMigrations(migrationDirectory);
            var factory = CreateFactory(databasePath);
            var runner = new SqlMigrationRunner(factory, migrationDirectory);
            await runner.ApplyPendingAsync(ct);

            await File.AppendAllTextAsync(
                Path.Combine(migrationDirectory, "009_mail_request_scheduled_at.sql"),
                $"{Environment.NewLine}-- edited after apply{Environment.NewLine}",
                ct);

            Assert.False(await runner.IsCurrentSchemaReadyAsync(ct));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Readyz_returns_503_on_migration_002_era_schema()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var harness = await SchemaReadyzHarness.CreateWithPartialMigrationsAsync(
            throughMigrationFileName: "002_worker_heartbeats.sql",
            cancellationToken: ct);

        using var response = await harness.Client.GetAsync("/readyz", ct);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
    }

    [Fact]
    public async Task Readyz_returns_ok_on_current_schema_when_worker_disabled()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var harness = await SchemaReadyzHarness.CreateFullyMigratedAsync(ct);

        using var response = await harness.Client.GetAsync("/readyz", ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    private static async Task ApplyMigrationsThroughAsync(
        string databasePath,
        string migrationDirectory,
        string throughMigrationFileName,
        CancellationToken cancellationToken)
    {
        CopyCurrentMigrations(migrationDirectory);
        var keep = true;
        foreach (var file in Directory.GetFiles(migrationDirectory, "*.sql")
                     .OrderBy(Path.GetFileName, StringComparer.Ordinal))
        {
            var fileName = Path.GetFileName(file);
            if (!keep)
            {
                File.Delete(file);
                continue;
            }

            if (string.Equals(fileName, throughMigrationFileName, StringComparison.Ordinal))
            {
                keep = false;
            }
        }

        var runner = new SqlMigrationRunner(CreateFactory(databasePath), migrationDirectory);
        await runner.ApplyPendingAsync(cancellationToken);
    }

    private static void CopyCurrentMigrations(string migrationDirectory)
    {
        Directory.CreateDirectory(migrationDirectory);
        foreach (var file in Directory.GetFiles(GetCurrentMigrationDirectory(), "*.sql"))
        {
            File.Copy(file, Path.Combine(migrationDirectory, Path.GetFileName(file)), overwrite: true);
        }
    }

    private static string GetCurrentMigrationDirectory()
    {
        var fromOutput = Path.Combine(AppContext.BaseDirectory, "Data", "Migrations");
        if (Directory.Exists(fromOutput))
        {
            return fromOutput;
        }

        return Path.GetFullPath(Path.Combine(
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

    private static SqliteConnectionFactory CreateFactory(string databasePath)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:Mailer"] = $"Data Source={databasePath}",
            })
            .Build();
        return new SqliteConnectionFactory(configuration);
    }

    private sealed class SchemaReadyzHarness : IAsyncDisposable
    {
        private readonly string _root;
        private readonly WebApplicationFactory<global::Program> _factory;

        private SchemaReadyzHarness(string root, WebApplicationFactory<global::Program> factory, HttpClient client)
        {
            _root = root;
            _factory = factory;
            Client = client;
        }

        public HttpClient Client { get; }

        public static Task<SchemaReadyzHarness> CreateFullyMigratedAsync(CancellationToken cancellationToken) =>
            CreateAsync(throughMigrationFileName: null, cancellationToken);

        public static Task<SchemaReadyzHarness> CreateWithPartialMigrationsAsync(
            string throughMigrationFileName,
            CancellationToken cancellationToken) =>
            CreateAsync(throughMigrationFileName, cancellationToken);

        private static async Task<SchemaReadyzHarness> CreateAsync(
            string? throughMigrationFileName,
            CancellationToken cancellationToken)
        {
            var root = Path.Combine(Path.GetTempPath(), "amane-mailer-schema-readyz", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            var databasePath = Path.Combine(root, "mailer.db");
            var tenantConfigDirectory = Path.Combine(root, "config");
            Directory.CreateDirectory(tenantConfigDirectory);
            var tenantConfigPath = Path.Combine(tenantConfigDirectory, "tenants.json");
            await File.WriteAllTextAsync(tenantConfigPath, TenantConfigJson, cancellationToken);

            if (throughMigrationFileName is null)
            {
                var configuration = new ConfigurationBuilder()
                    .AddInMemoryCollection(new Dictionary<string, string?>
                    {
                        ["ConnectionStrings:Mailer"] = $"Data Source={databasePath}",
                    })
                    .Build();
                await new SqlMigrationRunner(new SqliteConnectionFactory(configuration))
                    .ApplyPendingAsync(cancellationToken);
            }
            else
            {
                var migrationDirectory = Path.Combine(root, "migrations");
                await ApplyMigrationsThroughAsync(
                    databasePath,
                    migrationDirectory,
                    throughMigrationFileName,
                    cancellationToken);
            }

            var factory = new WebApplicationFactory<global::Program>().WithWebHostBuilder(builder =>
            {
                builder.UseEnvironment("Testing");
                builder.ConfigureAppConfiguration((_, config) =>
                {
                    config.AddInMemoryCollection(new Dictionary<string, string?>
                    {
                        ["ConnectionStrings:Mailer"] = $"Data Source={databasePath}",
                        ["MAILER_TENANTS_PATH"] = tenantConfigPath,
                        ["Mailer:Worker:Enabled"] = "false",
                        ["Mailer:Admin:Enabled"] = "false",
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
            return new SchemaReadyzHarness(root, factory, client);
        }

        public async ValueTask DisposeAsync()
        {
            Client.Dispose();
            await _factory.DisposeAsync();
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(_root))
            {
                MailerWebApplicationFixtureBase.DeleteDirectoryWithRetry(_root);
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
                  "name": "schema-ready-test",
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
}
