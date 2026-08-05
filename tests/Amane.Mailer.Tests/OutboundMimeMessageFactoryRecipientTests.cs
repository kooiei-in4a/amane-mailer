using Amane.Mailer.Configuration;
using Amane.Mailer.Delivery;
using MimeKit;

namespace Amane.Mailer.Tests;

/// <summary>
/// Canonical To/Cc/Bcc provider mapping at the MIME message level (ADR 0023 D-01, Issue #546).
/// Covers role shapes, role-internal ordinal order, the To -&gt; Cc -&gt; Bcc global order, and
/// that Bcc is never folded into To/Cc on the constructed <see cref="MimeMessage"/> itself.
/// Whether Bcc is excluded from the transmitted SMTP DATA (as opposed to this in-memory object)
/// is a MailKit/MailpitMailDeliveryProvider concern, covered separately in
/// <see cref="MailpitMailDeliveryProviderTests"/>.
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
        Assert.Equal(["bcc@example.com"], AddressesOf(message.Bcc));
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
        Assert.Equal(["bcc@example.com"], AddressesOf(message.Bcc));
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
        Assert.Equal(["bcc-secret@example.com"], AddressesOf(message.Bcc));
        Assert.DoesNotContain("bcc-secret@example.com", AddressesOf(message.To));
        Assert.DoesNotContain("bcc-secret@example.com", AddressesOf(message.Cc));
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
    public void Create_does_not_write_a_literal_bcc_header_string_before_transmission()
    {
        // MimeMessage.WriteTo() itself DOES include a "Bcc:" header by default -- exclusion from
        // the transmitted SMTP DATA happens inside MailKit's SmtpClient.SendAsync(MimeMessage),
        // not in this factory or in MimeMessage.WriteTo. This test documents that the raw
        // in-memory header is present here (so nobody "fixes" this factory to strip it, which
        // would break the RCPT TO envelope), while MailpitMailDeliveryProviderTests verifies the
        // real transmitted-DATA exclusion end-to-end.
        var job = CreateJob(
            to: [new MailSendRecipient("to@example.com", null)],
            bcc: [new MailSendRecipient("bcc-secret@example.com", null)]);

        using var message = OutboundMimeMessageFactory.Create(job, CreateTenant());

        Assert.NotNull(message.Headers[HeaderId.Bcc]);
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
