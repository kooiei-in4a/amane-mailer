using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using Amane.Mailer.Configuration;
using Amane.Mailer.Data.Sqlite;
using Amane.Mailer.Data.Sqlite.Models;

namespace Amane.Mailer.Admin;

public static class AdminMailRequestsPage
{
    private const int PageSize = 50;

    public static async Task<IResult> RenderAsync(
        HttpContext context,
        MailRequestRepository repository,
        MailerTenantRegistry tenantRegistry,
        AdminUserRepository userRepository,
        AdminDeadLetterCountCache deadLetterCountCache,
        MailerAdminOptions options,
        CancellationToken cancellationToken)
    {
        var query = context.Request.Query;
        if (!TryParseStatus(query["status"].ToString(), out var status, out var selectedStatus))
            return Results.Text("Invalid status filter.", statusCode: StatusCodes.Status400BadRequest);

        var access = await userRepository.GetTenantAccessAsync(
            AdminAuditLog.ResolveActor(context),
            cancellationToken);
        if (access is null)
            return Results.StatusCode(StatusCodes.Status403Forbidden);

        Guid? tenantId = null;
        var selectedTenantId = query["tenant_id"].ToString();
        if (!string.IsNullOrWhiteSpace(selectedTenantId))
        {
            if (!Guid.TryParse(selectedTenantId, out var parsedTenantId))
                return Results.Text("Invalid tenant_id filter.", statusCode: StatusCodes.Status400BadRequest);

            tenantId = parsedTenantId;
            selectedTenantId = parsedTenantId.ToString("D");
            if (!access.CanAccessTenant(parsedTenantId))
                return Results.StatusCode(StatusCodes.Status403Forbidden);
        }

        var sourceService = query["source_service"].ToString();
        if (string.IsNullOrWhiteSpace(sourceService))
            sourceService = null;

        AdminMailRequestCursor? cursor = null;
        var cursorValue = query["cursor"].ToString();
        if (!string.IsNullOrWhiteSpace(cursorValue))
        {
            if (!AdminMailRequestCursor.TryDecode(cursorValue, out var decodedCursor))
                return Results.Text("Invalid cursor.", statusCode: StatusCodes.Status400BadRequest);

            cursor = decodedCursor;
        }

        var listQuery = new AdminMailRequestListQuery
        {
            Status = status,
            TenantId = tenantId,
            AllowedTenantIds = access.AllowedTenantIdsForQuery,
            SourceService = sourceService,
            CursorUpdatedAt = cursor?.UpdatedAt,
            CursorId = cursor?.Id,
            PageSize = PageSize,
        };

        var page = await repository.ListForAdminAsync(listQuery, cancellationToken);
        var counts = await repository.CountByStatusForAdminAsync(
            new AdminMailRequestListQuery
            {
                TenantId = tenantId,
                AllowedTenantIds = access.AllowedTenantIdsForQuery,
                SourceService = sourceService,
            },
            cancellationToken);

        var deadLetterCount = await deadLetterCountCache.GetCountAsync(
            repository,
            access.AllowedTenantIdsForQuery,
            cancellationToken);
        var visibleTenants = tenantRegistry.ListTenants()
            .Where(tenant => access.CanAccessTenant(tenant.TenantId))
            .ToArray();

        context.Response.Headers.CacheControl = "no-store";
        return Results.Content(
            RenderHtml(
                page,
                counts,
                visibleTenants,
                selectedStatus,
                selectedTenantId,
                sourceService,
                cursorValue,
                deadLetterCount,
                access,
                options),
            "text/html; charset=utf-8");
    }

    private static string RenderHtml(
        AdminMailRequestListPage page,
        AdminMailRequestStatusCounts counts,
        IReadOnlyList<MailerTenant> tenants,
        string selectedStatus,
        string selectedTenantId,
        string? selectedSourceService,
        string? currentCursor,
        int deadLetterCount,
        AdminTenantAccess access,
        MailerAdminOptions options)
    {
        var html = new StringBuilder();
        AdminLayout.AppendDocumentStart(
            html,
            "送信依頼 - Amane Admin",
            AdminNavItem.MailRequests,
            deadLetterCount,
            access);

        html.AppendLine("                <header class=\"page-intro\">");
        html.AppendLine("                  <h1>送信依頼</h1>");
        html.AppendLine("                  <p>受け付けたメール送信依頼の状態を確認します。行を選ぶと詳細と配送履歴を確認できます。</p>");
        html.AppendLine("                </header>");
        html.AppendLine("                <section class=\"admin-card\" aria-label=\"送信依頼一覧\">");

        AppendStatusLegend(html, counts, selectedStatus, selectedTenantId, selectedSourceService);

        html.AppendLine("""
                  <div class="admin-toolbar">
                    <form method="get" action="/admin/mail-requests" class="filters">
            """);

        AppendStatusFilter(html, selectedStatus);
        AppendTenantFilter(html, tenants, selectedTenantId);
        AppendSourceServiceFilter(html, tenants, selectedSourceService);

        html.AppendLine("""
                      <button type="submit">適用</button>
                    </form>
                    <a class="filter-clear" href="/admin/mail-requests">条件をクリア</a>
                  </div>
                  <div class="table-region">
                    <table class="admin-table mail-request-table">
                      <thead>
                        <tr>
                          <th>ステータス</th>
                          <th>ID</th>
                          <th>テナント</th>
                          <th>SOURCE_SERVICE</th>
                          <th>宛先</th>
                          <th>更新日時</th>
                          <th><span class="visually-hidden">詳細</span></th>
                        </tr>
                      </thead>
                      <tbody>
            """);

        var showDeliveryUnknownHint =
            string.IsNullOrWhiteSpace(selectedStatus)
            && string.IsNullOrWhiteSpace(currentCursor)
            && page.Items.Count > 0
            && counts.DeliveryUnknown == 0;

        if (showDeliveryUnknownHint)
            AppendDeliveryUnknownHintRow(html);

        if (page.Items.Count == 0)
        {
            html.AppendLine("""
                        <tr>
                          <td class="empty-row" colspan="7">送信依頼がありません</td>
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
                  </div>
            """);

        AppendPager(
            html,
            selectedStatus,
            selectedTenantId,
            selectedSourceService,
            currentCursor,
            page.NextCursor,
            page.Items.Count,
            FilteredTotal(counts, selectedStatus));

        html.AppendLine("                </section>");
        AdminLayout.AppendDocumentEnd(html);

        return html.ToString();
    }

    private static void AppendStatusLegend(
        StringBuilder html,
        AdminMailRequestStatusCounts counts,
        string selectedStatus,
        string selectedTenantId,
        string? selectedSourceService)
    {
        html.AppendLine("                  <div class=\"status-legend\" aria-label=\"ステータス件数\">");
        AppendStatusChip(html, "queued", "Queued", "queued", counts.Queued, "Queued は送信待ち", selectedStatus, selectedTenantId, selectedSourceService);
        AppendStatusChip(html, "processing", "Processing", "processing", counts.Processing, "Processing は処理中", selectedStatus, selectedTenantId, selectedSourceService);
        AppendStatusChip(html, "delivered", "Delivered", "delivered", counts.Delivered, "Delivered は配送完了", selectedStatus, selectedTenantId, selectedSourceService);
        AppendStatusChip(html, "failed", "Failed", "failed", counts.Failed, "Failed は失敗", selectedStatus, selectedTenantId, selectedSourceService);
        AppendStatusChip(html, "deadlettered", "DeadLettered", "deadlettered", counts.DeadLettered, "DeadLettered は自動処理が終了した終端状態", selectedStatus, selectedTenantId, selectedSourceService);
        AppendStatusChip(html, "cancelled", "Cancelled", "cancelled", counts.Cancelled, "Cancelled はキャンセルされた送信依頼の終端状態", selectedStatus, selectedTenantId, selectedSourceService);
        AppendStatusChip(html, "deliveryunknown", "DeliveryUnknown", "deliveryunknown", counts.DeliveryUnknown, "DeliveryUnknown はProviderの結果を確定できない状態", selectedStatus, selectedTenantId, selectedSourceService);
        html.AppendLine("                  </div>");
    }

    private static void AppendStatusChip(
        StringBuilder html,
        string statusKey,
        string label,
        string dotClass,
        int count,
        string title,
        string selectedStatus,
        string selectedTenantId,
        string? selectedSourceService)
    {
        var active = string.Equals(selectedStatus, statusKey, StringComparison.Ordinal);
        html.Append("                    <a class=\"status-chip");
        if (active)
            html.Append(" is-active");
        html.Append("\" title=\"");
        html.Append(title);
        html.Append("\" href=\"");
        html.Append(Html(BuildListUrl(statusKey, selectedTenantId, selectedSourceService, cursor: null)));
        html.Append("\"><span class=\"status-dot ");
        html.Append(Html(dotClass));
        html.Append("\" aria-hidden=\"true\"></span>");
        html.Append(Html(label));
        html.Append(" (");
        html.Append(count.ToString(CultureInfo.InvariantCulture));
        html.AppendLine(")</a>");
    }

    private static void AppendStatusFilter(StringBuilder html, string selectedStatus)
    {
        html.AppendLine("""
                      <label>
                        <span>ステータス</span>
                        <select name="status">
            """);

        AppendOption(html, string.Empty, "全", selectedStatus);
        AppendOption(html, "queued", "Queued", selectedStatus);
        AppendOption(html, "processing", "Processing", selectedStatus);
        AppendOption(html, "delivered", "Delivered", selectedStatus);
        AppendOption(html, "failed", "Failed", selectedStatus);
        AppendOption(html, "deadlettered", "DeadLettered", selectedStatus);
        AppendOption(html, "cancelled", "Cancelled", selectedStatus);
        AppendOption(html, "deliveryunknown", "DeliveryUnknown", selectedStatus);

        html.AppendLine("""
                        </select>
                      </label>
            """);
    }

    private static void AppendTenantFilter(
        StringBuilder html,
        IReadOnlyList<MailerTenant> tenants,
        string selectedTenantId)
    {
        html.AppendLine("""
                      <label>
                        <span>テナント</span>
                        <select name="tenant_id">
            """);

        AppendOption(html, string.Empty, "全", selectedTenantId);
        foreach (var tenant in tenants)
        {
            var value = tenant.TenantId.ToString("D");
            AppendOption(html, value, $"{tenant.Name} ({value})", selectedTenantId);
        }

        html.AppendLine("""
                        </select>
                      </label>
            """);
    }

    private static void AppendSourceServiceFilter(
        StringBuilder html,
        IReadOnlyList<MailerTenant> tenants,
        string? selectedSourceService)
    {
        html.AppendLine("""
                      <label>
                        <span>source_service</span>
                        <select name="source_service">
            """);

        AppendOption(html, string.Empty, "全", selectedSourceService ?? string.Empty);
        var services = tenants
            .SelectMany(tenant => tenant.SourceServices)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(source => source, StringComparer.Ordinal)
            .ToArray();
        foreach (var sourceService in services)
        {
            AppendOption(html, sourceService, sourceService, selectedSourceService ?? string.Empty);
        }

        if (!string.IsNullOrWhiteSpace(selectedSourceService)
            && !services.Contains(selectedSourceService, StringComparer.Ordinal))
        {
            AppendOption(html, selectedSourceService, selectedSourceService, selectedSourceService);
        }

        html.AppendLine("""
                        </select>
                      </label>
            """);
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

    private static void AppendDeliveryUnknownHintRow(StringBuilder html)
    {
        html.AppendLine("                        <tr class=\"empty-status-row\">");
        html.AppendLine("                          <td><span class=\"status-badge status-deliveryunknown\">—</span></td>");
        html.AppendLine("                          <td></td><td></td><td></td>");
        html.AppendLine("                          <td class=\"empty-hint\">DeliveryUnknown は現在 0 件です</td>");
        html.AppendLine("                          <td></td><td></td>");
        html.AppendLine("                        </tr>");
    }

    private static void AppendRow(StringBuilder html, AdminMailRequestListRow item, MailerAdminOptions options)
    {
        var statusText = StatusText((int)item.Status);
        var statusClass = StatusClass((int)item.Status);
        var href = "/admin/mail-requests/" + item.Id.ToString("D");
        var idN = item.Id.ToString("N");
        var tenantN = item.TenantId.ToString("N");

        html.Append("                        <tr class=\"mail-request-row\" onclick=\"window.location.href='");
        html.Append(Html(href));
        html.AppendLine("'\">");
        html.Append("                          <td><span class=\"status-badge ");
        html.Append(statusClass);
        html.Append("\">");
        html.Append(statusText);
        html.AppendLine("</span></td>");
        html.Append("                          <td><a class=\"mono\" href=\"");
        html.Append(Html(href));
        html.Append("\" title=\"");
        html.Append(Html(item.Id.ToString("D")));
        html.Append("\">");
        html.Append(Html(idN[..8] + "…"));
        html.AppendLine("</a></td>");
        html.Append("                          <td><span class=\"mono\" title=\"");
        html.Append(Html(item.TenantId.ToString("D")));
        html.Append("\">…");
        html.Append(Html(tenantN[^7..]));
        html.AppendLine("</span></td>");
        html.Append("                          <td><span class=\"source-chip\" title=\"");
        html.Append(Html(item.SourceService));
        html.Append("\">");
        html.Append(Html(item.SourceService));
        html.AppendLine("</span></td>");
        html.Append("                          <td class=\"col-recipients\" title=\"");
        html.Append(Html(AdminRecipientSummaryRenderer.RenderList(item.Recipients, options.MaskRecipients)));
        html.Append("\">");
        html.Append(Html(AdminRecipientSummaryRenderer.RenderCompact(item.Recipients, options.MaskRecipients)));
        html.AppendLine("</td>");
        AppendCell(html, FormatLocalTime(item.UpdatedAt));
        html.Append("                          <td class=\"row-chevron\"><a href=\"");
        html.Append(Html(href));
        html.AppendLine("\" aria-label=\"詳細\">›</a></td>");
        html.AppendLine("                        </tr>");
    }

    private static void AppendCell(StringBuilder html, string value)
    {
        html.Append("                          <td>");
        html.Append(Html(value));
        html.AppendLine("</td>");
    }

    private static void AppendPager(
        StringBuilder html,
        string selectedStatus,
        string selectedTenantId,
        string? selectedSourceService,
        string? currentCursor,
        string? nextCursor,
        int itemCount,
        int totalCount)
    {
        html.AppendLine("                  <nav class=\"pager\" aria-label=\"ページング\">");
        html.Append("                    <p class=\"pager-summary\">");
        html.Append(Html(BuildPagerSummary(currentCursor, itemCount, totalCount)));
        html.AppendLine("</p>");
        html.AppendLine("                    <div class=\"pager-actions\">");
        if (string.IsNullOrWhiteSpace(currentCursor))
        {
            html.AppendLine("                      <span class=\"pager-disabled\">前へ</span>");
        }
        else
        {
            html.AppendLine("                      <button type=\"button\" class=\"pager-button\" onclick=\"history.back()\">前へ</button>");
        }

        if (string.IsNullOrWhiteSpace(nextCursor))
        {
            html.AppendLine("                      <span class=\"pager-disabled\">次へ</span>");
        }
        else
        {
            html.Append("                      <a class=\"pager-link\" href=\"");
            html.Append(Html(BuildListUrl(selectedStatus, selectedTenantId, selectedSourceService, nextCursor)));
            html.AppendLine("\">次へ</a>");
        }

        html.AppendLine("                    </div>");
        html.AppendLine("                  </nav>");
    }

    private static string BuildPagerSummary(string? currentCursor, int itemCount, int totalCount)
    {
        if (totalCount == 0)
            return "全 0 件";

        if (string.IsNullOrWhiteSpace(currentCursor))
        {
            var end = Math.Max(itemCount, 0);
            return $"全 {totalCount.ToString(CultureInfo.InvariantCulture)} 件中 1-{end.ToString(CultureInfo.InvariantCulture)} 件を表示";
        }

        return $"全 {totalCount.ToString(CultureInfo.InvariantCulture)} 件";
    }

    private static int FilteredTotal(AdminMailRequestStatusCounts counts, string selectedStatus)
    {
        if (string.IsNullOrWhiteSpace(selectedStatus))
            return counts.Total - counts.DeliveryUnknown;

        return selectedStatus switch
        {
            "queued" => counts.Queued,
            "processing" => counts.Processing,
            "delivered" => counts.Delivered,
            "failed" => counts.Failed,
            "deadlettered" => counts.DeadLettered,
            "cancelled" => counts.Cancelled,
            "deliveryunknown" => counts.DeliveryUnknown,
            _ => counts.Total,
        };
    }

    private static string BuildListUrl(
        string selectedStatus,
        string selectedTenantId,
        string? selectedSourceService,
        string? cursor)
    {
        var query = new List<KeyValuePair<string, string>>();
        if (!string.IsNullOrWhiteSpace(selectedStatus))
            query.Add(new("status", selectedStatus));
        if (!string.IsNullOrWhiteSpace(selectedTenantId))
            query.Add(new("tenant_id", selectedTenantId));
        if (!string.IsNullOrWhiteSpace(selectedSourceService))
            query.Add(new("source_service", selectedSourceService));
        if (!string.IsNullOrWhiteSpace(cursor))
            query.Add(new("cursor", cursor));

        if (query.Count == 0)
            return "/admin/mail-requests";

        return "/admin/mail-requests?" + string.Join(
            '&',
            query.Select(parameter =>
                $"{Uri.EscapeDataString(parameter.Key)}={Uri.EscapeDataString(parameter.Value)}"));
    }

    private static bool TryParseStatus(string value, out int? status, out string selectedStatus)
    {
        status = null;
        selectedStatus = string.Empty;
        if (string.IsNullOrWhiteSpace(value))
            return true;

        var normalized = value.Trim().Replace("_", string.Empty, StringComparison.Ordinal).ToLowerInvariant();
        status = normalized switch
        {
            "queued" => (int)MailRequestState.Queued,
            "processing" => (int)MailRequestState.Processing,
            "delivered" => (int)MailRequestState.Delivered,
            "failed" => (int)MailRequestState.Failed,
            "deadlettered" => (int)MailRequestState.DeadLettered,
            "cancelled" => (int)MailRequestState.Cancelled,
            "deliveryunknown" => (int)MailRequestState.DeliveryUnknown,
            _ => null,
        };

        if (status is null)
            return false;

        selectedStatus = normalized;
        return true;
    }

    private static string StatusText(int status) =>
        status switch
        {
            (int)MailRequestState.Queued => "Queued",
            (int)MailRequestState.Processing => "Processing",
            (int)MailRequestState.Delivered => "Delivered",
            (int)MailRequestState.Failed => "Failed",
            (int)MailRequestState.DeadLettered => "DeadLettered",
            (int)MailRequestState.Cancelled => "Cancelled",
            (int)MailRequestState.DeliveryUnknown => "DeliveryUnknown",
            _ => "Unknown",
        };

    private static string StatusClass(int status) =>
        status switch
        {
            (int)MailRequestState.Queued => "status-queued",
            (int)MailRequestState.Processing => "status-processing",
            (int)MailRequestState.Delivered => "status-delivered",
            (int)MailRequestState.Failed => "status-failed",
            (int)MailRequestState.DeadLettered => "status-deadlettered",
            (int)MailRequestState.Cancelled => "status-cancelled",
            (int)MailRequestState.DeliveryUnknown => "status-deliveryunknown",
            _ => "status-unknown",
        };

    private static string FormatLocalTime(DateTimeOffset updatedAt) =>
        updatedAt.ToLocalTime().ToString("MM/dd HH:mm", CultureInfo.InvariantCulture);

    private static string Html(string value) =>
        HtmlEncoder.Default.Encode(value);
}
