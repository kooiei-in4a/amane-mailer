using Amane.Mailer.Admin;
using Amane.Mailer.Configuration;
using Amane.Mailer.Data.Sqlite.Models;

namespace Amane.Mailer.Tests.Admin;

public sealed class AdminSuppressionsPageRenderTests
{
    private static readonly Guid TenantId = Guid.Parse("00000000-0000-0000-0000-000000000301");

    [Fact]
    public void Masked_mode_hides_full_recipient_email()
    {
        var page = new AdminSuppressionListPage(
            [
                new AdminSuppressionListRow(
                    Id: Guid.NewGuid(),
                    TenantId: TenantId,
                    RecipientEmail: "secret-user@example.com",
                    Reason: "hard_bounce",
                    SourceBounceEventId: Guid.NewGuid(),
                    CreatedAt: DateTimeOffset.UtcNow),
            ],
            NextCursor: null);

        var html = AdminSuppressionsPage.RenderHtml(
            page,
            deadLetterCount: 0,
            selectedTenantId: null,
            currentCursor: null,
            visibleTenants: [],
            options: new MailerAdminOptions { MaskRecipients = true, MaskSubjects = true });

        Assert.Contains("s***@example.com", html, StringComparison.Ordinal);
        Assert.DoesNotContain("secret-user@example.com", html, StringComparison.Ordinal);
        Assert.Contains("hard_bounce", html, StringComparison.Ordinal);
    }

    [Fact]
    public void Visible_mode_shows_recipient_email_when_opted_in()
    {
        var page = new AdminSuppressionListPage(
            [
                new AdminSuppressionListRow(
                    Id: Guid.NewGuid(),
                    TenantId: TenantId,
                    RecipientEmail: "visible-user@example.com",
                    Reason: "manual",
                    SourceBounceEventId: null,
                    CreatedAt: DateTimeOffset.UtcNow),
            ],
            NextCursor: null);

        var html = AdminSuppressionsPage.RenderHtml(
            page,
            deadLetterCount: 0,
            selectedTenantId: null,
            currentCursor: null,
            visibleTenants: [],
            options: new MailerAdminOptions { MaskRecipients = false, MaskSubjects = false });

        Assert.Contains("visible-user@example.com", html, StringComparison.Ordinal);
    }

    [Fact]
    public void Recipient_script_payload_is_escaped()
    {
        var page = new AdminSuppressionListPage(
            [
                new AdminSuppressionListRow(
                    Id: Guid.NewGuid(),
                    TenantId: TenantId,
                    RecipientEmail: "<script>alert(1)</script>@example.com",
                    Reason: "hard_bounce",
                    SourceBounceEventId: null,
                    CreatedAt: DateTimeOffset.UtcNow),
            ],
            NextCursor: null);

        var html = AdminSuppressionsPage.RenderHtml(
            page,
            deadLetterCount: 0,
            selectedTenantId: null,
            currentCursor: null,
            visibleTenants: Array.Empty<MailerTenant>(),
            options: new MailerAdminOptions { MaskRecipients = false, MaskSubjects = false });

        Assert.DoesNotContain("<script>", html, StringComparison.Ordinal);
        Assert.Contains("&lt;script&gt;", html, StringComparison.Ordinal);
    }

    [Fact]
    public void Page_is_view_only_without_remove_controls()
    {
        var html = AdminSuppressionsPage.RenderHtml(
            new AdminSuppressionListPage([], null),
            deadLetterCount: 0,
            selectedTenantId: null,
            currentCursor: null,
            visibleTenants: [],
            options: new MailerAdminOptions { MaskRecipients = true });

        Assert.Contains("閲覧のみ", html, StringComparison.Ordinal);
        Assert.DoesNotContain("解除する", html, StringComparison.Ordinal);
        Assert.DoesNotContain("method=\"post\"", html, StringComparison.Ordinal);
    }
}
