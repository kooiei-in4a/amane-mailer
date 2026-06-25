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
    public const string SourceService = "example-service";
    public const string Token = "test-mail-service-token";

    public WebApplicationFactory<global::Program> Factory => _factory!;
    public string ConnectionString => $"Data Source={_databasePath}";

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
        await File.WriteAllTextAsync(tenantConfigPath, TenantConfigJson);

        var factory = new SqliteConnectionFactory(
            new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["ConnectionStrings:Mailer"] = ConnectionString,
                })
                .Build());
        var runner = new SqlMigrationRunner(factory);
        await runner.ApplyPendingAsync();

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

        SqliteConnection.ClearAllPools();

        if (_databasePath is not null)
        {
            var root = Path.GetDirectoryName(_databasePath);
            if (root is not null && Directory.Exists(root))
            {
                DeleteDirectoryWithRetry(root);
            }
        }
    }

    private static void DeleteDirectoryWithRetry(string path)
    {
        for (var attempt = 0; attempt < 5; attempt++)
        {
            try
            {
                Directory.Delete(path, recursive: true);
                return;
            }
            catch (IOException) when (attempt < 4)
            {
                SqliteConnection.ClearAllPools();
                Thread.Sleep(50);
            }
        }
    }

    protected virtual void ConfigureMailerServices(IServiceCollection services)
    {
    }

    private static string TenantConfigJson =>
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
