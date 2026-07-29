using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using Amane.Mailer.Configuration;
using Amane.Mailer.Data.Sqlite;
using Amane.Mailer.Setup;

namespace Amane.Mailer.Admin;

/// <summary>
/// Read-only Easy Setup / runtime configuration status (ADR 0021 D-06 / #454).
/// GET only — no doctor, send, Docker, or secret operations.
/// </summary>
public static class AdminSetupStatusPage
{
    public const string OperationalVerificationMessageJa =
        "Easy Setupでは記録していません。通常Mailer経路によるManual verificationが必要です。";

    public static async Task<IResult> RenderAsync(
        HttpContext context,
        MailerAdminOptions options,
        IConfiguration configuration,
        AdminUserRepository userRepository,
        AdminDeadLetterCountCache deadLetterCountCache,
        MailRequestRepository mailRequestRepository,
        TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        var access = await userRepository.GetTenantAccessAsync(
            AdminAuditLog.ResolveActor(context),
            cancellationToken);
        if (access is null)
            return Results.StatusCode(StatusCodes.Status403Forbidden);

        var deadLetterCount = await deadLetterCountCache.GetCountAsync(
            mailRequestRepository,
            access.AllowedTenantIdsForQuery,
            cancellationToken);

        var model = AdminSetupStatusReadModel.CreateFromConfiguration(configuration);
        var asOfUtc = timeProvider.GetUtcNow();

        context.Response.Headers.CacheControl = "no-store";
        return Results.Content(
            RenderHtml(model, options, deadLetterCount, asOfUtc),
            "text/html; charset=utf-8");
    }

    internal static string RenderHtml(
        AdminSetupStatusReadModel model,
        MailerAdminOptions options,
        int deadLetterCount,
        DateTimeOffset asOfUtc)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(options);

        var html = new StringBuilder();
        AdminLayout.AppendDocumentStart(html, "Setup status - Amane Admin", AdminNavItem.SetupStatus, deadLetterCount);

        html.AppendLine("                <section class=\"ops-section\" aria-label=\"Setup status\">");
        html.AppendLine("                  <h1 class=\"ops-heading\">Setup status</h1>");
        html.AppendLine("                  <p class=\"ops-meta\">");
        html.Append("                    As of ");
        html.Append(Html(FormatUtc(asOfUtc)));
        html.AppendLine(" (UTC) · read-only");
        html.AppendLine("                  </p>");
        html.AppendLine("                </section>");

        AppendDeploymentSection(html, model);
        AppendRecordedSection(html, model);
        AppendEffectiveSection(html, model, options);
        AppendFingerprintSection(html, model);
        AppendIntegritySection(html, model);
        AppendVerificationSection(html, model);
        AppendDeploymentStateSection(html, model);
        AppendStagingSection(html, model);
        AppendOperationalVerificationSection(html);
        AppendNextStepsSection(html, model);

        AdminLayout.AppendDocumentEnd(html);
        return html.ToString();
    }

    private static void AppendDeploymentSection(StringBuilder html, AdminSetupStatusReadModel model)
    {
        html.AppendLine("                <section class=\"ops-section\" aria-label=\"Deployment mode\">");
        html.AppendLine("                  <h2 class=\"ops-heading\">Deployment mode</h2>");
        html.AppendLine("                  <dl class=\"ops-dl\">");
        AppendDefinition(html, "Deployment", FormatDeploymentKind(model.DeploymentKind));
        AppendDefinition(html, "Mailer version", NullAsNa(model.MailerVersion));
        if (model.DeploymentKind == AdminSetupDeploymentKind.Manual)
        {
            AppendDefinition(html, "Easy Setup", "Easy Setup管理外");
            AppendDefinition(html, "Setup bundle ID", "n/a");
            AppendDefinition(html, "Image reference", "n/a");
            AppendDefinition(html, "Compose identity", "n/a");
            AppendDefinition(html, "Environment / mode", "n/a");
        }
        else if (model.DeploymentKind == AdminSetupDeploymentKind.InvalidManagedMetadata)
        {
            AppendDefinition(html, "Easy Setup", "Managed metadata invalid");
            AppendDefinition(html, "Setup bundle ID", "n/a (invalid metadata)");
            AppendDefinition(html, "Image reference", "n/a");
            AppendDefinition(html, "Compose identity", "n/a");
            AppendDefinition(html, "Environment / mode", "n/a");
            AppendDefinition(html, "Inspect reason", NullAsNa(model.InspectReason));
        }
        else
        {
            AppendDefinition(html, "Setup bundle ID", NullAsNa(model.SetupBundleId));
            AppendDefinition(html, "Image reference", FormatImageReference(model));
            AppendDefinition(html, "Compose identity", NullAsNa(model.ComposeIdentity));
            AppendDefinition(html, "Environment / mode", NullAsNa(model.Mode));
            AppendDefinition(html, "Recorded created at", NullAsNa(model.RecordedCreatedAt));
        }

        html.AppendLine("                  </dl>");
        html.AppendLine("                </section>");
    }

    private static void AppendRecordedSection(StringBuilder html, AdminSetupStatusReadModel model)
    {
        html.AppendLine("                <section class=\"ops-section\" aria-label=\"Recorded configuration\">");
        html.AppendLine("                  <h2 class=\"ops-heading\">Recorded configuration</h2>");
        html.AppendLine("                  <dl class=\"ops-dl\">");
        if (model.DeploymentKind == AdminSetupDeploymentKind.Manual)
        {
            AppendDefinition(html, "Recorded fingerprint", "n/a (Easy Setup管理外)");
            AppendDefinition(html, "Platform sender present", "n/a");
        }
        else
        {
            AppendDefinition(html, "Recorded fingerprint", NullAsNa(model.RecordedFingerprint));
            AppendDefinition(html, "Platform sender present", FormatBool(model.PlatformSenderPresent));
        }

        html.AppendLine("                  </dl>");
        html.AppendLine("                </section>");
    }

    private static void AppendEffectiveSection(
        StringBuilder html,
        AdminSetupStatusReadModel model,
        MailerAdminOptions options)
    {
        html.AppendLine("                <section class=\"ops-section\" aria-label=\"Effective configuration\">");
        html.AppendLine("                  <h2 class=\"ops-heading\">Effective configuration</h2>");
        html.AppendLine("                  <dl class=\"ops-dl\">");
        AppendDefinition(html, "Effective fingerprint", NullAsNa(model.EffectiveFingerprint));
        AppendDefinition(html, "Provider summary", NullAsNa(model.ProviderSummary));
        AppendDefinition(html, "Credential loaded", FormatCredential(model.CredentialStatus));
        AppendDefinition(html, "Live sending", FormatNullableBool(model.LiveSendingEnabled));
        AppendDefinition(html, "Sender", FormatSender(model, options));
        if (!string.IsNullOrWhiteSpace(model.InspectReason))
            AppendDefinition(html, "Inspect reason", model.InspectReason);
        html.AppendLine("                  </dl>");
        html.AppendLine("                </section>");
    }

    private static void AppendFingerprintSection(StringBuilder html, AdminSetupStatusReadModel model)
    {
        html.AppendLine("                <section class=\"ops-section\" aria-label=\"Fingerprint comparison\">");
        html.AppendLine("                  <h2 class=\"ops-heading\">Fingerprint comparison</h2>");
        html.AppendLine("                  <dl class=\"ops-dl\">");
        if (model.DeploymentKind == AdminSetupDeploymentKind.Manual)
        {
            AppendDefinition(html, "Configuration fingerprint", "n/a (Easy Setup管理外)");
        }
        else if (model.FingerprintsMatchRecorded == true)
        {
            AppendDefinition(html, "Configuration fingerprint", "match");
        }
        else if (model.FingerprintsMatchRecorded == false)
        {
            AppendDefinition(html, "Configuration fingerprint", "mismatch (warning)");
            html.AppendLine("                  </dl>");
            html.AppendLine("                  <p class=\"ops-warning\" role=\"status\">");
            html.AppendLine("                    Recorded and effective configuration fingerprints do not match.");
            html.AppendLine("                  </p>");
            html.AppendLine("                </section>");
            return;
        }
        else
        {
            AppendDefinition(html, "Configuration fingerprint", "unknown");
        }

        html.AppendLine("                  </dl>");
        html.AppendLine("                </section>");
    }

    private static void AppendIntegritySection(StringBuilder html, AdminSetupStatusReadModel model)
    {
        html.AppendLine("                <section class=\"ops-section\" aria-label=\"Bundle integrity\">");
        html.AppendLine("                  <h2 class=\"ops-heading\">Bundle integrity</h2>");
        html.AppendLine("                  <dl class=\"ops-dl\">");
        AppendDefinition(html, "Observed bundle integrity", NullAsNa(model.BundleIntegrityResult));
        if (!string.IsNullOrWhiteSpace(model.BundleIntegrityReason))
            AppendDefinition(html, "Observed integrity reason", model.BundleIntegrityReason);
        AppendDefinition(html, "Host canonical bundle integrity", NullAsNa(model.HostCanonicalBundleIntegrity));

        if (string.Equals(model.BundleIntegrityResult, SetupInspectIntegrityResult.NotVerified, StringComparison.Ordinal)
            || string.Equals(model.BundleIntegrityResult, SetupInspectIntegrityResult.Mismatch, StringComparison.Ordinal)
            || string.Equals(model.BundleIntegrityResult, SetupInspectIntegrityResult.InvalidMetadata, StringComparison.Ordinal))
        {
            html.AppendLine("                  </dl>");
            html.AppendLine("                  <p class=\"ops-warning\" role=\"status\">");
            html.AppendLine("                    Bundle integrity is not a success state. Fingerprint match alone does not mean bundle integrity matched.");
            html.AppendLine("                  </p>");
            html.AppendLine("                </section>");
            return;
        }

        html.AppendLine("                  </dl>");
        html.AppendLine("                </section>");
    }

    private static void AppendVerificationSection(StringBuilder html, AdminSetupStatusReadModel model)
    {
        html.AppendLine("                <section class=\"ops-section\" aria-label=\"Deployment verification\">");
        html.AppendLine("                  <h2 class=\"ops-heading\">Deployment verification</h2>");
        html.AppendLine("                  <dl class=\"ops-dl\">");
        AppendDefinition(html, "Verification freshness", FormatVerificationFreshness(model.VerificationFreshness));
        AppendDefinition(html, "Verification status", NullAsNa(model.VerificationStatus));
        AppendDefinition(html, "Verification reason", NullAsNa(model.VerificationReason));
        AppendDefinition(html, "Committed at", NullAsNa(model.VerificationCommittedAt));
        AppendDefinition(
            html,
            "Activation generation (record / ACTIVE)",
            FormatGenerationPair(model.VerificationActivationGeneration, model.ActiveActivationGeneration));

        if (model.DisplayVerificationCommittedPass)
            AppendDefinition(html, "Committed success (current ACTIVE)", "yes");
        else if (model.VerificationFreshness == AdminSetupVerificationFreshness.Stale)
            AppendDefinition(html, "Committed success (current ACTIVE)", "no (stale — past PASS is not current PASS)");
        else if (model.VerificationFreshness == AdminSetupVerificationFreshness.Current)
            AppendDefinition(html, "Committed success (current ACTIVE)", "no");
        else
            AppendDefinition(html, "Committed success (current ACTIVE)", "n/a");

        html.AppendLine("                  </dl>");
        html.AppendLine("                </section>");
    }

    private static void AppendDeploymentStateSection(StringBuilder html, AdminSetupStatusReadModel model)
    {
        html.AppendLine("                <section class=\"ops-section\" aria-label=\"Deployment state\">");
        html.AppendLine("                  <h2 class=\"ops-heading\">Deployment state</h2>");
        html.AppendLine("                  <dl class=\"ops-dl\">");
        AppendDefinition(html, "Configuration applied", FormatConfigurationApplied(model.ConfigurationApplied));
        AppendDefinition(html, "Production send-ready", FormatSendReady(model.SendReady));
        if (!string.IsNullOrWhiteSpace(model.SendReadyReasonCode))
            AppendDefinition(html, "Send-ready reason", model.SendReadyReasonCode);
        html.AppendLine("                  </dl>");
        html.AppendLine("                </section>");
    }

    private static void AppendStagingSection(StringBuilder html, AdminSetupStatusReadModel model)
    {
        html.AppendLine("                <section class=\"ops-section\" aria-label=\"Staging verification summary\">");
        html.AppendLine("                  <h2 class=\"ops-heading\">Staging verification summary</h2>");
        html.AppendLine("                  <dl class=\"ops-dl\">");

        if (!model.StagingSummaryApplicable
            || model.StagingSummaryAvailability == AdminSetupStagingSummaryAvailability.NotApplicable)
        {
            AppendDefinition(html, "Staging summary", "n/a (not staging-verification mode)");
            html.AppendLine("                  </dl>");
            html.AppendLine("                </section>");
            return;
        }

        if (model.StagingSummaryAvailability == AdminSetupStagingSummaryAvailability.Unavailable)
        {
            AppendDefinition(html, "Staging summary", "unavailable");
            AppendDefinition(html, "Reason", NullAsNa(model.StagingSummaryReason ?? AdminSetupStatusReadModel.StagingUnavailableReason));
            html.AppendLine("                  </dl>");
            html.AppendLine("                </section>");
            return;
        }

        if (model.StagingSummaryAvailability == AdminSetupStagingSummaryAvailability.Stale)
        {
            AppendDefinition(html, "Staging summary", "stale");
            AppendDefinition(html, "Reason", NullAsNa(model.StagingSummaryReason ?? AdminSetupStatusReadModel.StagingStaleReason));
            html.AppendLine("                  </dl>");
            html.AppendLine("                </section>");
            return;
        }

        AppendDefinition(html, "Staging summary", "available");
        AppendDefinition(html, "Staging verification code", NullAsNa(model.StagingVerificationCode));
        AppendDefinition(html, "Mailbox check status", NullAsNa(model.StagingMailboxCheckStatus));
        AppendDefinition(html, "Send request accepted", FormatNullableBool(model.StagingSendRequestAccepted));
        AppendDefinition(html, "Operation completed", FormatNullableBool(model.StagingOperationCompleted));
        html.AppendLine("                  </dl>");
        html.AppendLine("                </section>");
    }

    private static void AppendOperationalVerificationSection(StringBuilder html)
    {
        html.AppendLine("                <section class=\"ops-section\" aria-label=\"Deployment operational verification\">");
        html.AppendLine("                  <h2 class=\"ops-heading\">Deployment operational verification</h2>");
        html.AppendLine("                  <p class=\"ops-meta\">");
        html.Append(Html(OperationalVerificationMessageJa));
        html.AppendLine("</p>");
        html.AppendLine("                </section>");
    }

    private static void AppendNextStepsSection(StringBuilder html, AdminSetupStatusReadModel model)
    {
        html.AppendLine("                <section class=\"ops-section\" aria-label=\"Next steps\">");
        html.AppendLine("                  <h2 class=\"ops-heading\">Next steps</h2>");
        html.AppendLine("                  <ul class=\"ops-list\">");
        html.AppendLine("                    <li>このページは状態確認のみです。doctor / test send / Docker / secret 変更は実行しません。</li>");
        if (model.DeploymentKind == AdminSetupDeploymentKind.Manual)
        {
            html.AppendLine("                    <li>Manual Deployment の更新は docs/ops/setup-guide.md の Manual 導線に従ってください。</li>");
        }
        else
        {
            html.AppendLine("                    <li>Easy Setup の更新・再適用は host 上の Easy Setup assistant から行ってください。</li>");
            html.AppendLine("                    <li>Manual 経路へ切り替える場合も docs/ops/setup-guide.md を参照してください。</li>");
        }

        html.Append("                    <li>関連する運用観測: <a href=\"");
        html.Append(Html("/admin/ops"));
        html.AppendLine("\">運用状況</a></li>");
        html.AppendLine("                  </ul>");
        html.AppendLine("                </section>");
    }

    private static string FormatDeploymentKind(AdminSetupDeploymentKind kind) =>
        kind switch
        {
            AdminSetupDeploymentKind.Managed => "Managed Deployment",
            AdminSetupDeploymentKind.Manual => "Manual Deployment",
            AdminSetupDeploymentKind.InvalidManagedMetadata => "Managed metadata invalid",
            _ => "unknown",
        };

    private static string FormatImageReference(AdminSetupStatusReadModel model)
    {
        if (!string.IsNullOrWhiteSpace(model.ImageRepository) && !string.IsNullOrWhiteSpace(model.ImageTag))
            return model.ImageRepository + ":" + model.ImageTag;
        if (!string.IsNullOrWhiteSpace(model.ImageTag))
            return model.ImageTag;
        return "n/a";
    }

    private static string FormatVerificationFreshness(AdminSetupVerificationFreshness freshness) =>
        freshness switch
        {
            AdminSetupVerificationFreshness.NotManaged => "not-managed (Easy Setup管理外)",
            AdminSetupVerificationFreshness.Unavailable => "unavailable",
            AdminSetupVerificationFreshness.Missing => "missing",
            AdminSetupVerificationFreshness.Invalid => "invalid",
            AdminSetupVerificationFreshness.Pending => "pending",
            AdminSetupVerificationFreshness.Stale => "stale",
            AdminSetupVerificationFreshness.Current => "current",
            _ => "unknown",
        };

    private static string FormatConfigurationApplied(AdminSetupConfigurationAppliedDisplay value) =>
        value switch
        {
            AdminSetupConfigurationAppliedDisplay.NotManaged => "n/a (Easy Setup管理外)",
            AdminSetupConfigurationAppliedDisplay.Unavailable => "unavailable",
            AdminSetupConfigurationAppliedDisplay.No => "no",
            AdminSetupConfigurationAppliedDisplay.Yes => "yes",
            _ => "unknown",
        };

    private static string FormatSendReady(AdminSetupSendReadyDisplay value) =>
        value switch
        {
            AdminSetupSendReadyDisplay.NotManaged => "n/a (Easy Setup管理外)",
            AdminSetupSendReadyDisplay.Unavailable => "unavailable",
            AdminSetupSendReadyDisplay.Pending => "pending",
            AdminSetupSendReadyDisplay.NotReady => "not-ready",
            AdminSetupSendReadyDisplay.Ready => "ready",
            _ => "unknown",
        };

    private static string FormatCredential(string status) =>
        string.Equals(status, SetupInspectCredentialStatus.Loaded, StringComparison.Ordinal)
            ? "yes"
            : status;

    private static string FormatSender(AdminSetupStatusReadModel model, MailerAdminOptions options)
    {
        if (string.IsNullOrWhiteSpace(model.SenderEmail))
        {
            return model.PlatformSenderPresent ? "present (address unavailable)" : "n/a";
        }

        return AdminCapabilities.Has(options, AdminCapabilities.ViewUnmaskedListPii)
            ? model.SenderEmail
            : AdminSuppressionsPage.MaskSuppressionRecipient(model.SenderEmail);
    }

    private static string FormatGenerationPair(long? recordGeneration, long? activeGeneration)
    {
        var left = recordGeneration?.ToString(CultureInfo.InvariantCulture) ?? "n/a";
        var right = activeGeneration?.ToString(CultureInfo.InvariantCulture) ?? "n/a";
        return left + " / " + right;
    }

    private static string FormatBool(bool value) => value ? "yes" : "no";

    private static string FormatNullableBool(bool? value) =>
        value switch
        {
            true => "yes",
            false => "no",
            null => "n/a",
        };

    private static string NullAsNa(string? value) =>
        string.IsNullOrWhiteSpace(value) ? "n/a" : value;

    private static string FormatUtc(DateTimeOffset value) =>
        value.UtcDateTime.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);

    private static void AppendDefinition(StringBuilder html, string term, string value)
    {
        html.Append("                    <dt>");
        html.Append(Html(term));
        html.AppendLine("</dt>");
        html.Append("                    <dd>");
        html.Append(Html(value));
        html.AppendLine("</dd>");
    }

    private static string Html(string value) =>
        HtmlEncoder.Default.Encode(value);
}
