using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using Amane.Mailer.Data.Sqlite;
using Amane.Mailer.Data.Sqlite.Models;
using Amane.Mailer.Identity;
using Microsoft.AspNetCore.Antiforgery;

namespace Amane.Mailer.Admin;

public static class AdminSendersPage
{
    public static async Task<IResult> RenderAsync(
        HttpContext context,
        SenderRepository senderRepository,
        AdminUserRepository userRepository,
        AdminDeadLetterCountCache deadLetterCountCache,
        MailRequestRepository mailRequestRepository,
        IAntiforgery antiforgery,
        CancellationToken cancellationToken)
    {
        var accessResult = await AdminManagedConfigurationAuthorization.RequireInstanceOwnerAsync(
            context,
            userRepository,
            cancellationToken);
        if (accessResult.Error is not null)
            return accessResult.Error;

        var senders = await senderRepository.ListAsync(cancellationToken);
        var deadLetterCount = await deadLetterCountCache.GetCountAsync(
            mailRequestRepository,
            accessResult.Access!.AllowedTenantIdsForQuery,
            cancellationToken);
        var csrfToken = HtmlEncoder.Default.Encode(
            antiforgery.GetAndStoreTokens(context).RequestToken ?? string.Empty);

        SetNoStore(context);
        return Results.Content(
            RenderListHtml(senders, deadLetterCount, csrfToken),
            "text/html; charset=utf-8");
    }

    public static async Task<IResult> RenderDetailAsync(
        Guid senderId,
        HttpContext context,
        SenderRepository senderRepository,
        AdminUserRepository userRepository,
        AdminDeadLetterCountCache deadLetterCountCache,
        MailRequestRepository mailRequestRepository,
        IAntiforgery antiforgery,
        CancellationToken cancellationToken)
    {
        var accessResult = await AdminManagedConfigurationAuthorization.RequireInstanceOwnerAsync(
            context,
            userRepository,
            cancellationToken);
        if (accessResult.Error is not null)
            return accessResult.Error;

        var sender = await senderRepository.FindAsync(senderId, cancellationToken);
        if (sender is null)
            return Results.NotFound();

        var keys = await senderRepository.ListApiKeysAsync(senderId, cancellationToken);
        var deadLetterCount = await deadLetterCountCache.GetCountAsync(
            mailRequestRepository,
            accessResult.Access!.AllowedTenantIdsForQuery,
            cancellationToken);
        var csrfToken = HtmlEncoder.Default.Encode(
            antiforgery.GetAndStoreTokens(context).RequestToken ?? string.Empty);

        SetNoStore(context);
        return Results.Content(
            RenderDetailHtml(sender, keys, deadLetterCount, csrfToken, createdApiKey: null),
            "text/html; charset=utf-8");
    }

    internal static string RenderListHtml(
        IReadOnlyList<SenderSummary> senders,
        int deadLetterCount,
        string csrfToken)
    {
        var html = new StringBuilder();
        AdminLayout.AppendDocumentStart(html, "Senders - Amane Admin", AdminNavItem.Senders, deadLetterCount);
        html.AppendLine("                <section class=\"ops-section\" aria-label=\"Sender作成\">");
        html.AppendLine("                  <h1 class=\"ops-heading\">Senders</h1>");
        html.AppendLine("                  <form method=\"post\" action=\"/admin/senders\" class=\"ops-form\">");
        AppendCsrfInput(html, csrfToken);
        AppendTextInput(html, "email", "Email", "email", required: true, maxLength: 320);
        AppendTextInput(html, "display_name", "Display name", "organization", required: false, maxLength: 200);
        html.AppendLine("                    <label><input type=\"checkbox\" name=\"confirmation\" value=\"confirm\" required> Senderを作成することを確認します。</label>");
        html.AppendLine("                    <button type=\"submit\">Senderを作成</button>");
        html.AppendLine("                  </form>");
        html.AppendLine("                </section>");

        html.AppendLine("                <section class=\"table-region\" aria-label=\"Sender一覧\">");
        html.AppendLine("                  <table class=\"admin-table\">");
        html.AppendLine("                    <thead><tr><th>sender_id</th><th>email</th><th>display_name</th><th>status</th><th>created_at</th><th>disabled_at</th><th>API Keys</th></tr></thead>");
        html.AppendLine("                    <tbody>");
        if (senders.Count == 0)
        {
            html.AppendLine("                      <tr><td class=\"empty-row\" colspan=\"7\">Senderがありません</td></tr>");
        }
        else
        {
            foreach (var sender in senders)
            {
                html.AppendLine("                      <tr>");
                html.Append("                        <td><a href=\"/admin/senders/");
                html.Append(Html(sender.SenderId.ToString("D")));
                html.Append("\">");
                html.Append(Html(sender.SenderId.ToString("D")));
                html.AppendLine("</a></td>");
                AppendTableCell(html, sender.Email);
                AppendTableCell(html, sender.DisplayName ?? string.Empty);
                AppendTableCell(html, sender.Enabled ? "enabled" : "disabled");
                AppendTableCell(html, FormatUtc(sender.CreatedAt));
                AppendTableCell(html, sender.DisabledAt is null ? string.Empty : FormatUtc(sender.DisabledAt.Value));
                AppendTableCell(html, sender.ApiKeyCount.ToString(CultureInfo.InvariantCulture));
                html.AppendLine("                      </tr>");
            }
        }

        html.AppendLine("                    </tbody>");
        html.AppendLine("                  </table>");
        html.AppendLine("                </section>");
        AdminLayout.AppendDocumentEnd(html);
        return html.ToString();
    }

    internal static string RenderDetailHtml(
        SenderIdentity sender,
        IReadOnlyList<ApiKeyMetadata> keys,
        int deadLetterCount,
        string csrfToken,
        CreatedApiKey? createdApiKey)
    {
        var html = new StringBuilder();
        AdminLayout.AppendDocumentStart(
            html,
            $"Sender {sender.Email} - Amane Admin",
            AdminNavItem.Senders,
            deadLetterCount);

        html.AppendLine("                <p class=\"ops-meta\"><a href=\"/admin/senders\">← Senders</a></p>");
        html.AppendLine("                <section class=\"ops-section\" aria-label=\"Sender詳細\">");
        html.AppendLine("                  <h1 class=\"ops-heading\">Sender detail</h1>");
        html.AppendLine("                  <dl class=\"ops-dl\">");
        AppendDefinition(html, "sender_id", sender.SenderId.ToString("D"));
        AppendDefinition(html, "email", sender.Email);
        AppendDefinition(html, "display_name", sender.DisplayName ?? string.Empty);
        AppendDefinition(html, "status", sender.Enabled ? "enabled" : "disabled");
        AppendDefinition(html, "created_at", FormatUtc(sender.CreatedAt));
        AppendDefinition(html, "disabled_at", sender.DisabledAt is null ? string.Empty : FormatUtc(sender.DisabledAt.Value));
        html.AppendLine("                  </dl>");
        html.AppendLine("                  <div class=\"ops-actions\">");
        AppendSenderMutationForm(
            html,
            sender,
            sender.Enabled ? "disable" : "enable",
            sender.Enabled ? "Senderを無効化" : "Senderを有効化",
            sender.Enabled ? "このSenderを無効化することを確認します。" : "このSenderを有効化することを確認します。",
            csrfToken);
        html.AppendLine("                  </div>");
        html.AppendLine("                </section>");

        if (createdApiKey is not null)
        {
            html.AppendLine("                <section class=\"ops-section\" aria-label=\"API Key one-time reveal\">");
            html.AppendLine("                  <h2 class=\"ops-heading\">API Keyを保存してください</h2>");
            html.AppendLine("                  <p>このキーは今だけ表示されます。<br>安全な場所へ保存してください。<br>後から再表示することはできません。<br>紛失した場合は新しいキーを作成してください。</p>");
            html.Append("                  <p><code>");
            html.Append(Html(createdApiKey.Plaintext));
            html.AppendLine("</code></p>");
            html.AppendLine("                </section>");
        }

        html.AppendLine("                <section class=\"ops-section\" aria-label=\"API Key作成\">");
        html.AppendLine("                  <h2 class=\"ops-heading\">API Keys</h2>");
        html.AppendLine("                  <form method=\"post\" action=\"/admin/senders/");
        html.Append(Html(sender.SenderId.ToString("D")));
        html.AppendLine("/api-keys\" class=\"ops-form\">");
        AppendCsrfInput(html, csrfToken);
        AppendTextInput(html, "name", "Name", "off", required: true, maxLength: 200);
        html.AppendLine("                    <label><input type=\"checkbox\" name=\"confirmation\" value=\"confirm\" required> API Keyを作成し、plaintextを今だけ表示することを確認します。</label>");
        html.AppendLine("                    <button type=\"submit\">API Keyを作成</button>");
        html.AppendLine("                  </form>");

        html.AppendLine("                  <div class=\"table-region\">");
        html.AppendLine("                    <table class=\"admin-table\">");
        html.AppendLine("                      <thead><tr><th>key_id</th><th>name</th><th>created_at</th><th>revoked_at</th><th>status</th><th>action</th></tr></thead>");
        html.AppendLine("                      <tbody>");
        if (keys.Count == 0)
        {
            html.AppendLine("                        <tr><td class=\"empty-row\" colspan=\"6\">API Keyがありません</td></tr>");
        }
        else
        {
            foreach (var key in keys)
            {
                html.AppendLine("                        <tr>");
                AppendTableCell(html, key.KeyId.ToString("D"));
                AppendTableCell(html, key.Name);
                AppendTableCell(html, FormatUtc(key.CreatedAt));
                AppendTableCell(html, key.RevokedAt is null ? string.Empty : FormatUtc(key.RevokedAt.Value));
                AppendTableCell(html, key.RevokedAt is null ? "active" : "revoked");
                html.Append("                          <td>");
                if (key.RevokedAt is null)
                {
                    html.Append("<form method=\"post\" action=\"/admin/senders/");
                    html.Append(Html(sender.SenderId.ToString("D")));
                    html.Append("/api-keys/");
                    html.Append(Html(key.KeyId.ToString("D")));
                    html.AppendLine("/revoke\" class=\"ops-form\">");
                    AppendCsrfInput(html, csrfToken, indentation: "                            ");
                    html.AppendLine("                            <label><input type=\"checkbox\" name=\"confirmation\" value=\"confirm\" required> 不可逆であることを確認</label>");
                    html.AppendLine("                            <button type=\"submit\">Revoke</button>");
                    html.AppendLine("                          </form>");
                }
                else
                {
                    html.AppendLine("                            —");
                }

                html.AppendLine("                          </td>");
                html.AppendLine("                        </tr>");
            }
        }

        html.AppendLine("                      </tbody>");
        html.AppendLine("                    </table>");
        html.AppendLine("                  </div>");
        html.AppendLine("                </section>");
        AdminLayout.AppendDocumentEnd(html);
        return html.ToString();
    }

    private static void AppendSenderMutationForm(
        StringBuilder html,
        SenderIdentity sender,
        string operation,
        string buttonLabel,
        string confirmationLabel,
        string csrfToken)
    {
        html.Append("                    <form method=\"post\" action=\"/admin/senders/");
        html.Append(Html(sender.SenderId.ToString("D")));
        html.Append('/');
        html.Append(operation);
        html.AppendLine("\" class=\"ops-form\">");
        AppendCsrfInput(html, csrfToken, indentation: "                      ");
        html.Append("                      <label><input type=\"checkbox\" name=\"confirmation\" value=\"confirm\" required> ");
        html.Append(Html(confirmationLabel));
        html.AppendLine("</label>");
        html.Append("                      <button type=\"submit\">");
        html.Append(Html(buttonLabel));
        html.AppendLine("</button>");
        html.AppendLine("                    </form>");
    }

    private static void AppendCsrfInput(
        StringBuilder html,
        string csrfToken,
        string indentation = "                    ")
    {
        html.Append(indentation);
        html.Append("<input type=\"hidden\" name=\"__RequestVerificationToken\" value=\"");
        html.Append(csrfToken);
        html.AppendLine("\">");
    }

    private static void AppendTextInput(
        StringBuilder html,
        string name,
        string label,
        string autocomplete,
        bool required,
        int maxLength)
    {
        html.Append("                    <label><span>");
        html.Append(Html(label));
        html.Append("</span><input name=\"");
        html.Append(Html(name));
        html.Append("\" autocomplete=\"");
        html.Append(Html(autocomplete));
        html.Append("\" maxlength=\"");
        html.Append(maxLength.ToString(CultureInfo.InvariantCulture));
        html.Append('"');
        if (required)
            html.Append(" required");
        html.AppendLine("></label>");
    }

    private static void AppendDefinition(StringBuilder html, string term, string value)
    {
        html.Append("                    <dt>");
        html.Append(Html(term));
        html.AppendLine("</dt>");
        html.Append("                    <dd>");
        html.Append(Html(value));
        html.AppendLine("</dd>");
    }

    private static void AppendTableCell(StringBuilder html, string value)
    {
        html.Append("                        <td>");
        html.Append(Html(value));
        html.AppendLine("</td>");
    }

    private static string FormatUtc(DateTimeOffset value) =>
        SqliteTime.ToStorageUtc(value.ToUniversalTime());

    private static string Html(string value) => HtmlEncoder.Default.Encode(value);

    private static void SetNoStore(HttpContext context)
    {
        context.Response.Headers.CacheControl = "no-store";
        context.Response.Headers.Pragma = "no-cache";
    }
}

internal static class AdminManagedConfigurationAuthorization
{
    internal static async Task<(AdminTenantAccess? Access, IResult? Error)> RequireInstanceOwnerAsync(
        HttpContext context,
        AdminUserRepository userRepository,
        CancellationToken cancellationToken)
    {
        var access = await userRepository.GetTenantAccessAsync(
            AdminAuditLog.ResolveActor(context),
            cancellationToken);
        return access is { IsInstanceOwner: true }
            ? (access, null)
            : (null, Results.StatusCode(StatusCodes.Status403Forbidden));
    }
}
