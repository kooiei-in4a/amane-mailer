using System.Text.Encodings.Web;
using Amane.Mailer.Admin;
using Amane.Mailer.Configuration;
using Amane.Mailer.Setup;

namespace Amane.Mailer.Tests.Admin;

public sealed class AdminSetupStatusPageRenderTests
{
    private static readonly DateTimeOffset AsOf = DateTimeOffset.Parse("2026-07-29T15:00:00Z");

    [Fact]
    public void Manual_deployment_renders_easy_setup_unmanaged_without_bundle_guesses()
    {
        var html = Render(ManualModel());

        Assert.Contains("Manual Deployment", html, StringComparison.Ordinal);
        Assert.Contains(Html("Easy Setup管理外"), html, StringComparison.Ordinal);
        Assert.DoesNotContain("20260729-abcd1234", html, StringComparison.Ordinal);
        Assert.DoesNotContain("method=\"post\"", html, StringComparison.Ordinal);
        Assert.DoesNotContain("action=\"/admin/setup-status\"", html, StringComparison.Ordinal);
        Assert.Contains("doctor / test send / Docker / secret", html, StringComparison.Ordinal);
    }

    [Fact]
    public void Managed_unavailable_verification_does_not_display_current_pass()
    {
        var html = Render(ManagedUnavailableModel());

        Assert.Contains("Managed Deployment", html, StringComparison.Ordinal);
        Assert.Contains("Image reference", html, StringComparison.Ordinal);
        Assert.Contains("Compose identity", html, StringComparison.Ordinal);
        Assert.DoesNotContain("Compose bundle version", html, StringComparison.Ordinal);
        Assert.Contains("unavailable", html, StringComparison.Ordinal);
        Assert.Contains(AdminSetupStatusReadModel.VerificationUnavailableReason, html, StringComparison.Ordinal);
        Assert.Contains("Committed success (current ACTIVE)", html, StringComparison.Ordinal);
        Assert.Contains(">n/a<", html, StringComparison.Ordinal);
        Assert.DoesNotContain("past PASS is current", html, StringComparison.Ordinal);
    }

    [Fact]
    public void Stale_verification_does_not_render_as_current_pass()
    {
        var model = ManagedUnavailableModel() with
        {
            VerificationFreshness = AdminSetupVerificationFreshness.Stale,
            VerificationStatus = SetupVerificationRecord.StatusCommitted,
            VerificationReason = "active-generation-or-bundle-mismatch",
            VerificationCommittedAt = "2026-07-28T12:00:00Z",
            VerificationActivationGeneration = 1,
            ActiveActivationGeneration = 2,
            DisplayVerificationCommittedPass = false,
            ConfigurationApplied = AdminSetupConfigurationAppliedDisplay.No,
            SendReady = AdminSetupSendReadyDisplay.NotReady,
            SendReadyReasonCode = "verification-not-current",
        };

        var html = Render(model);

        Assert.Contains("stale", html, StringComparison.Ordinal);
        Assert.Contains("past PASS is not current PASS", html, StringComparison.Ordinal);
        Assert.DoesNotContain("Committed success (current ACTIVE)</dt>\n                    <dd>yes", html, StringComparison.Ordinal);
    }

    [Fact]
    public void Fingerprint_mismatch_and_integrity_not_verified_are_separate_warnings()
    {
        var model = ManagedUnavailableModel() with
        {
            FingerprintsMatchRecorded = false,
            BundleIntegrityResult = SetupInspectIntegrityResult.NotVerified,
            BundleIntegrityReason = SetupInspectReason.HostAtRestPending,
        };

        var html = Render(model);

        Assert.Contains("mismatch (warning)", html, StringComparison.Ordinal);
        Assert.Contains("Recorded and effective configuration fingerprints do not match.", html, StringComparison.Ordinal);
        Assert.Contains("not-verified", html, StringComparison.Ordinal);
        Assert.Contains("Fingerprint match alone does not mean bundle integrity matched.", html, StringComparison.Ordinal);
    }

    [Fact]
    public void Integrity_mismatch_is_not_collapsed_into_fingerprint_match()
    {
        var model = ManagedUnavailableModel() with
        {
            FingerprintsMatchRecorded = true,
            BundleIntegrityResult = SetupInspectIntegrityResult.Mismatch,
            BundleIntegrityReason = SetupInspectReason.MountMismatch,
        };

        var html = Render(model);

        Assert.Contains(">match<", html, StringComparison.Ordinal);
        Assert.Contains(">mismatch<", html, StringComparison.Ordinal);
        Assert.Contains(SetupInspectReason.MountMismatch, html, StringComparison.Ordinal);
    }

    [Fact]
    public void Configuration_applied_and_send_ready_are_separate_rows()
    {
        var html = Render(ManagedUnavailableModel() with
        {
            ConfigurationApplied = AdminSetupConfigurationAppliedDisplay.Yes,
            SendReady = AdminSetupSendReadyDisplay.NotReady,
            SendReadyReasonCode = AcsSendReadyEvaluator.ReasonLiveSendingDisabled,
        });

        Assert.Contains("Configuration applied", html, StringComparison.Ordinal);
        Assert.Contains("Production send-ready", html, StringComparison.Ordinal);
        Assert.Contains(AcsSendReadyEvaluator.ReasonLiveSendingDisabled, html, StringComparison.Ordinal);
        Assert.Contains(">yes<", html, StringComparison.Ordinal);
        Assert.Contains(">not-ready<", html, StringComparison.Ordinal);
    }

    [Fact]
    public void Operational_verification_uses_fixed_unrecorded_message()
    {
        var html = Render(ManagedUnavailableModel());

        Assert.Contains(Html(AdminSetupStatusPage.OperationalVerificationMessageJa), html, StringComparison.Ordinal);
        Assert.DoesNotContain(Html("確認済み"), html, StringComparison.Ordinal);
        Assert.DoesNotContain(">PASS<", html, StringComparison.Ordinal);
        Assert.DoesNotContain("Recorded by Easy Setup", html, StringComparison.Ordinal);
        Assert.DoesNotContain("release qualification", html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("#456", html, StringComparison.Ordinal);
    }

    [Fact]
    public void Staging_summary_not_fabricated_for_production_mode()
    {
        var html = Render(ManagedUnavailableModel() with
        {
            Mode = "production-acs",
            StagingSummaryApplicable = false,
            StagingSummaryAvailability = AdminSetupStagingSummaryAvailability.NotApplicable,
        });

        Assert.Contains("n/a (not staging-verification mode)", html, StringComparison.Ordinal);
        Assert.DoesNotContain("Staging summary</dt>\n                    <dd>available", html, StringComparison.Ordinal);
    }

    [Fact]
    public void Staging_mode_unavailable_summary_is_explicit()
    {
        var html = Render(ManagedUnavailableModel() with
        {
            Mode = "staging-verification",
            StagingSummaryApplicable = true,
            StagingSummaryAvailability = AdminSetupStagingSummaryAvailability.Unavailable,
        });

        Assert.Contains(AdminSetupStatusReadModel.StagingUnavailableReason, html, StringComparison.Ordinal);
    }

    [Fact]
    public void Sender_follows_admin_list_pii_masking_policy()
    {
        var model = ManagedUnavailableModel() with { SenderEmail = "sender.user@example.com" };

        var masked = Render(model, new MailerAdminOptions { ListPiiVisible = false, MaskRecipients = true });
        Assert.Contains("s***@e***.com", masked, StringComparison.Ordinal);
        Assert.DoesNotContain("sender.user@example.com", masked, StringComparison.Ordinal);

        var unmasked = Render(model, new MailerAdminOptions { ListPiiVisible = true, MaskRecipients = true });
        Assert.Contains("sender.user@example.com", unmasked, StringComparison.Ordinal);
    }

    [Fact]
    public void Navigation_includes_setup_status_link_and_no_secret_or_path_leakage()
    {
        var html = Render(ManagedUnavailableModel() with
        {
            SenderEmail = null,
            InspectReason = SetupInspectReason.HostAtRestPending,
        });

        Assert.Contains("href=\"/admin/setup-status\"", html, StringComparison.Ordinal);
        Assert.Contains("href=\"/admin/ops\"", html, StringComparison.Ordinal);
        Assert.DoesNotContain("Endpoint=sb://", html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("connection string", html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("SharedAccessKey", html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("/var/lib/", html, StringComparison.Ordinal);
        Assert.DoesNotContain("C:\\", html, StringComparison.Ordinal);
        Assert.DoesNotContain("provider raw", html, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Page_is_get_only_without_mutation_forms()
    {
        var html = Render(ManagedUnavailableModel());

        Assert.DoesNotContain("<form", html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("method=\"post\"", html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("method=\"POST\"", html, StringComparison.Ordinal);
    }

    private static string Render(AdminSetupStatusReadModel model, MailerAdminOptions? options = null) =>
        AdminSetupStatusPage.RenderHtml(
            model,
            options ?? new MailerAdminOptions { ListPiiVisible = false, MaskRecipients = true },
            deadLetterCount: 0,
            AsOf);

    private static AdminSetupStatusReadModel ManualModel() =>
        new()
        {
            DeploymentKind = AdminSetupDeploymentKind.Manual,
            MailerVersion = "1.2.0-test",
            CredentialStatus = SetupInspectCredentialStatus.NotApplicable,
            BundleIntegrityResult = SetupInspectIntegrityResult.NotManaged,
            VerificationFreshness = AdminSetupVerificationFreshness.NotManaged,
            ConfigurationApplied = AdminSetupConfigurationAppliedDisplay.NotManaged,
            SendReady = AdminSetupSendReadyDisplay.NotManaged,
            StagingSummaryApplicable = false,
            StagingSummaryAvailability = AdminSetupStagingSummaryAvailability.NotApplicable,
            EffectiveFingerprint = "sha256:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
            ProviderSummary = "mailpit",
            LiveSendingEnabled = false,
        };

    private static AdminSetupStatusReadModel ManagedUnavailableModel() =>
        new()
        {
            DeploymentKind = AdminSetupDeploymentKind.Managed,
            MailerVersion = "1.2.0-test",
            SetupBundleId = "20260729-abcd1234",
            Mode = "local-mailpit",
            ProviderSummary = "acs",
            CredentialStatus = SetupInspectCredentialStatus.Loaded,
            LiveSendingEnabled = false,
            RecordedFingerprint = "sha256:0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef",
            EffectiveFingerprint = "sha256:0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef",
            FingerprintsMatchRecorded = true,
            BundleIntegrityResult = SetupInspectIntegrityResult.NotVerified,
            BundleIntegrityReason = SetupInspectReason.HostAtRestPending,
            RecordedCreatedAt = "2026-07-29T12:00:00Z",
            ImageRepository = "ghcr.io/example/amane-mailer",
            ImageTag = "1.2.0",
            VerificationFreshness = AdminSetupVerificationFreshness.Unavailable,
            VerificationReason = AdminSetupStatusReadModel.VerificationUnavailableReason,
            DisplayVerificationCommittedPass = false,
            ConfigurationApplied = AdminSetupConfigurationAppliedDisplay.Unavailable,
            SendReady = AdminSetupSendReadyDisplay.Unavailable,
            SendReadyReasonCode = AdminSetupStatusReadModel.SendReadyUnavailableReason,
            StagingSummaryApplicable = false,
            StagingSummaryAvailability = AdminSetupStagingSummaryAvailability.NotApplicable,
        };

    private static string Html(string value) => HtmlEncoder.Default.Encode(value);
}
