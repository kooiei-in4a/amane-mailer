using System.Net;
using Amane.Mailer.Admin;
using Amane.Mailer.Configuration;
using Amane.Mailer.Data.Sqlite;
using Amane.Mailer.Operations;
using Amane.Mailer.Tests.Fixtures;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Amane.Mailer.Tests;

public sealed class AdminCredentialStartupTests
{
    [Fact]
    public async Task Enabled_admin_syncs_credentials_before_admin_routes_are_served()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var fixture = new MailerAdminFixture();
        await fixture.InitializeAsync();

        using var client = CreateClient(fixture.Factory);
        Assert.True(await AdminConfigExistsAsync(fixture.ConnectionString, ct));

        using var response = await client.GetAsync("/admin/login", ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(fixture.Factory.Services.GetRequiredService<MailerAdminOptions>().Enabled);
    }

    [Fact]
    public async Task Disabled_admin_does_not_run_credential_sync()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var fixture = new MailerApiFixture();
        await fixture.InitializeAsync();

        using var client = CreateClient(fixture.Factory);
        using var health = await client.GetAsync("/healthz", ct);
        using var admin = await client.GetAsync("/admin/login", ct);

        Assert.Equal(HttpStatusCode.OK, health.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, admin.StatusCode);
        Assert.False(fixture.Factory.Services.GetRequiredService<MailerAdminOptions>().Enabled);
        Assert.False(await AdminConfigExistsAsync(fixture.ConnectionString, ct));
    }

    [Fact]
    public async Task Credential_sync_propagates_startup_cancellation_to_db_operations()
    {
        var ct = TestContext.Current.CancellationToken;
        var root = Path.Combine(Path.GetTempPath(), "amane-mailer-admin-sync-cancel", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var databasePath = Path.Combine(root, "mailer.db");
        var tenantConfigPath = Path.Combine(root, "tenants.json");
        await File.WriteAllTextAsync(tenantConfigPath, MailerAdminFixtureHelpers.TenantConfigJson, ct);
        var connectionString = $"Data Source={databasePath}";

        try
        {
            var configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["ConnectionStrings:Mailer"] = connectionString,
                })
                .Build();
            await new SqlMigrationRunner(new SqliteConnectionFactory(configuration)).ApplyPendingAsync(ct);

            var connections = new SqliteConnectionFactory(configuration);
            var options = new MailerAdminOptions
            {
                Enabled = true,
                Username = MailerAdminFixture.Username,
                PasswordHash = MailerAdminFixture.PasswordHash,
            };
            var sessions = new AdminSessionRepository(connections);
            var users = new AdminUserRepository(connections, TimeProvider.System);
            var tenantRegistry = MailerTenantRegistry.Load(
                new ConfigurationBuilder()
                    .AddInMemoryCollection(new Dictionary<string, string?>
                    {
                        ["MAILER_TENANTS_PATH"] = tenantConfigPath,
                        ["MAIL_SERVICE_TOKEN"] = MailerWebApplicationFixtureBase.Token,
                    })
                    .Build());
            var sync = new AdminCredentialSync(sessions, users, tenantRegistry, options);

            using var cancelled = new CancellationTokenSource();
            await cancelled.CancelAsync();

            // EnsureAdminReadyAsync awaits this same method with the startup token (#350).
            await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => sync.EnsureSyncedAsync(cancelled.Token));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    private static async Task<bool> AdminConfigExistsAsync(
        string connectionString,
        CancellationToken cancellationToken)
    {
        await using var connection = new SqliteConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM admin_config;";
        var count = (long)(await command.ExecuteScalarAsync(cancellationToken) ?? 0L);
        return count > 0;
    }

    private static HttpClient CreateClient(WebApplicationFactory<global::Program> factory) =>
        factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
            BaseAddress = new Uri("https://localhost"),
        });
}
