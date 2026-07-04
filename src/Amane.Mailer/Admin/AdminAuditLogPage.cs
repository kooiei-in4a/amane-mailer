using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using Amane.Mailer.Configuration;
using Amane.Mailer.Data.Sqlite;
using Amane.Mailer.Data.Sqlite.Models;

namespace Amane.Mailer.Admin;

public static class AdminAuditLogPage
{
    private const int PageSize = 50;

    internal const string RetentionRunbookUrl =
        "https://github.com/kooiei-in4a/amane-mailer/blob/main/docs/ops/local-mailer-docker-runbook.md#admin-audit-retention";

    public static async Task<IResult> RenderAsync(
        HttpContext context,
        AdminAuditRepository auditRepository,
        AdminUserRepository userRepository,
        AdminDeadLetterCountCache deadLetterCountCache,
        MailRequestRepository mailRequestRepository,
        MailerAdminAuditRetentionOptions retentionOptions,
        CancellationToken cancellationToken)
    {
        var access = await userRepository.GetTenantAccessAsync(
            AdminAuditLog.ResolveActor(context),
            cancellationToken);
        if (access is null)
            return Results.StatusCode(StatusCodes.Status403Forbidden);

        var queryParams = context.Request.Query;
        var selectedEventType = queryParams["event_type"].ToString();
        if (!string.IsNullOrWhiteSpace(selectedEventType)
            && !AdminAuditLog.EventTypes.All.Contains(selectedEventType, StringComparer.Ordinal))
        {
            return Results.Text("Invalid event_type filter.", statusCode: StatusCodes.Status400BadRequest);
        }

        if (string.IsNullOrWhiteSpace(selectedEventType))
            selectedEventType = string.Empty;

        var actorInput = queryParams["actor"].ToString();
        string? selectedActor = null;
        if (!string.IsNullOrWhiteSpace(actorInput))
        {
            selectedActor = AdminAuditLog.NormalizeActor(actorInput);
            if (string.Equals(selectedActor, "unknown", StringComparison.Ordinal))
                return Results.Text("Invalid actor filter.", statusCode: StatusCodes.Status400BadRequest);
        }

        if (!TryParseDateFilter(queryParams["from"].ToString(), out var occurredFrom, out var selectedFrom))
            return Results.Text("Invalid from filter.", statusCode: StatusCodes.Status400BadRequest);

        if (!TryParseDateFilter(queryParams["to"].ToString(), out var occurredTo, out var selectedTo))
            return Results.Text("Invalid to filter.", statusCode: StatusCodes.Status400BadRequest);

        DateTimeOffset? occurredToExclusive = null;
        if (occurredTo is not null)
            occurredToExclusive = occurredTo.Value.AddDays(1);

        AdminAuditEventCursor? cursor = null;
        var cursorValue = queryParams["cursor"].ToString();
        if (!string.IsNullOrWhiteSpace(cursorValue))
        {
            if (!AdminAuditEventCursor.TryDecode(cursorValue, out var decodedCursor))
                return Results.Text("Invalid cursor.", statusCode: StatusCodes.Status400BadRequest);

            cursor = decodedCursor;
        }

        var page = await auditRepository.ListForAdminAsync(
            new AdminAuditListQuery
            {
                EventType = string.IsNullOrWhiteSpace(selectedEventType) ? null : selectedEventType,
                Actor = selectedActor,
                OccurredFrom = occurredFrom,
                OccurredToExclusive = occurredToExclusive,
                AllowedTenantIds = access.AllowedTenantIdsForQuery,
                CursorOccurredAt = cursor?.OccurredAt,
                CursorId = cursor?.Id,
                PageSize = PageSize,
            },
            cancellationToken);

        var deadLetterCount = await deadLetterCountCache.GetCountAsync(
            mailRequestRepository,
            access.AllowedTenantIdsForQuery,
            cancellationToken);

        context.Response.Headers.CacheControl = "no-store";
        return Results.Content(
            RenderHtml(
                page,
                access,
                selectedEventType,
                actorInput,
                selectedFrom,
                selectedTo,
                cursorValue,
                deadLetterCount,
                retentionOptions),
            "text/html; charset=utf-8");
    }

    private static string RenderHtml(
        AdminAuditListPage page,
        AdminTenantAccess access,
        string selectedEventType,
        string actorInput,
        string selectedFrom,
        string selectedTo,
        string? currentCursor,
        int deadLetterCount,
        MailerAdminAuditRetentionOptions retentionOptions)
    {
        var html = new StringBuilder();
        AdminLayout.AppendDocumentStart(html, "監査ログ - Amane Admin", AdminNavItem.AuditLog, deadLetterCount);

        AppendScopeNote(html, access);
        AppendRetentionNote(html, retentionOptions);

        html.AppendLine("""
                <section class="admin-toolbar" aria-label="監査ログフィルタ">
                  <form method="get" action="/admin/audit-log" class="filters">
            """);

        AppendEventTypeFilter(html, selectedEventType);
        AppendActorFilter(html, actorInput);
        AppendDateFilter(html, "from", "開始日", selectedFrom);
        AppendDateFilter(html, "to", "終了日", selectedTo);

        html.AppendLine("""
                    <button type="submit">適用</button>
                  </form>
                </section>
                <section class="table-region" aria-label="監査ログ一覧">
                  <table class="admin-table">
                    <thead>
                      <tr>
                        <th>ID</th>
                        <th>時刻</th>
                        <th>イベント</th>
                        <th>actor</th>
                        <th>結果</th>
                        <th>target</th>
                        <th>field</th>
                      </tr>
                    </thead>
                    <tbody>
            """);

        if (page.Items.Count == 0)
        {
            html.AppendLine("""
                      <tr>
                        <td class="empty-row" colspan="7">監査イベントがありません</td>
                      </tr>
                """);
        }
        else
        {
            foreach (var item in page.Items)
            {
                AppendRow(html, item);
            }
        }

        html.AppendLine("""
                    </tbody>
                  </table>
                </section>
            """);

        AppendPager(
            html,
            selectedEventType,
            actorInput,
            selectedFrom,
            selectedTo,
            currentCursor,
            page.NextCursor);

        AdminLayout.AppendDocumentEnd(html);
        return html.ToString();
    }

    private static void AppendScopeNote(StringBuilder html, AdminTenantAccess access)
    {
        html.AppendLine("                <section class=\"ops-section\" aria-label=\"Tenant scope\">");
        html.AppendLine("                  <p class=\"ops-meta\">");
        if (access.IsBreakGlass)
        {
            html.AppendLine("                    break-glass 管理者: 全監査イベントを閲覧できます。");
        }
        else
        {
            html.AppendLine("                    scoped 管理者: <code>mail_request</code> 対象イベントは許可 tenant のみ表示します。");
            html.AppendLine("                    認証・セッションイベントは tenant 非依存のため service-wide で表示します（PII は含みません）。");
        }

        html.AppendLine("                  </p>");
        html.AppendLine("                </section>");
    }

    private static void AppendRetentionNote(StringBuilder html, MailerAdminAuditRetentionOptions retentionOptions)
    {
        html.AppendLine("                <section class=\"ops-section\" aria-label=\"Audit retention\">");
        html.AppendLine("                  <p class=\"ops-meta\">");
        html.Append("                    保持期間: ");
        html.Append(Html(retentionOptions.RetentionDays.ToString(CultureInfo.InvariantCulture)));
        html.AppendLine(" 日（<code>MAILER_ADMIN_AUDIT_RETENTION_DAYS</code>）。append-only のため UI から削除できません。");
        html.Append("                    詳細: <a href=\"");
        html.Append(Html(RetentionRunbookUrl));
        html.AppendLine("\">Admin audit retention runbook</a>");
        html.AppendLine("                  </p>");
        html.AppendLine("                </section>");
    }

    private static void AppendEventTypeFilter(StringBuilder html, string selectedEventType)
    {
        html.AppendLine("""
                    <label>
                      <span>イベント種別</span>
                      <select name="event_type">
            """);

        AppendOption(html, string.Empty, "全", selectedEventType);
        foreach (var eventType in AdminAuditLog.EventTypes.All)
        {
            AppendOption(html, eventType, eventType, selectedEventType);
        }

        html.AppendLine("""
                      </select>
                    </label>
            """);
    }

    private static void AppendActorFilter(StringBuilder html, string actorInput)
    {
        html.AppendLine("""
                    <label>
                      <span>actor</span>
                      <input type="text" name="actor" maxlength="256" autocomplete="off"
            """);
        html.Append(" value=\"");
        html.Append(Html(actorInput));
        html.AppendLine("\">");
        html.AppendLine("                    </label>");
    }

    private static void AppendDateFilter(StringBuilder html, string name, string label, string value)
    {
        html.Append("                    <label><span>");
        html.Append(Html(label));
        html.AppendLine("</span>");
        html.Append("                      <input type=\"date\" name=\"");
        html.Append(Html(name));
        html.Append("\" value=\"");
        html.Append(Html(value));
        html.AppendLine("\">");
        html.AppendLine("                    </label>");
    }

    private static void AppendOption(StringBuilder html, string value, string text, string selectedValue)
    {
        var selected = string.Equals(value, selectedValue, StringComparison.Ordinal)
            ? " selected"
            : string.Empty;
        html.Append("<option value=\"");
        html.Append(Html(value));
        html.Append('"');
        html.Append(selected);
        html.Append('>');
        html.Append(Html(text));
        html.AppendLine("</option>");
    }

    private static void AppendRow(StringBuilder html, AdminAuditEventRow item)
    {
        html.AppendLine("                  <tr>");
        html.Append("                    <td><a href=\"/admin/audit-log/");
        html.Append(Html(item.Id.ToString(CultureInfo.InvariantCulture)));
        html.Append("\">");
        html.Append(Html(item.Id.ToString(CultureInfo.InvariantCulture)));
        html.AppendLine("</a></td>");
        AppendCell(html, FormatLocalTime(item.OccurredAt));
        AppendCell(html, item.EventType);
        AppendCell(html, item.Actor);
        AppendCell(html, item.Result);
        AppendCell(html, FormatTarget(item));
        AppendCell(html, item.FieldName ?? string.Empty);
        html.AppendLine("                  </tr>");
    }

    private static string FormatTarget(AdminAuditEventRow item)
    {
        if (string.IsNullOrWhiteSpace(item.TargetType) && string.IsNullOrWhiteSpace(item.TargetId))
            return string.Empty;

        if (string.Equals(item.TargetType, AdminAuditLog.TargetTypes.MailRequest, StringComparison.Ordinal)
            && Guid.TryParse(item.TargetId, out var mailRequestId))
        {
            return $"{item.TargetType}/{mailRequestId:D}";
        }

        return $"{item.TargetType}/{item.TargetId}";
    }

    private static void AppendCell(StringBuilder html, string value)
    {
        html.Append("                    <td>");
        html.Append(Html(value));
        html.AppendLine("</td>");
    }

    private static void AppendPager(
        StringBuilder html,
        string selectedEventType,
        string actorInput,
        string selectedFrom,
        string selectedTo,
        string? currentCursor,
        string? nextCursor)
    {
        html.AppendLine("                <nav class=\"pager\" aria-label=\"ページング\">");
        if (string.IsNullOrWhiteSpace(currentCursor))
        {
            html.AppendLine("                  <span class=\"pager-disabled\">前へ</span>");
        }
        else
        {
            html.AppendLine("                  <button type=\"button\" class=\"pager-button\" onclick=\"history.back()\">前へ</button>");
        }

        if (string.IsNullOrWhiteSpace(nextCursor))
        {
            html.AppendLine("                  <span class=\"pager-disabled\">次へ</span>");
        }
        else
        {
            html.Append("                  <a class=\"pager-link\" href=\"");
            html.Append(Html(BuildListUrl(selectedEventType, actorInput, selectedFrom, selectedTo, nextCursor)));
            html.AppendLine("\">次へ</a>");
        }

        html.AppendLine("                </nav>");
    }

    internal static string BuildListUrl(
        string selectedEventType,
        string actorInput,
        string selectedFrom,
        string selectedTo,
        string? cursor)
    {
        var query = new List<KeyValuePair<string, string>>();
        if (!string.IsNullOrWhiteSpace(selectedEventType))
            query.Add(new("event_type", selectedEventType));
        if (!string.IsNullOrWhiteSpace(actorInput))
            query.Add(new("actor", actorInput));
        if (!string.IsNullOrWhiteSpace(selectedFrom))
            query.Add(new("from", selectedFrom));
        if (!string.IsNullOrWhiteSpace(selectedTo))
            query.Add(new("to", selectedTo));
        if (!string.IsNullOrWhiteSpace(cursor))
            query.Add(new("cursor", cursor));

        if (query.Count == 0)
            return "/admin/audit-log";

        return "/admin/audit-log?" + string.Join(
            '&',
            query.Select(parameter =>
                $"{Uri.EscapeDataString(parameter.Key)}={Uri.EscapeDataString(parameter.Value)}"));
    }

    private static bool TryParseDateFilter(
        string value,
        out DateTimeOffset? parsed,
        out string selected)
    {
        parsed = null;
        selected = string.Empty;
        if (string.IsNullOrWhiteSpace(value))
            return true;

        if (!DateOnly.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.None, out var dateOnly))
            return false;

        parsed = new DateTimeOffset(dateOnly.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc));
        selected = dateOnly.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        return true;
    }

    private static string FormatLocalTime(DateTimeOffset occurredAt) =>
        occurredAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss zzz", CultureInfo.InvariantCulture);

    private static string Html(string value) =>
        HtmlEncoder.Default.Encode(value);
}
