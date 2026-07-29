using Amane.Mailer.Admin;
using Amane.Mailer.Configuration;
using Amane.Mailer.Setup;

namespace Amane.Mailer.Tests.Admin;

public sealed class AdminSetupStatusPageXssTests
{
    private static readonly DateTimeOffset AsOf = DateTimeOffset.Parse("2026-07-29T15:00:00Z");

    [Fact]
    public void Bundle_id_script_payload_is_escaped_in_text()
    {
        var html = Render(ManagedModel(bundleId: "<script>alert(1)</script>"));

        Assert.DoesNotContain("<script>", html, StringComparison.Ordinal);
        Assert.Contains("&lt;script&gt;alert(1)&lt;/script&gt;", html, StringComparison.Ordinal);
    }

    [Fact]
    public void Mode_and_provider_iframe_payloads_are_escaped()
    {
        var html = Render(ManagedModel(
            mode: "<iframe src=//evil.example>",
            providerSummary: "<iframe src=javascript:alert(1)>"));

        Assert.DoesNotContain("<iframe", html, StringComparison.Ordinal);
        Assert.Contains("&lt;iframe", html, StringComparison.Ordinal);
    }

    [Fact]
    public void Reason_and_integrity_payloads_are_escaped_in_attributes_and_text()
    {
        var html = Render(ManagedModel(
            integrityReason: "\" onmouseover=\"alert(1)",
            inspectReason: "'><img src=x onerror=alert(1)>"));

        Assert.DoesNotContain("onmouseover=\"alert", html, StringComparison.Ordinal);
        Assert.DoesNotContain("<img", html, StringComparison.Ordinal);
        Assert.Contains("&quot; onmouseover=&quot;alert(1)", html, StringComparison.Ordinal);
        Assert.Contains("&lt;img", html, StringComparison.Ordinal);
    }

    [Fact]
    public void Sender_script_payload_is_escaped_when_unmasked()
    {
        var html = AdminSetupStatusPage.RenderHtml(
            ManagedModel(senderEmail: "<script>alert(1)</script>@example.com"),
            new MailerAdminOptions { ListPiiVisible = true },
            deadLetterCount: 0,
            AsOf);

        Assert.DoesNotContain("<script>", html, StringComparison.Ordinal);
        Assert.Contains("&lt;script&gt;", html, StringComparison.Ordinal);
    }

    [Fact]
    public void User_controlled_values_are_not_emitted_into_href_attributes()
    {
        var html = Render(ManagedModel(
            bundleId: "javascript:alert(1)",
            mode: "https://evil.example/path"));

        Assert.DoesNotContain("href=\"javascript:alert(1)\"", html, StringComparison.Ordinal);
        Assert.DoesNotContain("href=\"https://evil.example/path\"", html, StringComparison.Ordinal);
        Assert.Contains("href=\"/admin/ops\"", html, StringComparison.Ordinal);
        Assert.Contains("href=\"/admin/setup-status\"", html, StringComparison.Ordinal);
    }

    [Fact]
    public void Page_title_escapes_angle_brackets_from_static_title_contract()
    {
        var html = Render(ManagedModel());
        Assert.Contains("<title>Setup status - Amane Admin</title>", html, StringComparison.Ordinal);
        Assert.DoesNotContain("<title><", html, StringComparison.Ordinal);
    }

    private static string Render(AdminSetupStatusReadModel model) =>
        AdminSetupStatusPage.RenderHtml(
            model,
            new MailerAdminOptions { ListPiiVisible = false, MaskRecipients = true },
            deadLetterCount: 0,
            AsOf);

    private static AdminSetupStatusReadModel ManagedModel(
        string bundleId = "20260729-abcd1234",
        string mode = "local-mailpit",
        string? providerSummary = "acs",
        string? integrityReason = SetupInspectReason.HostAtRestPending,
        string? inspectReason = null,
        string? senderEmail = null) =>
        new()
        {
            DeploymentKind = AdminSetupDeploymentKind.Managed,
            MailerVersion = "1.2.0-test",
            SetupBundleId = bundleId,
            Mode = mode,
            ProviderSummary = providerSummary,
            SenderEmail = senderEmail,
            CredentialStatus = SetupInspectCredentialStatus.Loaded,
            LiveSendingEnabled = false,
            RecordedFingerprint = "sha256:0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef",
            EffectiveFingerprint = "sha256:0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef",
            FingerprintsMatchRecorded = true,
            BundleIntegrityResult = SetupInspectIntegrityResult.NotVerified,
            BundleIntegrityReason = integrityReason,
            InspectReason = inspectReason,
            VerificationFreshness = AdminSetupVerificationFreshness.Unavailable,
            VerificationReason = AdminSetupStatusReadModel.VerificationUnavailableReason,
            ConfigurationApplied = AdminSetupConfigurationAppliedDisplay.Unavailable,
            SendReady = AdminSetupSendReadyDisplay.Unavailable,
            SendReadyReasonCode = AdminSetupStatusReadModel.SendReadyUnavailableReason,
            StagingSummaryApplicable = false,
            StagingSummaryAvailability = AdminSetupStagingSummaryAvailability.NotApplicable,
        };
}
