using Amane.Mailer.Admin;
using Amane.Mailer.Data.Sqlite;
using Amane.Mailer.Data.Sqlite.Models;

namespace Amane.Mailer.Tests.Admin;

public sealed class AdminMailRequestDetailBounceRenderTests
{
    private static readonly MailerAdminOptions NoMaskOptions = new()
    {
        MaskRecipients = false,
        MaskSubjects = false,
    };

    [Fact]
    public void Bounce_status_message_script_tag_is_escaped()
    {
        var now = DateTimeOffset.UtcNow;
        var bounce = new AdminBounceEventRow(
            Id: Guid.NewGuid(),
            Provider: "acs",
            ProviderEventId: "evt-1",
            ProviderMessageId: "msg-1",
            DeliveryStatus: "Bounced",
            StatusMessage: "<script>alert(1)</script>",
            OccurredAt: now,
            CreatedAt: now);

        var html = AdminMailRequestDetailPage.RenderHtml(
            CreateDetail(),
            [],
            [],
            [bounce],
            NoMaskOptions);

        Assert.Contains("バウンス履歴", html, StringComparison.Ordinal);
        Assert.DoesNotContain("<script>", html, StringComparison.Ordinal);
        Assert.Contains("&lt;script&gt;alert(1)&lt;/script&gt;", html, StringComparison.Ordinal);
    }

    [Fact]
    public void Empty_bounce_section_shows_placeholder()
    {
        var html = AdminMailRequestDetailPage.RenderHtml(CreateDetail(), [], [], [], NoMaskOptions);

        Assert.Contains("バウンス履歴はありません", html, StringComparison.Ordinal);
    }

    private static AdminMailRequestDetail CreateDetail() =>
        new(
            Id: Guid.NewGuid(),
            TenantId: Guid.NewGuid(),
            SourceService: "svc",
            MailRequestId: Guid.NewGuid(),
            Purpose: "test",
            PayloadJson: "{}",
            PayloadHash: new string('0', 64),
            Subject: "subject",
            HtmlBody: null,
            TextBody: null,
            ReplyTo: null,
            RecipientEmail: "to@example.com",
            RecipientDisplayName: null,
            MetadataJson: null,
            Status: MailRequestState.Delivered,
            AttemptCount: 1,
            MaxAttempts: 3,
            AttachmentCount: 0,
            NextAttemptAt: null,
            LockToken: null,
            LockExpiresAt: null,
            DeliveredAt: DateTimeOffset.UtcNow,
            FailedAt: null,
            DeliveryUnknownAt: null,
            LastErrorMessage: null,
            AcceptedAt: DateTimeOffset.UtcNow,
            CreatedAt: DateTimeOffset.UtcNow,
            UpdatedAt: DateTimeOffset.UtcNow,
            CompletedAt: DateTimeOffset.UtcNow);
}
