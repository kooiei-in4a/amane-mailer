using Amane.Mailer.Admin;
using Amane.Mailer.Data.Sqlite.Models;

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

    [Fact]
    public void Normalize_actor_strips_control_characters_from_login_input()
    {
        var actor = AdminAuditLog.NormalizeActor("admin\r\nFORGED log entry");

        Assert.Equal("admin  FORGED log entry", actor);
    }

    [Theory]
    [InlineData("plain", "plain")]
    [InlineData("a\rb", "a b")]
    [InlineData("a\nb", "a b")]
    [InlineData("a\tb", "a b")]
    [InlineData("a\u0001b", "ab")]
    [InlineData(null, null)]
    public void Sanitize_audit_log_value_strips_control_characters(string? input, string? expected)
    {
        Assert.Equal(expected, AdminAuditLog.SanitizeAuditLogValue(input));
    }

    [Fact]
    public void Sanitize_for_output_normalizes_actor_before_logging_fields()
    {
        var sanitized = AdminAuditLog.SanitizeForOutput(
            new AdminAuditEvent
            {
                EventType = AdminAuditLog.EventTypes.LoginFailed,
                Actor = "user\r\nspoof",
                OccurredAt = DateTimeOffset.UtcNow,
                Result = AdminAuditLog.Results.Failure,
            });

        Assert.Equal("user  spoof", sanitized.Actor);
    }
}
