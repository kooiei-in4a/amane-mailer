using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using Amane.Mailer.Data.Sqlite;
using Amane.Mailer.Data.Sqlite.Models;

namespace Amane.Mailer.Admin;

/// <summary>
/// Dedicated, audited BCC reveal endpoint (ADR 0023 D-09 / Issue #560). Normal Admin queries
/// never carry raw BCC values; this path serves one exact role/ordinal only after the durable audit
/// write succeeds.
/// </summary>
public static class AdminBccRecipientRevealPage
{
    private const string AuditLoggerCategoryName = "Amane.Mailer.Admin.BccRecipientRevealAudit";
    private static readonly EventId AuditWriteFailedEvent =
        new(1005, "AdminBccRecipientRevealAuditWriteFailed");

    public static async Task<IResult> RenderAsync(
        string id,
        int ordinal,
        HttpContext context,
        ILoggerFactory loggerFactory,
        MailerAdminOptions options,
        MailRequestRepository repository,
        AdminUserRepository userRepository,
        AdminAuditRepository auditRepository,
        TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        if (!Guid.TryParse(id, out var requestId) || ordinal is < 0 or > 9)
            return Results.NotFound();

        var actor = AdminAuditLog.ResolveActor(context);
        var access = await userRepository.GetTenantAccessAsync(actor, cancellationToken);
        if (access is null)
            return Results.StatusCode(StatusCodes.Status403Forbidden);

        // Resolve the request identity inside the authenticated tenant scope before checking the
        // capability. This also avoids any raw recipient lookup before authorization.
        var detail = await repository.GetDetailForAdminAsync(
            requestId,
            access.AllowedTenantIdsForQuery,
            cancellationToken);
        if (detail is null)
            return Results.NotFound();

        if (!await userRepository.HasCapabilityAsync(
                actor,
                AdminCapabilities.BccRecipientReveal,
                cancellationToken))
        {
            return Results.NotFound();
        }

        var recipient = await repository.FindBccRecipientForRevealAsync(
            detail.Id,
            detail.TenantId,
            detail.SourceService,
            ordinal,
            cancellationToken);
        if (recipient is null)
            return Results.NotFound();

        try
        {
            await auditRepository.WriteAsync(
                AdminAuditLog.SanitizeForOutput(
                    new AdminAuditEvent
                    {
                        EventType = AdminAuditLog.EventTypes.BccRecipientRevealed,
                        Actor = actor,
                        OccurredAt = timeProvider.GetUtcNow(),
                        SourceIp = options.ResolveAuditSourceIp(AdminAuditLog.ResolveSourceIp(context)),
                        UserAgentSummary = AdminAuditLog.SummarizeUserAgent(context),
                        TargetType = AdminAuditLog.TargetTypes.MailRequest,
                        TargetId = detail.Id.ToString("D"),
                        TenantId = detail.TenantId,
                        FieldName = FormattableString.Invariant($"bcc[{ordinal}]"),
                        Result = AdminAuditLog.Results.Success,
                    }),
                cancellationToken);
        }
        catch (Exception ex)
        {
            loggerFactory.CreateLogger(AuditLoggerCategoryName).LogError(
                AuditWriteFailedEvent,
                ex,
                "Admin BCC recipient reveal denied because the audit event could not be persisted.");
            return Results.Text(
                "Audit log write failed.",
                "text/plain; charset=utf-8",
                statusCode: StatusCodes.Status503ServiceUnavailable);
        }

        context.Response.Headers.CacheControl = "no-store";
        return Results.Content(
            RenderHtml(detail.Id, ordinal, recipient.Address, recipient.DisplayName),
            "text/html; charset=utf-8");
    }

    internal static string RenderHtml(
        Guid requestId,
        int ordinal,
        string address,
        string? displayName)
    {
        var html = new StringBuilder();
        var enc = HtmlEncoder.Default;
        var id = requestId.ToString("D");

        AdminLayout.AppendDocumentStart(
            html,
            "BCC宛先 - Amane Admin",
            AdminNavItem.MailRequests,
            0);
        html.AppendLine("      <nav class=\"admin-breadcrumb\">");
        html.AppendLine("        <a href=\"/admin/mail-requests\">送信依頼一覧</a> &rsaquo;");
        html.Append("        <a href=\"/admin/mail-requests/");
        html.Append(enc.Encode(id));
        html.AppendLine("\">詳細</a> &rsaquo; BCC</nav>");
        html.AppendLine("      <section class=\"detail-section\" aria-label=\"BCC宛先\">");
        html.AppendLine("        <table class=\"admin-table detail-table\"><tbody>");
        AppendRow(html, "Role", "Bcc");
        AppendRow(html, "#", ordinal.ToString(CultureInfo.InvariantCulture));
        AppendRow(html, "Recipient", address);
        if (displayName is not null)
            AppendRow(html, "Display name", displayName);
        html.AppendLine("        </tbody></table>");
        html.AppendLine("      </section>");
        AdminLayout.AppendDocumentEnd(html);
        return html.ToString();

        void AppendRow(StringBuilder builder, string label, string value)
        {
            builder.Append("          <tr><th>");
            builder.Append(enc.Encode(label));
            builder.Append("</th><td>");
            builder.Append(enc.Encode(value));
            builder.AppendLine("</td></tr>");
        }
    }
}
