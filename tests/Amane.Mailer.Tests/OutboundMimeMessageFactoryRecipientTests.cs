using Amane.Mailer.Configuration;
using Amane.Mailer.Delivery;
using MimeKit;

namespace Amane.Mailer.Tests;

/// <summary>
/// Canonical To/Cc/Bcc provider mapping at the MIME message level (ADR 0023 D-01, Issue #546).
/// Covers role shapes, role-internal ordinal order, and the To -&gt; Cc -&gt; Bcc global order.
/// Since Issue #546 review finding F4, Bcc is never added to the constructed
/// <see cref="MimeMessage"/> at all (see <see cref="OutboundMimeMessageFactory.Create"/>) -- it
/// is represented only in <see cref="OutboundMimeMessageFactory.BuildEnvelopeRecipients"/>'s
/// separate SMTP envelope list, verified here. End-to-end wire behavior (real MailKit send, real
/// SMTP listener) is covered separately in <see cref="MailpitMailDeliveryProviderTests"/>.
/// </summary>
public sealed class OutboundMimeMessageFactoryRecipientTests
{
    [Fact]
    public void Create_maps_to_only_request()
    {
        var job = CreateJob(to: [new MailSendRecipient("to@example.com", "To Person")]);

        using var message = OutboundMimeMessageFactory.Create(job, CreateTenant());

        Assert.Equal(["to@example.com"], AddressesOf(message.To));
        Assert.Empty(message.Cc);
        Assert.Empty(message.Bcc);
    }

    [Fact]
    public void Create_maps_cc_only_request_without_a_to_recipient()
    {
        var job = CreateJob(cc: [new MailSendRecipient("cc@example.com", null)]);

        using var message = OutboundMimeMessageFactory.Create(job, CreateTenant());

        Assert.Empty(message.To);
        Assert.Equal(["cc@example.com"], AddressesOf(message.Cc));
        Assert.Empty(message.Bcc);
    }

    [Fact]
    public void Create_maps_bcc_only_request_without_a_to_recipient()
    {
        var job = CreateJob(bcc: [new MailSendRecipient("bcc@example.com", null)]);

        using var message = OutboundMimeMessageFactory.Create(job, CreateTenant());

        Assert.Empty(message.To);
        Assert.Empty(message.Cc);
        Assert.Empty(message.Bcc);
        Assert.Equal(["bcc@example.com"], OutboundMimeMessageFactory.BuildEnvelopeRecipients(job).Select(r => r.Address));
    }

    [Fact]
    public void Create_maps_to_plus_cc_request()
    {
        var job = CreateJob(
            to: [new MailSendRecipient("to@example.com", null)],
            cc: [new MailSendRecipient("cc@example.com", null)]);

        using var message = OutboundMimeMessageFactory.Create(job, CreateTenant());

        Assert.Equal(["to@example.com"], AddressesOf(message.To));
        Assert.Equal(["cc@example.com"], AddressesOf(message.Cc));
        Assert.Empty(message.Bcc);
    }

    [Fact]
    public void Create_maps_to_plus_bcc_request()
    {
        var job = CreateJob(
            to: [new MailSendRecipient("to@example.com", null)],
            bcc: [new MailSendRecipient("bcc@example.com", null)]);

        using var message = OutboundMimeMessageFactory.Create(job, CreateTenant());

        Assert.Equal(["to@example.com"], AddressesOf(message.To));
        Assert.Empty(message.Cc);
        Assert.Empty(message.Bcc);
        Assert.Equal(
            ["to@example.com", "bcc@example.com"],
            OutboundMimeMessageFactory.BuildEnvelopeRecipients(job).Select(r => r.Address));
    }

    [Fact]
    public void Create_maps_to_plus_cc_plus_bcc_request_and_never_folds_bcc_into_to_or_cc()
    {
        var job = CreateJob(
            to: [new MailSendRecipient("to@example.com", null)],
            cc: [new MailSendRecipient("cc@example.com", null)],
            bcc: [new MailSendRecipient("bcc-secret@example.com", null)]);

        using var message = OutboundMimeMessageFactory.Create(job, CreateTenant());

        Assert.Equal(["to@example.com"], AddressesOf(message.To));
        Assert.Equal(["cc@example.com"], AddressesOf(message.Cc));
        Assert.Empty(message.Bcc);
        Assert.DoesNotContain("bcc-secret@example.com", AddressesOf(message.To));
        Assert.DoesNotContain("bcc-secret@example.com", AddressesOf(message.Cc));
        Assert.Equal(
            ["to@example.com", "cc@example.com", "bcc-secret@example.com"],
            OutboundMimeMessageFactory.BuildEnvelopeRecipients(job).Select(r => r.Address));
    }

    [Fact]
    public void Create_preserves_role_internal_ordinal_order_for_multiple_recipients_per_role()
    {
        var job = CreateJob(
            to:
            [
                new MailSendRecipient("to-1@example.com", null),
                new MailSendRecipient("to-2@example.com", null),
                new MailSendRecipient("to-3@example.com", null),
            ],
            cc:
            [
                new MailSendRecipient("cc-1@example.com", null),
                new MailSendRecipient("cc-2@example.com", null),
            ]);

        using var message = OutboundMimeMessageFactory.Create(job, CreateTenant());

        Assert.Equal(
            ["to-1@example.com", "to-2@example.com", "to-3@example.com"],
            AddressesOf(message.To));
        Assert.Equal(["cc-1@example.com", "cc-2@example.com"], AddressesOf(message.Cc));
    }

    [Fact]
    public void Create_never_adds_a_bcc_header_to_the_message_itself()
    {
        // Issue #546 review finding F4: MimeMessage.WriteTo() would include a literal "Bcc:"
        // header (with the raw address) verbatim once message.Bcc is populated -- relying on a
        // downstream transport to strip it before transmission was deemed too fragile (a direct
        // WriteTo() call, a future MailKit change, or a different send path could all leak it).
        // Bcc is therefore never added to this MimeMessage at all; the SMTP envelope recipient
        // list (including Bcc) is built separately by BuildEnvelopeRecipients and passed to
        // MailKit's explicit-envelope SendAsync(message, sender, recipients, ct) overload
        // (MailpitMailDeliveryProvider), which computes RCPT TO independently of message headers.
        var job = CreateJob(
            to: [new MailSendRecipient("to@example.com", null)],
            bcc: [new MailSendRecipient("bcc-secret@example.com", null)]);

        using var message = OutboundMimeMessageFactory.Create(job, CreateTenant());

        Assert.Null(message.Headers[HeaderId.Bcc]);
        Assert.Empty(message.Bcc);

        using var stream = new MemoryStream();
        message.WriteTo(stream);
        var raw = System.Text.Encoding.UTF8.GetString(stream.ToArray());
        Assert.DoesNotContain("Bcc", raw, StringComparison.Ordinal);
        Assert.DoesNotContain("bcc-secret@example.com", raw, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void BuildEnvelopeRecipients_returns_to_then_cc_then_bcc_in_role_order()
    {
        var job = CreateJob(
            to: [new MailSendRecipient("to@example.com", null)],
            cc: [new MailSendRecipient("cc@example.com", null)],
            bcc: [new MailSendRecipient("bcc-secret@example.com", null)]);

        var recipients = OutboundMimeMessageFactory.BuildEnvelopeRecipients(job);

        Assert.Equal(
            ["to@example.com", "cc@example.com", "bcc-secret@example.com"],
            recipients.Select(r => r.Address));
    }

    private static MailSendJob CreateJob(
        IReadOnlyList<MailSendRecipient>? to = null,
        IReadOnlyList<MailSendRecipient>? cc = null,
        IReadOnlyList<MailSendRecipient>? bcc = null) =>
        new(
            Guid.NewGuid(),
            "example-service",
            "Subject",
            HtmlBody: null,
            TextBody: "body",
            ReplyTo: null,
            To: to ?? [],
            Cc: cc ?? [],
            Bcc: bcc ?? []);

    private static MailerTenant CreateTenant() =>
        new()
        {
            TenantId = Guid.NewGuid(),
            Name = "example-develop",
            SourceServices = ["example-service"],
            DefaultFrom = new MailerAddress { Email = "noreply@example.com", DisplayName = "Example Service" },
            TokenEnv = "MAIL_SERVICE_TOKEN",
            Provider = "mailpit",
            Retry = new MailerRetryOptions { MaxAttempts = 3, InitialDelaySeconds = 1, MaxDelaySeconds = 2 },
        };

    private static IReadOnlyList<string> AddressesOf(InternetAddressList list) =>
        list.Mailboxes.Select(mailbox => mailbox.Address).ToArray();
}
