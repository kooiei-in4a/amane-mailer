using System.Net;
using Amane.Mailer.Admin;
using Amane.Mailer.Configuration;
using Amane.Mailer.Data.Sqlite;
using Amane.Mailer.Identity;
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
    public async Task Initialized_runtime_without_active_owner_does_not_sync_legacy_admin_credentials()
    {
        var ct = TestContext.Current.CancellationToken;
        var root = Path.Combine(Path.GetTempPath(), "amane-mailer-admin-managed-gate", Guid.NewGuid().ToString("N"));
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
            var connections = new SqliteConnectionFactory(configuration);
            await new SqlMigrationRunner(connections).ApplyPendingAsync(ct);

            var instance = new InstanceConfigurationRepository(connections, TimeProvider.System);
            Assert.True(await instance.ConfigureAcsAsync(
                Path.Combine(root, "secrets", "acs_connection_string"),
                ct));
            await using (var connection = new SqliteConnection(connectionString))
            {
                await connection.OpenAsync(ct);
                await using var command = connection.CreateCommand();
                command.CommandText = "UPDATE instance_configuration SET initialized_at = '2026-01-01T00:00:00Z' WHERE id = 1;";
                Assert.Equal(1, await command.ExecuteNonQueryAsync(ct));
            }

            var legacyPasswordHash = AdminPasswordHasher.Hash("legacy-environment-password");
            await using var factory = MailerAdminFixtureHelpers.CreateFactory(
                connectionString,
                tenantConfigPath,
                legacyPasswordHash,
                new Dictionary<string, string?>
                {
                    ["AMANE_ADMIN_ENABLED"] = "true",
                    ["AMANE_ADMIN_USERNAME"] = "legacy-environment-admin",
                },
                useEarlyInstanceProbe: true);

            using var client = CreateClient(factory);
            using var response = await client.GetAsync("/admin/login", ct);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);

            var options = factory.Services.GetRequiredService<MailerAdminOptions>();
            var runtimeState = factory.Services.GetRequiredService<InstanceRuntimeState>();
            Assert.True(runtimeState.IsInitialized, runtimeState.ToString());
            Assert.True(options.Enabled);
            Assert.True(options.DatabaseOwnedCredentials);
            Assert.Empty(options.PasswordHash);

            var counts = await ReadAdminCountsAsync(connectionString, ct);
            Assert.Equal(0L, counts.AdminConfigCount);
            Assert.Equal(0L, counts.AdminUserCount);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Managed_admin_is_enabled_by_sqlite_owner_when_environment_flag_is_false()
    {
        var ct = TestContext.Current.CancellationToken;
        var root = Path.Combine(Path.GetTempPath(), "amane-mailer-admin-managed-contract", Guid.NewGuid().ToString("N"));
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
            var connections = new SqliteConnectionFactory(configuration);
            await new SqlMigrationRunner(connections).ApplyPendingAsync(ct);

            var instance = new InstanceConfigurationRepository(connections, TimeProvider.System);
            Assert.True(await instance.ConfigureAcsAsync(
                Path.Combine(root, "secrets", "acs_connection_string"),
                ct));
            const string databaseUsername = "database-owner";
            const string databasePassword = "database-owner-password";
            var users = new AdminUserRepository(connections, TimeProvider.System);
            Assert.True(await users.EnsureInstanceOwnerAsync(
                databaseUsername,
                AdminPasswordHasher.Hash(databasePassword),
                ct));
            var senders = new SenderRepository(connections, TimeProvider.System);
            await senders.CreateAsync("noreply@example.com", "Example", ct);
            Assert.True(await instance.FinalizeAsync(ct));

            var legacyPasswordHash = AdminPasswordHasher.Hash("legacy-environment-password");
            await using var factory = MailerAdminFixtureHelpers.CreateFactory(
                connectionString,
                tenantConfigPath,
                legacyPasswordHash,
                new Dictionary<string, string?>
                {
                    ["AMANE_ADMIN_ENABLED"] = "false",
                    ["AMANE_ADMIN_USERNAME"] = "legacy-environment-admin",
                },
                useEarlyInstanceProbe: true);

            var options = factory.Services.GetRequiredService<MailerAdminOptions>();
            var runtimeState = factory.Services.GetRequiredService<InstanceRuntimeState>();
            Assert.True(runtimeState.IsInitialized, runtimeState.ToString());
            Assert.True(options.Enabled);
            Assert.True(options.DatabaseOwnedCredentials);
            Assert.Empty(options.PasswordHash);

            using var client = CreateClient(factory);
            using var page = await client.GetAsync("/admin/login", ct);
            Assert.Equal(HttpStatusCode.OK, page.StatusCode);
            var requestToken = ReadRequestToken(
                await page.Content.ReadAsStringAsync(ct));
            var csrfCookie = page.Headers.GetValues("Set-Cookie")
                .Single(value => value.StartsWith("__Host-amane-admin-csrf=", StringComparison.Ordinal))
                .Split(';', 2)[0];

            using var login = new HttpRequestMessage(HttpMethod.Post, "/admin/api/login")
            {
                Content = new FormUrlEncodedContent(new Dictionary<string, string>
                {
                    ["__RequestVerificationToken"] = requestToken,
                    ["username"] = databaseUsername,
                    ["password"] = databasePassword,
                }),
            };
            login.Headers.TryAddWithoutValidation("Cookie", csrfCookie);
            using var response = await client.SendAsync(login, ct);
            Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
            Assert.Equal("/admin", response.Headers.Location?.OriginalString);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

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
                    .Build(),
                "Testing");
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

    private static async Task<(long AdminConfigCount, long AdminUserCount)> ReadAdminCountsAsync(
        string connectionString,
        CancellationToken cancellationToken)
    {
        await using var connection = new SqliteConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT (SELECT COUNT(*) FROM admin_config), (SELECT COUNT(*) FROM admin_users);";
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        Assert.True(await reader.ReadAsync(cancellationToken));
        return (reader.GetInt64(0), reader.GetInt64(1));
    }

    private static string ReadRequestToken(string html)
    {
        const string prefix = "name=\"__RequestVerificationToken\" value=\"";
        var start = html.IndexOf(prefix, StringComparison.Ordinal);
        Assert.True(start >= 0);
        start += prefix.Length;
        var end = html.IndexOf('"', start);
        Assert.True(end > start);
        return html[start..end];
    }

    private static HttpClient CreateClient(WebApplicationFactory<global::Program> factory) =>
        factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
            BaseAddress = new Uri("https://localhost"),
        });
}
