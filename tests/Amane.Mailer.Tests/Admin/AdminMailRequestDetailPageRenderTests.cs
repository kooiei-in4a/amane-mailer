using Amane.Mailer.Admin;
using Amane.Mailer.Contracts.MailRequests;
using Amane.Mailer.Data.Sqlite;
using Amane.Mailer.Data.Sqlite.Models;

namespace Amane.Mailer.Tests.Admin;

public sealed class AdminMailRequestDetailPageRenderTests
{
    private static readonly MailerAdminOptions NoMaskOptions = new()
    {
        MaskRecipients = false,
        MaskSubjects = false,
    };

    [Fact]
    public void Lock_expires_at_is_shown_when_status_is_processing()
    {
        var lockExpiry = new DateTimeOffset(2025, 1, 1, 12, 0, 0, TimeSpan.Zero);
        var detail = CreateDetail(status: MailRequestState.Processing, lockExpiresAt: lockExpiry);

        var html = AdminMailRequestDetailPage.RenderHtml(detail, [], NoMaskOptions);

        Assert.Contains("lock_expires_at", html, StringComparison.Ordinal);
        Assert.Contains("2025-01-01", html, StringComparison.Ordinal);
    }

    [Fact]
    public void Lock_expires_at_is_not_shown_when_status_is_not_processing()
    {
        var lockExpiry = new DateTimeOffset(2025, 1, 1, 12, 0, 0, TimeSpan.Zero);
        var detail = CreateDetail(status: MailRequestState.Queued, lockExpiresAt: lockExpiry);

        var html = AdminMailRequestDetailPage.RenderHtml(detail, [], NoMaskOptions);

        Assert.DoesNotContain("lock_expires_at", html, StringComparison.Ordinal);
    }

    [Fact]
    public void Lock_expires_at_is_not_shown_when_processing_but_lock_expires_at_is_null()
    {
        var detail = CreateDetail(status: MailRequestState.Processing, lockExpiresAt: null);

        var html = AdminMailRequestDetailPage.RenderHtml(detail, [], NoMaskOptions);

        Assert.DoesNotContain("lock_expires_at", html, StringComparison.Ordinal);
    }

    [Fact]
    public void Body_links_appear_only_when_body_is_present()
    {
        var id = Guid.NewGuid();
        var withBody = CreateDetail(id: id, htmlBody: "<p>hello</p>");
        var withoutBody = CreateDetail(htmlBody: null);

        var htmlWith = AdminMailRequestDetailPage.RenderHtml(withBody, [], NoMaskOptions);
        var htmlWithout = AdminMailRequestDetailPage.RenderHtml(withoutBody, [], NoMaskOptions);

        Assert.Contains($"/admin/mail-requests/{id:D}/body?field=html_body", htmlWith, StringComparison.Ordinal);
        Assert.DoesNotContain("html_body", htmlWithout, StringComparison.Ordinal);
    }

    [Fact]
    public void Masked_mode_hides_short_subject_reply_to_and_display_name()
    {
        var maskOptions = new MailerAdminOptions
        {
            MaskRecipients = true,
            MaskSubjects = true,
        };
        var detail = CreateDetail(
            subject: "Secret",
            replyTo: "reply@example.com",
            recipientDisplayName: "Operator Name");

        var html = AdminMailRequestDetailPage.RenderHtml(detail, [], maskOptions);

        Assert.Contains("S***", html, StringComparison.Ordinal);
        Assert.Contains("r***@example.com", html, StringComparison.Ordinal);
        Assert.Contains("t***@example.com", html, StringComparison.Ordinal);
        Assert.DoesNotContain("Secret", html, StringComparison.Ordinal);
        Assert.DoesNotContain("reply@example.com", html, StringComparison.Ordinal);
        Assert.DoesNotContain("Operator Name", html, StringComparison.Ordinal);
    }

    [Fact]
    public void Masked_mode_truncates_long_subject()
    {
        var maskOptions = new MailerAdminOptions
        {
            MaskRecipients = true,
            MaskSubjects = true,
        };
        var detail = CreateDetail(subject: "Sensitive Subject ABC");

        var html = AdminMailRequestDetailPage.RenderHtml(detail, [], maskOptions);

        Assert.Contains("Sensitive Su...", html, StringComparison.Ordinal);
        Assert.DoesNotContain("Sensitive Subject ABC", html, StringComparison.Ordinal);
    }

    [Fact]
    public void Attachment_filename_is_masked_and_reveal_link_is_present()
    {
        var id = Guid.NewGuid();
        var detail = CreateDetail(id: id);
        var attachment = new AttachmentMetadataRow(
            Order: 0,
            FileName: "invoice-2026.pdf",
            ContentType: "application/pdf",
            ByteLength: 2048,
            ContentSha256: new string('f', 64),
            SpoolKey: Guid.NewGuid());

        var html = AdminMailRequestDetailPage.RenderHtml(detail, [], [attachment], [], NoMaskOptions);

        Assert.DoesNotContain("invoice-2026.pdf", html, StringComparison.Ordinal);
        Assert.Contains("i***.pdf", html, StringComparison.Ordinal);
        Assert.Contains($"/admin/mail-requests/{id:D}/attachments/0/filename", html, StringComparison.Ordinal);
    }

    [Fact]
    public void Attachments_section_is_omitted_when_there_are_no_attachments()
    {
        var html = AdminMailRequestDetailPage.RenderHtml(CreateDetail(), [], [], [], NoMaskOptions);

        Assert.DoesNotContain("添付ファイル", html, StringComparison.Ordinal);
    }

    private static AdminMailRequestDetail CreateDetail(
        MailRequestState status = MailRequestState.Queued,
        DateTimeOffset? lockExpiresAt = null,
        string? htmlBody = null,
        Guid? id = null,
        string subject = "Test subject",
        string? replyTo = null,
        string? recipientDisplayName = null)
    {
        var now = DateTimeOffset.UtcNow;
        return new AdminMailRequestDetail(
            Id: id ?? Guid.NewGuid(),
            TenantId: Guid.NewGuid(),
            SourceService: "test-service",
            MailRequestId: Guid.NewGuid(),
            Purpose: "test",
            PayloadHash: new string('a', 64),
            Subject: subject,
            HtmlBody: htmlBody,
            TextBody: null,
            ReplyTo: replyTo,
            RecipientEmail: "test@example.com",
            RecipientDisplayName: recipientDisplayName,
            MetadataJson: null,
            Status: status,
            AttemptCount: 0,
            MaxAttempts: 3,
            AttachmentCount: 0,
            NextAttemptAt: null,
            LockToken: null,
            LockExpiresAt: lockExpiresAt,
            DeliveredAt: null,
            FailedAt: null,
            DeliveryUnknownAt: null,
            LastErrorMessage: null,
            AcceptedAt: now,
            CreatedAt: now,
            UpdatedAt: now,
            CompletedAt: null)
        {
            Recipients =
            [
                new AdminRecipientSummary(
                    MailRecipientRole.To,
                    0,
                    "test@example.com",
                    recipientDisplayName,
                    MailRecipientDeliveryState.NotSent),
            ],
        };
    }
}
