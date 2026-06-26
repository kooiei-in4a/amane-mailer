using Amane.Mailer.Admin;

namespace Amane.Mailer.Tests.Admin;

public sealed class AdminAuditLogTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Summarize_user_agent_returns_null_for_blank(string? userAgent)
    {
        Assert.Null(AdminAuditLog.SummarizeUserAgent(userAgent));
    }

    [Fact]
    public void Summarize_user_agent_collapses_whitespace()
    {
        var summary = AdminAuditLog.SummarizeUserAgent("Mozilla/5.0   (X11;\tLinux)\n Gecko");

        Assert.Equal("Mozilla/5.0 (X11; Linux) Gecko", summary);
    }

    [Fact]
    public void Summarize_user_agent_truncates_to_summary_length()
    {
        var summary = AdminAuditLog.SummarizeUserAgent(new string('a', 300));

        Assert.NotNull(summary);
        Assert.Equal(120, summary!.Length);
    }

    [Fact]
    public void Normalize_actor_falls_back_to_unknown_for_blank()
    {
        Assert.Equal("unknown", AdminAuditLog.NormalizeActor(null));
        Assert.Equal("unknown", AdminAuditLog.NormalizeActor("   "));
    }

    [Fact]
    public void Normalize_actor_bounds_attacker_supplied_length()
    {
        var actor = AdminAuditLog.NormalizeActor(new string('z', 1000));

        Assert.Equal(256, actor.Length);
    }
}
