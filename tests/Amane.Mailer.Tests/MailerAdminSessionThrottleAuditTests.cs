using System.Net;
using System.Net.Http;
using Amane.Mailer.Admin;
using Amane.Mailer.Data.Sqlite;
using Amane.Mailer.Data.Sqlite.Models;
using Amane.Mailer.Operations;
using Amane.Mailer.Tests.Fixtures;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Amane.Mailer.Tests;

public sealed class MailerAdminSessionThrottleMigrationTests
{
    [Fact]
    public async Task Db_migrate_creates_admin_session_and_throttle_tables()
    {
        var ct = TestContext.Current.CancellationToken;
        var root = Path.Combine(Path.GetTempPath(), "amane-mailer-admin-session", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var databasePath = Path.Combine(root, "mailer.db");

        try
        {
            var configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["ConnectionStrings:Mailer"] = $"Data Source={databasePath}",
                })
                .Build();

            var factory = new SqliteConnectionFactory(configuration);
            var runner = new SqlMigrationRunner(factory);
            var applied = await runner.ApplyPendingAsync(ct);

            Assert.Contains("005_admin_session_and_throttle.sql", applied);

            await using var connection = new SqliteConnection($"Data Source={databasePath}");
            await connection.OpenAsync(ct);

            Assert.True(await TableExistsAsync(connection, "admin_config", ct));
            Assert.True(await TableExistsAsync(connection, "admin_sessions", ct));
            Assert.True(await TableExistsAsync(connection, "admin_login_throttle", ct));

            var indexes = await GetIndexNamesAsync(connection, ct);
            Assert.Contains("idx_admin_sessions_actor_active", indexes);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(root, recursive: true);
        }
    }

    private static async Task<bool> TableExistsAsync(
        SqliteConnection connection,
        string tableName,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT 1 FROM sqlite_master WHERE type = 'table' AND name = @Name LIMIT 1;";
        command.Parameters.AddWithValue("@Name", tableName);
        var result = await command.ExecuteScalarAsync(cancellationToken);
        return result is not null;
    }

    private static async Task<IReadOnlySet<string>> GetIndexNamesAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        var indexes = new HashSet<string>(StringComparer.Ordinal);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT name FROM sqlite_master WHERE type = 'index';";

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            indexes.Add(reader.GetString(0));
        }

        return indexes;
    }
}

[Collection(MailerTestCollection.Name)]
public sealed class MailerAdminSessionThrottleAuditTests(MailerAdminFixture fixture)
    : IClassFixture<MailerAdminFixture>, IAsyncLifetime
{
    public async ValueTask InitializeAsync()
    {
        await fixture.ResetAsync();
        fixture.Factory.Services.GetRequiredService<AdminLoginThrottle>().Clear();
        fixture.Factory.Services.GetRequiredService<AdminSessionExpiredDedupe>().Clear();
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    [Fact]
    public async Task Login_throttle_survives_process_restart_simulation()
    {
        var ct = TestContext.Current.CancellationToken;
        using var client = CreateClient(fixture.Factory);
        var csrfToken = await ReadCsrfTokenAsync(client, ct);

        for (var attempt = 0; attempt < 5; attempt++)
        {
            using var response = await client.PostAsync(
                "/admin/api/login",
                CreateLoginContent(csrfToken, MailerAdminFixture.Username, "wrong-password"),
                ct);
            Assert.Equal(
                attempt < 4 ? HttpStatusCode.Unauthorized : HttpStatusCode.TooManyRequests,
                response.StatusCode);
        }

        fixture.Factory.Services.GetRequiredService<AdminLoginThrottle>().Clear();

        using var locked = await client.PostAsync(
            "/admin/api/login",
            CreateLoginContent(csrfToken, MailerAdminFixture.Username, MailerAdminFixture.Password),
            ct);

        Assert.Equal(HttpStatusCode.TooManyRequests, locked.StatusCode);
    }

    [Fact]
    public async Task Password_hash_change_revokes_existing_session_after_restart()
    {
        var ct = TestContext.Current.CancellationToken;
        var root = Path.Combine(Path.GetTempPath(), "amane-mailer-admin-session-restart", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var databasePath = Path.Combine(root, "mailer.db");
        var tenantConfigDirectory = Path.Combine(root, "config");
        Directory.CreateDirectory(tenantConfigDirectory);
        var tenantConfigPath = Path.Combine(tenantConfigDirectory, "tenants.json");
        await File.WriteAllTextAsync(tenantConfigPath, MailerAdminFixtureHelpers.TenantConfigJson, ct);

        var connectionString = $"Data Source={databasePath}";
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["ConnectionStrings:Mailer"] = connectionString })
            .Build();
        var runner = new SqlMigrationRunner(new SqliteConnectionFactory(configuration));
        await runner.ApplyPendingAsync(ct);

        var originalHash = AdminPasswordHasher.Hash(MailerAdminFixture.Password);
        var rotatedHash = AdminPasswordHasher.Hash("new-password-for-rotation-test");

        await using var originalFactory = MailerAdminFixtureHelpers.CreateFactory(
            connectionString,
            tenantConfigPath,
            originalHash);
        using var client = originalFactory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
            BaseAddress = new Uri("https://localhost"),
        });
        var authCookie = await LoginAndCaptureAuthCookieAsync(client, ct);

        using var authorized = await client.GetAsync("/admin/mail-requests", ct);
        Assert.Equal(HttpStatusCode.OK, authorized.StatusCode);

        await originalFactory.DisposeAsync();
        SqliteConnection.ClearAllPools();

        await using var rotatedFactory = MailerAdminFixtureHelpers.CreateFactory(
            connectionString,
            tenantConfigPath,
            rotatedHash);
        using var rotatedClient = rotatedFactory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
            BaseAddress = new Uri("https://localhost"),
        });
        rotatedClient.DefaultRequestHeaders.Add("Cookie", authCookie);

        using var rejected = await rotatedClient.GetAsync("/admin/mail-requests", ct);
        Assert.Equal(HttpStatusCode.Redirect, rejected.StatusCode);
        Assert.Contains("/admin/login", rejected.Headers.Location?.OriginalString, StringComparison.Ordinal);

        var csrfToken = await ReadCsrfTokenAsync(rotatedClient, ct);
        using var loginWithNewPassword = await rotatedClient.PostAsync(
            "/admin/api/login",
            CreateLoginContent(csrfToken, MailerAdminFixture.Username, "new-password-for-rotation-test"),
            ct);
        Assert.Equal(HttpStatusCode.Redirect, loginWithNewPassword.StatusCode);

        SqliteConnection.ClearAllPools();
        Directory.Delete(root, recursive: true);
    }

    [Fact]
    public async Task Logout_revokes_server_session_and_records_audit_event()
    {
        var ct = TestContext.Current.CancellationToken;
        using var client = CreateClient(fixture.Factory);
        await LoginAsync(client, ct);

        var sessionRepository = fixture.Factory.Services.GetRequiredService<AdminSessionRepository>();
        var auditRepository = fixture.Factory.Services.GetRequiredService<AdminAuditRepository>();
        var options = fixture.Factory.Services.GetRequiredService<MailerAdminOptions>();
        var timeProvider = fixture.Factory.Services.GetRequiredService<TimeProvider>();
        var logger = fixture.Factory.Services.GetRequiredService<ILoggerFactory>()
            .CreateLogger(AdminAuditLog.LoggerCategory);

        var activeSession = await GetSingleActiveSessionAsync(fixture.ConnectionString, ct);
        var now = timeProvider.GetUtcNow();

        await sessionRepository.RevokeSessionAsync(
            activeSession,
            AdminSessionRevokeReasons.Logout,
            now,
            ct);

        await AdminAuditLog.WriteBestEffortAsync(
            auditRepository,
            logger,
            new AdminAuditEvent
            {
                EventType = AdminAuditLog.EventTypes.Logout,
                Actor = MailerAdminFixture.Username,
                OccurredAt = now,
                TargetType = AdminAuditLog.TargetTypes.AdminSession,
                TargetId = activeSession,
                Result = AdminAuditLog.Results.Success,
            },
            ct);

        var revoked = await sessionRepository.GetSessionAsync(activeSession, ct);
        Assert.NotNull(revoked?.RevokedAt);
        Assert.Equal(AdminSessionRevokeReasons.Logout, revoked.RevokeReason);

        var rows = await auditRepository.ListRecentAsync(10, ct);
        var logoutEvent = rows.FirstOrDefault(row => row.EventType == AdminAuditLog.EventTypes.Logout);
        Assert.NotNull(logoutEvent);
        Assert.Equal(MailerAdminFixture.Username, logoutEvent.Actor);
        Assert.Equal(AdminAuditLog.Results.Success, logoutEvent.Result);
        Assert.Equal(activeSession, logoutEvent.TargetId);
    }

    private static async Task<string> GetSingleActiveSessionAsync(string connectionString, CancellationToken ct)
    {
        await using var connection = new SqliteConnection(connectionString);
        await connection.OpenAsync(ct);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT session_id
            FROM admin_sessions
            WHERE revoked_at IS NULL
            ORDER BY issued_at DESC
            LIMIT 1;
            """;
        var result = await command.ExecuteScalarAsync(ct);
        return Assert.IsType<string>(result);
    }

    [Fact]
    public async Task Account_temporarily_locked_and_login_rate_limited_are_audited()
    {
        var ct = TestContext.Current.CancellationToken;
        using var client = CreateClient(fixture.Factory);
        var csrfToken = await ReadCsrfTokenAsync(client, ct);

        for (var attempt = 0; attempt < 4; attempt++)
        {
            using var response = await client.PostAsync(
                "/admin/api/login",
                CreateLoginContent(csrfToken, MailerAdminFixture.Username, "wrong-password"),
                ct);
            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        }

        using var locked = await client.PostAsync(
            "/admin/api/login",
            CreateLoginContent(csrfToken, MailerAdminFixture.Username, "wrong-password"),
            ct);
        Assert.Equal(HttpStatusCode.TooManyRequests, locked.StatusCode);

        using var rateLimited = await client.PostAsync(
            "/admin/api/login",
            CreateLoginContent(csrfToken, MailerAdminFixture.Username, MailerAdminFixture.Password),
            ct);
        Assert.Equal(HttpStatusCode.TooManyRequests, rateLimited.StatusCode);

        var auditRepository = fixture.Factory.Services.GetRequiredService<AdminAuditRepository>();
        var rows = await auditRepository.ListRecentAsync(20, ct);

        Assert.Contains(rows, row => row.EventType == AdminAuditLog.EventTypes.AccountTemporarilyLocked);
        Assert.Contains(rows, row => row.EventType == AdminAuditLog.EventTypes.LoginRateLimited);
    }

    [Fact]
    public void Hash_network_identifiers_requires_key_when_admin_enabled()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["AMANE_ADMIN_ENABLED"] = "true",
                ["AMANE_ADMIN_USERNAME"] = "admin",
                ["AMANE_ADMIN_PASSWORD_HASH"] = AdminPasswordHasher.Hash("password"),
                ["AMANE_ADMIN_AUDIT_HASH_NETWORK_IDENTIFIERS"] = "true",
            })
            .Build();

        var options = MailerAdminOptions.Load(configuration);
        var ex = Assert.Throws<InvalidOperationException>(() => options.Validate());
        Assert.Contains("AMANE_ADMIN_AUDIT_IDENTIFIER_HASH_KEY", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Parallel_failed_logins_persist_atomic_failure_count()
    {
        var ct = TestContext.Current.CancellationToken;
        const int parallelAttempts = 5;
        var tasks = new Task<HttpStatusCode>[parallelAttempts];
        for (var index = 0; index < parallelAttempts; index++)
        {
            tasks[index] = PostFailedLoginAsync(fixture.Factory, ct);
        }

        var statuses = await Task.WhenAll(tasks);

        Assert.Contains(HttpStatusCode.TooManyRequests, statuses);

        var failureCount = await ReadThrottleFailureCountAsync(fixture.ConnectionString, ct);
        Assert.Equal(parallelAttempts, failureCount);
    }

    private static async Task<HttpStatusCode> PostFailedLoginAsync(
        WebApplicationFactory<global::Program> factory,
        CancellationToken cancellationToken)
    {
        using var client = CreateClient(factory);
        var csrfToken = await ReadCsrfTokenAsync(client, cancellationToken);
        using var response = await client.PostAsync(
            "/admin/api/login",
            CreateLoginContent(csrfToken, MailerAdminFixture.Username, "wrong-password"),
            cancellationToken);
        return response.StatusCode;
    }

    private static async Task<int> ReadThrottleFailureCountAsync(string connectionString, CancellationToken cancellationToken)
    {
        await using var connection = new SqliteConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT failure_count FROM admin_login_throttle LIMIT 1;";
        var result = await command.ExecuteScalarAsync(cancellationToken);
        return Convert.ToInt32(result, System.Globalization.CultureInfo.InvariantCulture);
    }

    private static HttpClient CreateClient(WebApplicationFactory<global::Program> factory) =>
        factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
            BaseAddress = new Uri("https://localhost"),
        });

    private static async Task<string> LoginAndCaptureAuthCookieAsync(HttpClient client, CancellationToken cancellationToken)
    {
        var csrfToken = await ReadCsrfTokenAsync(client, cancellationToken);
        using var response = await client.PostAsync(
            "/admin/api/login",
            CreateLoginContent(csrfToken, MailerAdminFixture.Username, MailerAdminFixture.Password),
            cancellationToken);

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        var setCookie = Assert.Single(response.Headers.GetValues("Set-Cookie"));
        return setCookie.Split(';')[0];
    }

    private static async Task LoginAsync(HttpClient client, CancellationToken cancellationToken)
    {
        var csrfToken = await ReadCsrfTokenAsync(client, cancellationToken);
        using var response = await client.PostAsync(
            "/admin/api/login",
            CreateLoginContent(csrfToken, MailerAdminFixture.Username, MailerAdminFixture.Password),
            cancellationToken);

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
    }

    private static async Task<string> ReadCsrfTokenAsync(HttpClient client, CancellationToken cancellationToken)
    {
        using var response = await client.GetAsync("/admin/login", cancellationToken);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var html = await response.Content.ReadAsStringAsync(cancellationToken);
        const string marker = "name=\"__RequestVerificationToken\" value=\"";
        var start = html.IndexOf(marker, StringComparison.Ordinal);
        Assert.True(start >= 0, "Login page did not contain a CSRF token.");
        start += marker.Length;
        var end = html.IndexOf('"', start);
        Assert.True(end > start, "Login page CSRF token value was empty.");
        return html[start..end];
    }

    private static FormUrlEncodedContent CreateLoginContent(
        string csrfToken,
        string username,
        string password) =>
        new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["__RequestVerificationToken"] = csrfToken,
            ["username"] = username,
            ["password"] = password,
        });

    private static FormUrlEncodedContent CreateLogoutContent(string csrfToken) =>
        new(new Dictionary<string, string>
        {
            ["__RequestVerificationToken"] = csrfToken,
        });
}

internal static class MailerAdminFixtureHelpers
{
    internal static WebApplicationFactory<global::Program> CreateFactory(
        string connectionString,
        string tenantConfigPath,
        string passwordHash,
        IReadOnlyDictionary<string, string?>? extraConfiguration = null) =>
        new AdminTestFactory(connectionString, tenantConfigPath, passwordHash, extraConfiguration);

    internal static string TenantConfigJson =>
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

    private sealed class AdminTestFactory(
        string connectionString,
        string tenantConfigPath,
        string passwordHash,
        IReadOnlyDictionary<string, string?>? extraConfiguration) : WebApplicationFactory<global::Program>
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
                    ["Mailer:Worker:Enabled"] = "false",
                    ["MAIL_SERVICE_TOKEN"] = MailerWebApplicationFixtureBase.Token,
                    ["AMANE_ADMIN_ENABLED"] = "true",
                    ["AMANE_ADMIN_USERNAME"] = MailerAdminFixture.Username,
                    ["AMANE_ADMIN_PASSWORD_HASH"] = passwordHash,
                    ["AMANE_ADMIN_ALLOWED_LOCAL_ADDRESS"] = "127.0.0.1",
                };

                if (extraConfiguration is not null)
                {
                    foreach (var (key, value) in extraConfiguration)
                    {
                        settings[key] = value;
                    }
                }

                configuration.AddInMemoryCollection(settings);
            });
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<IHostedService>();
                services.AddSingleton<IStartupFilter>(new LoopbackLocalAddressStartupFilter());
            });
        }
    }

    private sealed class LoopbackLocalAddressStartupFilter : IStartupFilter
    {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) =>
            app =>
            {
                app.Use(async (context, nextMiddleware) =>
                {
                    context.Connection.LocalIpAddress ??= IPAddress.Loopback;
                    context.Connection.RemoteIpAddress ??= IPAddress.Loopback;
                    await nextMiddleware();
                });

                next(app);
            };
    }
}
