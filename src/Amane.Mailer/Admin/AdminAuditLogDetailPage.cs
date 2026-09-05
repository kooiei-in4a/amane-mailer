using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using Amane.Mailer.Data.Sqlite;
using Amane.Mailer.Data.Sqlite.Models;

namespace Amane.Mailer.Admin;

public static class AdminAuditLogDetailPage
{
    public static async Task<IResult> RenderAsync(
        HttpContext context,
        long id,
        AdminAuditRepository auditRepository,
        AdminUserRepository userRepository,
        AdminDeadLetterCountCache deadLetterCountCache,
        MailRequestRepository mailRequestRepository,
        CancellationToken cancellationToken)
    {
        var access = await userRepository.GetTenantAccessAsync(
            AdminAuditLog.ResolveActor(context),
            cancellationToken);
        if (access is null)
            return Results.StatusCode(StatusCodes.Status403Forbidden);

        var row = await auditRepository.GetForAdminAsync(
            id,
            access.AllowedTenantIdsForQuery,
            cancellationToken,
            includeManagedConfiguration: access.IsInstanceOwner);
        if (row is null)
            return Results.NotFound();

        var deadLetterCount = await deadLetterCountCache.GetCountAsync(
            mailRequestRepository,
            access.AllowedTenantIdsForQuery,
            cancellationToken);

        context.Response.Headers.CacheControl = "no-store";
        return Results.Content(RenderHtml(row, deadLetterCount), "text/html; charset=utf-8");
    }

    private static string RenderHtml(AdminAuditEventRow row, int deadLetterCount)
    {
        var html = new StringBuilder();
        AdminLayout.AppendDocumentStart(
            html,
            $"監査ログ #{row.Id} - Amane Admin",
            AdminNavItem.AuditLog,
            deadLetterCount);

        html.AppendLine("                <section class=\"ops-section\" aria-label=\"監査イベント詳細\">");
        html.AppendLine("                  <p class=\"ops-meta\"><a href=\"/admin/audit-log\">← 監査ログ一覧</a></p>");
        html.AppendLine("                  <dl class=\"ops-dl\">");
        AppendDefinition(html, "ID", row.Id.ToString(CultureInfo.InvariantCulture));
        AppendDefinition(html, "occurred_at", FormatLocalTime(row.OccurredAt));
        AppendDefinition(html, "event_type", row.EventType);
        AppendDefinition(html, "actor", row.Actor);
        AppendDefinition(html, "result", row.Result);
        AppendDefinition(html, "target_type", row.TargetType ?? string.Empty);
        AppendDefinition(html, "target_id", row.TargetId ?? string.Empty);
        AppendDefinition(html, "field_name", row.FieldName ?? string.Empty);
        AppendDefinition(html, "error_code", row.ErrorCode ?? string.Empty);
        AppendDefinition(html, "source_ip", row.SourceIp ?? string.Empty);
        AppendDefinition(html, "user_agent_summary", row.UserAgentSummary ?? string.Empty);
        html.AppendLine("                  </dl>");

        if (string.Equals(row.TargetType, AdminAuditLog.TargetTypes.MailRequest, StringComparison.Ordinal)
            && Guid.TryParse(row.TargetId, out var mailRequestId))
        {
            html.Append("                  <p class=\"ops-meta\">mail_request: <a href=\"/admin/mail-requests/");
            html.Append(Html(mailRequestId.ToString("D")));
            html.Append("\">");
            html.Append(Html(mailRequestId.ToString("D")));
            html.AppendLine("</a></p>");
        }

        html.AppendLine("                </section>");

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

    private static string FormatLocalTime(DateTimeOffset occurredAt) =>
        occurredAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss zzz", CultureInfo.InvariantCulture);

    private static string Html(string value) =>
        HtmlEncoder.Default.Encode(value);
}
