using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using Amane.Mailer.Data.Sqlite;
using Amane.Mailer.Webhooks;
using Amane.Mailer.Webhooks.Models;
using Microsoft.AspNetCore.Antiforgery;

namespace Amane.Mailer.Admin;

public static class AdminWebhookDeadLettersPage
{
    private const int PageSize = 50;

    public static async Task<IResult> RenderAsync(
        HttpContext context,
        DeliveryEventRepository repository,
        AdminUserRepository userRepository,
        AdminDeadLetterCountCache deadLetterCountCache,
        MailRequestRepository mailRequestRepository,
        IAntiforgery antiforgery,
        CancellationToken cancellationToken)
    {
        var access = await userRepository.GetTenantAccessAsync(
            AdminAuditLog.ResolveActor(context),
            cancellationToken);
        if (access is null)
            return Results.StatusCode(StatusCodes.Status403Forbidden);

        AdminWebhookDeadLetterCursor? cursor = null;
        var cursorValue = context.Request.Query["cursor"].ToString();
        if (!string.IsNullOrWhiteSpace(cursorValue))
        {
            if (!AdminWebhookDeadLetterCursor.TryDecode(cursorValue, out var decodedCursor))
                return Results.Text("Invalid cursor.", statusCode: StatusCodes.Status400BadRequest);

            cursor = decodedCursor;
        }

        var page = await repository.ListDeadLettersForAdminAsync(
            new AdminWebhookDeadLetterListQuery
            {
                CursorCompletedAt = cursor?.CompletedAt,
                CursorId = cursor?.Id,
                AllowedTenantIds = access.AllowedTenantIdsForQuery,
                PageSize = PageSize,
            },
            cancellationToken);

        var deadLetterCount = await deadLetterCountCache.GetCountAsync(
            mailRequestRepository,
            access.AllowedTenantIdsForQuery,
            cancellationToken);

        context.Response.Headers.CacheControl = "no-store";
        return Results.Content(
            RenderHtml(page, deadLetterCount, cursorValue),
            "text/html; charset=utf-8");
    }

    private static string RenderHtml(
        AdminWebhookDeadLetterListPage page,
        int deadLetterCount,
        string? currentCursor)
    {
        var html = new StringBuilder();
        AdminLayout.AppendDocumentStart(
            html,
            "Webhook Dead Letters - Amane Admin",
            AdminNavItem.Ops,
            deadLetterCount);

        html.AppendLine("""
                <section class="ops-section" aria-label="Webhook Dead Letter 一覧">
                  <h2 class="ops-heading">Webhook Dead Letters</h2>
                  <p class="ops-meta">Webhook URL や secret は表示しません。Consumer 側の重複排除には <code>event_id</code> を使用してください。</p>
                  <div class="table-region">
                    <table class="admin-table">
                      <thead>
                        <tr>
                          <th>event_id</th>
                          <th>mail_request_id</th>
                          <th>tenant_id</th>
                          <th>source_service</th>
                          <th>event_type</th>
                          <th>attempt_count</th>
                          <th>last_error_code</th>
                          <th>completed_at</th>
                        </tr>
                      </thead>
                      <tbody>
            """);

        if (page.Rows.Count == 0)
        {
            html.AppendLine("""
                        <tr>
                          <td class="empty-row" colspan="8">Webhook Dead Letter はありません</td>
                        </tr>
                """);
        }
        else
        {
            foreach (var item in page.Rows)
            {
                AppendRow(html, item);
            }
        }

        html.AppendLine("""
                      </tbody>
                    </table>
                  </div>
                </section>
            """);

        AppendPager(html, currentCursor, page.NextCursor);
        AdminLayout.AppendDocumentEnd(html);
        return html.ToString();
    }

    private static void AppendRow(StringBuilder html, AdminWebhookDeadLetterListRow item)
    {
        html.AppendLine("                        <tr>");
        AppendCell(html, item.EventId.ToString("D"));
        AppendCell(html, item.MailRequestId.ToString("D"));
        AppendCell(html, item.TenantId.ToString("D"));
        AppendCell(html, item.SourceService);
        AppendCell(html, item.EventType);
        AppendCell(html, item.AttemptCount.ToString(CultureInfo.InvariantCulture));
        AppendCell(html, item.LastErrorCode ?? string.Empty);
        AppendCell(html, FormatLocalTime(item.CompletedAt));
        html.AppendLine("                        </tr>");
    }

    private static void AppendCell(StringBuilder html, string value)
    {
        html.Append("                          <td>");
        html.Append(Html(value));
        html.AppendLine("</td>");
    }

    private static void AppendPager(StringBuilder html, string? currentCursor, string? nextCursor)
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
            html.Append("                  <a class=\"pager-link\" href=\"/admin/webhook-dead-letters?cursor=");
            html.Append(Html(nextCursor));
            html.AppendLine("\">次へ</a>");
        }

        html.AppendLine("                </nav>");
    }

    private static string FormatLocalTime(DateTimeOffset completedAt) =>
        completedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss zzz", CultureInfo.InvariantCulture);

    private static string Html(string value) =>
        HtmlEncoder.Default.Encode(value);
}
