using System.Security.Cryptography;
using System.Text;
using Amane.Mailer.Configuration;
using Amane.Mailer.Delivery;
using MimeKit;

namespace Amane.Mailer.Tests;

public sealed class OutboundMimeMessageFactoryAttachmentTests : IDisposable
{
    private readonly string _tempDirectory = Path.Combine(
        Path.GetTempPath(),
        "amane-mailer-mime-attachment-tests",
        Guid.NewGuid().ToString("N"));

    public OutboundMimeMessageFactoryAttachmentTests() => Directory.CreateDirectory(_tempDirectory);

    public void Dispose()
    {
        if (Directory.Exists(_tempDirectory))
        {
            Directory.Delete(_tempDirectory, recursive: true);
        }
    }

    [Fact]
    public void Create_attaches_file_with_forced_base64_encoding_and_byte_exact_round_trip()
    {
        // Bare-LF text content: MimeKit would auto-select 7bit for this (Spike525 S-15 finding),
        // which canonicalizes LF -> CRLF on the wire and would silently corrupt the digest.
        var originalBytes = Encoding.UTF8.GetBytes("line one\nline two\nline three\n");
        var filePath = Path.Combine(_tempDirectory, "attachment.bin");
        File.WriteAllBytes(filePath, originalBytes);

        var job = new MailSendJob(
            Guid.NewGuid(),
            "example-service",
            "Subject",
            HtmlBody: null,
            TextBody: "body",
            ReplyTo: null,
            RecipientEmail: "recipient@example.com",
            RecipientDisplayName: null,
            Attachments:
            [
                new MailSendAttachment("notes.txt", "text/plain", originalBytes.Length, filePath),
            ]);
        var tenant = CreateTenant();

        using var message = OutboundMimeMessageFactory.Create(job, tenant);

        var attachmentPart = Assert.Single(message.BodyParts.OfType<MimePart>().Where(p => p.IsAttachment));
        Assert.Equal(ContentEncoding.Base64, attachmentPart.ContentTransferEncoding);
        Assert.Equal("notes.txt", attachmentPart.FileName);

        using var decoded = new MemoryStream();
        attachmentPart.Content.DecodeTo(decoded);
        Assert.Equal(originalBytes, decoded.ToArray());
        Assert.Equal(
            Convert.ToHexString(SHA256.HashData(originalBytes)),
            Convert.ToHexString(SHA256.HashData(decoded.ToArray())));
    }

    [Fact]
    public void Create_preserves_nfc_japanese_filename()
    {
        var bytes = Encoding.UTF8.GetBytes("invoice contents");
        var filePath = Path.Combine(_tempDirectory, "invoice.pdf");
        File.WriteAllBytes(filePath, bytes);
        var fileName = "請求書.pdf".Normalize(NormalizationForm.FormC);

        var job = new MailSendJob(
            Guid.NewGuid(),
            "example-service",
            "Subject",
            HtmlBody: null,
            TextBody: "body",
            ReplyTo: null,
            RecipientEmail: "recipient@example.com",
            RecipientDisplayName: null,
            Attachments: [new MailSendAttachment(fileName, "application/pdf", bytes.Length, filePath)]);
        var tenant = CreateTenant();

        using var message = OutboundMimeMessageFactory.Create(job, tenant);

        var attachmentPart = Assert.Single(message.BodyParts.OfType<MimePart>().Where(p => p.IsAttachment));
        Assert.Equal(fileName, attachmentPart.FileName);
    }

    [Fact]
    public void Create_throws_a_path_free_exception_when_the_spool_file_is_missing()
    {
        // Simulates the crash window between the Worker's own File.Exists pre-flight check and
        // this factory's actual read (ADR 0022 D-08): a file-not-found exception message
        // embeds the private spool path, so it must never surface as the exception's Message.
        var missingFilePath = Path.Combine(_tempDirectory, "gone.bin");

        var job = new MailSendJob(
            Guid.NewGuid(),
            "example-service",
            "Subject",
            HtmlBody: null,
            TextBody: "body",
            ReplyTo: null,
            RecipientEmail: "recipient@example.com",
            RecipientDisplayName: null,
            Attachments: [new MailSendAttachment("notes.txt", "text/plain", 10, missingFilePath)]);
        var tenant = CreateTenant();

        var ex = Assert.Throws<AttachmentSpoolFileReadException>(() => OutboundMimeMessageFactory.Create(job, tenant));

        Assert.DoesNotContain(missingFilePath, ex.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(_tempDirectory, ex.Message, StringComparison.Ordinal);
        Assert.Null(ex.InnerException);
    }

    [Fact]
    public void Create_without_attachments_produces_no_attachment_parts()
    {
        var job = new MailSendJob(
            Guid.NewGuid(),
            "example-service",
            "Subject",
            HtmlBody: null,
            TextBody: "body",
            ReplyTo: null,
            RecipientEmail: "recipient@example.com",
            RecipientDisplayName: null);
        var tenant = CreateTenant();

        using var message = OutboundMimeMessageFactory.Create(job, tenant);

        Assert.Empty(message.BodyParts.OfType<MimePart>().Where(p => p.IsAttachment));
    }

    private static MailerTenant CreateTenant() => new()
    {
        TenantId = Guid.NewGuid(),
        Name = "tenant",
        SourceServices = ["example-service"],
        DefaultFrom = new MailerAddress { Email = "sender@example.com", DisplayName = "Sender" },
        TokenEnv = "MAIL_SERVICE_TOKEN",
        Provider = "mailpit",
        LiveSending = false,
        Retry = new MailerRetryOptions
        {
            MaxAttempts = 3,
            InitialDelaySeconds = 1,
            MaxDelaySeconds = 2,
        },
    };
}
