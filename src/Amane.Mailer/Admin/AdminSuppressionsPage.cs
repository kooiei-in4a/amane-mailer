using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using Amane.Mailer.Configuration;
using Amane.Mailer.Data.Sqlite;
using Amane.Mailer.Data.Sqlite.Models;

namespace Amane.Mailer.Admin;

/// <summary>
/// Admin list of mail_suppressions (view-only). Removal is CLI (#400).
/// Recipient addresses follow ADR 0013 D-05 / MailerAdminOptions.MaskRecipients.
/// </summary>
public static class AdminSuppressionsPage
{
    private const int PageSize = 50;

    public static async Task<IResult> RenderAsync(
        HttpContext context,
        MailSuppressionRepository suppressionRepository,
        MailRequestRepository mailRequestRepository,
        MailerTenantRegistry tenantRegistry,
        AdminUserRepository userRepository,
        AdminDeadLetterCountCache deadLetterCountCache,
        MailerAdminOptions options,
        CancellationToken cancellationToken)
    {
        var access = await userRepository.GetTenantAccessAsync(
            AdminAuditLog.ResolveActor(context),
            cancellationToken);
        if (access is null)
            return Results.StatusCode(StatusCodes.Status403Forbidden);

        Guid? tenantId = null;
        var selectedTenantId = context.Request.Query["tenant_id"].ToString();
        if (!string.IsNullOrWhiteSpace(selectedTenantId))
        {
            if (!Guid.TryParse(selectedTenantId, out var parsedTenantId))
                return Results.Text("Invalid tenant_id filter.", statusCode: StatusCodes.Status400BadRequest);

            tenantId = parsedTenantId;
            selectedTenantId = parsedTenantId.ToString("D");
            if (!access.CanAccessTenant(parsedTenantId))
                return Results.StatusCode(StatusCodes.Status403Forbidden);
        }

        AdminSuppressionCursor? cursor = null;
        var cursorValue = context.Request.Query["cursor"].ToString();
        if (!string.IsNullOrWhiteSpace(cursorValue))
        {
            if (!AdminSuppressionCursor.TryDecode(cursorValue, out var decodedCursor))
                return Results.Text("Invalid cursor.", statusCode: StatusCodes.Status400BadRequest);

            cursor = decodedCursor;
        }

        var page = await suppressionRepository.ListForAdminAsync(
            new AdminSuppressionListQuery
            {
                TenantId = tenantId,
                AllowedTenantIds = access.AllowedTenantIdsForQuery,
                CursorCreatedAt = cursor?.CreatedAt,
                CursorId = cursor?.Id,
                PageSize = PageSize,
            },
            cancellationToken);

        var deadLetterCount = await deadLetterCountCache.GetCountAsync(
            mailRequestRepository,
            access.AllowedTenantIdsForQuery,
            cancellationToken);
        var visibleTenants = tenantRegistry.ListTenants()
            .Where(tenant => access.CanAccessTenant(tenant.TenantId))
            .ToArray();

        context.Response.Headers.CacheControl = "no-store";
        return Results.Content(
            RenderHtml(page, deadLetterCount, selectedTenantId, cursorValue, visibleTenants, options),
            "text/html; charset=utf-8");
    }

    internal static string RenderHtml(
        AdminSuppressionListPage page,
        int deadLetterCount,
        string? selectedTenantId,
        string? currentCursor,
        IReadOnlyList<MailerTenant> visibleTenants,
        MailerAdminOptions options)
    {
        var html = new StringBuilder();
        AdminLayout.AppendDocumentStart(html, "抑制リスト - Amane Admin", AdminNavItem.Suppressions, deadLetterCount);

        html.AppendLine("""
                <section class="filter-region" aria-label="抑制リスト絞り込み">
                  <form method="get" action="/admin/suppressions" class="filter-form">
                    <label>
                      テナント
                      <select name="tenant_id">
                        <option value="">すべて</option>
            """);

        foreach (var tenant in visibleTenants)
        {
            var id = tenant.TenantId.ToString("D");
            html.Append("                        <option value=\"");
            html.Append(Html(id));
            html.Append('"');
            if (string.Equals(selectedTenantId, id, StringComparison.OrdinalIgnoreCase))
                html.Append(" selected");
            html.Append('>');
            html.Append(Html(id));
            html.AppendLine("</option>");
        }

        html.AppendLine("""
                      </select>
                    </label>
                    <button type="submit" class="action-button">絞り込む</button>
                  </form>
                  <p class="filter-note">閲覧のみ。解除は CLI（<code>db suppressions remove</code> / #400）を使用してください。</p>
                </section>
            """);

        html.AppendLine("""
                <section class="table-region" aria-label="抑制リスト一覧">
                  <table class="admin-table">
                    <thead>
                      <tr>
                        <th>テナント</th>
                        <th>宛先</th>
                        <th>理由</th>
                        <th>source_bounce_event_id</th>
                        <th>登録日時</th>
                      </tr>
                    </thead>
                    <tbody>
            """);

        if (page.Items.Count == 0)
        {
            html.AppendLine("""
                      <tr>
                        <td class="empty-row" colspan="5">抑制エントリはありません</td>
                      </tr>
                """);
        }
        else
        {
            foreach (var item in page.Items)
                AppendRow(html, item, options);
        }

        html.AppendLine("""
                    </tbody>
                  </table>
                </section>
            """);

        AppendPager(html, selectedTenantId, currentCursor, page.NextCursor);
        AdminLayout.AppendDocumentEnd(html);
        return html.ToString();
    }

    private static void AppendRow(
        StringBuilder html,
        AdminSuppressionListRow item,
        MailerAdminOptions options)
    {
        var recipient = options.MaskRecipients
            ? MaskRecipient(item.RecipientEmail)
            : item.RecipientEmail;

        html.AppendLine("                  <tr>");
        AppendCell(html, item.TenantId.ToString("D"));
        AppendCell(html, recipient);
        AppendCell(html, item.Reason);
        AppendCell(html, item.SourceBounceEventId?.ToString("D") ?? string.Empty);
        AppendCell(html, FormatLocalTime(item.CreatedAt));
        html.AppendLine("                  </tr>");
    }

    private static void AppendCell(StringBuilder html, string value)
    {
        html.Append("                    <td>");
        html.Append(Html(value));
        html.AppendLine("</td>");
    }

    private static void AppendPager(
        StringBuilder html,
        string? selectedTenantId,
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
            html.Append("                  <a class=\"pager-link\" href=\"/admin/suppressions?");
            if (!string.IsNullOrWhiteSpace(selectedTenantId))
            {
                html.Append("tenant_id=");
                html.Append(Html(selectedTenantId));
                html.Append('&');
            }

            html.Append("cursor=");
            html.Append(Html(nextCursor));
            html.AppendLine("\">次へ</a>");
        }

        html.AppendLine("                </nav>");
    }

    private static string MaskRecipient(string email)
    {
        if (string.IsNullOrEmpty(email))
            return "***";

        var at = email.IndexOf('@', StringComparison.Ordinal);
        if (at <= 0)
            return $"{email[0]}***";

        return $"{email[0]}***{email[at..]}";
    }

    private static string FormatLocalTime(DateTimeOffset value) =>
        value.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss zzz", CultureInfo.InvariantCulture);

    private static string Html(string value) =>
        HtmlEncoder.Default.Encode(value);
}
