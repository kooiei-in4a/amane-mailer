using Amane.Mailer.Admin;
using Amane.Mailer.Data.Sqlite.Models;
using Amane.Mailer.Identity;

namespace Amane.Mailer.Tests.Admin;

public sealed class AdminSendersPageRenderTests
{
    private static readonly DateTimeOffset CreatedAt = new(2026, 7, 3, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Sender_list_escapes_identity_values_and_shows_api_key_count()
    {
        var senderId = Guid.Parse("00000000-0000-0000-0000-000000000732");
        var html = AdminSendersPage.RenderListHtml(
            [new SenderSummary(
                senderId,
                "sender@example.com",
                "<img src=x onerror=alert(1)>",
                true,
                CreatedAt,
                null,
                2)],
            deadLetterCount: 0,
            csrfToken: "csrf");

        Assert.Contains("&lt;img src=x onerror=alert(1)&gt;", html, StringComparison.Ordinal);
        Assert.DoesNotContain("<img src=x", html, StringComparison.Ordinal);
        Assert.Contains("<td>2</td>", html, StringComparison.Ordinal);
        Assert.Contains("/admin/senders/00000000-0000-0000-0000-000000000732", html, StringComparison.Ordinal);
        Assert.Contains("Sender identityと、そのAPI Keyを管理する画面です", html, StringComparison.Ordinal);
        Assert.Contains("<code>enabled</code>", html, StringComparison.Ordinal);
        Assert.DoesNotContain("Instance-wide Admin", html, StringComparison.Ordinal);
        Assert.DoesNotContain("Break-glass Admin", html, StringComparison.Ordinal);
    }

    [Fact]
    public void Sender_list_shows_instance_owner_scope_in_topbar_when_access_is_provided()
    {
        var html = AdminSendersPage.RenderListHtml(
            [],
            deadLetterCount: 0,
            csrfToken: "csrf",
            new AdminTenantAccess("admin", IsBreakGlass: false, new HashSet<Guid>(), IsInstanceOwner: true));

        Assert.Contains("Instance-wide Admin", html, StringComparison.Ordinal);
        Assert.DoesNotContain("Break-glass Admin", html, StringComparison.Ordinal);
        Assert.DoesNotContain("Tenant-scoped Admin", html, StringComparison.Ordinal);
    }

    [Fact]
    public void Sender_list_distinguishes_break_glass_scope_from_instance_owner()
    {
        var html = AdminSendersPage.RenderListHtml(
            [],
            deadLetterCount: 0,
            csrfToken: "csrf",
            new AdminTenantAccess("break-glass", IsBreakGlass: true, new HashSet<Guid>(), IsInstanceOwner: false));

        Assert.Contains("Break-glass Admin", html, StringComparison.Ordinal);
        Assert.DoesNotContain("Instance-wide Admin", html, StringComparison.Ordinal);
    }

    [Fact]
    public void Sender_detail_reveals_plaintext_only_when_explicitly_supplied_and_escapes_it()
    {
        var senderId = Guid.Parse("00000000-0000-0000-0000-000000000732");
        var sender = new SenderIdentity(senderId, "sender@example.com", "Sender", true, CreatedAt, null);
        var key = new ApiKeyMetadata(
            Guid.Parse("00000000-0000-0000-0000-000000000733"),
            senderId,
            "<key-name>",
            CreatedAt,
            null);
        var plaintext = "secret<&\"";

        var normalHtml = AdminSendersPage.RenderDetailHtml(sender, [key], 0, "csrf", createdApiKey: null);
        Assert.DoesNotContain(plaintext, normalHtml, StringComparison.Ordinal);
        Assert.Contains("&lt;key-name&gt;", normalHtml, StringComparison.Ordinal);

        var revealHtml = AdminSendersPage.RenderDetailHtml(
            sender,
            [key],
            0,
            "csrf",
            new CreatedApiKey(key.KeyId, senderId, key.Name, plaintext, CreatedAt));
        Assert.Contains("このキーは今だけ表示されます。", revealHtml, StringComparison.Ordinal);
        Assert.Contains("secret&lt;&amp;&quot;", revealHtml, StringComparison.Ordinal);
        Assert.DoesNotContain("secret<&\"", revealHtml, StringComparison.Ordinal);
    }
}
