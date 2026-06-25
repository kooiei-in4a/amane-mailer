using Amane.Mailer.Admin;
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

    private static AdminMailRequestDetail CreateDetail(
        MailRequestState status = MailRequestState.Queued,
        DateTimeOffset? lockExpiresAt = null,
        string? htmlBody = null,
        Guid? id = null)
    {
        var now = DateTimeOffset.UtcNow;
        return new AdminMailRequestDetail(
            Id: id ?? Guid.NewGuid(),
            TenantId: Guid.NewGuid(),
            SourceService: "test-service",
            MailRequestId: Guid.NewGuid(),
            Purpose: "test",
            PayloadJson: "{}",
            PayloadHash: new string('a', 64),
            Subject: "Test subject",
            HtmlBody: htmlBody,
            TextBody: null,
            ReplyTo: null,
            RecipientEmail: "test@example.com",
            RecipientDisplayName: null,
            MetadataJson: null,
            Status: status,
            AttemptCount: 0,
            MaxAttempts: 3,
            NextAttemptAt: null,
            LockToken: null,
            LockExpiresAt: lockExpiresAt,
            DeliveredAt: null,
            FailedAt: null,
            LastErrorMessage: null,
            AcceptedAt: now,
            CreatedAt: now,
            UpdatedAt: now,
            CompletedAt: null);
    }
}
