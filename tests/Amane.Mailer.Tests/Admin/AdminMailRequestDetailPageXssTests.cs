using Amane.Mailer.Admin;
using Amane.Mailer.Data.Sqlite;
using Amane.Mailer.Data.Sqlite.Models;

namespace Amane.Mailer.Tests.Admin;

public sealed class AdminMailRequestDetailPageXssTests
{
    private static readonly MailerAdminOptions NoMaskOptions = new()
    {
        MaskRecipients = false,
        MaskSubjects = false,
    };

    [Fact]
    public void Html_body_content_is_not_embedded_in_detail_page()
    {
        const string marker = "UNIQUE_HTML_BODY_MARKER_12345";
        var detail = CreateDetail(htmlBody: marker);

        var html = AdminMailRequestDetailPage.RenderHtml(detail, [], NoMaskOptions);

        Assert.DoesNotContain(marker, html, StringComparison.Ordinal);
    }

    [Fact]
    public void Text_body_content_is_not_embedded_in_detail_page()
    {
        const string marker = "UNIQUE_TEXT_BODY_MARKER_12345";
        var detail = CreateDetail(textBody: marker);

        var html = AdminMailRequestDetailPage.RenderHtml(detail, [], NoMaskOptions);

        Assert.DoesNotContain(marker, html, StringComparison.Ordinal);
    }

    [Fact]
    public void Last_error_message_script_tag_is_escaped_and_present_as_entity()
    {
        var detail = CreateDetail(lastErrorMessage: "<script>alert(1)</script>");

        var html = AdminMailRequestDetailPage.RenderHtml(detail, [], NoMaskOptions);

        Assert.DoesNotContain("<script>", html, StringComparison.Ordinal);
        Assert.Contains("&lt;script&gt;alert(1)&lt;/script&gt;", html, StringComparison.Ordinal);
    }

    [Fact]
    public void Attempt_error_message_script_tag_is_escaped_and_present_as_entity()
    {
        var now = DateTimeOffset.UtcNow;
        var attempt = new AdminMailAttemptRow(
            AttemptNumber: 1,
            Provider: "acs",
            Status: (int)MailRequestState.Failed,
            ProviderMessageId: null,
            ErrorCode: "ERR",
            ErrorMessage: "<script>alert(1)</script>",
            StartedAt: now,
            CompletedAt: now);

        var html = AdminMailRequestDetailPage.RenderHtml(CreateDetail(), [attempt], NoMaskOptions);

        Assert.DoesNotContain("<script>", html, StringComparison.Ordinal);
        Assert.Contains("&lt;script&gt;alert(1)&lt;/script&gt;", html, StringComparison.Ordinal);
    }

    [Fact]
    public void Recipient_display_name_is_hidden_when_mask_recipients_enabled()
    {
        var maskOptions = new MailerAdminOptions { MaskRecipients = true, MaskSubjects = false };
        var detail = CreateDetail(recipientDisplayName: "John Doe");

        var html = AdminMailRequestDetailPage.RenderHtml(detail, [], maskOptions);

        Assert.DoesNotContain("John Doe", html, StringComparison.Ordinal);
    }

    [Fact]
    public void Recipient_display_name_is_visible_when_mask_recipients_disabled()
    {
        var detail = CreateDetail(recipientDisplayName: "John Doe");

        var html = AdminMailRequestDetailPage.RenderHtml(detail, [], NoMaskOptions);

        Assert.Contains("John Doe", html, StringComparison.Ordinal);
    }

    private static AdminMailRequestDetail CreateDetail(
        string? htmlBody = null,
        string? textBody = null,
        string? lastErrorMessage = null,
        string? recipientDisplayName = null)
    {
        var now = DateTimeOffset.UtcNow;
        return new AdminMailRequestDetail(
            Id: Guid.NewGuid(),
            TenantId: Guid.NewGuid(),
            SourceService: "test-service",
            MailRequestId: Guid.NewGuid(),
            Purpose: "test",
            PayloadJson: "{}",
            PayloadHash: new string('a', 64),
            Subject: "Test subject",
            HtmlBody: htmlBody,
            TextBody: textBody,
            ReplyTo: null,
            RecipientEmail: "test@example.com",
            RecipientDisplayName: recipientDisplayName,
            MetadataJson: null,
            Status: MailRequestState.Queued,
            AttemptCount: 0,
            MaxAttempts: 3,
            NextAttemptAt: null,
            LockToken: null,
            LockExpiresAt: null,
            DeliveredAt: null,
            FailedAt: null,
            LastErrorMessage: lastErrorMessage,
            AcceptedAt: now,
            CreatedAt: now,
            UpdatedAt: now,
            CompletedAt: null);
    }
}
