using System.Net;
using Amane.Mailer.Setup.Assistant;
using Amane.Mailer.Tests.Fixtures;

namespace Amane.Mailer.Tests.Setup.Assistant;

/// <summary>
/// The normal Mailer runtime must never gain a setup route. The assistant lives in its own
/// isolated host, started only by <c>setup assistant</c> (Issue #452, ADR 0021 D-01).
/// </summary>
public sealed class SetupAssistantRuntimeSeparationTests(MailerApiFixture fixture)
    : IClassFixture<MailerApiFixture>
{
    [Theory]
    [InlineData("/token")]
    [InlineData("/welcome")]
    [InlineData("/mode")]
    [InlineData("/confirm")]
    [InlineData("/admin-bootstrap")]
    [InlineData("/assistant.css")]
    [InlineData("/setup/assistant")]
    public async Task Runtime_does_not_serve_assistant_routes(string path)
    {
        using var client = fixture.Factory.CreateClient();

        using var response = await client.GetAsync(path, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Runtime_root_is_not_the_assistant_landing_page()
    {
        using var client = fixture.Factory.CreateClient();

        using var response = await client.GetAsync("/", TestContext.Current.CancellationToken);
        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        Assert.DoesNotContain("Amane Mailer Easy Setup", body, StringComparison.Ordinal);
        Assert.DoesNotContain(SetupAssistantSecurity.CsrfFieldName, body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Runtime_never_issues_the_assistant_session_cookie()
    {
        using var client = fixture.Factory.CreateClient();

        using var response = await client.GetAsync("/healthz", TestContext.Current.CancellationToken);

        Assert.DoesNotContain(
            response.Headers.TryGetValues("Set-Cookie", out var cookies) ? cookies : [],
            value => value.Contains(SetupAssistantSecurity.SessionCookieName, StringComparison.Ordinal));
    }
}
