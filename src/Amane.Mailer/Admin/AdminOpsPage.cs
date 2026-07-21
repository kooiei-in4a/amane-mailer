using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using Amane.Mailer.Configuration;
using Amane.Mailer.Data.Sqlite;
using Amane.Mailer.Operations;
using Amane.Mailer.Webhooks;
using Amane.Mailer.Worker;
using Microsoft.AspNetCore.Antiforgery;

namespace Amane.Mailer.Admin;

public static class AdminOpsPage
{
    public static async Task<IResult> RenderAsync(
        HttpContext context,
        AdminUserRepository userRepository,
        AdminDeadLetterCountCache deadLetterCountCache,
        MailRequestRepository mailRequestRepository,
        DeliveryEventRepository deliveryEventRepository,
        MailerDbStatsReader statsReader,
        MailerDbStorageInfoReader storageInfoReader,
        WorkerServiceStatus serviceStatus,
        MailerAdminDbOpsOptions dbOpsOptions,
        MailerTenantRegistry tenantRegistry,
        IAntiforgery antiforgery,
        IConfiguration configuration,
        TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        var access = await userRepository.GetTenantAccessAsync(
            AdminAuditLog.ResolveActor(context),
            cancellationToken);
        if (access is null)
            return Results.StatusCode(StatusCodes.Status403Forbidden);

        var canRunServiceWideDbOps = dbOpsOptions.Enabled
            && await userRepository.CanRunServiceWideBackupAsync(
                access.Username,
                tenantRegistry.ListTenants().Select(tenant => tenant.TenantId),
                cancellationToken);

        var deadLetterCount = await deadLetterCountCache.GetCountAsync(
            mailRequestRepository,
            access.AllowedTenantIdsForQuery,
            cancellationToken);

        var now = timeProvider.GetUtcNow();
        var statsQuery = new MailerDbStatsQuery(access.AllowedTenantIdsForQuery);
        var stats = await statsReader.LoadStatsAsync(statsQuery, now, cancellationToken);
        var providerStats = await statsReader.LoadProviderAttemptStatsAsync(
            access.AllowedTenantIdsForQuery,
            cancellationToken);
        var storageInfo = await storageInfoReader.LoadAsync(cancellationToken);
        var webhookCounts = await deliveryEventRepository.CountOperationalAsync(cancellationToken);
        var webhookDeadLetterCount = await deliveryEventRepository.CountDeadLettersForAdminAsync(
            access.AllowedTenantIdsForQuery,
            cancellationToken);

        var workerEnabled = configuration.GetValue("Mailer:Worker:Enabled", true);
        var readiness = BuildReadiness(storageInfo, serviceStatus, workerEnabled);
        var csrfToken = dbOpsOptions.Enabled && canRunServiceWideDbOps
            ? HtmlEncoder.Default.Encode(antiforgery.GetAndStoreTokens(context).RequestToken ?? string.Empty)
            : null;

        context.Response.Headers.CacheControl = "no-store";
        return Results.Content(
            RenderHtml(
                access,
                stats,
                providerStats,
                storageInfo,
                readiness,
                deadLetterCount,
                webhookCounts,
                webhookDeadLetterCount,
                dbOpsOptions,
                canRunServiceWideDbOps,
                csrfToken,
                now),
            "text/html; charset=utf-8");
    }

    private static AdminOpsReadiness BuildReadiness(
        MailerDbStorageInfo storageInfo,
        WorkerServiceStatus serviceStatus,
        bool workerEnabled)
    {
        var ready = storageInfo.SchemaMigrated && storageInfo.CanConnect;
        if (workerEnabled)
            ready = ready && serviceStatus.IsWorkerRunning && serviceStatus.IsSweepRunning;

        return new AdminOpsReadiness(
            ready,
            storageInfo.SchemaMigrated,
            storageInfo.CanConnect,
            workerEnabled,
            serviceStatus.IsWorkerRunning,
            serviceStatus.IsSweepRunning);
    }

    private static string RenderHtml(
        Data.Sqlite.Models.AdminTenantAccess access,
        MailerDbStatsResult? stats,
        IReadOnlyList<MailerProviderAttemptStat> providerStats,
        MailerDbStorageInfo storageInfo,
        AdminOpsReadiness readiness,
        int deadLetterCount,
        (long PendingCount, long DeadLetteredCount) webhookCounts,
        int webhookDeadLetterCount,
        MailerAdminDbOpsOptions dbOpsOptions,
        bool canRunServiceWideDbOps,
        string? csrfToken,
        DateTimeOffset asOfUtc)
    {
        var html = new StringBuilder();
        AdminLayout.AppendDocumentStart(html, "運用状況 - Amane Admin", AdminNavItem.Ops, deadLetterCount);

        html.AppendLine("                <section class=\"ops-section\" aria-label=\"Readiness\">");
        html.AppendLine("                  <h2 class=\"ops-heading\">Readiness</h2>");
        html.AppendLine("                  <dl class=\"ops-dl\">");
        AppendDefinition(html, "Overall", readiness.IsReady ? "Ready" : "Not ready");
        AppendDefinition(html, "Schema migrated", FormatBool(storageInfo.SchemaMigrated));
        AppendDefinition(html, "DB connection", FormatBool(storageInfo.CanConnect));
        if (readiness.WorkerEnabled)
        {
            AppendDefinition(html, "Worker running", FormatBool(readiness.WorkerRunning));
            AppendDefinition(html, "Sweep running", FormatBool(readiness.SweepRunning));
        }
        else
        {
            AppendDefinition(html, "Worker", "Disabled");
        }

        html.AppendLine("                  </dl>");
        html.AppendLine("                </section>");

        html.AppendLine("                <section class=\"ops-section\" aria-label=\"Tenant scope\">");
        html.AppendLine("                  <h2 class=\"ops-heading\">Tenant scope</h2>");
        html.AppendLine("                  <dl class=\"ops-dl\">");
        if (access.IsBreakGlass)
        {
            AppendDefinition(html, "Scope", "All tenants (break-glass)");
        }
        else
        {
            AppendDefinition(html, "Scoped tenants", access.TenantIds.Count.ToString(CultureInfo.InvariantCulture));
            foreach (var tenantId in access.TenantIds.OrderBy(id => id))
            {
                AppendDefinition(html, "Tenant", tenantId.ToString("D"));
            }
        }

        html.AppendLine("                  </dl>");
        html.AppendLine("                </section>");

        html.AppendLine("                <section class=\"ops-section\" aria-label=\"Queue metrics\">");
        html.AppendLine("                  <h2 class=\"ops-heading\">Queue metrics</h2>");
        html.AppendLine("                  <p class=\"ops-meta\">");
        html.Append("                    As of ");
        html.Append(Html(FormatUtc(asOfUtc)));
        html.AppendLine(" (UTC)");
        html.AppendLine("                  </p>");
        if (stats is null)
        {
            html.AppendLine("                  <p class=\"ops-empty\">Database schema is not migrated.</p>");
        }
        else
        {
            html.AppendLine("                  <dl class=\"ops-dl\">");
            AppendDefinition(html, "Queued", FormatCount(stats.QueuedCount));
            AppendDefinition(html, "Processing", FormatCount(stats.ProcessingCount));
            AppendDefinition(html, "Delivered", FormatCount(stats.DeliveredCount));
            AppendDefinition(html, "Failed", FormatCount(stats.FailedCount));
            AppendDefinition(html, "Dead lettered", FormatCount(stats.DeadLetteredCount));
            AppendDefinition(html, "Ready backlog", FormatCount(stats.ReadyBacklogCount));
            AppendDefinition(html, "Oldest queued age (seconds)", FormatCount(stats.OldestQueuedAgeSeconds));
            AppendDefinition(html, "Queued stale count", FormatCount(stats.QueuedStaleCount));
            AppendDefinition(html, "Stale processing count", FormatCount(stats.StaleProcessingCount));
            AppendDefinition(html, "Expired processing count", FormatCount(stats.ExpiredProcessingCount));
            AppendDefinition(html, "Recent failed (60m window)", FormatCount(stats.RecentFailedCount));
            AppendDefinition(html, "Recent dead lettered (60m window)", FormatCount(stats.RecentDeadLetteredCount));
            AppendDefinition(html, "Worker heartbeat age (seconds)", FormatHeartbeatAge(stats.WorkerHeartbeatAgeSeconds));
            AppendDefinition(html, "Sweep heartbeat age (seconds)", FormatHeartbeatAge(stats.SweepHeartbeatAgeSeconds));
            html.AppendLine("                  </dl>");
        }

        html.AppendLine("                </section>");

        html.AppendLine("                <section class=\"ops-section\" aria-label=\"Webhook delivery\">");
        html.AppendLine("                  <h2 class=\"ops-heading\">Webhook delivery</h2>");
        html.AppendLine("                  <dl class=\"ops-dl\">");
        AppendDefinition(html, "Pending webhook events", FormatCount(webhookCounts.PendingCount));
        AppendDefinition(html, "Webhook dead letters (scoped)", FormatCount(webhookDeadLetterCount));
        AppendDefinition(html, "Webhook dead letters (service-wide)", FormatCount(webhookCounts.DeadLetteredCount));
        html.AppendLine("                  </dl>");
        html.AppendLine("                  <p><a href=\"/admin/webhook-dead-letters\">Webhook Dead Letters 一覧</a></p>");
        html.AppendLine("                </section>");

        html.AppendLine("                <section class=\"ops-section\" aria-label=\"Provider attempts\">");
        html.AppendLine("                  <h2 class=\"ops-heading\">Provider attempts</h2>");
        if (providerStats.Count == 0)
        {
            html.AppendLine("                  <p class=\"ops-empty\">No provider attempts in scope.</p>");
        }
        else
        {
            html.AppendLine("                  <div class=\"table-region\">");
            html.AppendLine("                    <table class=\"admin-table ops-table\">");
            html.AppendLine("                      <thead>");
            html.AppendLine("                        <tr>");
            html.AppendLine("                          <th>provider</th>");
            html.AppendLine("                          <th>attempt status</th>");
            html.AppendLine("                          <th>count</th>");
            html.AppendLine("                        </tr>");
            html.AppendLine("                      </thead>");
            html.AppendLine("                      <tbody>");
            foreach (var row in providerStats)
            {
                html.AppendLine("                        <tr>");
                AppendCell(html, row.Provider);
                AppendCell(html, FormatAttemptStatus(row.AttemptStatus));
                AppendCell(html, FormatCount(row.Count));
                html.AppendLine("                        </tr>");
            }

            html.AppendLine("                      </tbody>");
            html.AppendLine("                    </table>");
            html.AppendLine("                  </div>");
        }

        html.AppendLine("                </section>");

        html.AppendLine("                <section class=\"ops-section\" aria-label=\"Database storage\">");
        html.AppendLine("                  <h2 class=\"ops-heading\">Database storage</h2>");
        html.AppendLine("                  <dl class=\"ops-dl\">");
        AppendDefinition(html, "Database file", storageInfo.DatabaseFileName ?? "n/a");
        AppendDefinition(html, "Database size", FormatBytes(storageInfo.DatabaseFileSizeBytes));
        AppendDefinition(html, "WAL size", FormatBytes(storageInfo.WalFileSizeBytes));
        AppendDefinition(html, "Journal mode", storageInfo.JournalMode ?? "n/a");
        AppendDefinition(html, "Current schema version", storageInfo.CurrentSchemaVersion ?? "n/a");
        if (dbOpsOptions.Enabled && canRunServiceWideDbOps)
        {
            AppendDefinition(html, "WAL checkpoint", "available via Database operations");
        }
        else if (dbOpsOptions.Enabled)
        {
            AppendDefinition(html, "WAL checkpoint", "requires break-glass or all tenant scopes");
        }
        else
        {
            AppendDefinition(html, "WAL checkpoint", "unavailable (DB ops disabled)");
        }

        html.AppendLine("                  </dl>");

        if (storageInfo.AppliedMigrations.Count > 0)
        {
            html.AppendLine("                  <div class=\"table-region\">");
            html.AppendLine("                    <table class=\"admin-table ops-table\">");
            html.AppendLine("                      <thead>");
            html.AppendLine("                        <tr>");
            html.AppendLine("                          <th>migration</th>");
            html.AppendLine("                          <th>applied_at (UTC)</th>");
            html.AppendLine("                        </tr>");
            html.AppendLine("                      </thead>");
            html.AppendLine("                      <tbody>");
            foreach (var migration in storageInfo.AppliedMigrations)
            {
                html.AppendLine("                        <tr>");
                AppendCell(html, migration.Version);
                AppendCell(html, FormatUtc(migration.AppliedAt));
                html.AppendLine("                        </tr>");
            }

            html.AppendLine("                      </tbody>");
            html.AppendLine("                    </table>");
            html.AppendLine("                  </div>");
        }

        html.AppendLine("                </section>");

        if (dbOpsOptions.Enabled)
        {
            html.AppendLine("                <section class=\"ops-section\" aria-label=\"Database operations\">");
            html.AppendLine("                  <h2 class=\"ops-heading\">Database operations</h2>");
            if (canRunServiceWideDbOps && csrfToken is not null)
            {
                html.AppendLine("                  <p class=\"ops-meta\">");
                html.Append("                    Backups are written to the configured directory as ");
                html.Append(Html("mailer-<UTC-timestamp>.db"));
                html.AppendLine(".");
                html.AppendLine("                  </p>");
                html.AppendLine("                  <div class=\"ops-actions\">");
                html.AppendLine("                    <form method=\"post\" action=\"/admin/ops/checkpoint\" class=\"ops-form\">");
                html.Append("                      <input type=\"hidden\" name=\"__RequestVerificationToken\" value=\"");
                html.Append(csrfToken);
                html.AppendLine("\">");
                html.AppendLine("                      <button type=\"submit\">Run WAL checkpoint</button>");
                html.AppendLine("                    </form>");
                html.AppendLine("                    <form method=\"post\" action=\"/admin/ops/backup\" class=\"ops-form\">");
                html.Append("                      <input type=\"hidden\" name=\"__RequestVerificationToken\" value=\"");
                html.Append(csrfToken);
                html.AppendLine("\">");
                html.AppendLine("                      <button type=\"submit\">Run online backup</button>");
                html.AppendLine("                    </form>");
                html.AppendLine("                  </div>");
            }
            else
            {
                html.AppendLine("                  <p class=\"ops-empty\">Service-wide DB operations require break-glass access or all effective tenant scopes.</p>");
            }

            html.AppendLine("                </section>");
        }

        AdminLayout.AppendDocumentEnd(html);
        return html.ToString();
    }

    private static void AppendDefinition(StringBuilder html, string term, string value)
    {
        html.Append("                    <dt>");
        html.Append(Html(term));
        html.AppendLine("</dt>");
        html.Append("                    <dd>");
        html.Append(Html(value));
        html.AppendLine("</dd>");
    }

    private static void AppendCell(StringBuilder html, string value)
    {
        html.Append("                          <td>");
        html.Append(Html(value));
        html.AppendLine("</td>");
    }

    private static string FormatBool(bool value) => value ? "yes" : "no";

    private static string FormatCount(long value) =>
        value.ToString(CultureInfo.InvariantCulture);

    private static string FormatHeartbeatAge(long ageSeconds) =>
        ageSeconds < 0 ? "n/a" : ageSeconds.ToString(CultureInfo.InvariantCulture);

    private static string FormatBytes(long? bytes)
    {
        if (bytes is null)
            return "n/a";

        return bytes.Value.ToString(CultureInfo.InvariantCulture) + " bytes";
    }

    private static string FormatUtc(DateTimeOffset value) =>
        SqliteTime.ToStorageUtc(value.ToUniversalTime());

    private static string FormatAttemptStatus(int status) => status switch
    {
        (int)MailRequestState.Delivered => "delivered",
        (int)MailRequestState.Failed => "failed",
        (int)MailRequestState.DeadLettered => "dead_lettered",
        _ => status.ToString(CultureInfo.InvariantCulture),
    };

    private static string Html(string value) =>
        HtmlEncoder.Default.Encode(value);

    private sealed record AdminOpsReadiness(
        bool IsReady,
        bool SchemaMigrated,
        bool DbConnected,
        bool WorkerEnabled,
        bool WorkerRunning,
        bool SweepRunning);
}
