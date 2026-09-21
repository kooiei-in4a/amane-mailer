using System.Net;
using Amane.Mailer.Admin;
using Amane.Mailer.Configuration;
using Amane.Mailer.Data.Sqlite;
using Amane.Mailer.Data.Sqlite.Models;
using Amane.Mailer.Identity;
using Amane.Mailer.Operations;
using Amane.Mailer.Setup;
using Amane.Mailer.Tests;
using Amane.Mailer.Tests.Fixtures;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Amane.Mailer.Tests.Admin;

public sealed class AdminOverviewPageTests
{
    private const string OwnerUsername = "overview-owner";
    private const string OwnerPassword = "overview-owner-password";
    private const string ScopedUsername = "overview-scoped";
    private const string ScopedPassword = "overview-scoped-password";
    private const string BreakGlassUsername = "overview-break-glass";
    private const string BreakGlassPassword = "overview-break-glass-password";
    private const string AcsSecret = "Endpoint=https://example.communication.azure.com/;AccessKey=overview-acs-secret-canary";
    private const string RotatedAcsSecret = "Endpoint=https://example.communication.azure.com/;AccessKey=overview-acs-rotated-canary";
    private const string SenderEmail = "overview-sender-email-canary@example.com";
    private const string ClientId = "overview-client-id-canary.apps.googleusercontent.com";

    [Fact]
    public async Task Overview_is_owner_only_read_only_uncached_and_keeps_pii_out_of_html()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var harness = await OverviewHarness.CreateAsync(ct);
        using var owner = CreateClient(harness.Factory);
        await LoginAsync(owner, OwnerUsername, OwnerPassword, ct);
        var startupMailerOptions = harness.Factory.Services.GetRequiredService<MailerOptions>();
        var instanceConfiguration = await harness.Factory.Services
            .GetRequiredService<InstanceConfigurationRepository>()
            .GetAsync(ct);
        Assert.Equal(
            AdminAcsRuntimeState.Applied,
            AdminAcsRuntimeStatusReader.Evaluate(instanceConfiguration, startupMailerOptions.AcsConnectionString).RuntimeState);

        var auditBefore = await harness.Factory.Services.GetRequiredService<AdminAuditRepository>()
            .ListRecentAsync(20, ct);
        using var response = await owner.GetAsync(AdminOverviewPage.PagePath, ct);
        var html = await response.Content.ReadAsStringAsync(ct);
        var decodedHtml = WebUtility.HtmlDecode(html);
        var auditAfter = await harness.Factory.Services.GetRequiredService<AdminAuditRepository>()
            .ListRecentAsync(20, ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(response.Headers.CacheControl?.NoStore);
        Assert.Equal("no-cache", response.Headers.Pragma.ToString());
        Assert.Contains("システム状態", html, StringComparison.Ordinal);
        Assert.Contains("Ready", html, StringComparison.Ordinal);
        Assert.Contains("反映済み", decodedHtml, StringComparison.Ordinal);
        Assert.Contains("Live Sending", decodedHtml, StringComparison.Ordinal);
        Assert.Contains("OFF", decodedHtml, StringComparison.Ordinal);
        Assert.Contains("<span class=\"admin-overview-status admin-overview-status--info\">情報</span> OFF", decodedHtml, StringComparison.Ordinal);
        Assert.Contains("Restore verification", decodedHtml, StringComparison.Ordinal);
        Assert.Contains("未記録", decodedHtml, StringComparison.Ordinal);
        Assert.DoesNotContain("Restore readiness: Ready", decodedHtml, StringComparison.Ordinal);
        Assert.Contains("href=\"/admin/ops\"", decodedHtml, StringComparison.Ordinal);
        Assert.Contains("href=\"/admin/setup-status\"", decodedHtml, StringComparison.Ordinal);
        Assert.Contains("href=\"/admin/secrets\"", decodedHtml, StringComparison.Ordinal);
        Assert.Contains("href=\"/admin/auth-settings\"", decodedHtml, StringComparison.Ordinal);
        Assert.Contains(
            $"href=\"{AdminDiagnosticReportPage.PagePath}\"",
            decodedHtml,
            StringComparison.Ordinal);
        Assert.DoesNotContain("<form", decodedHtml, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("<button", decodedHtml, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("method=\"post\"", decodedHtml, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(AcsSecret, decodedHtml, StringComparison.Ordinal);
        Assert.DoesNotContain(RotatedAcsSecret, decodedHtml, StringComparison.Ordinal);
        Assert.DoesNotContain(harness.AcsSecretPath, decodedHtml, StringComparison.Ordinal);
        Assert.DoesNotContain(SenderEmail, decodedHtml, StringComparison.Ordinal);
        Assert.DoesNotContain(ClientId, decodedHtml, StringComparison.Ordinal);
        Assert.DoesNotContain("overview-recipient-canary@example.com", decodedHtml, StringComparison.Ordinal);
        Assert.DoesNotContain("overview-subject-canary", decodedHtml, StringComparison.Ordinal);
        Assert.DoesNotContain("overview-body-canary", decodedHtml, StringComparison.Ordinal);
        Assert.Equal(auditBefore.Select(static item => item.EventType), auditAfter.Select(static item => item.EventType));

        var diagnosticAuditBefore = auditAfter;
        using var diagnostic = await owner.GetAsync(AdminDiagnosticReportPage.PagePath, ct);
        var report = await diagnostic.Content.ReadAsStringAsync(ct);
        var diagnosticAuditAfter = await harness.Factory.Services.GetRequiredService<AdminAuditRepository>()
            .ListRecentAsync(20, ct);

        Assert.Equal(HttpStatusCode.OK, diagnostic.StatusCode);
        Assert.Equal("text/plain", diagnostic.Content.Headers.ContentType?.MediaType);
        Assert.Equal("utf-8", diagnostic.Content.Headers.ContentType?.CharSet);
        Assert.True(diagnostic.Headers.CacheControl?.NoStore);
        Assert.Equal("no-cache", diagnostic.Headers.Pragma.ToString());
        Assert.StartsWith(
            "Amane Mailer sanitized diagnostic report\nformat_version: 1\n\n[application]\n",
            report,
            StringComparison.Ordinal);
        foreach (var requiredKey in new[]
        {
            "mailer_version:",
            "build_identity:",
            "health:",
            "readiness:",
            "schema_classification:",
            "current_applied_migration:",
            "migration_action:",
            "setup:",
            "configuration_authority:",
            "provider:",
            "provider_credential:",
            "live_sending:",
            "acs_secret_configuration:",
            "acs_runtime:",
            "google_login_saved_state:",
            "google_login_runtime_state:",
            "google_login_reflection_state:",
            "google_login_authority:",
            "restart_required:",
            "full_instance_latest_outcome:",
            "full_instance_last_success_utc:",
            "full_instance_freshness:",
            "latest_offsite_result:",
            "db_only_last_success:",
            "restore_verification:",
        })
        {
            Assert.Contains(requiredKey, report, StringComparison.Ordinal);
        }

        Assert.DoesNotContain(AcsSecret, report, StringComparison.Ordinal);
        Assert.DoesNotContain(RotatedAcsSecret, report, StringComparison.Ordinal);
        Assert.DoesNotContain(harness.AcsSecretPath, report, StringComparison.Ordinal);
        Assert.DoesNotContain(SenderEmail, report, StringComparison.Ordinal);
        Assert.DoesNotContain(ClientId, report, StringComparison.Ordinal);
        Assert.Equal(
            diagnosticAuditBefore.Select(static item => item.EventType),
            diagnosticAuditAfter.Select(static item => item.EventType));

        using var scoped = CreateClient(harness.Factory);
        await LoginAsync(scoped, ScopedUsername, ScopedPassword, ct);
        using (var denied = await scoped.GetAsync(AdminOverviewPage.PagePath, ct))
            Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
        using (var denied = await scoped.GetAsync(AdminDiagnosticReportPage.PagePath, ct))
            Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
        using (var home = await scoped.GetAsync("/admin/mail-requests", ct))
        {
            Assert.Equal(HttpStatusCode.OK, home.StatusCode);
            Assert.DoesNotContain($"href=\"{AdminOverviewPage.PagePath}\"", await home.Content.ReadAsStringAsync(ct), StringComparison.Ordinal);
        }

        using var breakGlass = CreateClient(harness.Factory);
        await LoginAsync(breakGlass, BreakGlassUsername, BreakGlassPassword, ct);
        using (var denied = await breakGlass.GetAsync(AdminOverviewPage.PagePath, ct))
            Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
        using (var denied = await breakGlass.GetAsync(AdminDiagnosticReportPage.PagePath, ct))
            Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);

        using var unauthenticated = CreateClient(harness.Factory);
        using (var redirect = await unauthenticated.GetAsync(AdminOverviewPage.PagePath, ct))
        {
            Assert.Equal(HttpStatusCode.Redirect, redirect.StatusCode);
            Assert.StartsWith("/admin/login", redirect.Headers.Location?.PathAndQuery, StringComparison.Ordinal);
        }
        using (var redirect = await unauthenticated.GetAsync(AdminDiagnosticReportPage.PagePath, ct))
        {
            Assert.Equal(HttpStatusCode.Redirect, redirect.StatusCode);
            Assert.StartsWith("/admin/login", redirect.Headers.Location?.PathAndQuery, StringComparison.Ordinal);
        }

        using var adminRoot = await owner.GetAsync("/admin", ct);
        Assert.Equal(HttpStatusCode.Redirect, adminRoot.StatusCode);
        Assert.Equal("/admin/mail-requests", adminRoot.Headers.Location?.ToString());
        using var ownerHome = await owner.GetAsync("/admin/mail-requests", ct);
        Assert.Contains(
            $"href=\"{AdminOverviewPage.PagePath}\"",
            await ownerHome.Content.ReadAsStringAsync(ct),
            StringComparison.Ordinal);
    }

    [Fact]
    public void Diagnostic_report_uses_a_fixed_allowlist_and_deterministic_lf_output()
    {
        const string futureCanaries =
            "SECRET-CANARY API-KEY-CANARY recipient-canary@example.invalid "
            + "sender-canary@example.invalid subject-canary body-canary "
            + "raw-provider-error-canary /private/secret/path "
            + "google-client-id-canary.apps.googleusercontent.com";
        var presentation = new AdminOverviewPresentation(
        [
            new("Backup",
            [
                ReportItem("Restore verification", "restore-value"),
                ReportItem("DB-only last success", "db-only-value"),
                ReportItem("Latest offsite result", "offsite-value"),
                ReportItem("Full-instance freshness", "freshness-value"),
                ReportItem("Full-instance last success UTC", "last-success-value"),
                ReportItem("Full-instance latest outcome", "outcome-value"),
            ]),
            new("Setup / Provider",
            [
                ReportItem("Restart required", "restart-value"),
                ReportItem("Google Login authority", "google-authority-value"),
                ReportItem("Google Login reflection state", "google-reflection-value"),
                ReportItem("Google Login runtime state", "google-runtime-value"),
                ReportItem("Google Login saved state", "google-saved-value"),
                ReportItem("ACS runtime", "acs-runtime-value"),
                ReportItem("ACS secret configuration", "acs-config-value"),
                ReportItem("Live Sending", "live-sending-value"),
                ReportItem("Provider credential", "credential-value"),
                ReportItem("Provider", "provider-value"),
                ReportItem("Configuration authority", "authority-value"),
                ReportItem("Setup", "setup-value"),
            ]),
            new("Database",
            [
                ReportItem("Migration action", "migration-action-value"),
                ReportItem("Current applied migration", "migration-value"),
                ReportItem("Schema classification", "schema-value"),
            ]),
            new("Application",
            [
                ReportItem("Future unsafe field", futureCanaries),
                ReportItem("Readiness", "readiness-value"),
                ReportItem("Health", "health-value"),
                ReportItem("Build identity", "build-value"),
                ReportItem("Mailer version", "version-value"),
            ]),
        ]);

        const string expected =
            "Amane Mailer sanitized diagnostic report\n"
            + "format_version: 1\n\n"
            + "[application]\n"
            + "mailer_version: version-value\n"
            + "build_identity: build-value\n"
            + "health: health-value\n"
            + "readiness: readiness-value\n\n"
            + "[database]\n"
            + "schema_classification: schema-value\n"
            + "current_applied_migration: migration-value\n"
            + "migration_action: migration-action-value\n\n"
            + "[setup_provider]\n"
            + "setup: setup-value\n"
            + "configuration_authority: authority-value\n"
            + "provider: provider-value\n"
            + "provider_credential: credential-value\n"
            + "live_sending: live-sending-value\n"
            + "acs_secret_configuration: acs-config-value\n"
            + "acs_runtime: acs-runtime-value\n"
            + "google_login_saved_state: google-saved-value\n"
            + "google_login_runtime_state: google-runtime-value\n"
            + "google_login_reflection_state: google-reflection-value\n"
            + "google_login_authority: google-authority-value\n"
            + "restart_required: restart-value\n\n"
            + "[backup]\n"
            + "full_instance_latest_outcome: outcome-value\n"
            + "full_instance_last_success_utc: last-success-value\n"
            + "full_instance_freshness: freshness-value\n"
            + "latest_offsite_result: offsite-value\n"
            + "db_only_last_success: db-only-value\n"
            + "restore_verification: restore-value\n";

        var report = AdminDiagnosticReportPage.RenderReport(presentation);

        Assert.Equal(expected, report);
        Assert.Equal(report, AdminDiagnosticReportPage.RenderReport(presentation));
        Assert.DoesNotContain("\r", report, StringComparison.Ordinal);
        Assert.DoesNotContain("Future unsafe field", report, StringComparison.Ordinal);
        foreach (var canary in futureCanaries.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            Assert.DoesNotContain(canary, report, StringComparison.Ordinal);
        }

        var emptyReport = AdminDiagnosticReportPage.RenderReport(new AdminOverviewPresentation([]));
        Assert.Contains("mailer_version: 取得不可\n", emptyReport, StringComparison.Ordinal);
    }

    private static AdminOverviewStatusItem ReportItem(string label, string value) =>
        new(label, new(value, AdminOverviewStatusKind.Info));

    [Fact]
    public async Task Overview_shares_acs_restart_state_and_readyz_provider_secret_behavior()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var harness = await OverviewHarness.CreateAsync(ct);
        using var owner = CreateClient(harness.Factory);
        await LoginAsync(owner, OwnerUsername, OwnerPassword, ct);

        Assert.True(FirstRunSetupStorage.TryReplaceAcsSecret(harness.AcsSecretPath, RotatedAcsSecret));
        using (var pending = await owner.GetAsync(AdminOverviewPage.PagePath, ct))
        {
            var html = WebUtility.HtmlDecode(await pending.Content.ReadAsStringAsync(ct));
            Assert.Equal(HttpStatusCode.OK, pending.StatusCode);
            Assert.Contains("再起動待ち", html, StringComparison.Ordinal);
            Assert.Contains("Required", html, StringComparison.Ordinal);
            Assert.DoesNotContain(RotatedAcsSecret, html, StringComparison.Ordinal);
        }

        Assert.True(FirstRunSetupStorage.TryReplaceAcsSecret(harness.AcsSecretPath, AcsSecret));
        File.Delete(harness.AcsSecretPath);
        Assert.False(File.Exists(harness.AcsSecretPath));

        using (var readyz = await owner.GetAsync("/readyz", ct))
        {
            var body = await readyz.Content.ReadAsStringAsync(ct);
            Assert.Equal(HttpStatusCode.ServiceUnavailable, readyz.StatusCode);
            Assert.Contains(MailerReadinessReasons.ProviderSecretMissing, body, StringComparison.Ordinal);
        }

        using var unavailable = await owner.GetAsync(AdminOverviewPage.PagePath, ct);
        var unavailableHtml = WebUtility.HtmlDecode(await unavailable.Content.ReadAsStringAsync(ct));
        Assert.Equal(HttpStatusCode.OK, unavailable.StatusCode);
        Assert.Contains("Not Ready", unavailableHtml, StringComparison.Ordinal);
        Assert.Contains("取得不可", unavailableHtml, StringComparison.Ordinal);
        Assert.DoesNotContain(AcsSecret, unavailableHtml, StringComparison.Ordinal);
        Assert.DoesNotContain(RotatedAcsSecret, unavailableHtml, StringComparison.Ordinal);
        Assert.DoesNotContain(harness.AcsSecretPath, unavailableHtml, StringComparison.Ordinal);

        using var unavailableReportResponse = await owner.GetAsync(AdminDiagnosticReportPage.PagePath, ct);
        var unavailableReport = await unavailableReportResponse.Content.ReadAsStringAsync(ct);
        Assert.Equal(HttpStatusCode.OK, unavailableReportResponse.StatusCode);
        Assert.Contains("readiness: Not Ready\n", unavailableReport, StringComparison.Ordinal);
        Assert.DoesNotContain(MailerReadinessReasons.ProviderSecretMissing, unavailableReport, StringComparison.Ordinal);
        Assert.DoesNotContain(AcsSecret, unavailableReport, StringComparison.Ordinal);
        Assert.DoesNotContain(harness.AcsSecretPath, unavailableReport, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(SetupSchemaClassification.Current, "Current", "Ok", "不要", "Ok")]
    [InlineData(SetupSchemaClassification.Behind, "Behind", "ActionRequired", "必要", "ActionRequired")]
    [InlineData(SetupSchemaClassification.DatabaseAbsent, "DatabaseAbsent", "ActionRequired", "必要", "ActionRequired")]
    [InlineData(SetupSchemaClassification.AheadOrUnsupported, "AheadOrUnsupported", "ActionRequired", "安全な自動前方migrationは不可", "ActionRequired")]
    [InlineData(SetupSchemaClassification.Unknown, "取得不可", "Unavailable", "取得不可", "Unavailable")]
    public void Schema_mapping_preserves_classification_and_migration_action(
        string classification,
        string expectedClassification,
        string expectedClassificationKind,
        string expectedAction,
        string expectedActionKind)
    {
        var schema = AdminOverviewPage.MapSchemaClassification(classification);
        var action = AdminOverviewPage.MapMigrationAction(classification);

        Assert.Equal(expectedClassification, schema.Value);
        Assert.Equal(expectedClassificationKind, schema.Status.ToString());
        Assert.Equal(expectedAction, action.Value);
        Assert.Equal(expectedActionKind, action.Status.ToString());
    }

    [Fact]
    public void Readiness_and_live_sending_mapping_do_not_expose_failure_reason_or_warn_on_off()
    {
        var ready = AdminOverviewPage.MapReadiness(MailerReadinessResult.Ready());
        var notReady = AdminOverviewPage.MapReadiness(
            MailerReadinessResult.NotReady("raw-provider-exception-canary"));
        var liveSendingOff = AdminOverviewPage.MapLiveSending(false);

        Assert.Equal("Ready", ready.Value);
        Assert.Equal(AdminOverviewStatusKind.Ok, ready.Status);
        Assert.Equal("Not Ready", notReady.Value);
        Assert.Equal(AdminOverviewStatusKind.ActionRequired, notReady.Status);
        Assert.DoesNotContain("raw-provider-exception-canary", notReady.Value, StringComparison.Ordinal);
        Assert.Equal("OFF", liveSendingOff.Value);
        Assert.Equal(AdminOverviewStatusKind.Info, liveSendingOff.Status);
    }

    [Fact]
    public void Acs_status_is_sanitized_and_distinguishes_applied_pending_and_unavailable()
    {
        var applied = AdminAcsRuntimeStatusReader.Classify(AcsSecret, AcsSecret);
        var pending = AdminAcsRuntimeStatusReader.Classify(AcsSecret, RotatedAcsSecret);
        var unavailable = AdminAcsRuntimeStatusReader.Classify(string.Empty, RotatedAcsSecret);
        var pendingOverview = AdminOverviewPage.MapAcsRuntime(
            isAcsProvider: true,
            new AdminAcsRuntimeStatus(true, pending));

        Assert.Equal(AdminAcsRuntimeState.Applied, applied);
        Assert.Equal(AdminAcsRuntimeState.RestartPending, pending);
        Assert.Equal(AdminAcsRuntimeState.Unavailable, unavailable);
        Assert.Equal("再起動待ち", pendingOverview.Value);
        Assert.Equal(AdminOverviewStatusKind.Attention, pendingOverview.Status);
        Assert.DoesNotContain(AcsSecret, pending.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(RotatedAcsSecret, pending.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void Google_state_mapping_covers_applied_restart_pending_incomplete_and_disabled()
    {
        var configuration = new ConfigurationBuilder().Build();
        var disabledRow = GoogleRow(enabled: false, clientId: ClientId, secretRef: null);
        var disabledRuntime = AdminGoogleOptions.Load(configuration, ToRuntimeState(disabledRow));
        var disabled = AdminGoogleSettingsStatus.Evaluate(disabledRow, disabledRuntime, configuration);
        var disabledDisplay = AdminOverviewPage.MapGoogleSavedState(disabled);

        Assert.Equal(AdminGoogleSettingsStatus.DisplayOff, disabledDisplay.Value);
        Assert.Equal(AdminOverviewStatusKind.Info, disabledDisplay.Status);

        var incompleteRow = GoogleRow(enabled: true, clientId: ClientId, secretRef: null);
        var incompleteRuntime = AdminGoogleOptions.Load(configuration, ToRuntimeState(incompleteRow));
        var incomplete = AdminGoogleSettingsStatus.Evaluate(incompleteRow, incompleteRuntime, configuration);
        var incompleteDisplay = AdminOverviewPage.MapGoogleSavedState(incomplete);
        Assert.Equal(AdminGoogleSettingsStatus.DisplayOnIncomplete, incompleteDisplay.Value);
        Assert.Equal(AdminOverviewStatusKind.ActionRequired, incompleteDisplay.Status);

        using var root = new TemporaryDirectory();
        var secretPath = Path.Combine(root.Path, "client_secret");
        AdminGoogleSecretStore.WriteSecret(secretPath, "overview-google-secret-canary");
        var appliedRow = GoogleRow(enabled: true, clientId: ClientId, secretRef: secretPath);
        var appliedRuntime = AdminGoogleOptions.Load(configuration, ToRuntimeState(appliedRow));
        var applied = AdminGoogleSettingsStatus.Evaluate(appliedRow, appliedRuntime, configuration);
        var appliedDisplay = AdminOverviewPage.MapGoogleSavedState(applied);
        Assert.Equal(AdminOverviewStatusKind.Ok, appliedDisplay.Status);

        var restartRuntime = AdminGoogleOptions.Load(
            configuration,
            ToRuntimeState(GoogleRow(enabled: false, clientId: ClientId, secretRef: secretPath)));
        var restartPending = AdminGoogleSettingsStatus.Evaluate(appliedRow, restartRuntime, configuration);
        var restartDisplay = AdminOverviewPage.MapGoogleSavedState(restartPending);
        Assert.Equal(AdminOverviewStatusKind.Attention, restartDisplay.Status);
        Assert.Equal("Required", AdminOverviewPage.MapRestartRequired(
            new AdminAcsRuntimeStatus(true, AdminAcsRuntimeState.Applied),
            restartPending).Value);
    }

    [Fact]
    public void Backup_mapping_covers_fresh_stale_failed_invalid_offsite_and_restore_evidence()
    {
        var now = DateTimeOffset.UtcNow;
        var fresh = BackupStatus(
            latestOutcome: AdminBackupOutcome.Succeeded,
            freshnessPolicyConfigured: true,
            freshness: AdminBackupFreshness.Fresh,
            lastSuccessAtUtc: now);
        var stale = fresh with { Freshness = AdminBackupFreshness.Stale };
        var failed = fresh with { LatestOutcome = AdminBackupOutcome.Failed };
        var invalid = fresh with { SuccessEvidenceState = AdminBackupEvidenceState.Invalid };
        var noPolicy = fresh with { FreshnessPolicyConfigured = false, Freshness = AdminBackupFreshness.Unknown };

        Assert.Equal(AdminOverviewStatusKind.Ok, AdminOverviewPage.MapBackupFreshness(fresh).Status);
        Assert.Equal(AdminOverviewStatusKind.Attention, AdminOverviewPage.MapBackupFreshness(stale).Status);
        Assert.Equal(AdminOverviewStatusKind.ActionRequired, AdminOverviewPage.MapBackupFreshness(failed).Status);
        Assert.Equal(AdminOverviewStatusKind.Unavailable, AdminOverviewPage.MapBackupFreshness(invalid).Status);
        Assert.Equal(AdminOverviewStatusKind.Info, AdminOverviewPage.MapBackupFreshness(noPolicy).Status);
        Assert.Equal(AdminOverviewStatusKind.ActionRequired, AdminOverviewPage.MapBackupOutcome(failed).Status);
        Assert.Equal(AdminOverviewStatusKind.ActionRequired, AdminOverviewPage.MapOffsiteResult(
            fresh with { LatestAttemptOffsiteState = AdminBackupOffsiteState.Failed }).Status);

        var readModel = new AdminBackupStatusReadModel(
            now,
            new AdminOnlineDatabaseBackupStatus(
                AdminBackupEvidenceState.NotRecorded,
                AdminBackupOutcome.Unknown,
                null),
            BackupStatus(),
            fresh,
            RestoreVerificationRecorded: false);
        var restore = AdminOverviewPage.MapRestoreVerification(readModel);
        Assert.Equal("未記録", restore.Value);
        Assert.Equal(AdminOverviewStatusKind.Info, restore.Status);
        Assert.NotEqual("Ready", restore.Value);
    }

    private static AdminBackupScriptStatus BackupStatus(
        AdminBackupOutcome latestOutcome = AdminBackupOutcome.Unknown,
        bool freshnessPolicyConfigured = false,
        AdminBackupFreshness freshness = AdminBackupFreshness.Unknown,
        DateTimeOffset? lastSuccessAtUtc = null) =>
        new(
            AdminBackupEvidenceState.Available,
            AdminBackupEvidenceState.Available,
            latestOutcome,
            null,
            null,
            lastSuccessAtUtc,
            null,
            AdminBackupOffsiteState.Succeeded,
            null,
            freshnessPolicyConfigured,
            freshness);

    private static InstanceConfigurationRow GoogleRow(bool enabled, string? clientId, string? secretRef) =>
        new(
            InitializedAt: "2026-09-21T00:00:00Z",
            LiveSending: false,
            ProviderType: "acs",
            ProviderSecretRef: "/unused/acs/path",
            ProviderConfiguredAt: "2026-09-21T00:00:00Z",
            GoogleLoginEnabled: enabled,
            GoogleClientId: clientId,
            GoogleClientSecretRef: secretRef,
            GoogleConfiguredAt: "2026-09-21T00:00:00Z");

    private static InstanceRuntimeState ToRuntimeState(InstanceConfigurationRow row) =>
        new(
            InstanceRuntimeStateKind.Initialized,
            row.InitializedAt,
            row.LiveSending,
            row.ProviderType,
            row.ProviderSecretRef,
            row.ProviderConfiguredAt,
            HasInstanceOwner: true,
            row.GoogleLoginEnabled,
            row.GoogleClientId,
            row.GoogleClientSecretRef,
            row.GoogleConfiguredAt);

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
        var html = await loginPage.Content.ReadAsStringAsync(cancellationToken);
        const string marker = "name=\"__RequestVerificationToken\" value=\"";
        var start = html.IndexOf(marker, StringComparison.Ordinal);
        Assert.True(start >= 0, "No CSRF token in /admin/login.");
        start += marker.Length;
        var end = html.IndexOf('"', start);
        Assert.True(end > start);
        using var login = await client.PostAsync(
            "/admin/api/login",
            new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["__RequestVerificationToken"] = html[start..end],
                ["username"] = username,
                ["password"] = password,
            }),
            cancellationToken);
        Assert.Equal(HttpStatusCode.Redirect, login.StatusCode);
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        private readonly DirectoryInfo _directory = Directory.CreateTempSubdirectory("amane-admin-overview-");

        public string Path => _directory.FullName;

        public void Dispose() => _directory.Delete(recursive: true);
    }

    private sealed class OverviewHarness : IAsyncDisposable
    {
        private readonly string _root;

        private OverviewHarness(
            string root,
            string acsSecretPath,
            WebApplicationFactory<global::Program> factory)
        {
            _root = root;
            AcsSecretPath = acsSecretPath;
            Factory = factory;
        }

        public string AcsSecretPath { get; }

        public WebApplicationFactory<global::Program> Factory { get; }

        public static async Task<OverviewHarness> CreateAsync(CancellationToken cancellationToken)
        {
            var root = Path.Combine(Path.GetTempPath(), "amane-mailer-admin-overview", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            var databasePath = Path.Combine(root, "mailer.db");
            var connectionString = $"Data Source={databasePath}";
            var tenantConfigPath = Path.Combine(root, "tenants.json");
            var acsSecretPath = Path.Combine(root, "secrets", "acs", "acs_connection_string");
            var configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["ConnectionStrings:Mailer"] = connectionString,
                })
                .Build();
            var connections = new SqliteConnectionFactory(configuration);
            await new SqlMigrationRunner(connections).ApplyPendingAsync(cancellationToken);
            Assert.True(FirstRunSetupStorage.WriteAcsSecretCreateOnly(acsSecretPath, AcsSecret));

            var instance = new InstanceConfigurationRepository(connections, TimeProvider.System);
            Assert.True(await instance.ConfigureAcsAsync(acsSecretPath, cancellationToken));
            var users = new AdminUserRepository(connections, TimeProvider.System);
            Assert.True(await users.EnsureInstanceOwnerAsync(
                OwnerUsername,
                AdminPasswordHasher.Hash(OwnerPassword),
                cancellationToken));
            var senders = new SenderRepository(connections, TimeProvider.System);
            var sender = await senders.CreateAsync(SenderEmail, "Overview Test", cancellationToken);
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
            Assert.True(await instance.SetGoogleLoginSettingsAsync(
                enabled: false,
                clientId: ClientId,
                clientSecretRef: null,
                cancellationToken));

            var factory = MailerAdminFixtureHelpers.CreateFactory(
                connectionString,
                tenantConfigPath,
                AdminPasswordHasher.Hash("legacy-overview-password"),
                new Dictionary<string, string?>
                {
                    ["AMANE_ADMIN_ENABLED"] = "false",
                    ["AMANE_ADMIN_USERNAME"] = "legacy-admin",
                },
                useEarlyInstanceProbe: true);

            return new OverviewHarness(root, acsSecretPath, factory);
        }

        public async ValueTask DisposeAsync()
        {
            await Factory.DisposeAsync();
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(_root))
                Directory.Delete(_root, recursive: true);
        }
    }
}
