using System.Text;
using Amane.Mailer.Configuration;
using Amane.Mailer.Data.Sqlite;
using Amane.Mailer.Identity;
using Amane.Mailer.Operations;

namespace Amane.Mailer.Admin;

internal static class AdminDiagnosticReportPage
{
    public const string PagePath = "/admin/diagnostic-report";

    private const string Unavailable = "取得不可";

    // Keep the report contract explicit. Only these exact Overview section/item pairs
    // may cross into the plain-text report.
    private static readonly ReportSection[] Allowlist =
    [
        new("application",
        [
            new("Application", "Mailer version", "mailer_version"),
            new("Application", "Build identity", "build_identity"),
            new("Application", "Health", "health"),
            new("Application", "Readiness", "readiness"),
        ]),
        new("database",
        [
            new("Database", "Schema classification", "schema_classification"),
            new("Database", "Current applied migration", "current_applied_migration"),
            new("Database", "Migration action", "migration_action"),
        ]),
        new("setup_provider",
        [
            new("Setup / Provider", "Setup", "setup"),
            new("Setup / Provider", "Configuration authority", "configuration_authority"),
            new("Setup / Provider", "Provider", "provider"),
            new("Setup / Provider", "Provider credential", "provider_credential"),
            new("Setup / Provider", "Live Sending", "live_sending"),
            new("Setup / Provider", "ACS secret configuration", "acs_secret_configuration"),
            new("Setup / Provider", "ACS runtime", "acs_runtime"),
            new("Setup / Provider", "Google Login saved state", "google_login_saved_state"),
            new("Setup / Provider", "Google Login runtime state", "google_login_runtime_state"),
            new("Setup / Provider", "Google Login reflection state", "google_login_reflection_state"),
            new("Setup / Provider", "Google Login authority", "google_login_authority"),
            new("Setup / Provider", "Restart required", "restart_required"),
        ]),
        new("backup",
        [
            new("Backup", "Full-instance latest outcome", "full_instance_latest_outcome"),
            new("Backup", "Full-instance last success UTC", "full_instance_last_success_utc"),
            new("Backup", "Full-instance freshness", "full_instance_freshness"),
            new("Backup", "Latest offsite result", "latest_offsite_result"),
            new("Backup", "DB-only last success", "db_only_last_success"),
            new("Backup", "Restore verification", "restore_verification"),
        ]),
    ];

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

        var presentation = await AdminOverviewPage.CreatePresentationAsync(
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

        return Results.Content(RenderReport(presentation), "text/plain; charset=utf-8");
    }

    internal static string RenderReport(AdminOverviewPresentation presentation)
    {
        ArgumentNullException.ThrowIfNull(presentation);

        var report = new StringBuilder("Amane Mailer sanitized diagnostic report\nformat_version: 1");
        var firstSection = true;
        foreach (var section in Allowlist)
        {
            report.Append(firstSection ? "\n\n[" : "\n[").Append(section.Name).Append("]\n");
            firstSection = false;
            foreach (var field in section.Fields)
            {
                report.Append(field.Key)
                    .Append(": ")
                    .Append(FindAllowedValue(presentation, field))
                    .Append('\n');
            }
        }

        return report.ToString();
    }

    private static string FindAllowedValue(
        AdminOverviewPresentation presentation,
        ReportField field)
    {
        foreach (var section in presentation.Sections)
        {
            if (!string.Equals(section.Heading, field.SectionHeading, StringComparison.Ordinal))
                continue;

            foreach (var item in section.Items)
            {
                if (!string.Equals(item.Label, field.ItemLabel, StringComparison.Ordinal))
                    continue;

                var value = item.Value.Value;
                return string.IsNullOrWhiteSpace(value) || value.Any(char.IsControl)
                    ? Unavailable
                    : value;
            }
        }

        return Unavailable;
    }

    private sealed record ReportSection(string Name, ReportField[] Fields);

    private sealed record ReportField(string SectionHeading, string ItemLabel, string Key);
}
