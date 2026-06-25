using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using Amane.Mailer.Data.Sqlite;
using Amane.Mailer.Data.Sqlite.Models;

namespace Amane.Mailer.Admin;

public static class AdminDeadLettersPage
{
    private const int PageSize = 50;

    public static async Task<IResult> RenderAsync(
        HttpContext context,
        MailRequestRepository repository,
        AdminDeadLetterCountCache deadLetterCountCache,
        MailerAdminOptions options,
        CancellationToken cancellationToken)
    {
        AdminDeadLetterCursor? cursor = null;
        var cursorValue = context.Request.Query["cursor"].ToString();
        if (!string.IsNullOrWhiteSpace(cursorValue))
        {
            if (!AdminDeadLetterCursor.TryDecode(cursorValue, out var decodedCursor))
                return Results.Text("Invalid cursor.", statusCode: StatusCodes.Status400BadRequest);

            cursor = decodedCursor;
        }

        var page = await repository.ListDeadLettersForAdminAsync(
            new AdminDeadLetterListQuery
            {
                CursorCompletedAt = cursor?.CompletedAt,
                CursorId = cursor?.Id,
                PageSize = PageSize,
            },
            cancellationToken);

        var deadLetterCount = await deadLetterCountCache.GetCountAsync(repository, cancellationToken);

        context.Response.Headers.CacheControl = "no-store";
        return Results.Content(
            RenderHtml(page, deadLetterCount, cursorValue, options),
            "text/html; charset=utf-8");
    }

    private static string RenderHtml(
        AdminDeadLetterListPage page,
        int deadLetterCount,
        string? currentCursor,
        MailerAdminOptions options)
    {
        var html = new StringBuilder();
        AdminLayout.AppendDocumentStart(html, "Dead Letters - Amane Admin", AdminNavItem.DeadLetters, deadLetterCount);

        html.AppendLine("""
                <section class="table-region" aria-label="Dead Letter 一覧">
                  <table class="admin-table">
                    <thead>
                      <tr>
                        <th>mail_request_id</th>
                        <th>テナント</th>
                        <th>source_service</th>
                        <th>宛先</th>
                        <th>件名</th>
                        <th>last_error_message</th>
                        <th>試行回数</th>
                        <th>完了日時</th>
                        <th></th>
                      </tr>
                    </thead>
                    <tbody>
            """);

        if (page.Items.Count == 0)
        {
            html.AppendLine("""
                      <tr>
                        <td class="empty-row" colspan="9">DeadLetter はありません</td>
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

        AppendPager(html, currentCursor, page.NextCursor);
        AdminLayout.AppendDocumentEnd(html);
        return html.ToString();
    }

    private static void AppendRow(StringBuilder html, AdminDeadLetterListRow item, MailerAdminOptions options)
    {
        html.AppendLine("                  <tr>");
        html.Append("                    <td><a href=\"/admin/mail-requests/");
        html.Append(Html(item.Id.ToString("D")));
        html.Append("\">");
        html.Append(Html(item.MailRequestId.ToString("D")));
        html.AppendLine("</a></td>");
        AppendCell(html, item.TenantId.ToString("D"));
        AppendCell(html, item.SourceService);
        AppendCell(html, options.MaskRecipients ? MaskRecipient(item.RecipientEmail) : item.RecipientEmail);
        AppendCell(html, options.MaskSubjects ? MaskSubject(item.Subject) : item.Subject);
        AppendCell(html, TruncateErrorMessage(item.LastErrorMessage));
        AppendCell(html, $"{item.AttemptCount} / {item.MaxAttempts}");
        AppendCell(html, FormatLocalTime(item.CompletedAt));
        html.AppendLine("                    <td class=\"row-actions\">");
        html.Append("                      <a class=\"action-link\" href=\"/admin/mail-requests/");
        html.Append(Html(item.Id.ToString("D")));
        html.AppendLine("\">詳細</a>");
        html.AppendLine("                      <button type=\"button\" class=\"action-button action-button-disabled\" disabled title=\"手動再送は未実装です\">再送する</button>");
        html.AppendLine("                    </td>");
        html.AppendLine("                  </tr>");
    }

    private static void AppendCell(StringBuilder html, string value)
    {
        html.Append("                    <td>");
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
            html.Append("                  <a class=\"pager-link\" href=\"/admin/dead-letters?cursor=");
            html.Append(Html(nextCursor));
            html.AppendLine("\">次へ</a>");
        }

        html.AppendLine("                </nav>");
    }

    private static string TruncateErrorMessage(string? message)
    {
        if (string.IsNullOrEmpty(message))
            return string.Empty;

        if (message.Length <= 50)
            return message;

        return message[..50] + "...";
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

    private static string MaskSubject(string subject)
    {
        if (subject.Length <= 12)
            return subject;

        return subject[..12] + "...";
    }

    private static string FormatLocalTime(DateTimeOffset completedAt) =>
        completedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss zzz", CultureInfo.InvariantCulture);

    private static string Html(string value) =>
        HtmlEncoder.Default.Encode(value);
}
