using Amane.Mailer.Admin;
using Amane.Mailer.Data.Sqlite.Models;
using Amane.Mailer.Tests.Fixtures;
using Microsoft.Extensions.Configuration;

namespace Amane.Mailer.Tests.Admin;

public sealed class AdminSuppressionsPiiOptionsTests
{
    [Fact]
    public void Mask_recipients_false_without_list_mode_does_not_grant_unmasked_capability()
    {
        var options = LoadEnabledAdmin(new Dictionary<string, string?>
        {
            ["AMANE_ADMIN_MASK_RECIPIENTS"] = "false",
        });

        Assert.False(options.ListPiiVisible);
        Assert.False(options.MaskRecipients);
        Assert.False(AdminCapabilities.Has(options, AdminCapabilities.ViewUnmaskedListPii));
    }

    [Fact]
    public void List_pii_mode_visible_grants_unmasked_capability_even_if_mask_recipients_true()
    {
        var options = LoadEnabledAdmin(new Dictionary<string, string?>
        {
            ["MAILER_ADMIN_PII_LIST_MODE"] = "visible",
            ["AMANE_ADMIN_MASK_RECIPIENTS"] = "true",
        });

        Assert.True(options.ListPiiVisible);
        Assert.True(options.MaskRecipients);
        Assert.True(AdminCapabilities.Has(options, AdminCapabilities.ViewUnmaskedListPii));
    }

    [Fact]
    public void Config_to_html_mask_recipients_false_still_masks_suppressions()
    {
        var options = LoadEnabledAdmin(new Dictionary<string, string?>
        {
            ["AMANE_ADMIN_MASK_RECIPIENTS"] = "false",
        });

        var page = new AdminSuppressionListPage(
            [
                new AdminSuppressionListRow(
                    Id: Guid.NewGuid(),
                    TenantId: Guid.Parse("00000000-0000-0000-0000-000000000311"),
                    RecipientEmail: "config-user@example.com",
                    Reason: "hard_bounce",
                    SourceBounceEventId: null,
                    CreatedAt: DateTimeOffset.UtcNow),
            ],
            null);

        var html = AdminSuppressionsPage.RenderHtml(page, 0, null, null, [], options);

        Assert.Contains("c***@e***.com", html, StringComparison.Ordinal);
        Assert.DoesNotContain("config-user@example.com", html, StringComparison.Ordinal);
    }

    private static MailerAdminOptions LoadEnabledAdmin(Dictionary<string, string?> extra)
    {
        var settings = new Dictionary<string, string?>
        {
            ["AMANE_ADMIN_ENABLED"] = "true",
            ["AMANE_ADMIN_USERNAME"] = MailerAdminFixture.Username,
            ["AMANE_ADMIN_PASSWORD_HASH"] = MailerAdminFixture.PasswordHash,
        };
        foreach (var pair in extra)
            settings[pair.Key] = pair.Value;

        return MailerAdminOptions.Load(new ConfigurationBuilder().AddInMemoryCollection(settings).Build());
    }
}
