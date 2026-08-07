using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using Amane.Mailer.Configuration;
using Amane.Mailer.Data.Sqlite;
using Amane.Mailer.Data.Sqlite.Models;
using Microsoft.Extensions.Logging;

namespace Amane.Mailer.Admin;

/// <summary>
/// Admin list of mail_suppressions (view-only). Removal uses CLI <c>db suppressions remove</c> (#400).
/// Unmasked recipients require MAILER_ADMIN_PII_LIST_MODE=visible (ADR 0013 D-05/D-07/D-08);
/// MASK_RECIPIENTS=false alone must not unmask this page.
/// </summary>
public static class AdminSuppressionsPage
{
    private const int PageSize = 50;
    private const string AuditLoggerCategoryName = "Amane.Mailer.Admin.SuppressionsAccessAudit";
    private static readonly EventId ListUnmaskedAuditWriteFailedEvent =
        new(1003, "AdminSuppressionsListUnmaskedAuditWriteFailed");

    public static async Task<IResult> RenderAsync(
        HttpContext context,
        MailSuppressionRepository suppressionRepository,
        MailRequestRepository mailRequestRepository,
        MailerTenantRegistry tenantRegistry,
        AdminUserRepository userRepository,
        AdminDeadLetterCountCache deadLetterCountCache,
        AdminAuditRepository auditRepository,
        MailerAdminOptions options,
        TimeProvider timeProvider,
        ILoggerFactory loggerFactory,
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

        var showUnmasked = AdminCapabilities.Has(options, AdminCapabilities.ViewUnmaskedListPii);
        var visibleTenants = tenantRegistry.ListTenants()
            .Where(tenant => access.CanAccessTenant(tenant.TenantId))
            .ToArray();

        // Unmasked suppressions audits are tenant-scoped. Resolve tenant before any list query
        // so nav (/admin/suppressions) never returns 400 and never audits cross-tenant views.
        if (showUnmasked && tenantId is null)
        {
            if (visibleTenants.Length == 1)
            {
                return Results.Redirect(
                    "/admin/suppressions?tenant_id=" + visibleTenants[0].TenantId.ToString("D"));
            }

            var deadLetterCountForSelection = await deadLetterCountCache.GetCountAsync(
                mailRequestRepository,
                access.AllowedTenantIdsForQuery,
                cancellationToken);
            context.Response.Headers.CacheControl = "no-store";
            return Results.Content(
                RenderHtml(
                    new AdminSuppressionListPage([], null),
                    deadLetterCountForSelection,
                    selectedTenantId: null,
                    currentCursor: null,
                    visibleTenants,
                    options,
                    awaitingTenantSelection: true),
                "text/html; charset=utf-8");
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

        if (showUnmasked)
        {
            // Fail closed: do not render unmasked PII without a durable audit row (ADR 0013 D-08).
            try
            {
                await auditRepository.WriteAsync(
                    AdminAuditLog.SanitizeForOutput(
                        new AdminAuditEvent
                        {
                            EventType = AdminAuditLog.EventTypes.MailSuppressionsListUnmasked,
                            Actor = AdminAuditLog.ResolveActor(context),
                            OccurredAt = timeProvider.GetUtcNow(),
                            SourceIp = options.ResolveAuditSourceIp(AdminAuditLog.ResolveSourceIp(context)),
                            UserAgentSummary = AdminAuditLog.SummarizeUserAgent(context),
                            TargetType = AdminAuditLog.TargetTypes.MailSuppressions,
                            TargetId = null,
                            TenantId = tenantId,
                            FieldName = BuildUnmaskedAuditFieldName(page.Items.Count, tenantFiltered: true),
                            Result = AdminAuditLog.Results.Success,
                        }),
                    cancellationToken);
            }
            catch (Exception ex)
            {
                loggerFactory.CreateLogger(AuditLoggerCategoryName).LogError(
                    ListUnmaskedAuditWriteFailedEvent,
                    ex,
                    "Failed to persist suppressions list-unmasked audit; denying unmasked view.");
                return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
            }
        }

        var deadLetterCount = await deadLetterCountCache.GetCountAsync(
            mailRequestRepository,
            access.AllowedTenantIdsForQuery,
            cancellationToken);

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
        MailerAdminOptions options,
        bool awaitingTenantSelection = false)
    {
        var requireTenantFilter = AdminCapabilities.Has(options, AdminCapabilities.ViewUnmaskedListPii);
        var html = new StringBuilder();
        AdminLayout.AppendDocumentStart(html, "抑制リスト - Amane Admin", AdminNavItem.Suppressions, deadLetterCount);

        html.AppendLine("""
                <section class="filter-region" aria-label="抑制リスト絞り込み">
                  <form method="get" action="/admin/suppressions" class="filter-form">
                    <label>
                      テナント
                      <select name="tenant_id">
            """);

        if (!requireTenantFilter)
        {
            html.AppendLine("""                        <option value="">すべて</option>""");
        }


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
                  <p class="filter-note">閲覧のみ。解除は CLI <code>db suppressions remove</code>（bounce ingestion runbook §6）を使います。非マスク表示（MAILER_ADMIN_PII_LIST_MODE=visible）ではテナント絞り込みが必須です。</p>
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
            if (awaitingTenantSelection)
            {
                html.AppendLine("""
                      <tr>
                        <td class="empty-row" colspan="5">テナントを選択してください</td>
                      </tr>
                """);
            }
            else
            {
                html.AppendLine("""
                      <tr>
                        <td class="empty-row" colspan="5">抑制エントリはありません</td>
                      </tr>
                """);
            }
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
        var recipient = item.IsBccSensitive
            ? "***"
            : AdminCapabilities.Has(options, AdminCapabilities.ViewUnmaskedListPii)
            ? item.RecipientEmail
            : MaskSuppressionRecipient(item.RecipientEmail);

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

    /// <summary>
    /// Stronger than mail-request list mask: keeps only first local-part char and first
    /// domain-label char before the final TLD (ADR 0013 D-05 minimum for address lists).
    /// </summary>
    internal static string MaskSuppressionRecipient(string email)
    {
        if (string.IsNullOrEmpty(email))
            return "***";

        var at = email.IndexOf('@', StringComparison.Ordinal);
        if (at <= 0)
            return "***";

        var local = email[..at];
        var domain = email[(at + 1)..];
        var localMask = local.Length == 0 ? "***" : $"{local[0]}***";
        return $"{localMask}@{MaskDomain(domain)}";
    }

    private static string MaskDomain(string domain)
    {
        if (string.IsNullOrEmpty(domain))
            return "***";

        var lastDot = domain.LastIndexOf('.');
        if (lastDot <= 0)
            return $"{domain[0]}***";

        var name = domain[..lastDot];
        var tld = domain[lastDot..];
        if (name.Length == 0)
            return "***" + tld;

        return $"{name[0]}***{tld}";
    }

    internal static string BuildUnmaskedAuditFieldName(int resultCount, bool tenantFiltered) =>
        string.Create(
            CultureInfo.InvariantCulture,
            $"result_count={resultCount};tenant_filter={(tenantFiltered ? "specific" : "all")}");

    private static string FormatLocalTime(DateTimeOffset value) =>
        value.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss zzz", CultureInfo.InvariantCulture);

    private static string Html(string value) =>
        HtmlEncoder.Default.Encode(value);
}
