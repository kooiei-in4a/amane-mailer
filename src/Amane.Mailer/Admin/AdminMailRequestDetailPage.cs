using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using Amane.Mailer.Data.Sqlite;
using Amane.Mailer.Data.Sqlite.Models;

namespace Amane.Mailer.Admin;

public static class AdminMailRequestDetailPage
{
    public static async Task<IResult> RenderAsync(
        string id,
        HttpContext context,
        MailRequestRepository repository,
        AdminDeadLetterCountCache deadLetterCountCache,
        MailerAdminOptions options,
        CancellationToken cancellationToken)
    {
        if (!Guid.TryParse(id, out var requestId))
            return Results.NotFound();

        var detail = await repository.GetDetailForAdminAsync(requestId, cancellationToken);
        if (detail is null)
            return Results.NotFound();

        var attempts = await repository.ListAttemptsForAdminAsync(requestId, cancellationToken);
        var deadLetterCount = await deadLetterCountCache.GetCountAsync(repository, cancellationToken);

        context.Response.Headers.CacheControl = "no-store";
        return Results.Content(
            RenderHtml(detail, attempts, options, deadLetterCount),
            "text/html; charset=utf-8");
    }

    internal static string RenderHtml(
        AdminMailRequestDetail detail,
        IReadOnlyList<AdminMailAttemptRow> attempts,
        MailerAdminOptions options,
        int deadLetterCount = 0)
    {
        var html = new StringBuilder();

        AdminLayout.AppendDocumentStart(
            html,
            "送信依頼詳細 - Amane Admin",
            AdminNavItem.MailRequests,
            deadLetterCount);

        html.AppendLine("""
                <nav class="admin-breadcrumb">
                  <a href="/admin/mail-requests">送信依頼一覧</a> &rsaquo; 詳細
                </nav>
            """);

        AppendDetailSection(html, detail, options);
        AppendAttemptsSection(html, attempts);

        AdminLayout.AppendDocumentEnd(html);

        return html.ToString();
    }

    private static void AppendDetailSection(
        StringBuilder html,
        AdminMailRequestDetail detail,
        MailerAdminOptions options)
    {
        var recipientEmail = options.MaskRecipients
            ? MaskRecipient(detail.RecipientEmail)
            : detail.RecipientEmail;
        var subject = options.MaskSubjects
            ? MaskSubject(detail.Subject)
            : detail.Subject;

        html.AppendLine("""
              <section class="detail-section" aria-label="送信依頼詳細">
                <table class="admin-table detail-table">
                  <tbody>
            """);

        AppendDetailRow(html, "ID", detail.Id.ToString("D"));
        AppendDetailRow(html, "テナント", detail.TenantId.ToString("D"));
        AppendDetailRow(html, "source_service", detail.SourceService);
        AppendDetailRow(html, "mail_request_id", detail.MailRequestId.ToString("D"));
        AppendDetailRow(html, "purpose", detail.Purpose);
        AppendDetailRow(html, "payload_hash", detail.PayloadHash);

        html.Append("                  <tr><th>ステータス</th><td><span class=\"status-badge ");
        html.Append(StatusClass(detail.Status));
        html.Append("\">");
        html.Append(StatusText(detail.Status));
        html.AppendLine("</span></td></tr>");

        if (detail.Status == MailRequestState.Processing && detail.LockExpiresAt.HasValue)
            AppendDetailRow(html, "lock_expires_at", FormatLocalTime(detail.LockExpiresAt.Value));

        AppendDetailRow(html, "宛先メールアドレス", recipientEmail);
        if (detail.RecipientDisplayName is not null && !options.MaskRecipients)
            AppendDetailRow(html, "宛先表示名", detail.RecipientDisplayName);
        AppendDetailRow(html, "件名", subject);
        if (detail.ReplyTo is not null)
            AppendDetailRow(html, "reply_to", detail.ReplyTo);
        AppendDetailRow(html, "試行回数", $"{detail.AttemptCount} / {detail.MaxAttempts}");
        if (detail.NextAttemptAt.HasValue)
            AppendDetailRow(html, "next_attempt_at", FormatLocalTime(detail.NextAttemptAt.Value));
        if (detail.LastErrorMessage is not null)
        {
            html.Append("                  <tr><th>");
            html.Append(Html("last_error_message"));
            html.Append("</th><td><pre class=\"inline-pre\">");
            html.Append(Html(detail.LastErrorMessage));
            html.AppendLine("</pre></td></tr>");
        }

        AppendDetailRow(html, "accepted_at", FormatLocalTime(detail.AcceptedAt));
        AppendDetailRow(html, "created_at", FormatLocalTime(detail.CreatedAt));
        AppendDetailRow(html, "updated_at", FormatLocalTime(detail.UpdatedAt));
        if (detail.DeliveredAt.HasValue)
            AppendDetailRow(html, "delivered_at", FormatLocalTime(detail.DeliveredAt.Value));
        if (detail.FailedAt.HasValue)
            AppendDetailRow(html, "failed_at", FormatLocalTime(detail.FailedAt.Value));
        if (detail.CompletedAt.HasValue)
            AppendDetailRow(html, "completed_at", FormatLocalTime(detail.CompletedAt.Value));

        html.AppendLine("""
                  </tbody>
                </table>
            """);

        AppendBodyLink(html, detail.Id, "html_body", detail.HtmlBody);
        AppendBodyLink(html, detail.Id, "text_body", detail.TextBody);
        AppendBodyLink(html, detail.Id, "metadata_json", detail.MetadataJson);

        html.AppendLine("              </section>");
    }

    private static void AppendBodyLink(StringBuilder html, Guid requestId, string field, string? body)
    {
        if (body is null)
            return;

        var idStr = requestId.ToString("D");
        html.Append("              <p><a href=\"/admin/mail-requests/");
        html.Append(idStr);
        html.Append("/body?field=");
        html.Append(Html(field));
        html.Append("\">");
        html.Append(Html(field));
        html.AppendLine(" を表示</a></p>");
    }

    private static void AppendAttemptsSection(StringBuilder html, IReadOnlyList<AdminMailAttemptRow> attempts)
    {
        html.AppendLine("""
              <section class="detail-section" aria-label="試行履歴">
                <h2 class="section-heading">試行履歴</h2>
                <table class="admin-table">
                  <thead>
                    <tr>
                      <th>#</th>
                      <th>プロバイダ</th>
                      <th>ステータス</th>
                      <th>message_id</th>
                      <th>error_code</th>
                      <th>error_message</th>
                      <th>started_at</th>
                      <th>completed_at</th>
                    </tr>
                  </thead>
                  <tbody>
            """);

        if (attempts.Count == 0)
        {
            html.AppendLine("""
                      <tr>
                        <td class="empty-row" colspan="8">試行履歴がありません</td>
                      </tr>
                """);
        }
        else
        {
            foreach (var attempt in attempts)
                AppendAttemptRow(html, attempt);
        }

        html.AppendLine("""
                  </tbody>
                </table>
              </section>
            """);
    }

    private static void AppendAttemptRow(StringBuilder html, AdminMailAttemptRow attempt)
    {
        html.AppendLine("                  <tr>");
        AppendCell(html, attempt.AttemptNumber.ToString(CultureInfo.InvariantCulture));
        AppendCell(html, attempt.Provider);
        html.Append("                    <td><span class=\"status-badge ");
        html.Append(AttemptStatusClass(attempt.Status));
        html.Append("\">");
        html.Append(AttemptStatusText(attempt.Status));
        html.AppendLine("</span></td>");
        AppendCell(html, attempt.ProviderMessageId ?? string.Empty);
        AppendCell(html, attempt.ErrorCode ?? string.Empty);
        AppendCell(html, attempt.ErrorMessage ?? string.Empty);
        AppendCell(html, FormatLocalTime(attempt.StartedAt));
        AppendCell(html, FormatLocalTime(attempt.CompletedAt));
        html.AppendLine("                  </tr>");
    }

    private static void AppendDetailRow(StringBuilder html, string label, string value)
    {
        html.Append("                  <tr><th>");
        html.Append(Html(label));
        html.Append("</th><td>");
        html.Append(Html(value));
        html.AppendLine("</td></tr>");
    }

    private static void AppendCell(StringBuilder html, string value)
    {
        html.Append("                    <td>");
        html.Append(Html(value));
        html.AppendLine("</td>");
    }

    private static string StatusText(MailRequestState status) =>
        status switch
        {
            MailRequestState.Queued => "Queued",
            MailRequestState.Processing => "Processing",
            MailRequestState.Delivered => "Delivered",
            MailRequestState.Failed => "Failed",
            MailRequestState.DeadLettered => "DeadLettered",
            _ => "Unknown",
        };

    private static string StatusClass(MailRequestState status) =>
        status switch
        {
            MailRequestState.Queued => "status-queued",
            MailRequestState.Processing => "status-processing",
            MailRequestState.Delivered => "status-delivered",
            MailRequestState.Failed => "status-failed",
            MailRequestState.DeadLettered => "status-deadlettered",
            _ => "status-unknown",
        };

    private static string AttemptStatusText(int status) =>
        status switch
        {
            (int)MailRequestState.Delivered => "Delivered",
            (int)MailRequestState.Failed => "Failed",
            (int)MailRequestState.DeadLettered => "DeadLettered",
            _ => status.ToString(CultureInfo.InvariantCulture),
        };

    private static string AttemptStatusClass(int status) =>
        status switch
        {
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

    private static string FormatLocalTime(DateTimeOffset value) =>
        value.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss zzz", CultureInfo.InvariantCulture);

    private static string Html(string value) =>
        HtmlEncoder.Default.Encode(value);
}
