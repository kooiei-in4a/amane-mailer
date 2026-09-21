using System.Net;
using System.Text.RegularExpressions;
using Amane.Mailer.Admin;
using Amane.Mailer.Configuration;
using Amane.Mailer.Data.Sqlite;
using Amane.Mailer.Identity;
using Amane.Mailer.Operations;
using Amane.Mailer.Setup;
using Amane.Mailer.Tests.Fixtures;
using Microsoft.AspNetCore.Authentication.Google;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

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
    private const string FakeManagedCutoverSecret = "amane-mailer-test-google-ui-managed-cutover-secret-not-real";

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
            Assert.Contains(AdminGoogleSettingsStatus.ReflectionRestartPending, html, StringComparison.Ordinal);
            Assert.Contains("設定を保存しました。変更を有効にするには Mailer を再起動してください。", html, StringComparison.Ordinal);
            Assert.Contains("Password Login", html, StringComparison.Ordinal);
            Assert.Contains("always available", html, StringComparison.Ordinal);
            Assert.DoesNotContain(FakeClientSecret, html, StringComparison.Ordinal);
            AssertFingerprintAbsent(html, FakeClientSecret);
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
    public void Secret_ref_update_keeps_a_valid_current_ref_when_no_new_secret_is_written()
    {
        var root = Directory.CreateTempSubdirectory("amane-google-ref-");
        try
        {
            var canonical = Path.Combine(root.FullName, "canonical", "client_secret");
            var current = Path.Combine(root.FullName, "current", "client_secret");
            var missing = Path.Combine(root.FullName, "missing", "client_secret");
            AdminGoogleSecretStore.WriteSecret(canonical, FakeClientSecretRotated);
            AdminGoogleSecretStore.WriteSecret(current, FakeClientSecret);

            Assert.Equal(
                canonical,
                AdminGoogleSettingsPage.ResolveSecretRefForUpdate(
                    secretWasWritten: true,
                    canonical,
                    current));
            Assert.Equal(
                current,
                AdminGoogleSettingsPage.ResolveSecretRefForUpdate(
                    secretWasWritten: false,
                    canonical,
                    current));
            Assert.Equal(
                canonical,
                AdminGoogleSettingsPage.ResolveSecretRefForUpdate(
                    secretWasWritten: false,
                    canonical,
                    currentSecretRef: null));
            Assert.Equal(
                canonical,
                AdminGoogleSettingsPage.ResolveSecretRefForUpdate(
                    secretWasWritten: false,
                    canonical,
                    missing));
            Assert.Null(
                AdminGoogleSettingsPage.ResolveSecretRefForUpdate(
                    secretWasWritten: false,
                    missing,
                    currentSecretRef: null));
        }
        finally
        {
            root.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task Legacy_env_page_shows_effective_google_state_without_secret_plaintext()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var harness = await ManagedHarness.CreateAsync(ct, EnvGoogleConfiguration());

        using var client = CreateClient(harness.Factory);
        await LoginAsync(client, OwnerUsername, OwnerPassword, ct);
        using var page = await client.GetAsync(AdminGoogleSettingsPage.PagePath, ct);
        var html = await page.Content.ReadAsStringAsync(ct);

        Assert.Contains("保存済み設定", html, StringComparison.Ordinal);
        Assert.Contains("現在の稼働状態", html, StringComparison.Ordinal);
        Assert.Contains($">{AdminGoogleSettingsStatus.DisplayOn}</code>", html, StringComparison.Ordinal);
        Assert.Contains(AdminGoogleSettingsStatus.ReflectionApplied, html, StringComparison.Ordinal);
        Assert.Contains("legacy env (until first save)", html, StringComparison.Ordinal);
        Assert.Contains("name=\"google_login_enabled\" value=\"1\" checked", html, StringComparison.Ordinal);
        Assert.Contains($"value=\"{FakeClientId}\"", html, StringComparison.Ordinal);
        Assert.Contains("managed configuration が authority になります", html, StringComparison.Ordinal);
        Assert.Contains("managed 未設定", html, StringComparison.Ordinal);
        Assert.Contains("Password Login", html, StringComparison.Ordinal);
        Assert.Contains("always available", html, StringComparison.Ordinal);
        AssertNoSecretMaterial(html);
        AssertFingerprintAbsent(html, FakeClientSecret);
        AssertSecretAbsentFromInputs(html);
    }

    [Fact]
    public async Task First_enabled_save_without_managed_secret_does_not_claim_authority()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var harness = await ManagedHarness.CreateAsync(ct, EnvGoogleConfiguration());
        using var client = CreateClient(harness.Factory);
        await LoginAsync(client, OwnerUsername, OwnerPassword, ct);

        var token = await ReadCsrfTokenAsync(client, AdminGoogleSettingsPage.PagePath, ct);
        using var save = await client.PostAsync(
            AdminGoogleSettingsPage.PagePath,
            Form(
                token,
                ("google_login_enabled", "1"),
                ("client_id", FakeClientId),
                ("client_secret", "   "),
                ("confirmation", "confirm")),
            ct);
        Assert.Equal(HttpStatusCode.BadRequest, save.StatusCode);
        var body = await save.Content.ReadAsStringAsync(ct);
        Assert.Contains(AdminGoogleSettingsPage.ManagedCutoverSecretRequiredMessage, body, StringComparison.Ordinal);
        AssertNoSecretMaterial(body);

        var row = await harness.Instance.GetAsync(ct);
        Assert.NotNull(row);
        Assert.True(string.IsNullOrWhiteSpace(row.GoogleConfiguredAt));
        Assert.False(File.Exists(harness.GoogleSecretPath));

        using (var settings = await client.GetAsync(AdminGoogleSettingsPage.PagePath, ct))
        {
            var html = await settings.Content.ReadAsStringAsync(ct);
            Assert.Contains("legacy env (until first save)", html, StringComparison.Ordinal);
            Assert.Contains("name=\"google_login_enabled\" value=\"1\" checked", html, StringComparison.Ordinal);
            AssertNoSecretMaterial(html);
        }

        await harness.RestartAsync(ct);
        var options = harness.Factory.Services.GetRequiredService<AdminGoogleOptions>();
        Assert.False(options.UsesManagedConfiguration);
        Assert.True(options.Enabled);

        using var restarted = CreateClient(harness.Factory);
        using (var loginPage = await restarted.GetAsync("/admin/login", ct))
        {
            var html = await loginPage.Content.ReadAsStringAsync(ct);
            Assert.Contains("Googleでログイン", html, StringComparison.Ordinal);
            Assert.Contains("Username", html, StringComparison.Ordinal);
            AssertNoSecretMaterial(html);
        }

        await LoginAsync(restarted, OwnerUsername, OwnerPassword, ct);
        await AssertAuditOmitsSecretsAsync(harness, ct);
    }

    [Fact]
    public async Task First_enabled_save_with_new_secret_claims_managed_authority()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var harness = await ManagedHarness.CreateAsync(ct, EnvGoogleConfiguration());
        using var client = CreateClient(harness.Factory);
        await LoginAsync(client, OwnerUsername, OwnerPassword, ct);

        var token = await ReadCsrfTokenAsync(client, AdminGoogleSettingsPage.PagePath, ct);
        using (var save = await client.PostAsync(
            AdminGoogleSettingsPage.PagePath,
            Form(
                token,
                ("google_login_enabled", "1"),
                ("client_id", FakeClientId),
                ("client_secret", FakeManagedCutoverSecret),
                ("confirmation", "confirm")),
            ct))
        {
            Assert.Equal(HttpStatusCode.SeeOther, save.StatusCode);
        }

        var row = await harness.Instance.GetAsync(ct);
        Assert.NotNull(row);
        Assert.False(string.IsNullOrWhiteSpace(row.GoogleConfiguredAt));
        Assert.True(row.GoogleLoginEnabled);
        Assert.Equal(FakeClientId, row.GoogleClientId);
        Assert.Equal(harness.GoogleSecretPath, row.GoogleClientSecretRef);
        Assert.True(AdminGoogleSecretStore.TryReadSecret(harness.GoogleSecretPath, out var stored));
        Assert.Equal(FakeManagedCutoverSecret, stored);
        Assert.NotEqual(FakeClientSecret, stored);

        using (var settings = await client.GetAsync(AdminGoogleSettingsPage.PagePath, ct))
        {
            var html = await settings.Content.ReadAsStringAsync(ct);
            Assert.Contains("Admin UI managed configuration", html, StringComparison.Ordinal);
            Assert.DoesNotContain("authority になります", html, StringComparison.Ordinal);
            Assert.Contains(FakeClientId, html, StringComparison.Ordinal);
            AssertNoSecretMaterial(html);
            AssertSecretAbsentFromInputs(html);
        }

        await harness.RestartAsync(ct);
        var options = harness.Factory.Services.GetRequiredService<AdminGoogleOptions>();
        Assert.True(options.UsesManagedConfiguration);
        Assert.True(options.Enabled);
        Assert.DoesNotContain(FakeClientSecret, options.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(FakeManagedCutoverSecret, options.ToString(), StringComparison.Ordinal);

        using var restarted = CreateClient(harness.Factory);
        using (var loginPage = await restarted.GetAsync("/admin/login", ct))
        {
            var html = await loginPage.Content.ReadAsStringAsync(ct);
            Assert.Contains("Googleでログイン", html, StringComparison.Ordinal);
            Assert.Contains("Username", html, StringComparison.Ordinal);
            AssertNoSecretMaterial(html);
        }

        await LoginAsync(restarted, OwnerUsername, OwnerPassword, ct);
        await AssertAuditOmitsSecretsAsync(harness, ct);
    }

    [Fact]
    public async Task Explicit_disabled_save_can_cut_over_from_env_without_copying_secret()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var harness = await ManagedHarness.CreateAsync(ct, EnvGoogleConfiguration());
        using var client = CreateClient(harness.Factory);
        await LoginAsync(client, OwnerUsername, OwnerPassword, ct);

        var token = await ReadCsrfTokenAsync(client, AdminGoogleSettingsPage.PagePath, ct);
        using (var save = await client.PostAsync(
            AdminGoogleSettingsPage.PagePath,
            Form(
                token,
                ("client_id", FakeClientId),
                ("client_secret", string.Empty),
                ("confirmation", "confirm")),
            ct))
        {
            Assert.Equal(HttpStatusCode.SeeOther, save.StatusCode);
        }

        var row = await harness.Instance.GetAsync(ct);
        Assert.NotNull(row);
        Assert.False(string.IsNullOrWhiteSpace(row.GoogleConfiguredAt));
        Assert.False(row.GoogleLoginEnabled);
        Assert.True(string.IsNullOrWhiteSpace(row.GoogleClientSecretRef));
        Assert.False(File.Exists(harness.GoogleSecretPath));

        await harness.RestartAsync(ct);
        var options = harness.Factory.Services.GetRequiredService<AdminGoogleOptions>();
        Assert.True(options.UsesManagedConfiguration);
        Assert.False(options.Enabled);

        using var restarted = CreateClient(harness.Factory);
        using (var loginPage = await restarted.GetAsync("/admin/login", ct))
        {
            var html = await loginPage.Content.ReadAsStringAsync(ct);
            Assert.DoesNotContain("Googleでログイン", html, StringComparison.Ordinal);
            Assert.Contains("Username", html, StringComparison.Ordinal);
            AssertNoSecretMaterial(html);
        }

        await LoginAsync(restarted, OwnerUsername, OwnerPassword, ct);
        await AssertAuditOmitsSecretsAsync(harness, ct);
    }

    [Fact]
    public async Task Save_without_new_secret_keeps_a_custom_existing_secret_ref()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var harness = await ManagedHarness.CreateAsync(ct);
        var customPath = Path.Combine(
            Path.GetDirectoryName(Path.GetDirectoryName(harness.GoogleSecretPath)!)!,
            "custom_google",
            "client_secret");
        AdminGoogleSecretStore.WriteSecret(customPath, FakeClientSecret);
        AdminGoogleSecretStore.WriteSecret(harness.GoogleSecretPath, FakeClientSecretRotated);
        Assert.True(await harness.Instance.SetGoogleLoginSettingsAsync(
            enabled: true,
            FakeClientId,
            customPath,
            ct));

        using var client = CreateClient(harness.Factory);
        await LoginAsync(client, OwnerUsername, OwnerPassword, ct);
        var token = await ReadCsrfTokenAsync(client, AdminGoogleSettingsPage.PagePath, ct);
        using (var save = await client.PostAsync(
            AdminGoogleSettingsPage.PagePath,
            Form(
                token,
                ("google_login_enabled", "1"),
                ("client_id", FakeClientId),
                ("client_secret", string.Empty),
                ("confirmation", "confirm")),
            ct))
        {
            Assert.Equal(HttpStatusCode.SeeOther, save.StatusCode);
        }

        var row = await harness.Instance.GetAsync(ct);
        Assert.NotNull(row);
        Assert.Equal(customPath, row.GoogleClientSecretRef);
        Assert.True(AdminGoogleSecretStore.TryReadSecret(customPath, out var customSecret));
        Assert.Equal(FakeClientSecret, customSecret);
        Assert.True(AdminGoogleSecretStore.TryReadSecret(harness.GoogleSecretPath, out var canonicalSecret));
        Assert.Equal(FakeClientSecretRotated, canonicalSecret);

        using (var settings = await client.GetAsync(AdminGoogleSettingsPage.PagePath, ct))
        {
            var html = await settings.Content.ReadAsStringAsync(ct);
            AssertNoSecretMaterial(html);
            AssertSecretAbsentFromInputs(html);
        }

        await LoginAsync(CreateClient(harness.Factory), OwnerUsername, OwnerPassword, ct);
        await AssertAuditOmitsSecretsAsync(harness, ct);
    }

    [Fact]
    public async Task Unchanged_managed_config_after_restart_shows_applied_without_restart_flash()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var harness = await ManagedHarness.CreateAsync(ct);
        AdminGoogleSecretStore.WriteSecret(harness.GoogleSecretPath, FakeClientSecret);
        Assert.True(await harness.Instance.SetGoogleLoginSettingsAsync(
            enabled: true,
            FakeClientId,
            harness.GoogleSecretPath,
            ct));
        await harness.RestartAsync(ct);

        using var client = CreateClient(harness.Factory);
        await LoginAsync(client, OwnerUsername, OwnerPassword, ct);
        using (var page = await client.GetAsync(AdminGoogleSettingsPage.PagePath, ct))
        {
            var html = await page.Content.ReadAsStringAsync(ct);
            AssertContainsStatus(html, AdminGoogleSettingsStatus.DisplayOn, AdminGoogleSettingsStatus.DisplayOn, AdminGoogleSettingsStatus.ReflectionApplied);
            Assert.Contains("always available", html, StringComparison.Ordinal);
            AssertNoSecretMaterial(html);
            AssertFingerprintAbsent(html, FakeClientSecret);
        }

        var token = await ReadCsrfTokenAsync(client, AdminGoogleSettingsPage.PagePath, ct);
        using (var save = await client.PostAsync(
            AdminGoogleSettingsPage.PagePath,
            Form(
                token,
                ("google_login_enabled", "1"),
                ("client_id", FakeClientId),
                ("client_secret", string.Empty),
                ("confirmation", "confirm")),
            ct))
        {
            Assert.Equal(HttpStatusCode.SeeOther, save.StatusCode);
        }

        using (var afterSave = await client.GetAsync($"{AdminGoogleSettingsPage.PagePath}?saved=1", ct))
        {
            var html = await afterSave.Content.ReadAsStringAsync(ct);
            AssertContainsStatus(html, AdminGoogleSettingsStatus.DisplayOn, AdminGoogleSettingsStatus.DisplayOn, AdminGoogleSettingsStatus.ReflectionApplied);
            Assert.Contains("設定を保存しました。", html, StringComparison.Ordinal);
            Assert.DoesNotContain("変更を有効にするには Mailer を再起動してください。", html, StringComparison.Ordinal);
            AssertNoSecretMaterial(html);
            AssertFingerprintAbsent(html, FakeClientSecret);
        }

        await AssertAuditOmitsSecretsAsync(harness, ct);
        await AssertAuditOmitsFingerprintsAsync(harness, ct, FakeClientSecret);
    }

    [Fact]
    public async Task Saved_off_to_on_shows_restart_pending_until_restart()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var harness = await ManagedHarness.CreateAsync(ct);
        AdminGoogleSecretStore.WriteSecret(harness.GoogleSecretPath, FakeClientSecret);
        Assert.True(await harness.Instance.SetGoogleLoginSettingsAsync(
            enabled: false,
            FakeClientId,
            harness.GoogleSecretPath,
            ct));
        await harness.RestartAsync(ct);

        using var client = CreateClient(harness.Factory);
        await LoginAsync(client, OwnerUsername, OwnerPassword, ct);
        var token = await ReadCsrfTokenAsync(client, AdminGoogleSettingsPage.PagePath, ct);
        using (var save = await client.PostAsync(
            AdminGoogleSettingsPage.PagePath,
            Form(
                token,
                ("google_login_enabled", "1"),
                ("client_id", FakeClientId),
                ("client_secret", string.Empty),
                ("confirmation", "confirm")),
            ct))
        {
            Assert.Equal(HttpStatusCode.SeeOther, save.StatusCode);
        }

        using (var page = await client.GetAsync(AdminGoogleSettingsPage.PagePath, ct))
        {
            var html = await page.Content.ReadAsStringAsync(ct);
            AssertContainsStatus(
                html,
                AdminGoogleSettingsStatus.DisplayOn,
                AdminGoogleSettingsStatus.DisplayOff,
                AdminGoogleSettingsStatus.ReflectionRestartPending);
            AssertNoSecretMaterial(html);
        }

        await harness.RestartAsync(ct);
        using var restarted = CreateClient(harness.Factory);
        await LoginAsync(restarted, OwnerUsername, OwnerPassword, ct);
        using (var page = await restarted.GetAsync(AdminGoogleSettingsPage.PagePath, ct))
        {
            var html = await page.Content.ReadAsStringAsync(ct);
            AssertContainsStatus(
                html,
                AdminGoogleSettingsStatus.DisplayOn,
                AdminGoogleSettingsStatus.DisplayOn,
                AdminGoogleSettingsStatus.ReflectionApplied);
        }
    }

    [Fact]
    public async Task Saved_on_to_off_shows_restart_pending_until_restart()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var harness = await ManagedHarness.CreateAsync(ct);
        AdminGoogleSecretStore.WriteSecret(harness.GoogleSecretPath, FakeClientSecret);
        Assert.True(await harness.Instance.SetGoogleLoginSettingsAsync(
            enabled: true,
            FakeClientId,
            harness.GoogleSecretPath,
            ct));
        await harness.RestartAsync(ct);

        using var client = CreateClient(harness.Factory);
        await LoginAsync(client, OwnerUsername, OwnerPassword, ct);
        var token = await ReadCsrfTokenAsync(client, AdminGoogleSettingsPage.PagePath, ct);
        using (var save = await client.PostAsync(
            AdminGoogleSettingsPage.PagePath,
            Form(
                token,
                ("client_id", FakeClientId),
                ("client_secret", string.Empty),
                ("confirmation", "confirm")),
            ct))
        {
            Assert.Equal(HttpStatusCode.SeeOther, save.StatusCode);
        }

        using (var page = await client.GetAsync(AdminGoogleSettingsPage.PagePath, ct))
        {
            var html = await page.Content.ReadAsStringAsync(ct);
            AssertContainsStatus(
                html,
                AdminGoogleSettingsStatus.DisplayOff,
                AdminGoogleSettingsStatus.DisplayOn,
                AdminGoogleSettingsStatus.ReflectionRestartPending);
        }

        await harness.RestartAsync(ct);
        using var restarted = CreateClient(harness.Factory);
        await LoginAsync(restarted, OwnerUsername, OwnerPassword, ct);
        using (var page = await restarted.GetAsync(AdminGoogleSettingsPage.PagePath, ct))
        {
            var html = await page.Content.ReadAsStringAsync(ct);
            AssertContainsStatus(
                html,
                AdminGoogleSettingsStatus.DisplayOff,
                AdminGoogleSettingsStatus.DisplayOff,
                AdminGoogleSettingsStatus.ReflectionApplied);
        }
    }

    [Fact]
    public async Task Client_id_only_change_shows_restart_pending()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var harness = await ManagedHarness.CreateAsync(ct);
        AdminGoogleSecretStore.WriteSecret(harness.GoogleSecretPath, FakeClientSecret);
        Assert.True(await harness.Instance.SetGoogleLoginSettingsAsync(
            enabled: true,
            FakeClientId,
            harness.GoogleSecretPath,
            ct));
        await harness.RestartAsync(ct);

        using var client = CreateClient(harness.Factory);
        await LoginAsync(client, OwnerUsername, OwnerPassword, ct);
        var token = await ReadCsrfTokenAsync(client, AdminGoogleSettingsPage.PagePath, ct);
        var updatedClientId = FakeClientId + "-rotated-id";
        using (var save = await client.PostAsync(
            AdminGoogleSettingsPage.PagePath,
            Form(
                token,
                ("google_login_enabled", "1"),
                ("client_id", updatedClientId),
                ("client_secret", string.Empty),
                ("confirmation", "confirm")),
            ct))
        {
            Assert.Equal(HttpStatusCode.SeeOther, save.StatusCode);
        }

        using (var page = await client.GetAsync(AdminGoogleSettingsPage.PagePath, ct))
        {
            var html = await page.Content.ReadAsStringAsync(ct);
            Assert.Contains(updatedClientId, html, StringComparison.Ordinal);
            AssertContainsStatus(
                html,
                AdminGoogleSettingsStatus.DisplayOn,
                AdminGoogleSettingsStatus.DisplayOn,
                AdminGoogleSettingsStatus.ReflectionRestartPending);
            AssertNoSecretMaterial(html);
        }
    }

    [Fact]
    public async Task Client_secret_rotation_same_path_shows_restart_pending()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var harness = await ManagedHarness.CreateAsync(ct);
        AdminGoogleSecretStore.WriteSecret(harness.GoogleSecretPath, FakeClientSecret);
        Assert.True(await harness.Instance.SetGoogleLoginSettingsAsync(
            enabled: true,
            FakeClientId,
            harness.GoogleSecretPath,
            ct));
        await harness.RestartAsync(ct);

        using var client = CreateClient(harness.Factory);
        await LoginAsync(client, OwnerUsername, OwnerPassword, ct);
        var token = await ReadCsrfTokenAsync(client, AdminGoogleSettingsPage.PagePath, ct);
        using (var save = await client.PostAsync(
            AdminGoogleSettingsPage.PagePath,
            Form(
                token,
                ("google_login_enabled", "1"),
                ("client_id", FakeClientId),
                ("client_secret", FakeClientSecretRotated),
                ("confirmation", "confirm")),
            ct))
        {
            Assert.Equal(HttpStatusCode.SeeOther, save.StatusCode);
        }

        var row = await harness.Instance.GetAsync(ct);
        Assert.Equal(harness.GoogleSecretPath, row!.GoogleClientSecretRef);
        Assert.True(AdminGoogleSecretStore.TryReadSecret(harness.GoogleSecretPath, out var stored));
        Assert.Equal(FakeClientSecretRotated, stored);
        Assert.NotEqual(FakeClientSecret, stored);

        using (var page = await client.GetAsync(AdminGoogleSettingsPage.PagePath, ct))
        {
            var html = await page.Content.ReadAsStringAsync(ct);
            AssertContainsStatus(
                html,
                AdminGoogleSettingsStatus.DisplayOn,
                AdminGoogleSettingsStatus.DisplayOn,
                AdminGoogleSettingsStatus.ReflectionRestartPending);
            AssertNoSecretMaterial(html);
            AssertFingerprintAbsent(html, FakeClientSecret);
            AssertFingerprintAbsent(html, FakeClientSecretRotated);
        }

        await harness.RestartAsync(ct);
        using var restarted = CreateClient(harness.Factory);
        await LoginAsync(restarted, OwnerUsername, OwnerPassword, ct);
        using (var page = await restarted.GetAsync(AdminGoogleSettingsPage.PagePath, ct))
        {
            var html = await page.Content.ReadAsStringAsync(ct);
            AssertContainsStatus(
                html,
                AdminGoogleSettingsStatus.DisplayOn,
                AdminGoogleSettingsStatus.DisplayOn,
                AdminGoogleSettingsStatus.ReflectionApplied);
            AssertNoSecretMaterial(html);
            AssertFingerprintAbsent(html, FakeClientSecretRotated);
        }
    }

    [Fact]
    public async Task Secret_rotation_before_first_google_challenge_keeps_startup_google_options_until_restart()
    {
        // Regression for B1: AdminGoogleOptions pins a startup fingerprint, but the
        // Google authentication handler previously materialized named GoogleOptions
        // lazily. Rotating the same secret path before the first challenge could make
        // the handler pick up the new secret while the Admin UI still said 再起動待ち.
        var ct = TestContext.Current.CancellationToken;
        await using var harness = await ManagedHarness.CreateAsync(ct);
        AdminGoogleSecretStore.WriteSecret(harness.GoogleSecretPath, FakeClientSecret);
        Assert.True(await harness.Instance.SetGoogleLoginSettingsAsync(
            enabled: true,
            FakeClientId,
            harness.GoogleSecretPath,
            ct));

        // Host startup with managed Google Login enabled. Do not issue a Google challenge.
        await harness.RestartAsync(ct);
        Assert.Equal(
            FakeClientSecret,
            ReadGoogleSchemeClientSecret(harness.Factory));

        using var client = CreateClient(harness.Factory);
        await LoginAsync(client, OwnerUsername, OwnerPassword, ct);
        var token = await ReadCsrfTokenAsync(client, AdminGoogleSettingsPage.PagePath, ct);
        using (var save = await client.PostAsync(
            AdminGoogleSettingsPage.PagePath,
            Form(
                token,
                ("google_login_enabled", "1"),
                ("client_id", FakeClientId),
                ("client_secret", FakeClientSecretRotated),
                ("confirmation", "confirm")),
            ct))
        {
            Assert.Equal(HttpStatusCode.SeeOther, save.StatusCode);
        }

        Assert.True(AdminGoogleSecretStore.TryReadSecret(harness.GoogleSecretPath, out var stored));
        Assert.Equal(FakeClientSecretRotated, stored);

        using (var page = await client.GetAsync(AdminGoogleSettingsPage.PagePath, ct))
        {
            var html = await page.Content.ReadAsStringAsync(ct);
            AssertContainsStatus(
                html,
                AdminGoogleSettingsStatus.DisplayOn,
                AdminGoogleSettingsStatus.DisplayOn,
                AdminGoogleSettingsStatus.ReflectionRestartPending);
            AssertNoSecretMaterial(html);
            AssertFingerprintAbsent(html, FakeClientSecret);
            AssertFingerprintAbsent(html, FakeClientSecretRotated);
        }

        // Before restart, the Google scheme must keep the startup credential snapshot
        // even though the secret file already has the rotated value and no challenge
        // has run yet.
        Assert.Equal(
            FakeClientSecret,
            ReadGoogleSchemeClientSecret(harness.Factory));
        Assert.NotEqual(
            FakeClientSecretRotated,
            ReadGoogleSchemeClientSecret(harness.Factory));

        await harness.RestartAsync(ct);
        Assert.Equal(
            FakeClientSecretRotated,
            ReadGoogleSchemeClientSecret(harness.Factory));

        using var restarted = CreateClient(harness.Factory);
        await LoginAsync(restarted, OwnerUsername, OwnerPassword, ct);
        using (var page = await restarted.GetAsync(AdminGoogleSettingsPage.PagePath, ct))
        {
            var html = await page.Content.ReadAsStringAsync(ct);
            AssertContainsStatus(
                html,
                AdminGoogleSettingsStatus.DisplayOn,
                AdminGoogleSettingsStatus.DisplayOn,
                AdminGoogleSettingsStatus.ReflectionApplied);
            AssertNoSecretMaterial(html);
            AssertFingerprintAbsent(html, FakeClientSecretRotated);
        }
    }

    [Fact]
    public async Task Legacy_to_managed_cutover_shows_restart_pending_even_when_effective_stays_on()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var harness = await ManagedHarness.CreateAsync(ct, EnvGoogleConfiguration());
        using var client = CreateClient(harness.Factory);
        await LoginAsync(client, OwnerUsername, OwnerPassword, ct);

        var token = await ReadCsrfTokenAsync(client, AdminGoogleSettingsPage.PagePath, ct);
        using (var save = await client.PostAsync(
            AdminGoogleSettingsPage.PagePath,
            Form(
                token,
                ("google_login_enabled", "1"),
                ("client_id", FakeClientId),
                ("client_secret", FakeManagedCutoverSecret),
                ("confirmation", "confirm")),
            ct))
        {
            Assert.Equal(HttpStatusCode.SeeOther, save.StatusCode);
        }

        using (var page = await client.GetAsync($"{AdminGoogleSettingsPage.PagePath}?saved=1", ct))
        {
            var html = await page.Content.ReadAsStringAsync(ct);
            Assert.Contains(AdminGoogleSettingsStatus.AuthorityManaged, html, StringComparison.Ordinal);
            AssertContainsStatus(
                html,
                AdminGoogleSettingsStatus.DisplayOn,
                AdminGoogleSettingsStatus.DisplayOn,
                AdminGoogleSettingsStatus.ReflectionRestartPending);
            Assert.Contains("設定を保存しました。変更を有効にするには Mailer を再起動してください。", html, StringComparison.Ordinal);
            AssertNoSecretMaterial(html);
            AssertFingerprintAbsent(html, FakeManagedCutoverSecret);
        }
    }

    [Fact]
    public async Task Incomplete_managed_config_shows_incomplete_not_applied()
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
        await LoginAsync(client, OwnerUsername, OwnerPassword, ct);
        using (var page = await client.GetAsync(AdminGoogleSettingsPage.PagePath, ct))
        {
            var html = await page.Content.ReadAsStringAsync(ct);
            AssertContainsStatus(
                html,
                AdminGoogleSettingsStatus.DisplayOnIncomplete,
                AdminGoogleSettingsStatus.DisplayOff,
                AdminGoogleSettingsStatus.ReflectionIncomplete);
            Assert.DoesNotContain($">{AdminGoogleSettingsStatus.ReflectionApplied}</code>", html, StringComparison.Ordinal);
            Assert.Contains("always available", html, StringComparison.Ordinal);
            AssertNoSecretMaterial(html);
        }

        using (var loginPage = await CreateClient(harness.Factory).GetAsync("/admin/login", ct))
        {
            var html = await loginPage.Content.ReadAsStringAsync(ct);
            Assert.DoesNotContain("Googleでログイン", html, StringComparison.Ordinal);
            Assert.Contains("Username", html, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void Options_secret_fingerprint_matches_without_exposing_plaintext_in_tostring()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                [AdminGoogleOptions.ClientIdKey] = FakeClientId,
                [AdminGoogleOptions.ClientSecretKey] = FakeClientSecret,
            })
            .Build();
        var options = AdminGoogleOptions.Load(configuration);
        Assert.True(options.Enabled);
        Assert.True(options.MatchesCurrentSecret(FakeClientSecret));
        Assert.False(options.MatchesCurrentSecret(FakeClientSecretRotated));
        Assert.DoesNotContain(FakeClientSecret, options.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(
            Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
                System.Text.Encoding.UTF8.GetBytes(FakeClientSecret))),
            options.ToString(),
            StringComparison.OrdinalIgnoreCase);
    }

    private static void AssertContainsStatus(
        string html,
        string savedDisplay,
        string runtimeDisplay,
        string reflectionDisplay)
    {
        Assert.Matches(
            new Regex(
                $@"保存済み設定</dt>\s*<dd><code>{Regex.Escape(savedDisplay)}</code></dd>",
                RegexOptions.CultureInvariant),
            html);
        Assert.Matches(
            new Regex(
                $@"現在の稼働状態</dt>\s*<dd><code>{Regex.Escape(runtimeDisplay)}</code></dd>",
                RegexOptions.CultureInvariant),
            html);
        Assert.Matches(
            new Regex(
                $@"状態</dt>\s*<dd><code>{Regex.Escape(reflectionDisplay)}</code></dd>",
                RegexOptions.CultureInvariant),
            html);
    }

    private static void AssertFingerprintAbsent(string text, string secret)
    {
        var fingerprintHex = Convert.ToHexString(
            System.Security.Cryptography.SHA256.HashData(
                System.Text.Encoding.UTF8.GetBytes(secret)));
        Assert.DoesNotContain(fingerprintHex, text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(fingerprintHex.ToLowerInvariant(), text, StringComparison.Ordinal);
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
            Assert.DoesNotContain(FakeManagedCutoverSecret, match.Value, StringComparison.Ordinal);
            Assert.Contains("value=\"\"", match.Value, StringComparison.Ordinal);
        }
    }

    private static void AssertNoSecretMaterial(string text)
    {
        Assert.DoesNotContain(FakeClientSecret, text, StringComparison.Ordinal);
        Assert.DoesNotContain(FakeClientSecretRotated, text, StringComparison.Ordinal);
        Assert.DoesNotContain(FakeManagedCutoverSecret, text, StringComparison.Ordinal);
    }

    private static string ReadGoogleSchemeClientSecret(WebApplicationFactory<global::Program> factory) =>
        factory.Services
            .GetRequiredService<IOptionsMonitor<GoogleOptions>>()
            .Get(AdminGoogleAuthenticationConstants.AuthenticationScheme)
            .ClientSecret;

    private static Dictionary<string, string?> EnvGoogleConfiguration() =>
        new(StringComparer.Ordinal)
        {
            [AdminGoogleOptions.ClientIdKey] = FakeClientId,
            [AdminGoogleOptions.ClientSecretKey] = FakeClientSecret,
        };

    private static async Task AssertAuditOmitsSecretsAsync(
        ManagedHarness harness,
        CancellationToken cancellationToken)
    {
        var audits = await harness.Factory.Services.GetRequiredService<AdminAuditRepository>()
            .ListRecentAsync(20, cancellationToken);
        foreach (var audit in audits)
        {
            AssertNoSecretMaterial(audit.FieldName ?? string.Empty);
            AssertNoSecretMaterial(audit.ErrorCode ?? string.Empty);
            AssertNoSecretMaterial(audit.TargetId ?? string.Empty);
        }
    }

    private static async Task AssertAuditOmitsFingerprintsAsync(
        ManagedHarness harness,
        CancellationToken cancellationToken,
        params string[] secrets)
    {
        var audits = await harness.Factory.Services.GetRequiredService<AdminAuditRepository>()
            .ListRecentAsync(20, cancellationToken);
        foreach (var audit in audits)
        {
            foreach (var secret in secrets)
            {
                AssertFingerprintAbsent(audit.FieldName ?? string.Empty, secret);
                AssertFingerprintAbsent(audit.ErrorCode ?? string.Empty, secret);
                AssertFingerprintAbsent(audit.TargetId ?? string.Empty, secret);
            }
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
