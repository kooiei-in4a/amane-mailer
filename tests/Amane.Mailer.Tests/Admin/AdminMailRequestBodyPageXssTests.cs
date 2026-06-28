using System.Net;
using System.Security.Claims;
using Amane.Mailer.Admin;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace Amane.Mailer.Tests.Admin;

public sealed class AdminMailRequestBodyPageXssTests
{
    [Fact]
    public void Html_body_script_tag_is_escaped_and_present_as_entity()
    {
        var html = AdminMailRequestBodyPage.RenderHtml(
            Guid.NewGuid(), "html_body", "<script>alert(1)</script>");

        Assert.DoesNotContain("<script>", html, StringComparison.Ordinal);
        Assert.Contains("&lt;script&gt;alert(1)&lt;/script&gt;", html, StringComparison.Ordinal);
    }

    [Fact]
    public void Text_body_script_tag_is_escaped_and_present_as_entity()
    {
        var html = AdminMailRequestBodyPage.RenderHtml(
            Guid.NewGuid(), "text_body", "<script>alert(1)</script>");

        Assert.DoesNotContain("<script>", html, StringComparison.Ordinal);
        Assert.Contains("&lt;script&gt;alert(1)&lt;/script&gt;", html, StringComparison.Ordinal);
    }

    [Fact]
    public void Html_body_iframe_tag_is_escaped()
    {
        var html = AdminMailRequestBodyPage.RenderHtml(
            Guid.NewGuid(), "html_body", "<iframe src=\"javascript:alert(1)\"></iframe>");

        Assert.DoesNotContain("<iframe", html, StringComparison.Ordinal);
    }

    [Fact]
    public void Metadata_json_script_tag_is_escaped_and_present_as_entity()
    {
        var html = AdminMailRequestBodyPage.RenderHtml(
            Guid.NewGuid(), "metadata_json", "<script>alert(1)</script>");

        Assert.DoesNotContain("<script>", html, StringComparison.Ordinal);
        Assert.Contains("&lt;script&gt;alert(1)&lt;/script&gt;", html, StringComparison.Ordinal);
    }

    [Fact]
    public void Field_name_is_escaped_in_page_title()
    {
        var html = AdminMailRequestBodyPage.RenderHtml(
            Guid.NewGuid(), "<script>", "body content");

        Assert.DoesNotContain("<script>", html, StringComparison.Ordinal);
    }

    [Fact]
    public void Body_view_audit_log_records_minimal_context_without_content_values()
    {
        var logger = new CapturingLogger();
        var requestId = Guid.NewGuid();
        const string body = "secret-body-content";
        const string subject = "Sensitive Subject ABC";
        const string recipient = "user@example.com";
        const string metadataValue = "internal-metadata-value";
        var context = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(
                new ClaimsIdentity(
                    [new Claim(ClaimTypes.Name, "admin-user")],
                    AdminAuthenticationConstants.Scheme)),
        };
        context.Connection.RemoteIpAddress = IPAddress.Parse("203.0.113.10");

        AdminMailRequestBodyPage.RecordBodyViewedAuditLog(
            context,
            new MailerAdminOptions(),
            logger,
            requestId,
            "metadata_json");

        var entry = Assert.Single(logger.Entries);
        Assert.Equal(LogLevel.Information, entry.Level);
        Assert.Equal("AdminMailRequestBodyViewed", entry.EventId.Name);
        Assert.Equal("admin-user", entry.State["AdminUsername"]);
        Assert.Equal(requestId, entry.State["MailRequestId"]);
        Assert.Equal("metadata_json", entry.State["FieldName"]);
        Assert.Equal("203.0.113.10", entry.State["RemoteAddress"]);
        Assert.Equal(
            $"Admin mail request body field viewed by admin-user for {requestId} field metadata_json from 203.0.113.10.",
            entry.FormattedMessage);
        Assert.DoesNotContain("Body", entry.State.Keys, StringComparer.Ordinal);
        Assert.DoesNotContain("Subject", entry.State.Keys, StringComparer.Ordinal);
        Assert.DoesNotContain("Recipient", entry.State.Keys, StringComparer.Ordinal);
        Assert.DoesNotContain("Metadata", entry.State.Keys, StringComparer.Ordinal);
        Assert.DoesNotContain(body, entry.FormattedMessage, StringComparison.Ordinal);
        Assert.DoesNotContain(subject, entry.FormattedMessage, StringComparison.Ordinal);
        Assert.DoesNotContain(recipient, entry.FormattedMessage, StringComparison.Ordinal);
        Assert.DoesNotContain(metadataValue, entry.FormattedMessage, StringComparison.Ordinal);
    }

    [Fact]
    public void Body_view_audit_log_sanitizes_actor_control_characters()
    {
        var logger = new CapturingLogger();
        var requestId = Guid.NewGuid();
        var context = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(
                new ClaimsIdentity(
                    [new Claim(ClaimTypes.Name, "admin\r\nFORGED")],
                    AdminAuthenticationConstants.Scheme)),
        };

        AdminMailRequestBodyPage.RecordBodyViewedAuditLog(
            context,
            new MailerAdminOptions(),
            logger,
            requestId,
            "text_body");

        var entry = Assert.Single(logger.Entries);
        Assert.Equal("admin  FORGED", entry.State["AdminUsername"]);
        Assert.Equal(
            $"Admin mail request body field viewed by admin  FORGED for {requestId} field text_body from unknown.",
            entry.FormattedMessage);
    }

    private sealed class CapturingLogger : ILogger
    {
        public List<LogEntry> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull =>
            null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            var properties = state as IEnumerable<KeyValuePair<string, object?>>
                ?? [];
            Entries.Add(new LogEntry(
                logLevel,
                eventId,
                formatter(state, exception),
                properties.ToDictionary(
                    static pair => pair.Key,
                    static pair => pair.Value,
                    StringComparer.Ordinal)));
        }
    }

    private sealed record LogEntry(
        LogLevel Level,
        EventId EventId,
        string FormattedMessage,
        Dictionary<string, object?> State);
}
