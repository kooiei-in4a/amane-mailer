using System.Net;
using System.Net.Http.Headers;
using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Amane.Mailer.Admin;
using Amane.Mailer.Data.Sqlite;
using Amane.Mailer.Data.Sqlite.Models;
using Amane.Mailer.Identity;
using Amane.Mailer.Operations;
using Amane.Mailer.Setup;
using Amane.Mailer.Tests.Fixtures;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Data.Sqlite;

namespace Amane.Mailer.Tests.Admin;

public sealed class AdminSettingsBackupTests
{
    private const string OwnerUsername = "settings-backup-owner";
    private const string OwnerPassword = "settings-backup-owner-password";
    private const string ScopedUsername = "settings-backup-scoped";
    private const string ScopedPassword = "settings-backup-scoped-password";
    private const string BreakGlassUsername = "settings-backup-break-glass";
    private const string BreakGlassPassword = "settings-backup-break-glass-password";
    private const string Passphrase = "test-only-settings-backup-passphrase";
    private const string SourceAcsSecret = "Endpoint=https://example.communication.azure.com/;AccessKey=source-abc123";
    private const string TargetAcsSecret = "Endpoint=https://example.communication.azure.com/;AccessKey=target-abc123";
    private const string SourceGoogleSecret = "test-only-source-google-client-secret";
    private const string TargetGoogleSecret = "test-only-target-google-client-secret";
    private const string SourceGoogleClientId = "source-test.apps.exampleusercontent.com";
    private const string TargetGoogleClientId = "target-test.apps.exampleusercontent.com";

    [Fact]
    public async Task Owner_can_export_preview_and_restore_to_target_authority_safely()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var source = await ManagedBackupHarness.CreateAsync(
            "source",
            SourceAcsSecret,
            googleEnabled: true,
            SourceGoogleClientId,
            SourceGoogleSecret,
            cancellationToken);
        await using var target = await ManagedBackupHarness.CreateAsync(
            "target",
            TargetAcsSecret,
            googleEnabled: false,
            TargetGoogleClientId,
            TargetGoogleSecret,
            cancellationToken);

        var sourceShared = source.SharedSender;
        Assert.NotEqual(sourceShared.SenderId, target.SharedSender.SenderId);
        var sourceImported = await source.Senders.CreateAsync(
            "imported@example.com",
            "Imported sender",
            cancellationToken);
        await source.Senders.DisableAsync(sourceImported.SenderId, cancellationToken);
        var sourceOnlyKey = await source.Senders.CreateApiKeyAsync(
            sourceImported.SenderId,
            "source-only-key",
            cancellationToken);
        var targetKey = await target.Senders.CreateApiKeyAsync(
            target.SharedSender.SenderId,
            "target-key",
            cancellationToken);
        var targetLocalOnly = await target.Senders.CreateAsync(
            "local-only@example.com",
            "Keep this local sender",
            cancellationToken);

        var targetOwner = (await target.Users.ListUserSummariesAsync(cancellationToken))
            .Single(user => user.Username == OwnerUsername);
        Assert.Equal(AdminGoogleLinkResult.Linked, await target.GoogleIdentities.TryLinkAsync(
            targetOwner.Id,
            "https://accounts.google.com",
            "settings-backup-owner-subject",
            TimeProvider.System.GetUtcNow(),
            cancellationToken));
        Assert.True(await target.Instance.SetLiveSendingAsync(true, cancellationToken));

        using var sourceOwner = CreateClient(source.Factory);
        await LoginAsync(sourceOwner, OwnerUsername, OwnerPassword, cancellationToken);
        var sourceCsrf = await ReadCsrfTokenAsync(sourceOwner, cancellationToken);
        using (var missingExportCsrf = await sourceOwner.PostAsync(
            AdminSettingsBackupPage.ExportPath,
            Form(null,
                ("passphrase", Passphrase),
                ("passphrase_confirmation", Passphrase)),
            cancellationToken))
        {
            Assert.Equal(HttpStatusCode.BadRequest, missingExportCsrf.StatusCode);
        }

        using (var mismatchedPassphrases = await sourceOwner.PostAsync(
            AdminSettingsBackupPage.ExportPath,
            Form(sourceCsrf,
                ("passphrase", Passphrase),
                ("passphrase_confirmation", "different-test-only-passphrase")),
            cancellationToken))
        {
            Assert.Equal(HttpStatusCode.Conflict, mismatchedPassphrases.StatusCode);
            Assert.True(mismatchedPassphrases.Headers.CacheControl?.NoStore);
            Assert.DoesNotContain(Passphrase, await mismatchedPassphrases.Content.ReadAsStringAsync(cancellationToken), StringComparison.Ordinal);
        }

        sourceCsrf = await ReadCsrfTokenAsync(sourceOwner, cancellationToken);
        using var export = await sourceOwner.PostAsync(
            AdminSettingsBackupPage.ExportPath,
            Form(sourceCsrf,
                ("passphrase", Passphrase),
                ("passphrase_confirmation", Passphrase)),
            cancellationToken);
        var encrypted = await export.Content.ReadAsByteArrayAsync(cancellationToken);
        var exportText = Encoding.UTF8.GetString(encrypted);
        var disposition = export.Content.Headers.ContentDisposition?.ToString() ?? string.Empty;

        Assert.Equal(HttpStatusCode.OK, export.StatusCode);
        Assert.Equal("application/octet-stream", export.Content.Headers.ContentType?.MediaType);
        Assert.True(export.Headers.CacheControl?.NoStore);
        Assert.Contains("amane-mailer-settings-", disposition, StringComparison.Ordinal);
        Assert.Contains(".json.age", disposition, StringComparison.Ordinal);
        Assert.StartsWith("age-encryption.org/v1\n", exportText, StringComparison.Ordinal);
        Assert.DoesNotContain(SourceAcsSecret, exportText, StringComparison.Ordinal);
        Assert.DoesNotContain(SourceGoogleSecret, exportText, StringComparison.Ordinal);
        Assert.DoesNotContain(Passphrase, exportText, StringComparison.Ordinal);
        Assert.DoesNotContain(source.AcsSecretPath, exportText, StringComparison.Ordinal);
        Assert.DoesNotContain(source.GoogleSecretPath, exportText, StringComparison.Ordinal);
        Assert.True(SettingsBackupCrypto.TryDecryptAndValidate(encrypted, Passphrase, out var exportedPayload));
        Assert.NotNull(exportedPayload);
        Assert.Equal(SourceAcsSecret, exportedPayload.Secrets.AcsConnectionString);
        Assert.Equal(SourceGoogleSecret, exportedPayload.Secrets.GoogleClientSecret);
        Assert.True(exportedPayload.Settings.GoogleLogin.Included);
        Assert.True(exportedPayload.Settings.GoogleLogin.Enabled);
        Assert.Equal(SourceGoogleClientId, exportedPayload.Settings.GoogleLogin.ClientId);
        Assert.Equal(
            new[] { "imported@example.com", "shared@example.com" },
            exportedPayload.Settings.Senders.Select(sender => sender.Email).Order(StringComparer.Ordinal));
        var exportedJson = SettingsBackupFormat.Serialize(exportedPayload);
        try
        {
            var exportedJsonText = Encoding.UTF8.GetString(exportedJson);
            Assert.DoesNotContain("senderId", exportedJsonText, StringComparison.Ordinal);
            Assert.DoesNotContain("apiKey", exportedJsonText, StringComparison.Ordinal);
            Assert.DoesNotContain("providerSecretRef", exportedJsonText, StringComparison.Ordinal);
            Assert.DoesNotContain("googleClientSecretRef", exportedJsonText, StringComparison.Ordinal);
            Assert.DoesNotContain(source.AcsSecretPath, exportedJsonText, StringComparison.Ordinal);
            Assert.DoesNotContain(source.GoogleSecretPath, exportedJsonText, StringComparison.Ordinal);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(exportedJson);
        }

        using var owner = CreateClient(target.Factory);
        await LoginAsync(owner, OwnerUsername, OwnerPassword, cancellationToken);
        using (var page = await owner.GetAsync(AdminSettingsBackupPage.PagePath, cancellationToken))
        {
            var html = await page.Content.ReadAsStringAsync(cancellationToken);
            Assert.Equal(HttpStatusCode.OK, page.StatusCode);
            Assert.True(page.Headers.CacheControl?.NoStore);
            Assert.Contains("href=\"/admin/settings-backup\"", html, StringComparison.Ordinal);
            Assert.DoesNotContain(TargetAcsSecret, html, StringComparison.Ordinal);
            Assert.DoesNotContain(TargetGoogleSecret, html, StringComparison.Ordinal);
            Assert.DoesNotContain(target.AcsSecretPath, html, StringComparison.Ordinal);
            Assert.DoesNotContain(target.GoogleSecretPath, html, StringComparison.Ordinal);
        }

        using var scoped = CreateClient(target.Factory);
        await LoginAsync(scoped, ScopedUsername, ScopedPassword, cancellationToken);
        using (var denied = await scoped.GetAsync(AdminSettingsBackupPage.PagePath, cancellationToken))
            Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
        using (var scopedHome = await scoped.GetAsync("/admin/mail-requests", cancellationToken))
            Assert.DoesNotContain("href=\"/admin/settings-backup\"", await scopedHome.Content.ReadAsStringAsync(cancellationToken), StringComparison.Ordinal);

        using var breakGlass = CreateClient(target.Factory);
        await LoginAsync(breakGlass, BreakGlassUsername, BreakGlassPassword, cancellationToken);
        using (var denied = await breakGlass.GetAsync(AdminSettingsBackupPage.PagePath, cancellationToken))
            Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);

        var token = await ReadCsrfTokenAsync(owner, cancellationToken);
        using (var missingCsrf = await owner.PostAsync(
            AdminSettingsBackupPage.PreviewPath,
            UploadForm(null, encrypted, Passphrase),
            cancellationToken))
        {
            Assert.Equal(HttpStatusCode.BadRequest, missingCsrf.StatusCode);
        }

        using (var oversized = await owner.PostAsync(
            AdminSettingsBackupPage.PreviewPath,
            UploadForm(token, new byte[SettingsBackupCrypto.MaxEncryptedBytes + 1], Passphrase),
            cancellationToken))
        {
            Assert.Equal(HttpStatusCode.BadRequest, oversized.StatusCode);
        }

        using (var malformedAge = await owner.PostAsync(
            AdminSettingsBackupPage.PreviewPath,
            UploadForm(token, Encoding.UTF8.GetBytes("malformed age input"), Passphrase),
            cancellationToken))
        {
            Assert.Equal(HttpStatusCode.BadRequest, malformedAge.StatusCode);
            Assert.DoesNotContain(SourceAcsSecret, await malformedAge.Content.ReadAsStringAsync(cancellationToken), StringComparison.Ordinal);
        }

        var beforePreviewConfiguration = await target.Instance.GetAsync(cancellationToken);
        var beforePreviewShared = await target.Senders.FindAsync(target.SharedSender.SenderId, cancellationToken);
        var beforePreviewGoogleSecret = await File.ReadAllTextAsync(target.GoogleSecretPath, cancellationToken);
        using var previewResponse = await owner.PostAsync(
            AdminSettingsBackupPage.PreviewPath,
            UploadForm(token, encrypted, Passphrase),
            cancellationToken);
        var previewHtml = await previewResponse.Content.ReadAsStringAsync(cancellationToken);
        var previewText = WebUtility.HtmlDecode(previewHtml);
        Assert.Equal(HttpStatusCode.OK, previewResponse.StatusCode);
        Assert.True(previewResponse.Headers.CacheControl?.NoStore);
        Assert.Contains("included; OFF → ON", previewText, StringComparison.Ordinal);
        Assert.Contains($"{TargetGoogleClientId} → {SourceGoogleClientId}", previewText, StringComparison.Ordinal);
        Assert.Contains("Google Client Secret</dt><dd>different", previewHtml, StringComparison.Ordinal);
        Assert.Contains("ACS Secret</dt><dd>different", previewHtml, StringComparison.Ordinal);
        Assert.Contains("Live Sending imported</dt><dd>ON", previewHtml, StringComparison.Ordinal);
        Assert.Contains("Live Sending applied</dt><dd>OFF", previewHtml, StringComparison.Ordinal);
        Assert.Contains("Restart</dt><dd>required", previewHtml, StringComparison.Ordinal);
        Assert.Contains("display-name change: 1", previewHtml, StringComparison.Ordinal);
        Assert.Contains("enable/disable change: 1", previewHtml, StringComparison.Ordinal);
        Assert.Contains("current-only preserved: 1", previewHtml, StringComparison.Ordinal);
        Assert.Contains("imported@example.com", previewHtml, StringComparison.Ordinal);
        Assert.DoesNotContain(SourceAcsSecret, previewHtml, StringComparison.Ordinal);
        Assert.DoesNotContain(TargetAcsSecret, previewHtml, StringComparison.Ordinal);
        Assert.DoesNotContain(SourceGoogleSecret, previewHtml, StringComparison.Ordinal);
        Assert.DoesNotContain(TargetGoogleSecret, previewHtml, StringComparison.Ordinal);
        Assert.DoesNotContain(Passphrase, previewHtml, StringComparison.Ordinal);
        Assert.DoesNotContain(source.AcsSecretPath, previewHtml, StringComparison.Ordinal);
        Assert.DoesNotContain(source.GoogleSecretPath, previewHtml, StringComparison.Ordinal);
        Assert.DoesNotContain(target.AcsSecretPath, previewHtml, StringComparison.Ordinal);
        Assert.DoesNotContain(target.GoogleSecretPath, previewHtml, StringComparison.Ordinal);
        Assert.DoesNotContain("name=\"passphrase\" type=\"hidden\"", previewHtml, StringComparison.Ordinal);
        Assert.Contains("name=\"encrypted_ciphertext\"", previewHtml, StringComparison.Ordinal);
        var hiddenCiphertext = ReadInputValue(previewHtml, "encrypted_ciphertext");
        Assert.NotEqual(string.Empty, hiddenCiphertext);
        Assert.Equal(beforePreviewConfiguration!.LiveSending, (await target.Instance.GetAsync(cancellationToken))!.LiveSending);
        Assert.Equal(TargetAcsSecret, await File.ReadAllTextAsync(target.AcsSecretPath, cancellationToken));
        Assert.Equal(beforePreviewGoogleSecret, await File.ReadAllTextAsync(target.GoogleSecretPath, cancellationToken));
        Assert.Equal(beforePreviewShared, await target.Senders.FindAsync(target.SharedSender.SenderId, cancellationToken));

        var ownerHashBeforeRestore = await ReadOwnerCredentialsAsync(target, cancellationToken);
        var identityBeforeRestore = await target.GoogleIdentities.FindByAdminUserIdAsync(
            targetOwner.Id,
            cancellationToken);
        Assert.NotNull(identityBeforeRestore);

        token = ReadCsrf(previewHtml);
        using (var missingRestoreCsrf = await owner.PostAsync(
            AdminSettingsBackupPage.RestorePath,
            Form(null,
                ("encrypted_ciphertext", hiddenCiphertext),
                ("passphrase", Passphrase),
                ("confirmation", "restore")),
            cancellationToken))
        {
            Assert.Equal(HttpStatusCode.BadRequest, missingRestoreCsrf.StatusCode);
        }

        using (var missingConfirmation = await owner.PostAsync(
            AdminSettingsBackupPage.RestorePath,
            Form(token,
                ("encrypted_ciphertext", hiddenCiphertext),
                ("passphrase", Passphrase)),
            cancellationToken))
        {
            Assert.Equal(HttpStatusCode.BadRequest, missingConfirmation.StatusCode);
            Assert.DoesNotContain(SourceAcsSecret, await missingConfirmation.Content.ReadAsStringAsync(cancellationToken), StringComparison.Ordinal);
        }

        using (var wrongPassphrase = await owner.PostAsync(
            AdminSettingsBackupPage.RestorePath,
            Form(token,
                ("encrypted_ciphertext", hiddenCiphertext),
                ("passphrase", "different-test-only-passphrase"),
                ("confirmation", "restore")),
            cancellationToken))
        {
            Assert.Equal(HttpStatusCode.BadRequest, wrongPassphrase.StatusCode);
            Assert.DoesNotContain(SourceAcsSecret, await wrongPassphrase.Content.ReadAsStringAsync(cancellationToken), StringComparison.Ordinal);
        }

        Assert.Equal(TargetAcsSecret, await File.ReadAllTextAsync(target.AcsSecretPath, cancellationToken));
        Assert.Equal(TargetGoogleSecret, await File.ReadAllTextAsync(target.GoogleSecretPath, cancellationToken));
        Assert.True((await target.Instance.GetAsync(cancellationToken))!.LiveSending);

        using var restored = await owner.PostAsync(
            AdminSettingsBackupPage.RestorePath,
            Form(token,
                ("encrypted_ciphertext", hiddenCiphertext),
                ("passphrase", Passphrase),
                ("confirmation", "restore")),
            cancellationToken);
        var restoreHtml = await restored.Content.ReadAsStringAsync(cancellationToken);
        Assert.Equal(HttpStatusCode.OK, restored.StatusCode);
        Assert.True(restored.Headers.CacheControl?.NoStore);
        Assert.Contains("Restore完了", restoreHtml, StringComparison.Ordinal);
        Assert.Contains("Live Sending</dt><dd>OFF", restoreHtml, StringComparison.Ordinal);
        Assert.Contains("Restart</dt><dd>required", restoreHtml, StringComparison.Ordinal);
        Assert.DoesNotContain(SourceAcsSecret, restoreHtml, StringComparison.Ordinal);
        Assert.DoesNotContain(SourceGoogleSecret, restoreHtml, StringComparison.Ordinal);

        var restoredConfiguration = await target.Instance.GetAsync(cancellationToken);
        Assert.NotNull(restoredConfiguration);
        Assert.False(restoredConfiguration.LiveSending);
        Assert.Equal(target.AcsSecretPath, restoredConfiguration.ProviderSecretRef);
        Assert.Equal(SourceAcsSecret, await File.ReadAllTextAsync(target.AcsSecretPath, cancellationToken));
        Assert.Equal(target.GoogleSecretPath, restoredConfiguration.GoogleClientSecretRef);
        Assert.Equal(SourceGoogleSecret, await File.ReadAllTextAsync(target.GoogleSecretPath, cancellationToken));
        Assert.True(restoredConfiguration.GoogleLoginEnabled);
        Assert.Equal(SourceGoogleClientId, restoredConfiguration.GoogleClientId);

        var restoredShared = await target.Senders.FindByEmailAsync("shared@example.com", cancellationToken);
        Assert.NotNull(restoredShared);
        Assert.Equal(target.SharedSender.SenderId, restoredShared.SenderId);
        Assert.Equal("Source shared", restoredShared.DisplayName);
        Assert.True(restoredShared.Enabled);
        var restoredImported = await target.Senders.FindByEmailAsync("imported@example.com", cancellationToken);
        Assert.NotNull(restoredImported);
        Assert.False(restoredImported.Enabled);
        Assert.NotEqual(sourceImported.SenderId, restoredImported.SenderId);
        var restoredLocalOnly = await target.Senders.FindByEmailAsync("local-only@example.com", cancellationToken);
        Assert.NotNull(restoredLocalOnly);
        Assert.Equal(targetLocalOnly.SenderId, restoredLocalOnly.SenderId);
        Assert.True(restoredLocalOnly.Enabled);
        Assert.Equal("Keep this local sender", restoredLocalOnly.DisplayName);

        Assert.NotNull(await target.Senders.AuthenticateAsync(targetKey.Plaintext, cancellationToken));
        Assert.Null(await target.Senders.AuthenticateAsync(sourceOnlyKey.Plaintext, cancellationToken));
        Assert.Empty(await target.Senders.ListApiKeysAsync(restoredImported.SenderId, cancellationToken));
        Assert.Equal(identityBeforeRestore, await target.GoogleIdentities.FindByAdminUserIdAsync(
            targetOwner.Id,
            cancellationToken));
        Assert.Equal(ownerHashBeforeRestore, await ReadOwnerCredentialsAsync(target, cancellationToken));

        token = await ReadCsrfTokenAsync(owner, cancellationToken);
        using (var repeatedRestore = await owner.PostAsync(
            AdminSettingsBackupPage.RestorePath,
            Form(token,
                ("encrypted_ciphertext", hiddenCiphertext),
                ("passphrase", Passphrase),
                ("confirmation", "restore")),
            cancellationToken))
        {
            Assert.Equal(HttpStatusCode.OK, repeatedRestore.StatusCode);
            Assert.False((await target.Instance.GetAsync(cancellationToken))!.LiveSending);
            Assert.Equal(SourceAcsSecret, await File.ReadAllTextAsync(target.AcsSecretPath, cancellationToken));
            Assert.Equal(SourceGoogleSecret, await File.ReadAllTextAsync(target.GoogleSecretPath, cancellationToken));
            Assert.Equal("Source shared", (await target.Senders.FindByEmailAsync("shared@example.com", cancellationToken))!.DisplayName);
            Assert.Single(await target.Senders.ListApiKeysAsync(target.SharedSender.SenderId, cancellationToken));
        }

        await AssertAuditsContainNoBackupSecretsAsync(source, target, cancellationToken);

        using var ownerAfterRestore = CreateClient(target.Factory);
        await LoginAsync(ownerAfterRestore, OwnerUsername, OwnerPassword, cancellationToken);
        using var ownerPageAfterRestore = await ownerAfterRestore.GetAsync(
            AdminSettingsBackupPage.PagePath,
            cancellationToken);
        Assert.Equal(HttpStatusCode.OK, ownerPageAfterRestore.StatusCode);
    }

    [Fact]
    public async Task Export_excludes_legacy_google_environment_credentials()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var legacy = await ManagedBackupHarness.CreateAsync(
            "legacy",
            SourceAcsSecret,
            googleEnabled: true,
            SourceGoogleClientId,
            SourceGoogleSecret,
            cancellationToken,
            useManagedGoogleAuthority: false);
        using var owner = CreateClient(legacy.Factory);
        await LoginAsync(owner, OwnerUsername, OwnerPassword, cancellationToken);
        var token = await ReadCsrfTokenAsync(owner, cancellationToken);

        using var export = await owner.PostAsync(
            AdminSettingsBackupPage.ExportPath,
            Form(token,
                ("passphrase", Passphrase),
                ("passphrase_confirmation", Passphrase)),
            cancellationToken);
        var encrypted = await export.Content.ReadAsByteArrayAsync(cancellationToken);

        Assert.Equal(HttpStatusCode.OK, export.StatusCode);
        Assert.DoesNotContain(SourceGoogleSecret, Encoding.UTF8.GetString(encrypted), StringComparison.Ordinal);
        Assert.True(SettingsBackupCrypto.TryDecryptAndValidate(encrypted, Passphrase, out var payload));
        Assert.NotNull(payload);
        Assert.False(payload.Settings.GoogleLogin.Included);
        Assert.False(payload.Settings.GoogleLogin.Enabled);
        Assert.Null(payload.Settings.GoogleLogin.ClientId);
        Assert.Null(payload.Secrets.GoogleClientSecret);
    }

    [Fact]
    public async Task Export_rejects_noncanonical_acs_without_exposing_operator_secret()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var logs = new CapturingLoggerProvider();
        await using var operatorManaged = await ManagedBackupHarness.CreateAsync(
            "operator-acs",
            SourceAcsSecret,
            googleEnabled: false,
            SourceGoogleClientId,
            googleSecret: null,
            cancellationToken,
            acsSecretCanonical: false,
            loggerProvider: logs);
        using var owner = CreateClient(operatorManaged.Factory);
        await LoginAsync(owner, OwnerUsername, OwnerPassword, cancellationToken);
        var token = await ReadCsrfTokenAsync(owner, cancellationToken);

        using var response = await owner.PostAsync(
            AdminSettingsBackupPage.ExportPath,
            Form(token,
                ("passphrase", Passphrase),
                ("passphrase_confirmation", Passphrase)),
            cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        var auditMaterial = await ReadAuditMaterialAsync(operatorManaged, cancellationToken);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.True(response.Headers.CacheControl?.NoStore);
        Assert.DoesNotContain(SourceAcsSecret, body, StringComparison.Ordinal);
        Assert.DoesNotContain(SourceAcsSecret, auditMaterial, StringComparison.Ordinal);
        Assert.DoesNotContain(SourceAcsSecret, logs.Text, StringComparison.Ordinal);
        Assert.Contains(AdminAuditLog.EventTypes.SettingsBackupExported, auditMaterial, StringComparison.Ordinal);
        Assert.Contains(AdminAuditLog.Results.Failure, auditMaterial, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Export_excludes_noncanonical_managed_google_secret_without_reading_or_exposing_it()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var logs = new CapturingLoggerProvider();
        await using var operatorManaged = await ManagedBackupHarness.CreateAsync(
            "operator-google",
            SourceAcsSecret,
            googleEnabled: true,
            SourceGoogleClientId,
            SourceGoogleSecret,
            cancellationToken,
            googleSecretCanonical: false,
            loggerProvider: logs);
        using var owner = CreateClient(operatorManaged.Factory);
        await LoginAsync(owner, OwnerUsername, OwnerPassword, cancellationToken);
        var token = await ReadCsrfTokenAsync(owner, cancellationToken);

        using var response = await owner.PostAsync(
            AdminSettingsBackupPage.ExportPath,
            Form(token,
                ("passphrase", Passphrase),
                ("passphrase_confirmation", Passphrase)),
            cancellationToken);
        var encrypted = await response.Content.ReadAsByteArrayAsync(cancellationToken);
        var auditMaterial = await ReadAuditMaterialAsync(operatorManaged, cancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.DoesNotContain(SourceGoogleSecret, Encoding.UTF8.GetString(encrypted), StringComparison.Ordinal);
        Assert.True(SettingsBackupCrypto.TryDecryptAndValidate(encrypted, Passphrase, out var payload));
        Assert.NotNull(payload);
        Assert.False(payload.Settings.GoogleLogin.Included);
        Assert.False(payload.Settings.GoogleLogin.Enabled);
        Assert.Null(payload.Settings.GoogleLogin.ClientId);
        Assert.Null(payload.Secrets.GoogleClientSecret);
        Assert.DoesNotContain(SourceGoogleSecret, auditMaterial, StringComparison.Ordinal);
        Assert.DoesNotContain(SourceGoogleSecret, logs.Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Restore_without_google_secret_preserves_current_secret_ref_and_preview_matches()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var source = await ManagedBackupHarness.CreateAsync(
            "source-no-google-secret",
            SourceAcsSecret,
            googleEnabled: false,
            TargetGoogleClientId,
            googleSecret: null,
            cancellationToken);
        var resolvedTargetPath = Path.Combine(
            Path.GetDirectoryName(source.GoogleSecretPath)!,
            "configured-resolved-secret");
        await using var target = await ManagedBackupHarness.CreateAsync(
            "target-custom-google-secret-ref",
            TargetAcsSecret,
            googleEnabled: false,
            TargetGoogleClientId,
            TargetGoogleSecret,
            cancellationToken,
            configuredGoogleSecretPath: resolvedTargetPath);

        using var sourceOwner = CreateClient(source.Factory);
        await LoginAsync(sourceOwner, OwnerUsername, OwnerPassword, cancellationToken);
        var sourceToken = await ReadCsrfTokenAsync(sourceOwner, cancellationToken);
        using var export = await sourceOwner.PostAsync(
            AdminSettingsBackupPage.ExportPath,
            Form(sourceToken,
                ("passphrase", Passphrase),
                ("passphrase_confirmation", Passphrase)),
            cancellationToken);
        var encrypted = await export.Content.ReadAsByteArrayAsync(cancellationToken);
        Assert.Equal(HttpStatusCode.OK, export.StatusCode);
        Assert.True(SettingsBackupCrypto.TryDecryptAndValidate(encrypted, Passphrase, out var exportedPayload));
        Assert.NotNull(exportedPayload);
        Assert.True(exportedPayload.Settings.GoogleLogin.Included);
        Assert.False(exportedPayload.Settings.GoogleLogin.Enabled);
        Assert.Null(exportedPayload.Secrets.GoogleClientSecret);

        using var owner = CreateClient(target.Factory);
        await LoginAsync(owner, OwnerUsername, OwnerPassword, cancellationToken);
        var token = await ReadCsrfTokenAsync(owner, cancellationToken);
        using var previewResponse = await owner.PostAsync(
            AdminSettingsBackupPage.PreviewPath,
            UploadForm(token, encrypted, Passphrase),
            cancellationToken);
        var previewHtml = await previewResponse.Content.ReadAsStringAsync(cancellationToken);
        Assert.Equal(HttpStatusCode.OK, previewResponse.StatusCode);
        Assert.Contains("Google Client Secret</dt><dd>unchanged", previewHtml, StringComparison.Ordinal);
        Assert.Contains("既存のtarget secret/refを保持", previewHtml, StringComparison.Ordinal);
        Assert.DoesNotContain(TargetGoogleSecret, previewHtml, StringComparison.Ordinal);
        Assert.DoesNotContain(target.GoogleSecretPath, previewHtml, StringComparison.Ordinal);
        Assert.DoesNotContain(target.ResolvedGoogleSecretPath, previewHtml, StringComparison.Ordinal);
        var hiddenCiphertext = ReadInputValue(previewHtml, "encrypted_ciphertext");
        token = ReadCsrf(previewHtml);

        using var restore = await owner.PostAsync(
            AdminSettingsBackupPage.RestorePath,
            Form(token,
                ("encrypted_ciphertext", hiddenCiphertext),
                ("passphrase", Passphrase),
                ("confirmation", "restore")),
            cancellationToken);

        Assert.Equal(HttpStatusCode.OK, restore.StatusCode);
        var restoredConfiguration = await target.Instance.GetAsync(cancellationToken);
        Assert.NotNull(restoredConfiguration);
        Assert.Equal(target.GoogleSecretPath, restoredConfiguration.GoogleClientSecretRef);
        Assert.NotEqual(target.ResolvedGoogleSecretPath, restoredConfiguration.GoogleClientSecretRef);
        Assert.True(AdminGoogleSecretStore.TryReadSecret(target.GoogleSecretPath, out var preservedSecret));
        Assert.Equal(TargetGoogleSecret, preservedSecret);
        Assert.False(File.Exists(target.ResolvedGoogleSecretPath));
    }

    private static async Task AssertAuditsContainNoBackupSecretsAsync(
        ManagedBackupHarness source,
        ManagedBackupHarness target,
        CancellationToken cancellationToken)
    {
        var sourceAudits = await source.Factory.Services.GetRequiredService<AdminAuditRepository>()
            .ListRecentAsync(20, cancellationToken);
        Assert.Contains(sourceAudits, audit =>
            audit.EventType == AdminAuditLog.EventTypes.SettingsBackupExported
            && audit.Result == AdminAuditLog.Results.Success);
        Assert.Contains(sourceAudits, audit =>
            audit.EventType == AdminAuditLog.EventTypes.SettingsBackupExported
            && audit.Result == AdminAuditLog.Results.Failure
            && audit.ErrorCode == AdminAuditLog.ErrorCodes.OperationFailed);

        var targetAudits = await target.Factory.Services.GetRequiredService<AdminAuditRepository>()
            .ListRecentAsync(40, cancellationToken);
        Assert.Contains(targetAudits, audit =>
            audit.EventType == AdminAuditLog.EventTypes.SettingsBackupImportPreviewed
            && audit.Result == AdminAuditLog.Results.Success);
        Assert.Contains(targetAudits, audit =>
            audit.EventType == AdminAuditLog.EventTypes.SettingsBackupRestored
            && audit.Result == AdminAuditLog.Results.Success);
        Assert.Contains(targetAudits, audit =>
            audit.EventType == AdminAuditLog.EventTypes.SettingsBackupRestored
            && audit.Result == AdminAuditLog.Results.Failure
            && audit.ErrorCode == AdminAuditLog.ErrorCodes.OperationFailed);

        var allAudits = sourceAudits.Concat(targetAudits);
        var auditMaterial = string.Join('\n', allAudits.SelectMany(audit => new[]
        {
            audit.EventType,
            audit.TargetType,
            audit.TargetId,
            audit.FieldName,
            audit.ErrorCode,
        }));
        foreach (var secret in new[]
        {
            Passphrase,
            SourceAcsSecret,
            TargetAcsSecret,
            SourceGoogleSecret,
            TargetGoogleSecret,
            source.AcsSecretPath,
            source.GoogleSecretPath,
            target.AcsSecretPath,
            target.GoogleSecretPath,
        })
        {
            Assert.DoesNotContain(secret, auditMaterial, StringComparison.Ordinal);
        }
    }

    private static async Task<string> ReadAuditMaterialAsync(
        ManagedBackupHarness harness,
        CancellationToken cancellationToken)
    {
        var audits = await harness.Factory.Services.GetRequiredService<AdminAuditRepository>()
            .ListRecentAsync(50, cancellationToken);
        return string.Join('\n', audits.SelectMany(audit => new[]
        {
            audit.EventType,
            audit.Result,
            audit.TargetType,
            audit.TargetId,
            audit.FieldName,
            audit.ErrorCode,
        }));
    }

    private static async Task<(string PasswordHash, bool IsInstanceOwner, bool IsBreakGlass)> ReadOwnerCredentialsAsync(
        ManagedBackupHarness harness,
        CancellationToken cancellationToken)
    {
        await using var connection = await harness.Connections.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT password_hash, is_instance_owner, is_break_glass
            FROM admin_users
            WHERE username = @Username;
            """;
        command.Parameters.AddWithValue("@Username", OwnerUsername);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        Assert.True(await reader.ReadAsync(cancellationToken));
        return (reader.GetString(0), reader.GetInt32(1) == 1, reader.GetInt32(2) == 1);
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
        using var response = await client.GetAsync(AdminSettingsBackupPage.PagePath, cancellationToken);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return ReadCsrf(await response.Content.ReadAsStringAsync(cancellationToken));
    }

    private static string ReadCsrf(string html)
    {
        var match = Regex.Match(
            html,
            "name=\"__RequestVerificationToken\" value=\"([^\"]+)\"",
            RegexOptions.CultureInvariant);
        Assert.True(match.Success, "The Admin page did not include an antiforgery token.");
        return match.Groups[1].Value;
    }

    private static string ReadInputValue(string html, string name)
    {
        var match = Regex.Match(
            html,
            $"name=\"{Regex.Escape(name)}\" value=\"([^\"]*)\"",
            RegexOptions.CultureInvariant);
        Assert.True(match.Success, $"The {name} field was not present.");
        return match.Groups[1].Value;
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

    private static MultipartFormDataContent UploadForm(
        string? csrfToken,
        byte[] encrypted,
        string passphrase)
    {
        var form = new MultipartFormDataContent();
        if (csrfToken is not null)
            form.Add(new StringContent(csrfToken), "__RequestVerificationToken");
        form.Add(new StringContent(passphrase), "passphrase");
        var file = new ByteArrayContent(encrypted);
        file.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        form.Add(file, "encrypted_file", "settings.json.age");
        return form;
    }

    private sealed class ManagedBackupHarness : IAsyncDisposable
    {
        private readonly string _root;
        private WebApplicationFactory<global::Program>? _factory;

        private ManagedBackupHarness(
            string root,
            string connectionString,
            string tenantConfigPath,
            SqliteConnectionFactory connections,
            InstanceConfigurationRepository instance,
            AdminUserRepository users,
            SenderRepository senders,
            AdminGoogleIdentityRepository googleIdentities,
            SenderIdentity sharedSender,
            bool useManagedGoogleAuthority,
            string resolvedGoogleSecretPath,
            WebApplicationFactory<global::Program> factory)
        {
            _root = root;
            ConnectionString = connectionString;
            TenantConfigPath = tenantConfigPath;
            Connections = connections;
            Instance = instance;
            Users = users;
            Senders = senders;
            GoogleIdentities = googleIdentities;
            SharedSender = sharedSender;
            UseManagedGoogleAuthority = useManagedGoogleAuthority;
            _factory = factory;
            AcsSecretPath = Path.Combine(root, "secrets", "acs", "acs_connection_string");
            GoogleSecretPath = Path.Combine(root, "secrets", "admin_google", "client_secret");
            ResolvedGoogleSecretPath = resolvedGoogleSecretPath;
        }

        public string ConnectionString { get; }

        public string TenantConfigPath { get; }

        public string AcsSecretPath { get; }

        public string GoogleSecretPath { get; }

        public string ResolvedGoogleSecretPath { get; }

        public SqliteConnectionFactory Connections { get; }

        public InstanceConfigurationRepository Instance { get; }

        public AdminUserRepository Users { get; }

        public SenderRepository Senders { get; }

        public AdminGoogleIdentityRepository GoogleIdentities { get; }

        public SenderIdentity SharedSender { get; }

        public bool UseManagedGoogleAuthority { get; }

        public WebApplicationFactory<global::Program> Factory => _factory!;

        public static async Task<ManagedBackupHarness> CreateAsync(
            string label,
            string acsSecret,
            bool googleEnabled,
            string googleClientId,
            string? googleSecret,
            CancellationToken cancellationToken,
            bool useManagedGoogleAuthority = true,
            bool acsSecretCanonical = true,
            bool googleSecretCanonical = true,
            string? configuredGoogleSecretPath = null,
            ILoggerProvider? loggerProvider = null)
        {
            var root = Path.Combine(
                Path.GetTempPath(),
                "amane-mailer-settings-backup",
                $"{label}-{Guid.NewGuid():N}");
            Directory.CreateDirectory(root);
            var connectionString = $"Data Source={Path.Combine(root, "mailer.db")}";
            var tenantConfigPath = Path.Combine(root, "tenants.json");
            await File.WriteAllTextAsync(
                tenantConfigPath,
                MailerAdminFixtureHelpers.TenantConfigJson,
                cancellationToken);
            var acsSecretPath = Path.Combine(root, "secrets", "acs", "acs_connection_string");
            var googleSecretPath = Path.Combine(root, "secrets", "admin_google", "client_secret");
            var resolvedGoogleSecretPath = configuredGoogleSecretPath ?? googleSecretPath;

            var configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["ConnectionStrings:Mailer"] = connectionString,
                })
                .Build();
            var connections = new SqliteConnectionFactory(configuration);
            await new SqlMigrationRunner(connections).ApplyPendingAsync(cancellationToken);
            Assert.True(FirstRunSetupStorage.WriteAcsSecretCreateOnly(acsSecretPath, acsSecret));

            var instance = new InstanceConfigurationRepository(connections, TimeProvider.System);
            Assert.True(await instance.ConfigureAcsAsync(acsSecretPath, cancellationToken));
            var users = new AdminUserRepository(connections, TimeProvider.System);
            Assert.True(await users.EnsureInstanceOwnerAsync(
                OwnerUsername,
                AdminPasswordHasher.Hash(OwnerPassword),
                cancellationToken));
            var senders = new SenderRepository(connections, TimeProvider.System);
            var sharedSender = await senders.CreateAsync(
                "shared@example.com",
                label == "source" ? "Source shared" : "Target shared",
                cancellationToken);
            Assert.True(await instance.FinalizeAsync(cancellationToken));
            if (label == "target")
                await senders.DisableAsync(sharedSender.SenderId, cancellationToken);

            await users.CreateOrUpdateScopedUserAsync(
                ScopedUsername,
                AdminPasswordHasher.Hash(ScopedPassword),
                [sharedSender.SenderId],
                cancellationToken);
            await users.CreateBreakGlassUserAsync(
                BreakGlassUsername,
                AdminPasswordHasher.Hash(BreakGlassPassword),
                cancellationToken);

            if (useManagedGoogleAuthority)
            {
                if (googleSecret is not null)
                    AdminGoogleSecretStore.WriteSecret(googleSecretPath, googleSecret);
                Assert.True(await instance.SetGoogleLoginSettingsAsync(
                    googleEnabled,
                    googleClientId,
                    googleSecretPath,
                    cancellationToken));
            }
            Assert.True(await instance.SetLiveSendingAsync(true, cancellationToken));

            var extras = new Dictionary<string, string?>(StringComparer.Ordinal)
            {
                ["AMANE_ADMIN_ENABLED"] = "false",
                ["AMANE_ADMIN_USERNAME"] = "legacy-admin",
                [AdminGoogleSecretStore.SecretPathEnvKey] = resolvedGoogleSecretPath,
            };
            if (!useManagedGoogleAuthority)
            {
                extras[AdminGoogleOptions.ClientIdKey] = googleClientId;
                extras[AdminGoogleOptions.ClientSecretKey] = googleSecret;
            }
            var factory = MailerAdminFixtureHelpers.CreateFactory(
                connectionString,
                tenantConfigPath,
                AdminPasswordHasher.Hash("legacy-password"),
                extras,
                useEarlyInstanceProbe: true,
                backupCanonicalSecretPaths: new AdminSettingsBackupCanonicalSecretPaths(
                    acsSecretCanonical ? acsSecretPath : Path.Combine(root, "operator-acs-canonical"),
                    googleSecretCanonical ? googleSecretPath : Path.Combine(root, "operator-google-canonical")),
                loggerProvider: loggerProvider);

            return new ManagedBackupHarness(
                root,
                connectionString,
                tenantConfigPath,
                connections,
                instance,
                users,
                senders,
                new AdminGoogleIdentityRepository(connections),
                sharedSender,
                useManagedGoogleAuthority,
                resolvedGoogleSecretPath,
                factory);
        }

        public async ValueTask DisposeAsync()
        {
            if (_factory is not null)
                await _factory.DisposeAsync();
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(_root))
                Directory.Delete(_root, recursive: true);
        }
    }

    private sealed class CapturingLoggerProvider : ILoggerProvider
    {
        private readonly ConcurrentQueue<string> _entries = new();

        public string Text => string.Join(Environment.NewLine, _entries);

        public ILogger CreateLogger(string categoryName) => new CapturingLogger(this, categoryName);

        public void Dispose()
        {
        }

        private sealed class CapturingLogger(
            CapturingLoggerProvider owner,
            string categoryName) : ILogger
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
                var message = formatter(state, exception);
                owner._entries.Enqueue(exception is null
                    ? $"{categoryName}: {message}"
                    : $"{categoryName}: {message}{Environment.NewLine}{exception}");
            }
        }
    }
}
