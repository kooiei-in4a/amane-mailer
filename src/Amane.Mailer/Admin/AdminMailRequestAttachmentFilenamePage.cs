using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using Amane.Mailer.Data.Sqlite;
using Amane.Mailer.Data.Sqlite.Models;

namespace Amane.Mailer.Admin;

/// <summary>
/// ADR 0022 D-13: attachment filenames are potential PII and are masked by default in the
/// mail request detail view. This page is the explicit, audited reveal action for a single
/// attachment's raw filename -- the same fail-closed audit-then-serve pattern as
/// <see cref="AdminMailRequestBodyPage"/>. The audit record itself never stores the raw
/// filename, only the attachment's position within the request.
/// </summary>
public static class AdminMailRequestAttachmentFilenamePage
{
    private const string AuditLoggerCategoryName = "Amane.Mailer.Admin.AttachmentFilenameRevealAudit";
    private static readonly EventId FilenameRevealedEvent = new(1003, "AdminAttachmentFilenameRevealed");
    private static readonly EventId FilenameRevealAuditWriteFailedEvent =
        new(1004, "AdminAttachmentFilenameRevealAuditWriteFailed");

    public static async Task<IResult> RenderAsync(
        string id,
        int order,
        HttpContext context,
        ILoggerFactory loggerFactory,
        MailerAdminOptions options,
        MailRequestRepository repository,
        AdminUserRepository userRepository,
        AdminAuditRepository auditRepository,
        TimeProvider timeProvider,
        AdminDeadLetterCountCache deadLetterCountCache,
        CancellationToken cancellationToken)
    {
        if (!Guid.TryParse(id, out var requestId) || order < 0)
            return Results.NotFound();

        var access = await userRepository.GetTenantAccessAsync(
            AdminAuditLog.ResolveActor(context),
            cancellationToken);
        if (access is null)
            return Results.StatusCode(StatusCodes.Status403Forbidden);

        var detail = await repository.GetDetailForAdminAsync(
            requestId,
            access.AllowedTenantIdsForQuery,
            cancellationToken);
        if (detail is null)
            return Results.NotFound();

        var attachments = await repository.ListAttachmentsAsync(requestId, cancellationToken);
        var attachment = attachments.FirstOrDefault(a => a.Order == order);
        if (attachment is null)
            return Results.NotFound();

        var logger = loggerFactory.CreateLogger(AuditLoggerCategoryName);
        var fieldName = FormattableString.Invariant($"attachments[{order}].file_name");

        // Fail closed, same as the body-view page: revealing PII without a durable audit
        // record is exactly the risk ADR 0013 D-06/D-08 and ADR 0022 D-13 guard against.
        try
        {
            await auditRepository.WriteAsync(
                AdminAuditLog.SanitizeForOutput(
                    new AdminAuditEvent
                    {
                        EventType = AdminAuditLog.EventTypes.AttachmentFilenameRevealed,
                        Actor = AdminAuditLog.ResolveActor(context),
                        OccurredAt = timeProvider.GetUtcNow(),
                        SourceIp = options.ResolveAuditSourceIp(AdminAuditLog.ResolveSourceIp(context)),
                        UserAgentSummary = AdminAuditLog.SummarizeUserAgent(context),
                        TargetType = AdminAuditLog.TargetTypes.MailRequest,
                        TargetId = requestId.ToString("D"),
                        TenantId = detail.TenantId,
                        FieldName = fieldName,
                        Result = AdminAuditLog.Results.Success,
                    }),
                cancellationToken);
        }
        catch (Exception ex)
        {
            logger.LogError(
                FilenameRevealAuditWriteFailedEvent,
                ex,
                "Admin attachment filename reveal denied because the audit event could not be persisted.");
            return Results.Text(
                "Audit log write failed.",
                "text/plain; charset=utf-8",
                statusCode: StatusCodes.Status500InternalServerError);
        }

        RecordFilenameRevealedAuditLog(context, options, logger, requestId, order);

        var deadLetterCount = await deadLetterCountCache.GetCountAsync(
            repository,
            access.AllowedTenantIdsForQuery,
            cancellationToken);

        context.Response.Headers.CacheControl = "no-store";
        return Results.Content(
            RenderHtml(requestId, order, attachment.FileName, deadLetterCount),
            "text/html; charset=utf-8");
    }

    internal static string RenderHtml(Guid requestId, int order, string fileName, int deadLetterCount = 0)
    {
        var enc = HtmlEncoder.Default;
        var idStr = requestId.ToString("D");
        var html = new StringBuilder();

        AdminLayout.AppendDocumentStart(
            html,
            "添付ファイル名 - Amane Admin",
            AdminNavItem.MailRequests,
            deadLetterCount);

        html.AppendLine("      <nav class=\"admin-breadcrumb\">");
        html.AppendLine("        <a href=\"/admin/mail-requests\">送信依頼一覧</a> &rsaquo;");
        html.Append("        <a href=\"/admin/mail-requests/");
        html.Append(idStr);
        html.Append("\">詳細</a> &rsaquo; 添付ファイル[");
        html.Append(order.ToString(CultureInfo.InvariantCulture));
        html.AppendLine("]</nav>");
        html.Append("      <pre class=\"body-pre\">");
        html.Append(enc.Encode(fileName));
        html.AppendLine("</pre>");

        AdminLayout.AppendDocumentEnd(html);

        return html.ToString();
    }

    internal static void RecordFilenameRevealedAuditLog(
        HttpContext context,
        MailerAdminOptions options,
        ILogger logger,
        Guid requestId,
        int order)
    {
        var adminUsername = AdminAuditLog.NormalizeActor(AdminAuditLog.ResolveActor(context));
        var remoteAddress =
            AdminAuditLog.SanitizeAuditLogValue(
                options.ResolveAuditSourceIp(AdminAuditLog.ResolveSourceIp(context)))
            ?? "unknown";

        logger.LogInformation(
            FilenameRevealedEvent,
            "Admin attachment filename revealed by {AdminUsername} for {MailRequestId} attachment index {AttachmentOrder} from {RemoteAddress}.",
            adminUsername,
            requestId,
            order,
            remoteAddress);
    }
}
