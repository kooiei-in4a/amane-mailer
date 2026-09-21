using System.Globalization;
using System.Reflection;
using System.Text;
using System.Text.Encodings.Web;
using Amane.Mailer.Configuration;
using Amane.Mailer.Data.Sqlite;
using Amane.Mailer.Data.Sqlite.Models;
using Amane.Mailer.Identity;
using Amane.Mailer.Operations;
using Amane.Mailer.Setup;
using Microsoft.Extensions.Hosting;

namespace Amane.Mailer.Admin;

internal static class AdminOverviewPage
{
    public const string PagePath = "/admin/overview";

    public static async Task<IResult> RenderAsync(
        HttpContext context,
        AdminUserRepository userRepository,
        IConfiguration configuration,
        IHostEnvironment environment,
        InstanceConfigurationRepository instanceConfigurationRepository,
        SenderRepository senderRepository,
        MailerOptions mailerOptions,
        AdminGoogleOptions googleOptions,
        SqlMigrationRunner migrationRunner,
        MailerDbStorageInfoReader storageInfoReader,
        MailerRuntimeReadinessProbe readinessProbe,
        AdminBackupStatusReader backupStatusReader,
        TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        context.Response.Headers.CacheControl = "no-store";
        context.Response.Headers.Pragma = "no-cache";

        var accessResult = await AdminManagedConfigurationAuthorization.RequireInstanceOwnerAsync(
            context,
            userRepository,
            cancellationToken);
        if (accessResult.Error is not null)
            return accessResult.Error;

        var presentation = await CreatePresentationAsync(
            configuration,
            environment,
            instanceConfigurationRepository,
            senderRepository,
            mailerOptions,
            googleOptions,
            migrationRunner,
            storageInfoReader,
            readinessProbe,
            backupStatusReader,
            timeProvider,
            cancellationToken);

        return Results.Content(
            RenderHtml(presentation, accessResult.Access!),
            "text/html; charset=utf-8");
    }

    internal static async Task<AdminOverviewPresentation> CreatePresentationAsync(
        IConfiguration configuration,
        IHostEnvironment environment,
        InstanceConfigurationRepository instanceConfigurationRepository,
        SenderRepository senderRepository,
        MailerOptions mailerOptions,
        AdminGoogleOptions googleOptions,
        SqlMigrationRunner migrationRunner,
        MailerDbStorageInfoReader storageInfoReader,
        MailerRuntimeReadinessProbe readinessProbe,
        AdminBackupStatusReader backupStatusReader,
        TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        var instanceConfiguration = await instanceConfigurationRepository.GetAsync(cancellationToken);
        var sender = instanceConfiguration?.InitializedAt is null
            ? null
            : await senderRepository.FindFirstEnabledAsync(cancellationToken);
        var managedInstance = instanceConfiguration?.InitializedAt is null
            ? null
            : new AdminSetupManagedInstanceObservation
            {
                Initialized = true,
                LiveSendingEnabled = instanceConfiguration.LiveSending,
                ProviderType = instanceConfiguration.ProviderType,
                ProviderPreflightSafe = AdminManagedProviderPreflight.IsSafe(instanceConfiguration),
                SenderPresent = sender is not null,
            };
        var setup = AdminSetupStatusReadModel.CreateFromConfiguration(
            configuration,
            environment.EnvironmentName,
            managedInstance);
        var providerSummary = instanceConfiguration?.InitializedAt is not null
            ? instanceConfiguration.ProviderType
            : setup.ProviderSummary;
        var acsStatus = AdminAcsRuntimeStatusReader.Evaluate(
            instanceConfiguration,
            mailerOptions.AcsConnectionString);
        var googleStatus = AdminGoogleSettingsStatus.Evaluate(
            instanceConfiguration,
            googleOptions,
            configuration);
        var readiness = await readinessProbe.ProbeAsync(cancellationToken);

        var schemaClassification = await ReadSchemaClassificationAsync(
            migrationRunner,
            cancellationToken);
        var currentSchemaVersion = await ReadCurrentSchemaVersionAsync(
            storageInfoReader,
            cancellationToken);
        var backupStatus = await ReadBackupStatusAsync(
            backupStatusReader,
            timeProvider.GetUtcNow(),
            cancellationToken);

        var presentation = CreatePresentation(
            setup,
            instanceConfiguration?.InitializedAt is not null,
            providerSummary,
            readiness,
            schemaClassification,
            currentSchemaVersion,
            acsStatus,
            googleStatus,
            backupStatus,
            ResolveBuildIdentity());
        return presentation;
    }

    internal static AdminOverviewPresentation CreatePresentation(
        AdminSetupStatusReadModel setup,
        bool setupFinalized,
        string? providerSummary,
        MailerReadinessResult readiness,
        string schemaClassification,
        string? currentSchemaVersion,
        AdminAcsRuntimeStatus acsStatus,
        AdminGoogleSettingsStatus googleStatus,
        AdminBackupStatusReadModel? backupStatus,
        string buildIdentity)
    {
        var isAcsProvider = IsAcsProvider(providerSummary);
        var googleSaved = MapGoogleSavedState(googleStatus);
        var googleRuntime = MapGoogleRuntimeState(googleStatus);
        var googleReflection = MapGoogleReflectionState(googleStatus);
        var schema = MapSchemaClassification(schemaClassification);
        var schemaAction = MapMigrationAction(schemaClassification);

        return new AdminOverviewPresentation(
        [
            new("Application",
            [
                new("Mailer version", Information(EmptyAsUnavailable(setup.MailerVersion))),
                new("Build identity", Information(EmptyAsUnavailable(buildIdentity))),
                new("Health", new("OK", AdminOverviewStatusKind.Ok)),
                new("Readiness", MapReadiness(readiness)),
            ]),
            new("Database",
            [
                new("Schema classification", schema),
                new("Current applied migration", MapCurrentAppliedMigration(currentSchemaVersion, schema)),
                new("Migration action", schemaAction),
            ]),
            new("Setup / Provider",
            [
                new("Setup", MapSetupState(setupFinalized)),
                new("Configuration authority", MapConfigurationAuthority(setup)),
                new("Provider", MapProvider(providerSummary)),
                new("Provider credential", MapProviderCredential(setup.CredentialStatus)),
                new("Live Sending", MapLiveSending(setup.LiveSendingEnabled)),
                new("ACS secret configuration", MapAcsConfiguration(isAcsProvider, acsStatus)),
                new("ACS runtime", MapAcsRuntime(isAcsProvider, acsStatus)),
                new("Google Login saved state", googleSaved),
                new("Google Login runtime state", googleRuntime),
                new("Google Login reflection state", googleReflection),
                new("Google Login authority", MapGoogleAuthority(googleStatus)),
                new("Restart required", MapRestartRequired(acsStatus, googleStatus)),
            ]),
            new("Backup",
            [
                new("Full-instance latest outcome", MapBackupOutcome(backupStatus?.FullInstance)),
                new("Full-instance last success UTC", MapLastSuccess(backupStatus?.FullInstance)),
                new("Full-instance freshness", MapBackupFreshness(backupStatus?.FullInstance)),
                new("Latest offsite result", MapOffsiteResult(backupStatus?.FullInstance)),
                new("DB-only last success", MapLastSuccessSummary(backupStatus?.DatabaseOnly)),
                new("Restore verification", MapRestoreVerification(backupStatus)),
            ]),
        ]);
    }

    internal static AdminOverviewStatusValue MapReadiness(MailerReadinessResult readiness) =>
        readiness.IsReady
            ? new("Ready", AdminOverviewStatusKind.Ok)
            : new("Not Ready", AdminOverviewStatusKind.ActionRequired);

    internal static AdminOverviewStatusValue MapSchemaClassification(string classification) =>
        classification switch
        {
            SetupSchemaClassification.Current => new("Current", AdminOverviewStatusKind.Ok),
            SetupSchemaClassification.Behind => new("Behind", AdminOverviewStatusKind.ActionRequired),
            SetupSchemaClassification.DatabaseAbsent => new("DatabaseAbsent", AdminOverviewStatusKind.ActionRequired),
            SetupSchemaClassification.AheadOrUnsupported => new("AheadOrUnsupported", AdminOverviewStatusKind.ActionRequired),
            _ => new("取得不可", AdminOverviewStatusKind.Unavailable),
        };

    internal static AdminOverviewStatusValue MapMigrationAction(string classification) =>
        classification switch
        {
            SetupSchemaClassification.Current => new("不要", AdminOverviewStatusKind.Ok),
            SetupSchemaClassification.Behind or SetupSchemaClassification.DatabaseAbsent =>
                new("必要", AdminOverviewStatusKind.ActionRequired),
            SetupSchemaClassification.AheadOrUnsupported =>
                new("安全な自動前方migrationは不可", AdminOverviewStatusKind.ActionRequired),
            _ => new("取得不可", AdminOverviewStatusKind.Unavailable),
        };

    internal static AdminOverviewStatusValue MapBackupFreshness(AdminBackupScriptStatus? status)
    {
        if (status is null
            || status.AttemptEvidenceState == AdminBackupEvidenceState.Invalid
            || status.SuccessEvidenceState == AdminBackupEvidenceState.Invalid)
        {
            return new("取得不可", AdminOverviewStatusKind.Unavailable);
        }

        if (status.LatestOutcome == AdminBackupOutcome.Failed)
            return new("失敗", AdminOverviewStatusKind.ActionRequired);

        if (!status.FreshnessPolicyConfigured)
            return new("ポリシー未設定", AdminOverviewStatusKind.Info);

        return status.Freshness switch
        {
            AdminBackupFreshness.Fresh => new("Fresh", AdminOverviewStatusKind.Ok),
            AdminBackupFreshness.Stale => new("Stale", AdminOverviewStatusKind.Attention),
            _ => new("取得不可", AdminOverviewStatusKind.Unavailable),
        };
    }

    internal static AdminOverviewStatusValue MapAcsRuntime(
        bool isAcsProvider,
        AdminAcsRuntimeStatus status)
    {
        if (!isAcsProvider)
            return new("対象外", AdminOverviewStatusKind.Info);

        return status.RuntimeState switch
        {
            AdminAcsRuntimeState.Applied => new("反映済み", AdminOverviewStatusKind.Ok),
            AdminAcsRuntimeState.RestartPending => new("再起動待ち", AdminOverviewStatusKind.Attention),
            _ => new("取得不可", AdminOverviewStatusKind.ActionRequired),
        };
    }

    internal static AdminOverviewStatusValue MapLiveSending(bool? enabled) => enabled switch
    {
        true => new("ON", AdminOverviewStatusKind.Info),
        false => new("OFF", AdminOverviewStatusKind.Info),
        _ => new("取得不可", AdminOverviewStatusKind.Unavailable),
    };

    internal static AdminOverviewStatusValue MapGoogleSavedState(AdminGoogleSettingsStatus status)
    {
        if (!status.SavedRequestedEnabled)
            return new(AdminGoogleSettingsStatus.DisplayOff, AdminOverviewStatusKind.Info);
        if (!status.SavedComplete)
            return new(AdminGoogleSettingsStatus.DisplayOnIncomplete, AdminOverviewStatusKind.ActionRequired);
        return status.RestartRequired
            ? new(AdminGoogleSettingsStatus.DisplayOn, AdminOverviewStatusKind.Attention)
            : new(AdminGoogleSettingsStatus.DisplayOn, AdminOverviewStatusKind.Ok);
    }

    internal static AdminOverviewStatusValue MapGoogleRuntimeState(AdminGoogleSettingsStatus status)
    {
        var value = status.RuntimeEnabledDisplay;
        if (status.RestartRequired)
            return new(value, AdminOverviewStatusKind.Attention);
        return status.RuntimeEnabled
            ? new(value, AdminOverviewStatusKind.Ok)
            : new(value, AdminOverviewStatusKind.Info);
    }

    internal static AdminOverviewStatusValue MapGoogleReflectionState(AdminGoogleSettingsStatus status)
    {
        if (status.SavedRequestedEnabled && !status.SavedComplete)
            return new(AdminGoogleSettingsStatus.ReflectionIncomplete, AdminOverviewStatusKind.ActionRequired);
        if (status.RestartRequired)
            return new(AdminGoogleSettingsStatus.ReflectionRestartPending, AdminOverviewStatusKind.Attention);
        return status.SavedRequestedEnabled
            ? new(AdminGoogleSettingsStatus.ReflectionApplied, AdminOverviewStatusKind.Ok)
            : new(AdminGoogleSettingsStatus.ReflectionApplied, AdminOverviewStatusKind.Info);
    }

    internal static AdminOverviewStatusValue MapRestartRequired(
        AdminAcsRuntimeStatus acsStatus,
        AdminGoogleSettingsStatus googleStatus) =>
        acsStatus.RuntimeState == AdminAcsRuntimeState.RestartPending || googleStatus.RestartRequired
            ? new("Required", AdminOverviewStatusKind.Attention)
            : new("Not required", AdminOverviewStatusKind.Ok);

    internal static string ResolveBuildIdentity()
    {
        var informationalVersion = typeof(AdminOverviewPage).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()
            ?.InformationalVersion;
        if (string.IsNullOrWhiteSpace(informationalVersion))
            return "n/a";

        var separator = informationalVersion.IndexOf('+');
        if (separator < 1 || separator == informationalVersion.Length - 1)
            return "n/a";

        var identity = informationalVersion[(separator + 1)..];
        if (identity.StartsWith("sha.", StringComparison.Ordinal))
            identity = identity[4..];
        if (identity.Length is < 7 or > 64 || !identity.All(IsHexDigit))
            return "n/a";

        return identity;
    }

    internal static string RenderHtml(AdminOverviewPresentation presentation, AdminTenantAccess access)
    {
        var html = new StringBuilder();
        AdminLayout.AppendDocumentStart(
            html,
            "システム状態 - Amane Admin",
            AdminNavItem.Overview,
            deadLetterCount: 0,
            access);
        html.AppendLine("                <section class=\"ops-section\" aria-label=\"システム状態\">");
        html.AppendLine("                  <h1 class=\"ops-heading\">システム状態</h1>");
        html.AppendLine("                  <p class=\"ops-description\">Mailerの稼働状態を確認する読み取り専用の概要です。</p>");
        html.AppendLine("                </section>");

        foreach (var section in presentation.Sections)
        {
            html.AppendLine("                <section class=\"ops-section\">");
            html.Append("                  <h2 class=\"ops-heading\">");
            html.Append(Html(section.Heading));
            html.AppendLine("</h2>");
            html.AppendLine("                  <dl class=\"ops-dl\">");
            foreach (var item in section.Items)
            {
                html.Append("                    <dt>");
                html.Append(Html(item.Label));
                html.AppendLine("</dt>");
                html.Append("                    <dd><span class=\"admin-overview-status admin-overview-status--");
                html.Append(StatusClass(item.Value.Status));
                html.Append("\">");
                html.Append(StatusLabel(item.Value.Status));
                html.Append("</span> ");
                html.Append(Html(item.Value.Value));
                html.AppendLine("</dd>");
            }

            html.AppendLine("                  </dl>");
            html.AppendLine("                </section>");
        }

        html.AppendLine("                <section class=\"ops-section\" aria-label=\"詳細画面\">");
        html.AppendLine("                  <h2 class=\"ops-heading\">詳細画面</h2>");
        html.AppendLine("                  <ul class=\"ops-list\">");
        AppendDetailLink(html, "/admin/ops", "運用状況");
        AppendDetailLink(html, "/admin/setup-status", "Setup status");
        AppendDetailLink(html, AdminSecretsPage.PagePath, "Secret管理");
        AppendDetailLink(html, AdminGoogleSettingsPage.PagePath, "認証設定");
        AppendDetailLink(html, AdminDiagnosticReportPage.PagePath, "Sanitized diagnostic report");
        html.AppendLine("                  </ul>");
        html.AppendLine("                </section>");
        AdminLayout.AppendDocumentEnd(html);
        return html.ToString();
    }

    private static AdminOverviewStatusValue MapSetupState(bool finalized) =>
        finalized
            ? new("初期化済み", AdminOverviewStatusKind.Ok)
            : new("未初期化", AdminOverviewStatusKind.ActionRequired);

    private static AdminOverviewStatusValue MapConfigurationAuthority(AdminSetupStatusReadModel setup) =>
        setup.BrowserManagedInstance
            ? new("Browser managed instance configuration", AdminOverviewStatusKind.Info)
            : setup.DeploymentKind switch
            {
                AdminSetupDeploymentKind.Managed => new("Easy Setup metadata", AdminOverviewStatusKind.Info),
                AdminSetupDeploymentKind.Manual => new("Process configuration", AdminOverviewStatusKind.Info),
                _ => new("取得不可", AdminOverviewStatusKind.Unavailable),
            };

    private static AdminOverviewStatusValue MapProvider(string? provider) => provider switch
    {
        "acs" => new("acs", AdminOverviewStatusKind.Info),
        "mailpit" => new("mailpit", AdminOverviewStatusKind.Info),
        "acs+mailpit" => new("acs+mailpit", AdminOverviewStatusKind.Info),
        _ => new("取得不可", AdminOverviewStatusKind.Unavailable),
    };

    private static AdminOverviewStatusValue MapProviderCredential(string status) => status switch
    {
        SetupInspectCredentialStatus.Loaded => new("利用可能", AdminOverviewStatusKind.Ok),
        SetupInspectCredentialStatus.Missing => new("利用不可", AdminOverviewStatusKind.ActionRequired),
        SetupInspectCredentialStatus.Invalid => new("設定値に問題あり", AdminOverviewStatusKind.ActionRequired),
        SetupInspectCredentialStatus.NotApplicable => new("対象外", AdminOverviewStatusKind.Info),
        _ => new("取得不可", AdminOverviewStatusKind.Unavailable),
    };

    private static AdminOverviewStatusValue MapAcsConfiguration(
        bool isAcsProvider,
        AdminAcsRuntimeStatus status) =>
        !isAcsProvider
            ? new("対象外", AdminOverviewStatusKind.Info)
            : status.Configured
                ? new("設定済み", AdminOverviewStatusKind.Info)
                : new("未設定", AdminOverviewStatusKind.ActionRequired);

    private static AdminOverviewStatusValue MapCurrentAppliedMigration(
        string? version,
        AdminOverviewStatusValue schema)
    {
        if (schema.Status == AdminOverviewStatusKind.Unavailable)
            return new("取得不可", AdminOverviewStatusKind.Unavailable);

        var value = string.IsNullOrWhiteSpace(version) ? "未記録" : version;
        return new(value, schema.Status);
    }

    private static AdminOverviewStatusValue MapLastSuccess(AdminBackupScriptStatus? status)
    {
        if (status is null || status.SuccessEvidenceState == AdminBackupEvidenceState.Invalid)
            return new("取得不可", AdminOverviewStatusKind.Unavailable);
        if (status.LastSuccessAtUtc is not DateTimeOffset timestamp)
            return new("未記録", AdminOverviewStatusKind.Info);
        return new(FormatUtc(timestamp), AdminOverviewStatusKind.Info);
    }

    internal static AdminOverviewStatusValue MapBackupOutcome(AdminBackupScriptStatus? status)
    {
        if (status is null
            || status.AttemptEvidenceState == AdminBackupEvidenceState.Invalid
            || status.SuccessEvidenceState == AdminBackupEvidenceState.Invalid)
        {
            return new("取得不可", AdminOverviewStatusKind.Unavailable);
        }

        return status.LatestOutcome switch
        {
            AdminBackupOutcome.Succeeded => new("成功", AdminOverviewStatusKind.Ok),
            AdminBackupOutcome.Failed => new("失敗", AdminOverviewStatusKind.ActionRequired),
            AdminBackupOutcome.Running => new("実行中", AdminOverviewStatusKind.Attention),
            _ => new("未記録", AdminOverviewStatusKind.Info),
        };
    }

    internal static AdminOverviewStatusValue MapOffsiteResult(AdminBackupScriptStatus? status)
    {
        if (status is null || status.AttemptEvidenceState == AdminBackupEvidenceState.Invalid)
            return new("取得不可", AdminOverviewStatusKind.Unavailable);

        return status.LatestAttemptOffsiteState switch
        {
            AdminBackupOffsiteState.Succeeded => new("成功", AdminOverviewStatusKind.Ok),
            AdminBackupOffsiteState.Failed => new("失敗", AdminOverviewStatusKind.ActionRequired),
            AdminBackupOffsiteState.Pending => new("処理中", AdminOverviewStatusKind.Attention),
            AdminBackupOffsiteState.NotAttempted => new("未実施", AdminOverviewStatusKind.Info),
            AdminBackupOffsiteState.Skipped => new("スキップ", AdminOverviewStatusKind.Info),
            _ => new("未記録", AdminOverviewStatusKind.Info),
        };
    }

    private static AdminOverviewStatusValue MapLastSuccessSummary(AdminBackupScriptStatus? status)
    {
        if (status is null || status.SuccessEvidenceState == AdminBackupEvidenceState.Invalid)
            return new("取得不可", AdminOverviewStatusKind.Unavailable);
        return status.LastSuccessAtUtc is DateTimeOffset timestamp
            ? new($"成功 · {FormatUtc(timestamp)}", AdminOverviewStatusKind.Info)
            : new("未記録", AdminOverviewStatusKind.Info);
    }

    internal static AdminOverviewStatusValue MapRestoreVerification(AdminBackupStatusReadModel? status) =>
        status is null
            ? new("取得不可", AdminOverviewStatusKind.Unavailable)
            : new(
                status.RestoreVerificationRecorded ? "記録済み" : "未記録",
                AdminOverviewStatusKind.Info);

    private static AdminOverviewStatusValue MapGoogleAuthority(AdminGoogleSettingsStatus status) =>
        new(
            $"保存: {status.SavedAuthorityDisplay} / runtime: {status.RuntimeAuthorityDisplay}",
            status.RestartRequired ? AdminOverviewStatusKind.Attention : AdminOverviewStatusKind.Info);

    private static bool IsAcsProvider(string? provider) =>
        provider is "acs" or "acs+mailpit";

    private static AdminOverviewStatusValue Information(string value) =>
        value == "取得不可"
            ? new(value, AdminOverviewStatusKind.Unavailable)
            : new(value, AdminOverviewStatusKind.Info);

    private static string EmptyAsUnavailable(string? value) =>
        string.IsNullOrWhiteSpace(value) ? "取得不可" : value;

    private static string FormatUtc(DateTimeOffset value) =>
        value.ToUniversalTime().ToString("yyyy-MM-dd HH:mm:ss 'UTC'", CultureInfo.InvariantCulture);

    private static async Task<string> ReadSchemaClassificationAsync(
        SqlMigrationRunner migrationRunner,
        CancellationToken cancellationToken)
    {
        try
        {
            return await migrationRunner.ClassifySchemaAsync(cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return SetupSchemaClassification.Unknown;
        }
    }

    private static async Task<string?> ReadCurrentSchemaVersionAsync(
        MailerDbStorageInfoReader storageInfoReader,
        CancellationToken cancellationToken)
    {
        try
        {
            var storageInfo = await storageInfoReader.LoadAsync(cancellationToken);
            return storageInfo.CurrentSchemaVersion;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return null;
        }
    }

    private static async Task<AdminBackupStatusReadModel?> ReadBackupStatusAsync(
        AdminBackupStatusReader backupStatusReader,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        try
        {
            return await backupStatusReader.LoadAsync(now, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return null;
        }
    }

    private static string StatusLabel(AdminOverviewStatusKind status) => status switch
    {
        AdminOverviewStatusKind.Ok => "OK",
        AdminOverviewStatusKind.Attention => "注意",
        AdminOverviewStatusKind.ActionRequired => "要対応",
        AdminOverviewStatusKind.Info => "情報",
        _ => "取得不可",
    };

    private static string StatusClass(AdminOverviewStatusKind status) => status switch
    {
        AdminOverviewStatusKind.Ok => "ok",
        AdminOverviewStatusKind.Attention => "attention",
        AdminOverviewStatusKind.ActionRequired => "action-required",
        AdminOverviewStatusKind.Info => "info",
        _ => "unavailable",
    };

    private static bool IsHexDigit(char value) =>
        value is >= '0' and <= '9'
            or >= 'a' and <= 'f'
            or >= 'A' and <= 'F';

    private static void AppendDetailLink(StringBuilder html, string href, string label)
    {
        html.Append("                    <li><a href=\"");
        html.Append(Html(href));
        html.Append("\">");
        html.Append(Html(label));
        html.AppendLine("</a></li>");
    }

    private static string Html(string value) => HtmlEncoder.Default.Encode(value);
}

internal enum AdminOverviewStatusKind
{
    Ok,
    Attention,
    ActionRequired,
    Info,
    Unavailable,
}

internal readonly record struct AdminOverviewStatusValue(string Value, AdminOverviewStatusKind Status);

internal sealed record AdminOverviewStatusItem(string Label, AdminOverviewStatusValue Value);

internal sealed record AdminOverviewSection(string Heading, IReadOnlyList<AdminOverviewStatusItem> Items);

internal sealed record AdminOverviewPresentation(IReadOnlyList<AdminOverviewSection> Sections);
