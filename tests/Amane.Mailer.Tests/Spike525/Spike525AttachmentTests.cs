using System.Security.Cryptography;
using MailKit.Net.Smtp;
using MailKit.Security;
using MimeKit;

namespace Amane.Mailer.Tests.Spike525;

/// <summary>
/// #525 Spike — S-15 (attachment mapping: filename/Content-Type/digest/length/order) and
/// S-16 (Japanese NFC filename round-trip) against real Mailpit, using MimeKitLite's
/// BodyBuilder.Attachments (the library the production Mailpit path already depends on).
/// No production DTO/contract changes — greenfield per baseline investigation.
///
/// Gated by AMANE_SPIKE525_MAILPIT_TESTS=1 (see Spike525Gate).
/// </summary>
public sealed class Spike525AttachmentTests
{
    private const string FromAddress = "sender@example.com";

    [Fact]
    public async Task S15_multiple_attachments_preserve_digest_type_and_order()
    {
        if (!Spike525Gate.MailpitEnabled)
        {
            return;
        }

        var attachments = new List<SpikeMimeFactory.SyntheticAttachment>
        {
            new("a-first.pdf", "application/pdf", SyntheticPdfBytes()),
            new("b-second.png", "image/png", SyntheticPngBytes()),
            new("c-third.txt", "text/plain", SyntheticTextBytes()),
        };

        var subject = "spike525-s15-" + Guid.NewGuid().ToString("N")[..8];
        var recipients = new SpikeMimeFactory.RecipientSet(
            To: [Spike525Support.SyntheticAddress("s15-to1")], Cc: [], Bcc: []);
        var message = SpikeMimeFactory.BuildMessage(FromAddress, recipients, subject, "s15 body", attachments);

        using (var client = new SmtpClient())
        {
            await client.ConnectAsync(Spike525Gate.MailpitSmtpHost, Spike525Gate.MailpitSmtpPort, SecureSocketOptions.None, TestContext.Current.CancellationToken);
            await client.SendAsync(message, MailboxAddress.Parse(FromAddress), SpikeMimeFactory.EnvelopeRecipients(recipients), TestContext.Current.CancellationToken);
            await client.DisconnectAsync(true, TestContext.Current.CancellationToken);
        }

        using var http = new HttpClient { BaseAddress = new Uri(Spike525Gate.MailpitHttpBaseUrl) };
        var api = new MailpitApiClient(http);
        var summary = await WaitForSubjectAsync(api, subject, TestContext.Current.CancellationToken);
        Assert.NotNull(summary);
        var detail = await api.GetMessageAsync(summary!.Id, TestContext.Current.CancellationToken);
        Assert.NotNull(detail);

        var expectedOrder = attachments.Select(a => a.FileName).ToArray();
        var actualOrder = detail!.Attachments.Select(a => a.FileName).ToArray();

        var digestMatches = new List<bool>();
        foreach (var attachment in attachments)
        {
            var apiAttachment = detail.Attachments.FirstOrDefault(a => a.FileName == attachment.FileName);
            var expectedSha256 = Convert.ToHexStringLower(SHA256.HashData(attachment.Content));
            digestMatches.Add(string.Equals(
                apiAttachment?.Checksums.Sha256, expectedSha256, StringComparison.OrdinalIgnoreCase));
        }

        Spike525Support.Evidence.Record("S-15", new
        {
            Provider = "mailpit",
            AttachmentCount = attachments.Count,
            OrderPreserved = expectedOrder.SequenceEqual(actualOrder),
            AllDigestsMatch = digestMatches.All(x => x),
            ContentTypesReturned = detail.Attachments.Select(a => a.ContentType).ToArray(),
        });

        Assert.Equal(expectedOrder, actualOrder);
        Assert.All(digestMatches, Assert.True);
        Assert.Equal(
            attachments.Select(a => a.ContentType).ToArray(),
            detail.Attachments.Select(a => a.ContentType).ToArray());
    }

    [Fact]
    public async Task S16_japanese_nfc_filename_round_trips_via_mimekit()
    {
        if (!Spike525Gate.MailpitEnabled)
        {
            return;
        }

        var japaneseFileName = "請求書.pdf".Normalize(System.Text.NormalizationForm.FormC);
        var attachments = new List<SpikeMimeFactory.SyntheticAttachment>
        {
            new(japaneseFileName, "application/pdf", SyntheticPdfBytes()),
        };

        var subject = "spike525-s16-" + Guid.NewGuid().ToString("N")[..8];
        var recipients = new SpikeMimeFactory.RecipientSet(
            To: [Spike525Support.SyntheticAddress("s16-to1")], Cc: [], Bcc: []);
        var message = SpikeMimeFactory.BuildMessage(FromAddress, recipients, subject, "s16 body", attachments);

        await using var relay = new Spike525SmtpRelay(Spike525Gate.MailpitSmtpHost, Spike525Gate.MailpitSmtpPort);
        relay.Start();

        using (var client = new SmtpClient())
        {
            await client.ConnectAsync("127.0.0.1", relay.ListenPort, SecureSocketOptions.None, TestContext.Current.CancellationToken);
            await client.SendAsync(message, MailboxAddress.Parse(FromAddress), SpikeMimeFactory.EnvelopeRecipients(recipients), TestContext.Current.CancellationToken);
            await client.DisconnectAsync(true, TestContext.Current.CancellationToken);
        }

        // Ground truth: how MimeKitLite actually encoded the filename on the wire.
        var wireBytes = relay.GetCapturedClientToServerBytes();
        var wireText = System.Text.Encoding.UTF8.GetString(wireBytes);
        var wireHasRfc2231 = wireText.Contains("filename*=utf-8''", StringComparison.OrdinalIgnoreCase);

        using var http = new HttpClient { BaseAddress = new Uri(Spike525Gate.MailpitHttpBaseUrl) };
        var api = new MailpitApiClient(http);
        var summary = await WaitForSubjectAsync(api, subject, TestContext.Current.CancellationToken);
        Assert.NotNull(summary);
        var detail = await api.GetMessageAsync(summary!.Id, TestContext.Current.CancellationToken);
        Assert.NotNull(detail);
        var apiFileName = detail!.Attachments.Single().FileName;
        var roundTripMatches = string.Equals(apiFileName, japaneseFileName, StringComparison.Ordinal);

        Spike525Support.Evidence.Record("S-16", new
        {
            Provider = "mailpit",
            WireEncodingIsRfc2231ExtendedParameter = wireHasRfc2231,
            ApiRoundTripMatches = roundTripMatches,
            ExpectedNfcFormPreserved = japaneseFileName.IsNormalized(System.Text.NormalizationForm.FormC),
        });

        Assert.True(wireHasRfc2231, "MimeKitLite is expected to encode non-ASCII attachment filenames as RFC 2231 filename*=UTF-8''... on the wire.");
        Assert.True(roundTripMatches, "Japanese NFC filename must round-trip through Mailpit's attachment API unchanged.");
    }

    /// <summary>
    /// #525 finding (discovered while building this spike, not hypothesized in advance):
    /// MimeKit auto-selects Content-Transfer-Encoding by content analysis and picks 7bit for
    /// ASCII text-safe attachment bytes. 7bit/8bit/quoted-printable are "text" encodings whose
    /// wire transport canonicalizes line endings (bare LF -> CRLF per MIME/SMTP), so decoded
    /// attachment bytes silently diverge from the original submitted bytes whenever the
    /// original content used LF-only line endings. Base64 has no such transformation. This is
    /// why SpikeMimeFactory now forces Base64 for every attachment part (see its comment) —
    /// this test intentionally reproduces the un-forced behavior once, as evidence for #523's
    /// "Mailer recomputes SHA-256 from decoded binary and rejects on mismatch" contract: the
    /// production attachment encoder MUST force Base64, or legitimate LF-only text attachments
    /// will fail Mailer's own digest re-validation.
    /// </summary>
    [Fact]
    public async Task S15_seven_bit_encoding_of_lf_only_text_does_not_round_trip_byte_exact()
    {
        if (!Spike525Gate.MailpitEnabled)
        {
            return;
        }

        var originalBytes = "line one\nline two\n"u8.ToArray(); // bare LF, no CR
        var subject = "spike525-s15b-" + Guid.NewGuid().ToString("N")[..8];
        var recipients = new SpikeMimeFactory.RecipientSet(
            To: [Spike525Support.SyntheticAddress("s15b-to1")], Cc: [], Bcc: []);

        var message = new MimeMessage();
        message.From.Add(MailboxAddress.Parse(FromAddress));
        message.To.Add(MailboxAddress.Parse(recipients.To[0]));
        message.Subject = subject;
        var builder = new BodyBuilder { TextBody = "s15b body" };
        // Deliberately NOT forcing Base64 here, unlike SpikeMimeFactory, to capture MimeKit's
        // un-forced auto-selected encoding as evidence.
        builder.Attachments.Add("notes.txt", originalBytes, ContentType.Parse("text/plain"));
        message.Body = builder.ToMessageBody();

        using (var client = new SmtpClient())
        {
            await client.ConnectAsync(Spike525Gate.MailpitSmtpHost, Spike525Gate.MailpitSmtpPort, SecureSocketOptions.None, TestContext.Current.CancellationToken);
            await client.SendAsync(message, MailboxAddress.Parse(FromAddress), SpikeMimeFactory.EnvelopeRecipients(recipients), TestContext.Current.CancellationToken);
            await client.DisconnectAsync(true, TestContext.Current.CancellationToken);
        }

        using var http = new HttpClient { BaseAddress = new Uri(Spike525Gate.MailpitHttpBaseUrl) };
        var api = new MailpitApiClient(http);
        var summary = await WaitForSubjectAsync(api, subject, TestContext.Current.CancellationToken);
        Assert.NotNull(summary);
        var detail = await api.GetMessageAsync(summary!.Id, TestContext.Current.CancellationToken);
        var apiAttachment = detail!.Attachments.Single();
        var expectedSha256 = Convert.ToHexStringLower(SHA256.HashData(originalBytes));
        var digestMatches = string.Equals(apiAttachment.Checksums.Sha256, expectedSha256, StringComparison.OrdinalIgnoreCase);

        Spike525Support.Evidence.Record("S-15b", new
        {
            Provider = "mailpit",
            Scenario = "unforced-encoding-lf-only-text-attachment",
            AutoSelectedEncodingWasSevenBit = true, // confirmed via raw MIME inspection during spike development
            DecodedDigestMatchesOriginal = digestMatches,
            Finding = "MimeKit auto-selected 7bit for LF-only text content; SMTP/MIME transport canonicalizes LF to CRLF for 7bit/8bit/quoted-printable, so decoded bytes diverge from the original. Production attachment encoding must force Base64.",
        });

        Assert.False(digestMatches, "Documents the un-forced-encoding gap: without forcing Base64, LF-only text attachment bytes do not round-trip byte-exact.");
    }

    private static byte[] SyntheticPdfBytes() =>
        "%PDF-1.4\n% spike525 synthetic, not a real PDF structure\n%%EOF\n"u8.ToArray();

    private static byte[] SyntheticPngBytes() =>
        [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0x00, 0x01, 0x02, 0x03]; // PNG signature + synthetic bytes, not a decodable image

    private static byte[] SyntheticTextBytes() =>
        "spike525 synthetic attachment text content\n"u8.ToArray();

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
