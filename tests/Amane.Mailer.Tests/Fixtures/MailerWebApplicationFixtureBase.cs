using Amane.Mailer.Data.Sqlite;
using Amane.Mailer.Operations;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;

namespace Amane.Mailer.Tests.Fixtures;

public abstract class MailerWebApplicationFixtureBase(bool workerEnabled) : IAsyncLifetime
{
    private string? _databasePath;
    private string? _tenantConfigDirectory;
    private TestWebApplicationFactory? _factory;

    public static readonly Guid TenantId = Guid.Parse("00000000-0000-0000-0000-000000000101");
    public const string SourceService = "amane-v2-internal";
    public const string Token = "amk_00000000000000000000000000000101.AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA";

    public WebApplicationFactory<global::Program> Factory => _factory!;
    public string ConnectionString => $"Data Source={_databasePath}";
    public string TenantConfigPath => Path.Combine(_tenantConfigDirectory!, "tenants.json");

    protected virtual IReadOnlyDictionary<string, string?> ExtraConfiguration =>
        new Dictionary<string, string?>();

    public async ValueTask InitializeAsync()
    {
        var root = Path.Combine(Path.GetTempPath(), "amane-mailer-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        _databasePath = Path.Combine(root, "mailer.db");

        _tenantConfigDirectory = Path.Combine(root, "config");
        Directory.CreateDirectory(_tenantConfigDirectory);
        var tenantConfigPath = Path.Combine(_tenantConfigDirectory, "tenants.json");
        await File.WriteAllTextAsync(tenantConfigPath, BuildTenantConfigJson());

        var factory = new SqliteConnectionFactory(
            new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["ConnectionStrings:Mailer"] = ConnectionString,
                })
                .Build());
        var runner = new SqlMigrationRunner(factory);
        await runner.ApplyPendingAsync();
        await SeedManagedIdentityAsync(factory);

        _factory = new TestWebApplicationFactory(
            ConnectionString,
            tenantConfigPath,
            workerEnabled,
            ExtraConfiguration,
            ConfigureMailerServices);
    }

    public async Task ResetAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = new SqliteConnection(ConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            DELETE FROM mail_attempts;
            DELETE FROM mail_requests;
            DELETE FROM delivery_events;
            DELETE FROM mail_suppressions;
            DELETE FROM recipient_delivery_events;
            DELETE FROM provider_event_inbox;
            DELETE FROM admin_audit_events;
            DELETE FROM admin_login_throttle;
            DELETE FROM admin_user_capabilities;
            DELETE FROM admin_sessions;
            DELETE FROM admin_config;
            DELETE FROM mailer_maintenance_leases;
            """;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        if (_factory is not null)
        {
            await _factory.DisposeAsync();
            _factory = null;
        }

        // Hosted workers may release SQLite handles slightly after DisposeAsync returns.
        // Clear pools and give the finalizer a chance before deleting the temp DB directory
        // so faster event-driven tests do not race the Windows file lock (#287 review).
        PrepareForDirectoryDeleteRetry();

        if (_databasePath is not null)
        {
            var root = Path.GetDirectoryName(_databasePath);
            if (root is not null && Directory.Exists(root))
            {
                DeleteDirectoryWithRetry(root);
            }
        }
    }

    internal static void DeleteDirectoryWithRetry(string path)
    {
        const int maxAttempts = 10;
        for (var attempt = 0; attempt < maxAttempts; attempt++)
        {
            try
            {
                Directory.Delete(path, recursive: true);
                return;
            }
            catch (IOException) when (attempt < maxAttempts - 1)
            {
                PrepareForDirectoryDeleteRetry();
                Thread.Sleep(50 * (attempt + 1));
            }
            catch (UnauthorizedAccessException) when (attempt < maxAttempts - 1)
            {
                PrepareForDirectoryDeleteRetry();
                Thread.Sleep(50 * (attempt + 1));
            }
        }
    }

    private static void PrepareForDirectoryDeleteRetry()
    {
        SqliteConnection.ClearAllPools();
        GC.Collect();
        GC.WaitForPendingFinalizers();
        SqliteConnection.ClearAllPools();
    }

    protected virtual void ConfigureMailerServices(IServiceCollection services)
    {
    }

    private static async Task SeedManagedIdentityAsync(SqliteConnectionFactory factory)
    {
        await using var connection = await factory.OpenConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO senders (
                sender_id, email, display_name, enabled, created_at, disabled_at)
            VALUES (
                @SenderId, 'noreply@example.com', 'Example Service', 1,
                '2026-01-01T00:00:00.0000000Z', NULL);

            INSERT INTO api_keys (
                key_id, sender_id, name, secret_digest, created_at, revoked_at)
            VALUES (
                @KeyId, @SenderId, 'test',
                X'66687aadf862bd776c8fc18b8e9f8e20089714856ee233b3902a591d0d5f2925',
                '2026-01-01T00:00:00.0000000Z', NULL);
            """;
        command.Parameters.AddWithValue("@SenderId", TenantId.ToString("D"));
        command.Parameters.AddWithValue("@KeyId", TenantId.ToString("D"));
        await command.ExecuteNonQueryAsync();
    }

    protected virtual string BuildTenantConfigJson() =>
        $$"""
        {
          "version": 1,
          "environment": "develop",
          "tenants": [
            {
              "tenant_id": "{{TenantId}}",
              "name": "example-develop",
              "source_services": ["{{SourceService}}"],
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

    private sealed class TestWebApplicationFactory(
        string connectionString,
        string tenantConfigPath,
        bool workerEnabled,
        IReadOnlyDictionary<string, string?> extraConfiguration,
        Action<IServiceCollection> configureServices) : WebApplicationFactory<global::Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Testing");
            builder.ConfigureAppConfiguration((_, configuration) =>
            {
                var settings = new Dictionary<string, string?>
                {
                    ["ConnectionStrings:Mailer"] = connectionString,
                    ["MAILER_TENANTS_PATH"] = tenantConfigPath,
                    ["Mailer:Worker:Enabled"] = workerEnabled.ToString(),
                    ["MAIL_SERVICE_TOKEN"] = Token,
                };

                foreach (var (key, value) in extraConfiguration)
                {
                    settings[key] = value;
                }

                configuration.AddInMemoryCollection(settings);
            });
            builder.ConfigureServices(services =>
            {
                if (!workerEnabled)
                {
                    services.RemoveAll<IHostedService>();
                }

                configureServices(services);
            });
        }
    }
}
