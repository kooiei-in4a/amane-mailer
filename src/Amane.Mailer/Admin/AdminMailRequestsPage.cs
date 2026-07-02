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

        var page = await repository.ListForAdminAsync(
            new AdminMailRequestListQuery
            {
                Status = status,
                TenantId = tenantId,
                AllowedTenantIds = access.AllowedTenantIdsForQuery,
                SourceService = sourceService,
                CursorUpdatedAt = cursor?.UpdatedAt,
                CursorId = cursor?.Id,
                PageSize = PageSize,
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
                visibleTenants,
                selectedStatus,
                selectedTenantId,
                sourceService,
                cursorValue,
                deadLetterCount,
                options),
            "text/html; charset=utf-8");
    }

    private static string RenderHtml(
        AdminMailRequestListPage page,
        IReadOnlyList<MailerTenant> tenants,
        string selectedStatus,
        string selectedTenantId,
        string? selectedSourceService,
        string? currentCursor,
        int deadLetterCount,
        MailerAdminOptions options)
    {
        var html = new StringBuilder();
        AdminLayout.AppendDocumentStart(html, "送信依頼 - Amane Admin", AdminNavItem.MailRequests, deadLetterCount);

        html.AppendLine("""
                <section class="admin-toolbar" aria-label="送信依頼フィルタ">
                  <form method="get" action="/admin/mail-requests" class="filters">
            """);

        AppendStatusFilter(html, selectedStatus);
        AppendTenantFilter(html, tenants, selectedTenantId);
        AppendSourceServiceFilter(html, tenants, selectedSourceService);

        html.AppendLine("""
                    <button type="submit">適用</button>
                  </form>
                </section>
                <section class="table-region" aria-label="送信依頼一覧">
                  <table class="admin-table">
                    <thead>
                      <tr>
                        <th>ID</th>
                        <th>テナント</th>
                        <th>source_service</th>
                        <th>宛先</th>
                        <th>件名</th>
                        <th>ステータス</th>
                        <th>試行回数</th>
                        <th>更新日時</th>
                      </tr>
                    </thead>
                    <tbody>
            """);

        if (page.Items.Count == 0)
        {
            html.AppendLine("""
                      <tr>
                        <td class="empty-row" colspan="8">送信依頼がありません</td>
                      </tr>
                """);
        }
        else
        {
            foreach (var item in page.Items)
            {
                AppendRow(html, item, options);
            }
        }

        html.AppendLine("""
                    </tbody>
                  </table>
                </section>
            """);

        AppendPager(
            html,
            selectedStatus,
            selectedTenantId,
            selectedSourceService,
            currentCursor,
            page.NextCursor);

        AdminLayout.AppendDocumentEnd(html);

        return html.ToString();
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

    private static void AppendRow(StringBuilder html, AdminMailRequestListRow item, MailerAdminOptions options)
    {
        var statusText = StatusText((int)item.Status);
        var statusClass = StatusClass((int)item.Status);

        html.AppendLine("                  <tr>");
        html.Append("                    <td><a href=\"/admin/mail-requests/");
        html.Append(Html(item.Id.ToString("D")));
        html.Append("\">");
        html.Append(Html(item.Id.ToString("D")));
        html.AppendLine("</a></td>");
        AppendCell(html, item.TenantId.ToString("D"));
        AppendCell(html, item.SourceService);
        AppendCell(html, options.MaskRecipients ? MaskRecipient(item.RecipientEmail) : item.RecipientEmail);
        AppendCell(html, options.MaskSubjects ? MaskSubject(item.Subject) : item.Subject);
        html.Append("                    <td><span class=\"status-badge ");
        html.Append(statusClass);
        html.Append("\">");
        html.Append(statusText);
        html.AppendLine("</span></td>");
        AppendCell(html, $"{item.AttemptCount} / {item.MaxAttempts}");
        AppendCell(html, FormatLocalTime(item.UpdatedAt));
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
        string selectedStatus,
        string selectedTenantId,
        string? selectedSourceService,
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
            html.Append(Html(BuildListUrl(selectedStatus, selectedTenantId, selectedSourceService, nextCursor)));
            html.AppendLine("\">次へ</a>");
        }

        html.AppendLine("                </nav>");
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
            _ => "status-unknown",
        };

    private static string MaskRecipient(string email)
    {
        if (string.IsNullOrEmpty(email))
            return "***";

        var at = email.IndexOf('@', StringComparison.Ordinal);
        if (at <= 0)
            return $"{email[0]}***";

        return $"{email[0]}***{email[at..]}";
    }

    private static string MaskSubject(string subject)
    {
        if (subject.Length <= 12)
            return subject;

        return subject[..12] + "...";
    }

    private static string FormatLocalTime(DateTimeOffset updatedAt) =>
        updatedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss zzz", CultureInfo.InvariantCulture);

    private static string Html(string value) =>
        HtmlEncoder.Default.Encode(value);
}
