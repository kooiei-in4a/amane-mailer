using System.Text;
using System.Text.Encodings.Web;
using Amane.Mailer.Contracts.MailRequests;
using Amane.Mailer.Data.Sqlite;
using Amane.Mailer.Data.Sqlite.Models;

namespace Amane.Mailer.Admin;

internal static class AdminRecipientSummaryRenderer
{
    public static string RenderList(
        IReadOnlyList<AdminRecipientSummary> recipients,
        bool maskRecipients)
    {
        if (recipients.Count == 0)
            return "宛先情報なし";

        var parts = new List<string>();
        AppendRole(parts, recipients, MailRecipientRole.To, "To", maskRecipients);
        AppendRole(parts, recipients, MailRecipientRole.Cc, "Cc", maskRecipients);

        var bccCount = recipients.Count(recipient => recipient.Role == MailRecipientRole.Bcc);
        if (bccCount > 0)
            parts.Add($"Bcc: *** ({bccCount})");

        return string.Join(" | ", parts);
    }

    public static void AppendDetailTable(
        StringBuilder html,
        Guid requestId,
        IReadOnlyList<AdminRecipientSummary> recipients,
        bool maskRecipients,
        bool canRevealBcc)
    {
        html.AppendLine("              <section class=\"detail-section\" aria-label=\"宛先\">");
        html.AppendLine("                <h2 class=\"section-heading\">宛先</h2>");
        html.AppendLine("                <table class=\"admin-table\">");
        html.AppendLine("                  <thead><tr><th>Role</th><th>#</th><th>Recipient</th><th>Display name</th><th>Delivery state</th></tr></thead>");
        html.AppendLine("                  <tbody>");

        if (recipients.Count == 0)
        {
            html.AppendLine("                    <tr><td class=\"empty-row\" colspan=\"5\">canonical recipient rows are unavailable</td></tr>");
        }
        else
        {
            foreach (var recipient in recipients)
            {
                var isBcc = recipient.Role == MailRecipientRole.Bcc;
                var address = isBcc
                    ? "***"
                    : MaskRecipient(recipient.Address ?? string.Empty, maskRecipients);
                var displayName = isBcc
                    ? "***"
                    : maskRecipients ? string.Empty : recipient.DisplayName ?? string.Empty;

                html.AppendLine("                    <tr>");
                AppendCell(html, RoleText(recipient.Role));
                AppendCell(html, recipient.Ordinal.ToString(System.Globalization.CultureInfo.InvariantCulture));
                html.Append("                      <td>");
                html.Append(Html(address));
                if (isBcc && canRevealBcc)
                {
                    html.Append(" <a href=\"/admin/mail-requests/");
                    html.Append(Html(requestId.ToString("D")));
                    html.Append("/recipients/bcc/");
                    html.Append(recipient.Ordinal.ToString(System.Globalization.CultureInfo.InvariantCulture));
                    html.AppendLine("\">表示</a></td>");
                }
                else
                {
                    html.AppendLine("</td>");
                }

                AppendCell(html, displayName);
                AppendCell(html, DeliveryStateText(recipient.DeliveryState));
                html.AppendLine("                    </tr>");
            }
        }

        html.AppendLine("                  </tbody>");
        html.AppendLine("                </table>");
        html.AppendLine("              </section>");
    }

    private static void AppendRole(
        ICollection<string> parts,
        IReadOnlyList<AdminRecipientSummary> recipients,
        MailRecipientRole role,
        string label,
        bool maskRecipients)
    {
        var addresses = recipients
            .Where(recipient => recipient.Role == role)
            .OrderBy(recipient => recipient.Ordinal)
            .Select(recipient => MaskRecipient(recipient.Address ?? string.Empty, maskRecipients))
            .ToArray();
        if (addresses.Length > 0)
            parts.Add($"{label}: {string.Join(", ", addresses)}");
    }

    private static string MaskRecipient(string email, bool mask)
    {
        if (!mask)
            return email;

        if (string.IsNullOrEmpty(email))
            return "***";

        var at = email.IndexOf('@', StringComparison.Ordinal);
        if (at <= 0)
            return $"{email[0]}***";

        return $"{email[0]}***{email[at..]}";
    }

    private static string RoleText(MailRecipientRole role) =>
        role switch
        {
            MailRecipientRole.To => "To",
            MailRecipientRole.Cc => "Cc",
            MailRecipientRole.Bcc => "Bcc",
            _ => "Unknown",
        };

    private static string DeliveryStateText(MailRecipientDeliveryState state) =>
        state.ToString();

    private static void AppendCell(StringBuilder html, string value)
    {
        html.Append("                      <td>");
        html.Append(Html(value));
        html.AppendLine("</td>");
    }

    private static string Html(string value) => HtmlEncoder.Default.Encode(value);
}
