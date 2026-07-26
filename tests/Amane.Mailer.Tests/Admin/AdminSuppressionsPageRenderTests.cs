using Amane.Mailer.Admin;
using Amane.Mailer.Configuration;
using Amane.Mailer.Data.Sqlite.Models;

namespace Amane.Mailer.Tests.Admin;

public sealed class AdminSuppressionsPageRenderTests
{
    private static readonly Guid TenantId = Guid.Parse("00000000-0000-0000-0000-000000000301");

    [Fact]
    public void Default_mask_hides_local_part_and_domain_body()
    {
        var page = SinglePage("secret-user@example.com", "hard_bounce");

        var html = AdminSuppressionsPage.RenderHtml(
            page,
            deadLetterCount: 0,
            selectedTenantId: null,
            currentCursor: null,
            visibleTenants: [],
            options: new MailerAdminOptions { ListPiiVisible = false, MaskRecipients = true });

        Assert.Contains("s***@e***.com", html, StringComparison.Ordinal);
        Assert.DoesNotContain("secret-user@example.com", html, StringComparison.Ordinal);
        Assert.DoesNotContain("@example.com", html, StringComparison.Ordinal);
        Assert.Contains("hard_bounce", html, StringComparison.Ordinal);
    }

    [Fact]
    public void Mask_recipients_false_alone_does_not_unmask_suppressions_list()
    {
        var page = SinglePage("visible-user@example.com", "manual");

        var html = AdminSuppressionsPage.RenderHtml(
            page,
            deadLetterCount: 0,
            selectedTenantId: null,
            currentCursor: null,
            visibleTenants: [],
            options: new MailerAdminOptions { ListPiiVisible = false, MaskRecipients = false, MaskSubjects = false });

        Assert.Contains("v***@e***.com", html, StringComparison.Ordinal);
        Assert.DoesNotContain("visible-user@example.com", html, StringComparison.Ordinal);
    }

    [Fact]
    public void List_pii_visible_capability_shows_recipient_email()
    {
        var page = SinglePage("visible-user@example.com", "manual");

        var html = AdminSuppressionsPage.RenderHtml(
            page,
            deadLetterCount: 0,
            selectedTenantId: null,
            currentCursor: null,
            visibleTenants: [],
            options: new MailerAdminOptions { ListPiiVisible = true, MaskRecipients = true });

        Assert.Contains("visible-user@example.com", html, StringComparison.Ordinal);
    }

    [Fact]
    public void Recipient_script_payload_is_escaped_when_unmasked()
    {
        var page = SinglePage("<script>alert(1)</script>@example.com", "hard_bounce");

        var html = AdminSuppressionsPage.RenderHtml(
            page,
            deadLetterCount: 0,
            selectedTenantId: null,
            currentCursor: null,
            visibleTenants: Array.Empty<MailerTenant>(),
            options: new MailerAdminOptions { ListPiiVisible = true });

        Assert.DoesNotContain("<script>", html, StringComparison.Ordinal);
        Assert.Contains("&lt;script&gt;", html, StringComparison.Ordinal);
    }

    [Fact]
    public void Page_is_view_only_and_does_not_advertise_unimplemented_cli()
    {
        var html = AdminSuppressionsPage.RenderHtml(
            new AdminSuppressionListPage([], null),
            deadLetterCount: 0,
            selectedTenantId: null,
            currentCursor: null,
            visibleTenants: [],
            options: new MailerAdminOptions { ListPiiVisible = false });

        Assert.Contains("閲覧のみ", html, StringComparison.Ordinal);
        Assert.Contains("#400 で実装予定", html, StringComparison.Ordinal);
        Assert.Contains("すべて", html, StringComparison.Ordinal);
        Assert.DoesNotContain("db suppressions remove", html, StringComparison.Ordinal);
        Assert.DoesNotContain("解除する", html, StringComparison.Ordinal);
        Assert.DoesNotContain("method=\"post\"", html, StringComparison.Ordinal);
    }

    [Fact]
    public void Unmasked_awaiting_tenant_selection_omits_all_option_and_prompts_for_tenant()
    {
        var tenants = new[]
        {
            CreateTenant(Guid.Parse("00000000-0000-0000-0000-000000000301")),
            CreateTenant(Guid.Parse("00000000-0000-0000-0000-000000000302")),
        };

        var html = AdminSuppressionsPage.RenderHtml(
            new AdminSuppressionListPage([], null),
            deadLetterCount: 0,
            selectedTenantId: null,
            currentCursor: null,
            visibleTenants: tenants,
            options: new MailerAdminOptions { ListPiiVisible = true },
            awaitingTenantSelection: true);

        Assert.True(html.Contains("テナントを選択してください", StringComparison.Ordinal), "DUMP_HTML_MARKER empty-row=" + (html.Contains("empty-row") ? "yes" : "no") + " snippet=" + html[Math.Max(0, html.IndexOf("empty-row") - 20)..Math.Min(html.Length - 1, html.IndexOf("empty-row") + 120)]);
        Assert.DoesNotContain("<option value=\"\">すべて</option>", html, StringComparison.Ordinal);
        Assert.Contains(tenants[0].TenantId.ToString("D"), html, StringComparison.Ordinal);
        Assert.Contains(tenants[1].TenantId.ToString("D"), html, StringComparison.Ordinal);
    }

    [Fact]
    public void Mask_suppression_recipient_masks_domain_label()
    {
        Assert.Equal("a***@e***.com", AdminSuppressionsPage.MaskSuppressionRecipient("alice@example.com"));
        Assert.Equal("***", AdminSuppressionsPage.MaskSuppressionRecipient("not-an-email"));
    }

    private static MailerTenant CreateTenant(Guid tenantId) =>
        new()
        {
            TenantId = tenantId,
            Name = "t-" + tenantId.ToString("N")[..8],
            SourceServices = ["svc"],
            DefaultFrom = new MailerAddress { Email = "from@example.com" },
            TokenEnv = "TOKEN",
            Provider = "mailpit",
            Retry = new MailerRetryOptions { MaxAttempts = 3, InitialDelaySeconds = 1 },
        };

    private static AdminSuppressionListPage SinglePage(string recipient, string reason) =>
        new(
            [
                new AdminSuppressionListRow(
                    Id: Guid.NewGuid(),
                    TenantId: TenantId,
                    RecipientEmail: recipient,
                    Reason: reason,
                    SourceBounceEventId: Guid.NewGuid(),
                    CreatedAt: DateTimeOffset.UtcNow),
            ],
            NextCursor: null);
}
