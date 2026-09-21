using System.Net;
using System.Text.RegularExpressions;
using Amane.Mailer.Admin;
using Amane.Mailer.Configuration;
using Amane.Mailer.Data.Sqlite;
using Amane.Mailer.Identity;
using Amane.Mailer.Operations;
using Amane.Mailer.Setup;
using Amane.Mailer.Tests.Fixtures;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Amane.Mailer.Tests.Admin;

public sealed class AdminGoogleSettingsPageTests
{
    private const string OwnerUsername = "managed-owner";
    private const string OwnerPassword = "managed-owner-password";
    private const string ScopedUsername = "scoped-admin";
    private const string ScopedPassword = "scoped-admin-password";
    private const string FakeClientId = "amane-mailer-test-google-ui-client-id.apps.googleusercontent.com";
    private const string FakeClientSecret = "amane-mailer-test-google-ui-client-secret-not-real";
    private const string FakeClientSecretRotated = "amane-mailer-test-google-ui-client-secret-rotated-not-real";

    [Fact]
    public async Task Owner_can_save_google_settings_without_secret_readback_while_scoped_admin_is_denied()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var harness = await ManagedHarness.CreateAsync(ct);

        using var scopedClient = CreateClient(harness.Factory);
        await LoginAsync(scopedClient, ScopedUsername, ScopedPassword, ct);
        using (var denied = await scopedClient.GetAsync(AdminGoogleSettingsPage.PagePath, ct))
            Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);

        using var ownerClient = CreateClient(harness.Factory);
        await LoginAsync(ownerClient, OwnerUsername, OwnerPassword, ct);

        using (var get = await ownerClient.GetAsync(AdminGoogleSettingsPage.PagePath, ct))
        {
            Assert.Equal(HttpStatusCode.OK, get.StatusCode);
            var html = await get.Content.ReadAsStringAsync(ct);
            Assert.Contains("認証設定", html, StringComparison.Ordinal);
            Assert.Contains("Password Login", html, StringComparison.Ordinal);
            Assert.Contains("/admin/signin-google", html, StringComparison.Ordinal);
            Assert.Contains("未設定", html, StringComparison.Ordinal);
            Assert.DoesNotContain(FakeClientSecret, html, StringComparison.Ordinal);
            Assert.DoesNotContain("value=\"" + FakeClientSecret, html, StringComparison.Ordinal);
        }

        var token = await ReadCsrfTokenAsync(ownerClient, AdminGoogleSettingsPage.PagePath, ct);
        using (var save = await ownerClient.PostAsync(
            AdminGoogleSettingsPage.PagePath,
            Form(
                token,
                ("google_login_enabled", "1"),
                ("client_id", FakeClientId),
                ("client_secret", FakeClientSecret),
                ("confirmation", "confirm")),
            ct))
        {
            Assert.Equal(HttpStatusCode.SeeOther, save.StatusCode);
            Assert.Equal(
                $"{AdminGoogleSettingsPage.PagePath}?saved=1",
                save.Headers.Location?.ToString());
        }

        var row = await harness.Instance.GetAsync(ct);
        Assert.NotNull(row);
        Assert.True(row.GoogleLoginEnabled);
        Assert.Equal(FakeClientId, row.GoogleClientId);
        Assert.False(string.IsNullOrWhiteSpace(row.GoogleConfiguredAt));
        Assert.Equal(harness.GoogleSecretPath, row.GoogleClientSecretRef);
        Assert.True(AdminGoogleSecretStore.TryReadSecret(harness.GoogleSecretPath, out var storedSecret));
        Assert.Equal(FakeClientSecret, storedSecret);

        using (var afterSave = await ownerClient.GetAsync($"{AdminGoogleSettingsPage.PagePath}?saved=1", ct))
        {
            var html = await afterSave.Content.ReadAsStringAsync(ct);
            Assert.Contains(FakeClientId, html, StringComparison.Ordinal);
            Assert.Contains("設定済み", html, StringComparison.Ordinal);
            Assert.Contains("再起動", html, StringComparison.Ordinal);
            Assert.DoesNotContain(FakeClientSecret, html, StringComparison.Ordinal);
            AssertSecretAbsentFromInputs(html);
        }

        token = await ReadCsrfTokenAsync(ownerClient, AdminGoogleSettingsPage.PagePath, ct);
        using (var rotate = await ownerClient.PostAsync(
            AdminGoogleSettingsPage.PagePath,
            Form(
                token,
                ("google_login_enabled", "1"),
                ("client_id", FakeClientId + "-updated"),
                ("client_secret", FakeClientSecretRotated),
                ("confirmation", "confirm")),
            ct))
        {
            Assert.Equal(HttpStatusCode.SeeOther, rotate.StatusCode);
        }

        row = await harness.Instance.GetAsync(ct);
        Assert.Equal(FakeClientId + "-updated", row!.GoogleClientId);
        Assert.True(AdminGoogleSecretStore.TryReadSecret(harness.GoogleSecretPath, out storedSecret));
        Assert.Equal(FakeClientSecretRotated, storedSecret);

        token = await ReadCsrfTokenAsync(ownerClient, AdminGoogleSettingsPage.PagePath, ct);
        using (var disable = await ownerClient.PostAsync(
            AdminGoogleSettingsPage.PagePath,
            Form(
                token,
                ("client_id", FakeClientId + "-updated"),
                ("client_secret", string.Empty),
                ("confirmation", "confirm")),
            ct))
        {
            Assert.Equal(HttpStatusCode.SeeOther, disable.StatusCode);
        }

        row = await harness.Instance.GetAsync(ct);
        Assert.False(row!.GoogleLoginEnabled);
        Assert.True(AdminGoogleSecretStore.IsSecretConfigured(row.GoogleClientSecretRef));

        var audits = await harness.Factory.Services.GetRequiredService<AdminAuditRepository>()
            .ListRecentAsync(20, ct);
        Assert.Contains(
            audits,
            a => a.EventType == AdminAuditLog.EventTypes.InstanceGoogleLoginSettingsUpdated);
        foreach (var audit in audits)
        {
            Assert.DoesNotContain(FakeClientSecret, audit.FieldName ?? string.Empty, StringComparison.Ordinal);
            Assert.DoesNotContain(FakeClientSecretRotated, audit.FieldName ?? string.Empty, StringComparison.Ordinal);
            Assert.DoesNotContain(FakeClientSecret, audit.ErrorCode ?? string.Empty, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task Managed_authority_enables_google_button_after_restart_while_password_login_remains()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var harness = await ManagedHarness.CreateAsync(ct);

        AdminGoogleSecretStore.WriteSecret(harness.GoogleSecretPath, FakeClientSecret);
        Assert.True(await harness.Instance.SetGoogleLoginSettingsAsync(
            enabled: true,
            FakeClientId,
            harness.GoogleSecretPath,
            ct));

        // Restart host so startup snapshot picks up managed Google settings.
        await harness.RestartAsync(ct);

        using var client = CreateClient(harness.Factory);
        using (var loginPage = await client.GetAsync("/admin/login", ct))
        {
            var html = await loginPage.Content.ReadAsStringAsync(ct);
            Assert.Contains("Googleでログイン", html, StringComparison.Ordinal);
            Assert.Contains("Username", html, StringComparison.Ordinal);
            Assert.DoesNotContain(FakeClientSecret, html, StringComparison.Ordinal);
        }

        await LoginAsync(client, OwnerUsername, OwnerPassword, ct);
        using (var mailRequests = await client.GetAsync("/admin/mail-requests", ct))
            Assert.Equal(HttpStatusCode.OK, mailRequests.StatusCode);
    }

    [Fact]
    public async Task Managed_incomplete_configuration_keeps_password_login_without_google_button()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var harness = await ManagedHarness.CreateAsync(ct);

        Assert.True(await harness.Instance.SetGoogleLoginSettingsAsync(
            enabled: true,
            FakeClientId,
            clientSecretRef: null,
            ct));
        await harness.RestartAsync(ct);

        using var client = CreateClient(harness.Factory);
        using (var loginPage = await client.GetAsync("/admin/login", ct))
        {
            var html = await loginPage.Content.ReadAsStringAsync(ct);
            Assert.DoesNotContain("Googleでログイン", html, StringComparison.Ordinal);
            Assert.Contains("Username", html, StringComparison.Ordinal);
        }

        await LoginAsync(client, OwnerUsername, OwnerPassword, ct);
    }

    [Fact]
    public async Task Managed_authority_ignores_env_google_credentials()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var harness = await ManagedHarness.CreateAsync(
            ct,
            extraConfiguration: new Dictionary<string, string?>
            {
                ["AMANE_ADMIN_GOOGLE_CLIENT_ID"] = FakeClientId,
                ["AMANE_ADMIN_GOOGLE_CLIENT_SECRET"] = FakeClientSecret,
            });

        Assert.True(await harness.Instance.SetGoogleLoginSettingsAsync(
            enabled: false,
            clientId: null,
            clientSecretRef: null,
            ct));
        await harness.RestartAsync(ct);

        var options = harness.Factory.Services.GetRequiredService<AdminGoogleOptions>();
        Assert.True(options.UsesManagedConfiguration);
        Assert.False(options.Enabled);

        using var client = CreateClient(harness.Factory);
        using var loginPage = await client.GetAsync("/admin/login", ct);
        var html = await loginPage.Content.ReadAsStringAsync(ct);
        Assert.DoesNotContain("Googleでログイン", html, StringComparison.Ordinal);
    }

    [Fact]
    public void Options_load_from_env_when_managed_google_settings_are_unclaimed()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["AMANE_ADMIN_GOOGLE_CLIENT_ID"] = FakeClientId,
                ["AMANE_ADMIN_GOOGLE_CLIENT_SECRET"] = FakeClientSecret,
            })
            .Build();
        var state = new InstanceRuntimeState(
            InstanceRuntimeStateKind.Initialized,
            "2026-01-01T00:00:00Z",
            false,
            "acs",
            "/tmp/acs",
            "2026-01-01T00:00:00Z",
            true);

        var options = AdminGoogleOptions.Load(configuration, state);
        Assert.False(options.UsesManagedConfiguration);
        Assert.True(options.Enabled);
        Assert.Equal(FakeClientId, options.ClientId);
        Assert.DoesNotContain(FakeClientSecret, options.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void Redirect_uri_uses_request_scheme_and_host()
    {
        var context = new DefaultHttpContext();
        context.Request.Scheme = "https";
        context.Request.Host = new HostString("stg.mailer.amanesystem.net");

        Assert.Equal(
            "https://stg.mailer.amanesystem.net/admin/signin-google",
            AdminGoogleSettingsPage.BuildRedirectUri(context));
    }

    private static void AssertSecretAbsentFromInputs(string html)
    {
        foreach (Match match in Regex.Matches(
            html,
            "name=\"client_secret\"[^>]*>",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
        {
            Assert.DoesNotContain(FakeClientSecret, match.Value, StringComparison.Ordinal);
            Assert.DoesNotContain(FakeClientSecretRotated, match.Value, StringComparison.Ordinal);
            Assert.Contains("value=\"\"", match.Value, StringComparison.Ordinal);
        }
    }

    private static HttpClient CreateClient(WebApplicationFactory<global::Program> factory) =>
        factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
            BaseAddress = new Uri("https://localhost"),
        });

    private static async Task LoginAsync(
        HttpClient client,
        string username,
        string password,
        CancellationToken cancellationToken)
    {
        var token = await ReadCsrfTokenAsync(client, "/admin/login", cancellationToken);
        using var response = await client.PostAsync(
            "/admin/api/login",
            Form(token, ("username", username), ("password", password)),
            cancellationToken);
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
    }

    private static async Task<string> ReadCsrfTokenAsync(
        HttpClient client,
        string path,
        CancellationToken cancellationToken)
    {
        using var response = await client.GetAsync(path, cancellationToken);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var html = await response.Content.ReadAsStringAsync(cancellationToken);
        const string marker = "name=\"__RequestVerificationToken\" value=\"";
        var start = html.IndexOf(marker, StringComparison.Ordinal);
        Assert.True(start >= 0, $"No CSRF token in {path}.");
        start += marker.Length;
        var end = html.IndexOf('"', start);
        Assert.True(end > start);
        return html[start..end];
    }

    private static FormUrlEncodedContent Form(
        string csrfToken,
        params (string Name, string Value)[] values)
    {
        var form = new Dictionary<string, string>
        {
            ["__RequestVerificationToken"] = csrfToken,
        };
        foreach (var (name, value) in values)
            form[name] = value;
        return new FormUrlEncodedContent(form);
    }

    private sealed class ManagedHarness : IAsyncDisposable
    {
        private readonly string _root;
        private readonly string _connectionString;
        private readonly string _tenantConfigPath;
        private WebApplicationFactory<global::Program> _factory;
        private readonly Dictionary<string, string?> _extraConfiguration;

        private ManagedHarness(
            string root,
            string connectionString,
            string tenantConfigPath,
            string googleSecretPath,
            InstanceConfigurationRepository instance,
            WebApplicationFactory<global::Program> factory,
            Dictionary<string, string?> extraConfiguration)
        {
            _root = root;
            _connectionString = connectionString;
            _tenantConfigPath = tenantConfigPath;
            GoogleSecretPath = googleSecretPath;
            Instance = instance;
            _factory = factory;
            _extraConfiguration = extraConfiguration;
        }

        public string GoogleSecretPath { get; }

        public InstanceConfigurationRepository Instance { get; }

        public WebApplicationFactory<global::Program> Factory => _factory;

        public static async Task<ManagedHarness> CreateAsync(
            CancellationToken cancellationToken,
            IReadOnlyDictionary<string, string?>? extraConfiguration = null)
        {
            var root = Path.Combine(
                Path.GetTempPath(),
                "amane-mailer-google-settings",
                Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            var databasePath = Path.Combine(root, "mailer.db");
            var connectionString = $"Data Source={databasePath}";
            var tenantConfigPath = Path.Combine(root, "tenants.json");
            await File.WriteAllTextAsync(tenantConfigPath, MailerAdminFixtureHelpers.TenantConfigJson, cancellationToken);
            var acsSecretPath = Path.Combine(root, "secrets", "acs_connection_string");
            var googleSecretPath = Path.Combine(root, "secrets", "admin_google", "client_secret");

            var configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["ConnectionStrings:Mailer"] = connectionString,
                })
                .Build();
            var connections = new SqliteConnectionFactory(configuration);
            await new SqlMigrationRunner(connections).ApplyPendingAsync(cancellationToken);
            Assert.True(FirstRunSetupStorage.WriteAcsSecretCreateOnly(
                acsSecretPath,
                "Endpoint=https://example.communication.azure.com/;AccessKey=abc123"));

            var instance = new InstanceConfigurationRepository(connections, TimeProvider.System);
            Assert.True(await instance.ConfigureAcsAsync(acsSecretPath, cancellationToken));
            var users = new AdminUserRepository(connections, TimeProvider.System);
            Assert.True(await users.EnsureInstanceOwnerAsync(
                OwnerUsername,
                AdminPasswordHasher.Hash(OwnerPassword),
                cancellationToken));
            var senders = new SenderRepository(connections, TimeProvider.System);
            var firstSender = await senders.CreateAsync("first@example.com", "First", cancellationToken);
            Assert.True(await instance.FinalizeAsync(cancellationToken));
            await users.CreateOrUpdateScopedUserAsync(
                ScopedUsername,
                AdminPasswordHasher.Hash(ScopedPassword),
                [firstSender.SenderId],
                cancellationToken);

            var extras = new Dictionary<string, string?>(StringComparer.Ordinal)
            {
                ["AMANE_ADMIN_ENABLED"] = "false",
                ["AMANE_ADMIN_USERNAME"] = "legacy-admin",
                ["AMANE_ADMIN_GOOGLE_CLIENT_SECRET_FILE"] = googleSecretPath,
            };
            if (extraConfiguration is not null)
            {
                foreach (var (key, value) in extraConfiguration)
                    extras[key] = value;
            }

            var factory = MailerAdminFixtureHelpers.CreateFactory(
                connectionString,
                tenantConfigPath,
                AdminPasswordHasher.Hash("legacy-password"),
                extras,
                useEarlyInstanceProbe: true);

            return new ManagedHarness(
                root,
                connectionString,
                tenantConfigPath,
                googleSecretPath,
                instance,
                factory,
                extras);
        }

        public async Task RestartAsync(CancellationToken cancellationToken)
        {
            await _factory.DisposeAsync();
            _factory = MailerAdminFixtureHelpers.CreateFactory(
                _connectionString,
                _tenantConfigPath,
                AdminPasswordHasher.Hash("legacy-password"),
                _extraConfiguration,
                useEarlyInstanceProbe: true);
            _ = cancellationToken;
        }

        public async ValueTask DisposeAsync()
        {
            await _factory.DisposeAsync();
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(_root))
                Directory.Delete(_root, recursive: true);
        }
    }
}
