using Amane.Mailer.Admin;
using Amane.Mailer.Contracts.MailRequests;
using Amane.Mailer.Data.Sqlite;
using Amane.Mailer.Data.Sqlite.Models;

namespace Amane.Mailer.Tests.Admin;

public sealed class AdminRecipientSummaryRendererTests
{
    [Fact]
    public void List_always_masks_bcc_even_when_other_recipients_are_unmasked()
    {
        var recipients = new List<AdminRecipientSummary>
        {
            new(MailRecipientRole.To, 0, "to@example.com", "To User", MailRecipientDeliveryState.NotSent),
            new(MailRecipientRole.Bcc, 0, "bcc-secret@example.com", "Secret BCC", MailRecipientDeliveryState.NotSent),
        };

        var html = AdminRecipientSummaryRenderer.RenderList(recipients, maskRecipients: false);

        Assert.Contains("to@example.com", html, StringComparison.Ordinal);
        Assert.Contains("Bcc: *** (1)", html, StringComparison.Ordinal);
        Assert.DoesNotContain("bcc-secret@example.com", html, StringComparison.Ordinal);
        Assert.DoesNotContain("Secret BCC", html, StringComparison.Ordinal);
    }

    [Fact]
    public void Detail_adds_a_reveal_link_without_rendering_raw_bcc()
    {
        var requestId = Guid.NewGuid();
        var recipients = new List<AdminRecipientSummary>
        {
            new(MailRecipientRole.Bcc, 2, "bcc-secret@example.com", "Secret BCC", MailRecipientDeliveryState.Pending),
        };

        var html = new System.Text.StringBuilder();
        AdminRecipientSummaryRenderer.AppendDetailTable(
            html,
            requestId,
            recipients,
            maskRecipients: false,
            canRevealBcc: true);

        var rendered = html.ToString();
        Assert.Contains(">***</td>", rendered, StringComparison.Ordinal);
        Assert.Contains($"/admin/mail-requests/{requestId:D}/recipients/bcc/2", rendered, StringComparison.Ordinal);
        Assert.DoesNotContain("bcc-secret@example.com", rendered, StringComparison.Ordinal);
        Assert.DoesNotContain("Secret BCC", rendered, StringComparison.Ordinal);
    }
}
