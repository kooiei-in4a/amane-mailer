using System.Text;
using System.Text.Encodings.Web;
using System.Security.Claims;
using Amane.Mailer.Data.Sqlite;

namespace Amane.Mailer.Admin;

public static class AdminMailRequestBodyPage
{
    private static readonly HashSet<string> AllowedFields =
        ["html_body", "text_body", "metadata_json"];
    private const string AuditLoggerCategoryName = "Amane.Mailer.Admin.BodyAccessAudit";
    private static readonly EventId BodyViewedEvent = new(1001, "AdminMailRequestBodyViewed");

    public static async Task<IResult> RenderAsync(
        string id,
        string? field,
        HttpContext context,
        ILoggerFactory loggerFactory,
        MailRequestRepository repository,
        AdminDeadLetterCountCache deadLetterCountCache,
        CancellationToken cancellationToken)
    {
        if (!Guid.TryParse(id, out var requestId))
            return Results.NotFound();

        if (field is null || !AllowedFields.Contains(field))
            return Results.NotFound();

        var detail = await repository.GetDetailForAdminAsync(requestId, cancellationToken);
        if (detail is null)
            return Results.NotFound();

        var body = field switch
        {
            "html_body"     => detail.HtmlBody,
            "text_body"     => detail.TextBody,
            "metadata_json" => detail.MetadataJson,
            _               => null,
        };

        if (body is null)
            return Results.NotFound();

        var logger = loggerFactory.CreateLogger(AuditLoggerCategoryName);
        RecordBodyViewedAuditLog(context, logger, requestId, field);

        var deadLetterCount = await deadLetterCountCache.GetCountAsync(repository, cancellationToken);

        context.Response.Headers.CacheControl = "no-store";
        return Results.Content(
            RenderHtml(requestId, field, body, deadLetterCount),
            "text/html; charset=utf-8");
    }

    internal static string RenderHtml(Guid requestId, string field, string body, int deadLetterCount = 0)
    {
        var enc = HtmlEncoder.Default;
        var idStr = requestId.ToString("D");
        var fieldEnc = enc.Encode(field);
        var html = new StringBuilder();

        AdminLayout.AppendDocumentStart(
            html,
            $"{field} - Amane Admin",
            AdminNavItem.MailRequests,
            deadLetterCount);

        html.AppendLine("      <nav class=\"admin-breadcrumb\">");
        html.AppendLine("        <a href=\"/admin/mail-requests\">送信依頼一覧</a> &rsaquo;");
        html.Append("        <a href=\"/admin/mail-requests/");
        html.Append(idStr);
        html.Append("\">詳細</a> &rsaquo; ");
        html.AppendLine(fieldEnc);
        html.AppendLine("      </nav>");
        html.Append("      <pre class=\"body-pre\">");
        html.Append(enc.Encode(body));
        html.AppendLine("</pre>");

        AdminLayout.AppendDocumentEnd(html);

        return html.ToString();
    }

    internal static void RecordBodyViewedAuditLog(
        HttpContext context,
        ILogger logger,
        Guid requestId,
        string field)
    {
        var adminUsername =
            context.User.FindFirstValue(ClaimTypes.Name)
            ?? context.User.Identity?.Name
            ?? "unknown";
        var remoteAddress = context.Connection.RemoteIpAddress?.ToString() ?? "unknown";

        logger.LogInformation(
            BodyViewedEvent,
            "Admin mail request body field viewed by {AdminUsername} for {MailRequestId} field {FieldName} from {RemoteAddress}.",
            adminUsername,
            requestId,
            field,
            remoteAddress);
    }
}
