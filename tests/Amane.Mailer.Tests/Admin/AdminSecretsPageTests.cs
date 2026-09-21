using System.Collections.Concurrent;
using System.Net;
using Amane.Mailer.Admin;
using Amane.Mailer.Data.Sqlite;
using Amane.Mailer.Identity;
using Amane.Mailer.Operations;
using Amane.Mailer.Setup;
using Amane.Mailer.Tests;
using Amane.Mailer.Tests.Fixtures;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Amane.Mailer.Tests.Admin;

public sealed class AdminSecretsPageTests
{
    private const string OwnerUsername = "secrets-owner";
    private const string OwnerPassword = "secrets-owner-password";
    private const string ScopedUsername = "secrets-scoped";
    private const string ScopedPassword = "secrets-scoped-password";
    private const string BreakGlassUsername = "secrets-break-glass";
    private const string BreakGlassPassword = "secrets-break-glass-password";
    private const string InitialAcsSecret = "Endpoint=https://example.communication.azure.com/;AccessKey=old-acs-canary";
    private const string RotatedAcsSecret = "Endpoint=https://example.communication.azure.com/;AccessKey=new-acs-canary";
    private const string ManagedGoogleSecret = "google-client-secret-canary";
    private const string LegacyGoogleSecret = "legacy-google-secret-canary";
    private const string GoogleClientId = "secrets-test.apps.googleusercontent.com";

    [Fact]
    public async Task Secrets_page_shows_managed_inventory_only_to_instance_owner()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var harness = await ManagedSecretsHarness.CreateAsync(ct, managedGoogle: true);
        using var owner = CreateClient(harness.Factory);
        await LoginAsync(owner, OwnerUsername, OwnerPassword, ct);

        using var response = await owner.GetAsync(AdminSecretsPage.PagePath, ct);
        var html = await response.Content.ReadAsStringAsync(ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(response.Headers.CacheControl?.NoStore);
        Assert.Contains("Secret管理", html, StringComparison.Ordinal);
        Assert.Contains("ACS provider connection string", html, StringComparison.Ordinal);
        Assert.Contains("<dd>設定済み</dd>", html, StringComparison.Ordinal);
        Assert.Contains("<dd>managed instance</dd>", html, StringComparison.Ordinal);
        Assert.Contains("<dd>反映済み</dd>", html, StringComparison.Ordinal);
        Assert.Contains("full-instance backup対象外", html, StringComparison.Ordinal);
        Assert.Contains("operator-managed / full-instance backup対象外", html, StringComparison.Ordinal);
        Assert.Contains("Google Client Secret", html, StringComparison.Ordinal);
        Assert.Contains("Admin UI managed configuration", html, StringComparison.Ordinal);
        Assert.Contains("href=\"/admin/auth-settings\"", html, StringComparison.Ordinal);
        Assert.Contains("この画面では状態確認・編集・保存をしません", html, StringComparison.Ordinal);
        Assert.Contains("Bounce Queue credential", html, StringComparison.Ordinal);
        Assert.Contains("backup / rclone credential", html, StringComparison.Ordinal);
        Assert.Contains("age identity", html, StringComparison.Ordinal);
        Assert.Contains("legacy / manual secret", html, StringComparison.Ordinal);
        Assert.Contains("href=\"/admin/secrets\"", html, StringComparison.Ordinal);
        Assert.Contains(
            "name=\"acs_connection_string\" type=\"password\" autocomplete=\"new-password\"",
            html,
            StringComparison.Ordinal);
        Assert.Equal(1, Count(html, "<form "));
        Assert.DoesNotContain(harness.AcsSecretPath, html, StringComparison.Ordinal);
        Assert.DoesNotContain(harness.GoogleSecretPath, html, StringComparison.Ordinal);
        Assert.DoesNotContain(InitialAcsSecret, html, StringComparison.Ordinal);
        Assert.DoesNotContain(ManagedGoogleSecret, html, StringComparison.Ordinal);

        using var scoped = CreateClient(harness.Factory);
        await LoginAsync(scoped, ScopedUsername, ScopedPassword, ct);
        using (var denied = await scoped.GetAsync(AdminSecretsPage.PagePath, ct))
        {
            Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
            Assert.DoesNotContain("ACS provider connection string", await denied.Content.ReadAsStringAsync(ct), StringComparison.Ordinal);
        }

        using (var scopedHome = await scoped.GetAsync("/admin/mail-requests", ct))
        {
            var scopedHtml = await scopedHome.Content.ReadAsStringAsync(ct);
            Assert.Equal(HttpStatusCode.OK, scopedHome.StatusCode);
            Assert.DoesNotContain("href=\"/admin/secrets\"", scopedHtml, StringComparison.Ordinal);
        }

        using var breakGlass = CreateClient(harness.Factory);
        await LoginAsync(breakGlass, BreakGlassUsername, BreakGlassPassword, ct);
        using (var denied = await breakGlass.GetAsync(AdminSecretsPage.PagePath, ct))
            Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);

        using var unauthenticated = CreateClient(harness.Factory);
        using var redirect = await unauthenticated.GetAsync(AdminSecretsPage.PagePath, ct);
        Assert.Equal(HttpStatusCode.Redirect, redirect.StatusCode);
        Assert.StartsWith("/admin/login", redirect.Headers.Location?.PathAndQuery, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Acs_rotation_replaces_protected_file_and_waits_for_process_restart()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var harness = await ManagedSecretsHarness.CreateAsync(ct);
        using var owner = CreateClient(harness.Factory);
        await LoginAsync(owner, OwnerUsername, OwnerPassword, ct);
        var logs = new CapturingLoggerProvider();
        harness.Factory.Services.GetRequiredService<ILoggerFactory>().AddProvider(logs);

        var before = await harness.Instance.GetAsync(ct);
        var csrf = await ReadCsrfTokenAsync(owner, ct);
        using var response = await owner.PostAsync(
            AdminSecretsPage.AcsRotationPath,
            Form(csrf, ("acs_connection_string", RotatedAcsSecret), ("confirmation", "confirm")),
            ct);
        var responseBody = await response.Content.ReadAsStringAsync(ct);

        Assert.Equal(HttpStatusCode.SeeOther, response.StatusCode);
        Assert.Equal($"{AdminSecretsPage.PagePath}?saved=1", response.Headers.Location?.ToString());
        Assert.DoesNotContain(InitialAcsSecret, responseBody, StringComparison.Ordinal);
        Assert.DoesNotContain(RotatedAcsSecret, responseBody, StringComparison.Ordinal);
        Assert.Equal(RotatedAcsSecret, await File.ReadAllTextAsync(harness.AcsSecretPath, ct));
        Assert.False((await File.ReadAllTextAsync(harness.AcsSecretPath, ct))
            .Contains("old-acs-canary", StringComparison.Ordinal));
        if (OperatingSystem.IsLinux())
        {
            const UnixFileMode sharedBits = UnixFileMode.GroupRead | UnixFileMode.GroupWrite | UnixFileMode.GroupExecute
                | UnixFileMode.OtherRead | UnixFileMode.OtherWrite | UnixFileMode.OtherExecute;
            Assert.Equal(UnixFileMode.None, File.GetUnixFileMode(harness.AcsSecretPath) & sharedBits);
        }

        var after = await harness.Instance.GetAsync(ct);
        Assert.Equal(before!.ProviderSecretRef, after!.ProviderSecretRef);
        var audits = await harness.Factory.Services.GetRequiredService<AdminAuditRepository>()
            .ListRecentAsync(10, ct);
        var audit = Assert.Single(audits, item =>
            item.EventType == AdminAuditLog.EventTypes.InstanceProviderSecretRotated);
        Assert.Equal(AdminAuditLog.TargetTypes.InstanceConfiguration, audit.TargetType);
        Assert.Equal("1", audit.TargetId);
        Assert.Equal(AdminAuditLog.Results.Success, audit.Result);
        Assert.Equal("acs_provider_secret", audit.FieldName);
        var auditMaterial = string.Join('\n', audit.EventType, audit.TargetType, audit.TargetId, audit.FieldName, audit.ErrorCode);
        Assert.DoesNotContain(InitialAcsSecret, auditMaterial, StringComparison.Ordinal);
        Assert.DoesNotContain(RotatedAcsSecret, auditMaterial, StringComparison.Ordinal);
        Assert.DoesNotContain(harness.AcsSecretPath, auditMaterial, StringComparison.Ordinal);
        var logMaterial = string.Join('\n', logs.Messages);
        Assert.DoesNotContain(InitialAcsSecret, logMaterial, StringComparison.Ordinal);
        Assert.DoesNotContain(RotatedAcsSecret, logMaterial, StringComparison.Ordinal);
        Assert.DoesNotContain(harness.AcsSecretPath, logMaterial, StringComparison.Ordinal);

        using (var pending = await owner.GetAsync(AdminSecretsPage.PagePath, ct))
        {
            var html = await pending.Content.ReadAsStringAsync(ct);
            Assert.Equal(HttpStatusCode.OK, pending.StatusCode);
            Assert.Contains("<dd>再起動待ち</dd>", html, StringComparison.Ordinal);
            Assert.DoesNotContain(RotatedAcsSecret, html, StringComparison.Ordinal);
            Assert.DoesNotContain(harness.AcsSecretPath, html, StringComparison.Ordinal);
        }

        await harness.RestartFactoryAsync();
        using var restarted = CreateClient(harness.Factory);
        await LoginAsync(restarted, OwnerUsername, OwnerPassword, ct);
        using var applied = await restarted.GetAsync(AdminSecretsPage.PagePath, ct);
        var restartedHtml = await applied.Content.ReadAsStringAsync(ct);
        Assert.Equal(HttpStatusCode.OK, applied.StatusCode);
        Assert.Contains("<dd>反映済み</dd>", restartedHtml, StringComparison.Ordinal);
        Assert.DoesNotContain(RotatedAcsSecret, restartedHtml, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Invalid_replacement_csrf_and_missing_confirmation_do_not_change_acs_file()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var harness = await ManagedSecretsHarness.CreateAsync(ct);
        using var owner = CreateClient(harness.Factory);
        await LoginAsync(owner, OwnerUsername, OwnerPassword, ct);

        using (var missingCsrf = await owner.PostAsync(
            AdminSecretsPage.AcsRotationPath,
            Form(null, ("acs_connection_string", RotatedAcsSecret), ("confirmation", "confirm")),
            ct))
        {
            Assert.Equal(HttpStatusCode.BadRequest, missingCsrf.StatusCode);
        }

        var csrf = await ReadCsrfTokenAsync(owner, ct);
        using (var missingConfirmation = await owner.PostAsync(
            AdminSecretsPage.AcsRotationPath,
            Form(csrf, ("acs_connection_string", RotatedAcsSecret)),
            ct))
        {
            Assert.Equal(HttpStatusCode.BadRequest, missingConfirmation.StatusCode);
        }

        const string invalidSecret = "invalid-acs-canary";
        csrf = await ReadCsrfTokenAsync(owner, ct);
        using (var invalid = await owner.PostAsync(
            AdminSecretsPage.AcsRotationPath,
            Form(csrf, ("acs_connection_string", invalidSecret), ("confirmation", "confirm")),
            ct))
        {
            var body = await invalid.Content.ReadAsStringAsync(ct);
            Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
            Assert.DoesNotContain(invalidSecret, body, StringComparison.Ordinal);
        }

        Assert.Equal(InitialAcsSecret, await File.ReadAllTextAsync(harness.AcsSecretPath, ct));
        var audits = await harness.Factory.Services.GetRequiredService<AdminAuditRepository>()
            .ListRecentAsync(10, ct);
        var failure = Assert.Single(audits, item =>
            item.EventType == AdminAuditLog.EventTypes.InstanceProviderSecretRotated);
        Assert.Equal(AdminAuditLog.Results.Failure, failure.Result);
        Assert.DoesNotContain(invalidSecret, failure.FieldName ?? string.Empty, StringComparison.Ordinal);
        Assert.DoesNotContain(invalidSecret, failure.ErrorCode ?? string.Empty, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Missing_or_invalid_current_acs_file_is_unavailable_without_path_or_value()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var harness = await ManagedSecretsHarness.CreateAsync(ct);
        using var owner = CreateClient(harness.Factory);
        await LoginAsync(owner, OwnerUsername, OwnerPassword, ct);
        _ = await owner.GetAsync(AdminSecretsPage.PagePath, ct);

        File.Delete(harness.AcsSecretPath);
        using (var missing = await owner.GetAsync(AdminSecretsPage.PagePath, ct))
        {
            var html = await missing.Content.ReadAsStringAsync(ct);
            Assert.Equal(HttpStatusCode.OK, missing.StatusCode);
            Assert.Contains("<dd>未設定・無効</dd>", html, StringComparison.Ordinal);
            Assert.Contains("<dd>unavailable</dd>", html, StringComparison.Ordinal);
            Assert.DoesNotContain(harness.AcsSecretPath, html, StringComparison.Ordinal);
            Assert.DoesNotContain(InitialAcsSecret, html, StringComparison.Ordinal);
        }

        SecureFileCreate.WriteAllTextCreateNew(harness.AcsSecretPath, "not-a-valid-acs-secret");
        using var invalid = await owner.GetAsync(AdminSecretsPage.PagePath, ct);
        var invalidHtml = await invalid.Content.ReadAsStringAsync(ct);
        Assert.Equal(HttpStatusCode.OK, invalid.StatusCode);
        Assert.Contains("<dd>未設定・無効</dd>", invalidHtml, StringComparison.Ordinal);
        Assert.Contains("<dd>unavailable</dd>", invalidHtml, StringComparison.Ordinal);
        Assert.DoesNotContain(harness.AcsSecretPath, invalidHtml, StringComparison.Ordinal);
        Assert.DoesNotContain("not-a-valid-acs-secret", invalidHtml, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Legacy_google_authority_is_reported_without_secret_and_rotation_form()
    {
        var ct = TestContext.Current.CancellationToken;
        var extras = new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            [AdminGoogleOptions.ClientIdKey] = GoogleClientId,
            [AdminGoogleOptions.ClientSecretKey] = LegacyGoogleSecret,
        };
        await using var harness = await ManagedSecretsHarness.CreateAsync(ct, extraConfiguration: extras);
        using var owner = CreateClient(harness.Factory);
        await LoginAsync(owner, OwnerUsername, OwnerPassword, ct);

        using var response = await owner.GetAsync(AdminSecretsPage.PagePath, ct);
        var html = await response.Content.ReadAsStringAsync(ct);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("legacy env (until first save)", html, StringComparison.Ordinal);
        Assert.Contains("/admin/auth-settings", html, StringComparison.Ordinal);
        Assert.Equal(1, Count(html, "<form "));
        Assert.DoesNotContain(LegacyGoogleSecret, html, StringComparison.Ordinal);
        Assert.DoesNotContain(harness.GoogleSecretPath, html, StringComparison.Ordinal);
    }

    [Fact]
    public void Backup_display_claims_only_canonical_managed_paths()
    {
        Assert.Equal(
            "full-instance backup対象",
            AdminSecretsPage.ResolveAcsBackupDisplay(FirstRunSetupConstants.DefaultAcsSecretPath, managedAcs: true));
        Assert.Equal(
            "operator-managed / full-instance backup対象外",
            AdminSecretsPage.ResolveAcsBackupDisplay("/operator/secret/acs", managedAcs: true));
        Assert.Equal(
            "full-instance backup対象",
            AdminSecretsPage.ResolveGoogleBackupDisplay(AdminGoogleSecretStore.DefaultSecretPath, managedGoogle: true));
        Assert.Equal(
            "operator-managed / full-instance backup対象外",
            AdminSecretsPage.ResolveGoogleBackupDisplay("/operator/secret/google", managedGoogle: true));
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
        using var loginPage = await client.GetAsync("/admin/login", cancellationToken);
        Assert.Equal(HttpStatusCode.OK, loginPage.StatusCode);
        var csrf = ReadCsrf(await loginPage.Content.ReadAsStringAsync(cancellationToken));
        using var login = await client.PostAsync(
            "/admin/api/login",
            Form(csrf, ("username", username), ("password", password)),
            cancellationToken);
        Assert.Equal(HttpStatusCode.Redirect, login.StatusCode);
    }

    private static async Task<string> ReadCsrfTokenAsync(
        HttpClient client,
        CancellationToken cancellationToken)
    {
        using var response = await client.GetAsync(AdminSecretsPage.PagePath, cancellationToken);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return ReadCsrf(await response.Content.ReadAsStringAsync(cancellationToken));
    }

    private static string ReadCsrf(string html)
    {
        const string marker = "name=\"__RequestVerificationToken\" value=\"";
        var start = html.IndexOf(marker, StringComparison.Ordinal);
        Assert.True(start >= 0, "No CSRF token on the page.");
        start += marker.Length;
        var end = html.IndexOf('"', start);
        Assert.True(end > start);
        return html[start..end];
    }

    private static FormUrlEncodedContent Form(
        string? csrfToken,
        params (string Name, string Value)[] values)
    {
        var fields = new Dictionary<string, string>(StringComparer.Ordinal);
        if (csrfToken is not null)
            fields["__RequestVerificationToken"] = csrfToken;
        foreach (var (name, value) in values)
            fields[name] = value;
        return new FormUrlEncodedContent(fields);
    }

    private static int Count(string input, string value)
    {
        var count = 0;
        var offset = 0;
        while ((offset = input.IndexOf(value, offset, StringComparison.Ordinal)) >= 0)
        {
            count++;
            offset += value.Length;
        }

        return count;
    }

    private sealed class ManagedSecretsHarness : IAsyncDisposable
    {
        private readonly string _root;
        private readonly string _connectionString;
        private readonly string _tenantConfigPath;
        private readonly IReadOnlyDictionary<string, string?> _extraConfiguration;

        private ManagedSecretsHarness(
            string root,
            string connectionString,
            string tenantConfigPath,
            string acsSecretPath,
            string googleSecretPath,
            InstanceConfigurationRepository instance,
            IReadOnlyDictionary<string, string?> extraConfiguration,
            WebApplicationFactory<global::Program> factory)
        {
            _root = root;
            _connectionString = connectionString;
            _tenantConfigPath = tenantConfigPath;
            _extraConfiguration = extraConfiguration;
            AcsSecretPath = acsSecretPath;
            GoogleSecretPath = googleSecretPath;
            Instance = instance;
            Factory = factory;
        }

        public string AcsSecretPath { get; }

        public string GoogleSecretPath { get; }

        public InstanceConfigurationRepository Instance { get; }

        public WebApplicationFactory<global::Program> Factory { get; private set; }

        public static async Task<ManagedSecretsHarness> CreateAsync(
            CancellationToken cancellationToken,
            bool managedGoogle = false,
            IReadOnlyDictionary<string, string?>? extraConfiguration = null)
        {
            var root = Path.Combine(Path.GetTempPath(), "amane-mailer-admin-secrets", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            var databasePath = Path.Combine(root, "mailer.db");
            var connectionString = $"Data Source={databasePath}";
            var tenantConfigPath = Path.Combine(root, "tenants.json");
            await File.WriteAllTextAsync(tenantConfigPath, MailerAdminFixtureHelpers.TenantConfigJson, cancellationToken);
            var acsSecretPath = Path.Combine(root, "secrets", "acs", "acs_connection_string");
            var googleSecretPath = Path.Combine(root, "secrets", "admin_google", "client_secret");

            var configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["ConnectionStrings:Mailer"] = connectionString,
                })
                .Build();
            var connections = new SqliteConnectionFactory(configuration);
            await new SqlMigrationRunner(connections).ApplyPendingAsync(cancellationToken);
            Assert.True(FirstRunSetupStorage.WriteAcsSecretCreateOnly(acsSecretPath, InitialAcsSecret));

            var instance = new InstanceConfigurationRepository(connections, TimeProvider.System);
            Assert.True(await instance.ConfigureAcsAsync(acsSecretPath, cancellationToken));
            var users = new AdminUserRepository(connections, TimeProvider.System);
            Assert.True(await users.EnsureInstanceOwnerAsync(
                OwnerUsername,
                AdminPasswordHasher.Hash(OwnerPassword),
                cancellationToken));
            var senders = new SenderRepository(connections, TimeProvider.System);
            var sender = await senders.CreateAsync("secrets@example.com", "Secret Test", cancellationToken);
            Assert.True(await instance.FinalizeAsync(cancellationToken));
            await users.CreateOrUpdateScopedUserAsync(
                ScopedUsername,
                AdminPasswordHasher.Hash(ScopedPassword),
                [sender.SenderId],
                cancellationToken);
            await users.CreateBreakGlassUserAsync(
                BreakGlassUsername,
                AdminPasswordHasher.Hash(BreakGlassPassword),
                cancellationToken);

            var extras = new Dictionary<string, string?>(StringComparer.Ordinal)
            {
                ["AMANE_ADMIN_ENABLED"] = "false",
                ["AMANE_ADMIN_USERNAME"] = "legacy-admin",
                [AdminGoogleSecretStore.SecretPathEnvKey] = googleSecretPath,
            };
            if (managedGoogle)
            {
                AdminGoogleSecretStore.WriteSecret(googleSecretPath, ManagedGoogleSecret);
                Assert.True(await instance.SetGoogleLoginSettingsAsync(
                    enabled: true,
                    clientId: GoogleClientId,
                    clientSecretRef: googleSecretPath,
                    cancellationToken));
            }

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

            return new ManagedSecretsHarness(
                root,
                connectionString,
                tenantConfigPath,
                acsSecretPath,
                googleSecretPath,
                instance,
                extras,
                factory);
        }

        public async Task RestartFactoryAsync()
        {
            await Factory.DisposeAsync();
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            Factory = MailerAdminFixtureHelpers.CreateFactory(
                _connectionString,
                _tenantConfigPath,
                AdminPasswordHasher.Hash("legacy-password"),
                _extraConfiguration,
                useEarlyInstanceProbe: true);
        }

        public async ValueTask DisposeAsync()
        {
            await Factory.DisposeAsync();
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            if (Directory.Exists(_root))
                Directory.Delete(_root, recursive: true);
        }
    }

    private sealed class CapturingLoggerProvider : ILoggerProvider
    {
        public ConcurrentQueue<string> Messages { get; } = new();

        public ILogger CreateLogger(string categoryName) => new CapturingLogger(categoryName, Messages);

        public void Dispose()
        {
        }

        private sealed class CapturingLogger(string categoryName, ConcurrentQueue<string> messages) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state)
                where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(
                LogLevel logLevel,
                EventId eventId,
                TState state,
                Exception? exception,
                Func<TState, Exception?, string> formatter)
            {
                var exceptionText = exception is null ? string.Empty : $" {exception}";
                messages.Enqueue($"{categoryName}: {formatter(state, exception)}{exceptionText}");
            }
        }
    }
}
