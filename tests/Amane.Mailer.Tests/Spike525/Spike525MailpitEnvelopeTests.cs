using Amane.Mailer.Delivery;
using MailKit.Net.Smtp;
using MailKit.Security;
using MimeKit;

namespace Amane.Mailer.Tests.Spike525;

/// <summary>
/// #525 Spike — Mailpit SMTP envelope / MIME / API evidence (S-02, S-03 partial, S-14 partial).
/// Gated by AMANE_SPIKE525_MAILPIT_TESTS=1 (mirrors the AMANE_AZURITE_TESTS pattern already
/// used in this repo for AcsQueueAzuriteIntegrationTests) so this never runs unattended in CI
/// where a Mailpit instance at 127.0.0.1:1025/8025 is not provisioned for this spike.
/// </summary>
public sealed class Spike525MailpitEnvelopeTests
{
    private const string FromAddress = "sender@example.com";

    [Fact]
    public async Task S02_bcc_recipient_reaches_mailpit_without_appearing_on_the_wire()
    {
        if (!Spike525Gate.MailpitEnabled)
        {
            return;
        }

        await using var relay = new Spike525SmtpRelay(Spike525Gate.MailpitSmtpHost, Spike525Gate.MailpitSmtpPort);
        relay.Start();

        var recipients = new SpikeMimeFactory.RecipientSet(
            To: [Spike525Support.SyntheticAddress("s02-to1"), Spike525Support.SyntheticAddress("s02-to2")],
            Cc: [Spike525Support.SyntheticAddress("s02-cc1")],
            Bcc: [Spike525Support.SyntheticAddress("s02-bcc1")]);

        var subject = "spike525-s02-" + Guid.NewGuid().ToString("N")[..8];
        var message = SpikeMimeFactory.BuildMessage(FromAddress, recipients, subject, "s02 body");
        var envelopeRecipients = SpikeMimeFactory.EnvelopeRecipients(recipients);

        using (var client = new SmtpClient())
        {
            await client.ConnectAsync("127.0.0.1", relay.ListenPort, SecureSocketOptions.None, TestContext.Current.CancellationToken);
            await client.SendAsync(message, MailboxAddress.Parse(FromAddress), envelopeRecipients, TestContext.Current.CancellationToken);
            await client.DisconnectAsync(true, TestContext.Current.CancellationToken);
        }

        // Ground truth: literal bytes captured on the wire, independent of Mailpit's API.
        var wireHasBccHeader = relay.CapturedBytesContainHeader("Bcc");

        // Mailpit's own (proven-to-be-synthesized) view, recorded only for contrast — never as proof.
        using var http = new HttpClient { BaseAddress = new Uri(Spike525Gate.MailpitHttpBaseUrl) };
        var api = new MailpitApiClient(http);
        var found = await WaitForSubjectAsync(api, subject, TestContext.Current.CancellationToken);

        Assert.False(wireHasBccHeader, "Bcc header must never appear in the literal SMTP DATA bytes.");
        Assert.NotNull(found);
        Assert.Equal(2, found!.To.Length);
        Assert.Single(found.Cc);
        // Mailpit's API still reports the envelope-derived Bcc recipient (it received/queued the
        // message for that address) — this is expected and is not itself a header leak.
        Assert.Single(found.Bcc);

        Spike525Support.Evidence.Record("S-02", new
        {
            Provider = "mailpit",
            WireCapturedBccHeaderPresent = wireHasBccHeader,
            MailpitApiBccCount = found.Bcc.Length,
            MailpitApiToCount = found.To.Length,
            MailpitApiCcCount = found.Cc.Length,
            Note = "Mailpit API Bcc field is synthesized from envelope-minus-headers; not proof of wire content. See relay wire capture for ground truth.",
        });
    }

    /// <summary>
    /// #525 Agent B review (Draft PR #529) M-01: S-02 only verified a Spike-only "safe" BCC
    /// pattern (message.Bcc left empty, explicit envelope recipients). It did not verify the
    /// PRODUCTION-EQUIVALENT path: populate MimeMessage.Bcc directly (the obvious way BCC support
    /// would naturally be added to OutboundMimeMessageFactory) and send via the actual production
    /// <see cref="MailKitSmtpClient"/> class's single-argument <c>SendAsync(message, ct)</c> — the
    /// exact call <see cref="MailpitMailDeliveryProvider"/> makes today. This test closes that gap
    /// using the real internal production class (InternalsVisibleTo("Amane.Mailer.Tests")), not a
    /// reimplementation.
    /// </summary>
    [Fact]
    public async Task S02b_production_equivalent_sendasync_path_with_message_bcc_populated()
    {
        if (!Spike525Gate.MailpitEnabled)
        {
            return;
        }

        await using var relay = new Spike525SmtpRelay(Spike525Gate.MailpitSmtpHost, Spike525Gate.MailpitSmtpPort);
        relay.Start();

        var toAddress = Spike525Support.SyntheticAddress("s02b-to1");
        var ccAddress = Spike525Support.SyntheticAddress("s02b-cc1");
        var bccAddress = Spike525Support.SyntheticAddress("s02b-bcc1");
        var subject = "spike525-s02b-" + Guid.NewGuid().ToString("N")[..8];

        var message = new MimeMessage();
        message.From.Add(MailboxAddress.Parse(FromAddress));
        message.To.Add(MailboxAddress.Parse(toAddress));
        message.Cc.Add(MailboxAddress.Parse(ccAddress));
        message.Bcc.Add(MailboxAddress.Parse(bccAddress)); // production-equivalent: naive population of MimeMessage.Bcc
        message.Subject = subject;
        message.Body = new TextPart("plain") { Text = "s02b body" };

        // The real production class (src/Amane.Mailer/Delivery/MailKitSmtpClient.cs), used via its
        // IMailpitSmtpClient interface exactly as MailpitMailDeliveryProvider.SendAsync uses it:
        // single-argument SendAsync(message, ct), envelope derived internally by MailKit from
        // message.To/Cc/Bcc — no explicit recipient list.
        IMailpitSmtpClient client = new MailKitSmtpClient();
        await client.ConnectAsync("127.0.0.1", relay.ListenPort, SecureSocketOptions.None, TestContext.Current.CancellationToken);
        await client.SendAsync(message, TestContext.Current.CancellationToken);
        await client.DisconnectAsync(true, TestContext.Current.CancellationToken);
        await client.DisposeAsync();

        var wireHasBccHeader = relay.CapturedBytesContainHeader("Bcc");
        var wireBytes = relay.GetCapturedClientToServerBytes();
        var wireText = System.Text.Encoding.ASCII.GetString(wireBytes);
        var envelopeRcptToCount = System.Text.RegularExpressions.Regex.Matches(wireText, @"(?im)^RCPT TO:").Count;
        var envelopeIncludesBcc = wireText.Contains(bccAddress, StringComparison.OrdinalIgnoreCase);

        using var http = new HttpClient { BaseAddress = new Uri(Spike525Gate.MailpitHttpBaseUrl) };
        var api = new MailpitApiClient(http);
        var found = await WaitForSubjectAsync(api, subject, TestContext.Current.CancellationToken);
        Assert.NotNull(found);

        Spike525Support.Evidence.Record("S-02b", new
        {
            Provider = "mailpit",
            Scenario = "production-equivalent-message-bcc-populated-single-arg-sendasync",
            EnvelopeRcptToCount = envelopeRcptToCount,
            EnvelopeIncludesBccRecipient = envelopeIncludesBcc,
            WireCapturedBccHeaderPresent = wireHasBccHeader,
            Finding = wireHasBccHeader
                ? "CONFIRMED LEAK: MimeKit serializes a populated MimeMessage.Bcc into the literal SMTP DATA bytes when sent via the single-argument SendAsync(message, ct) overload. The current production IMailpitSmtpClient interface cannot safely support BCC without an explicit-envelope-recipients overload."
                : "MimeMessage.Bcc did NOT appear in literal DATA bytes even via the single-argument SendAsync(message, ct) overload; the existing interface may be sufficient, contingent on this finding replicating.",
        });

        // Ground truth check, resolved either way — do not assume the outcome in advance.
        Assert.True(envelopeIncludesBcc, "BCC recipient must still reach the SMTP envelope (RCPT TO) for mail to actually be delivered to that address.");
    }

    [Fact]
    public async Task S03_single_smtp_send_yields_one_mailpit_message_for_mixed_recipients()
    {
        if (!Spike525Gate.MailpitEnabled)
        {
            return;
        }

        var recipients = new SpikeMimeFactory.RecipientSet(
            To: [Spike525Support.SyntheticAddress("s03-to1")],
            Cc: [Spike525Support.SyntheticAddress("s03-cc1")],
            Bcc: [Spike525Support.SyntheticAddress("s03-bcc1")]);

        var subject = "spike525-s03-" + Guid.NewGuid().ToString("N")[..8];
        var message = SpikeMimeFactory.BuildMessage(FromAddress, recipients, subject, "s03 body");
        var envelopeRecipients = SpikeMimeFactory.EnvelopeRecipients(recipients);

        using (var client = new SmtpClient())
        {
            await client.ConnectAsync(Spike525Gate.MailpitSmtpHost, Spike525Gate.MailpitSmtpPort, SecureSocketOptions.None, TestContext.Current.CancellationToken);
            await client.SendAsync(message, MailboxAddress.Parse(FromAddress), envelopeRecipients, TestContext.Current.CancellationToken);
            await client.DisconnectAsync(true, TestContext.Current.CancellationToken);
        }

        using var http = new HttpClient { BaseAddress = new Uri(Spike525Gate.MailpitHttpBaseUrl) };
        var api = new MailpitApiClient(http);
        var all = await api.ListMessagesAsync(TestContext.Current.CancellationToken);
        var matches = all.Where(m => m.Subject == subject).ToArray();

        Assert.Single(matches);

        Spike525Support.Evidence.Record("S-03", new
        {
            Provider = "mailpit",
            SmtpInvocationCount = 1,
            MailpitMessageCount = matches.Length,
            MessageIdCardinality = "one-per-smtp-transaction",
            Note = "Mailpit/SMTP has no ACS-style operation-id concept; identity is the SMTP DATA transaction (Message-ID header) mapping 1:1 to one Mailpit message regardless of recipient-role mix. Not evidence for ACS cardinality (HOLD_ACS_CREDENTIAL_GATE).",
        });
    }

    [Theory]
    [InlineData(20, true)]  // product policy boundary: To10+CC10+BCC0 = 20
    [InlineData(21, true)]  // SMTP/Mailpit itself does not enforce the product's 20-recipient policy
    public async Task S14_mailpit_smtp_does_not_itself_enforce_the_product_recipient_policy(int totalRecipients, bool expectSmtpAccepts)
    {
        if (!Spike525Gate.MailpitEnabled)
        {
            return;
        }

        var to = Enumerable.Range(0, totalRecipients)
            .Select(i => Spike525Support.SyntheticAddress($"s14-{totalRecipients}-{i}"))
            .ToList();
        var recipients = new SpikeMimeFactory.RecipientSet(To: to, Cc: [], Bcc: []);
        var subject = "spike525-s14-" + totalRecipients + "-" + Guid.NewGuid().ToString("N")[..8];
        var message = SpikeMimeFactory.BuildMessage(FromAddress, recipients, subject, "s14 body");
        var envelopeRecipients = SpikeMimeFactory.EnvelopeRecipients(recipients);

        Exception? thrown = null;
        try
        {
            using var client = new SmtpClient();
            await client.ConnectAsync(Spike525Gate.MailpitSmtpHost, Spike525Gate.MailpitSmtpPort, SecureSocketOptions.None, TestContext.Current.CancellationToken);
            await client.SendAsync(message, MailboxAddress.Parse(FromAddress), envelopeRecipients, TestContext.Current.CancellationToken);
            await client.DisconnectAsync(true, TestContext.Current.CancellationToken);
        }
        catch (Exception ex)
        {
            thrown = ex;
        }

        Assert.Equal(expectSmtpAccepts, thrown is null);

        Spike525Support.Evidence.Record("S-14", new
        {
            Provider = "mailpit",
            TotalRecipients = totalRecipients,
            SmtpAccepted = thrown is null,
            Note = "SMTP/Mailpit recipient acceptance is a transport-level fact only; the 10/10/10/20 boundary is a Mailer product policy enforced before provider invocation, not a Mailpit limit.",
        });
    }

    private static async Task<MailpitMessageSummary?> WaitForSubjectAsync(
        MailpitApiClient api, string subject, CancellationToken cancellationToken)
    {
        for (var i = 0; i < 20; i++)
        {
            var all = await api.ListMessagesAsync(cancellationToken);
            var match = all.FirstOrDefault(m => m.Subject == subject);
            if (match is not null)
            {
                return match;
            }

            await Task.Delay(100, cancellationToken);
        }

        return null;
    }
}

/// <summary>Env-var gate for #525 Mailpit spike fixtures, mirroring AMANE_AZURITE_TESTS.</summary>
internal static class Spike525Gate
{
    internal static bool MailpitEnabled =>
        string.Equals(Environment.GetEnvironmentVariable("AMANE_SPIKE525_MAILPIT_TESTS"), "1", StringComparison.Ordinal);

    internal static string MailpitSmtpHost =>
        Environment.GetEnvironmentVariable("AMANE_SPIKE525_MAILPIT_SMTP_HOST") ?? "127.0.0.1";

    internal static int MailpitSmtpPort =>
        int.TryParse(Environment.GetEnvironmentVariable("AMANE_SPIKE525_MAILPIT_SMTP_PORT"), out var port) ? port : 1025;

    internal static string MailpitHttpBaseUrl =>
        Environment.GetEnvironmentVariable("AMANE_SPIKE525_MAILPIT_HTTP_URL") ?? "http://127.0.0.1:8025";
}
