using System.Net;
using Amane.Mailer.Setup.Assistant;

namespace Amane.Mailer.Tests.Setup.Assistant;

/// <summary>
/// Browser-equivalent transport checks: Host allowlist, Origin validation, CSRF, GET safety,
/// response hardening, and the absence of any external resource reference.
/// </summary>
public sealed class SetupAssistantHttpSecurityTests
{
    [Fact]
    public async Task Responses_carry_the_hardening_headers()
    {
        await using var harness = await SetupAssistantHarness.StartAsync();

        using var response = await harness.GetAsync("/");

        Assert.Equal(
            SetupAssistantSecurity.ContentSecurityPolicy,
            Assert.Single(response.Headers.GetValues("Content-Security-Policy")));
        Assert.Equal("DENY", Assert.Single(response.Headers.GetValues("X-Frame-Options")));
        Assert.Equal("nosniff", Assert.Single(response.Headers.GetValues("X-Content-Type-Options")));
        Assert.Equal("no-referrer", Assert.Single(response.Headers.GetValues("Referrer-Policy")));
        Assert.True(response.Headers.CacheControl?.NoStore);
    }

    [Fact]
    public async Task Content_security_policy_forbids_script_and_remote_sources()
    {
        await using var harness = await SetupAssistantHarness.StartAsync();

        using var response = await harness.GetAsync("/");
        var policy = Assert.Single(response.Headers.GetValues("Content-Security-Policy"));

        Assert.Contains("default-src 'none'", policy, StringComparison.Ordinal);
        Assert.Contains("frame-ancestors 'none'", policy, StringComparison.Ordinal);
        Assert.Contains("form-action 'self'", policy, StringComparison.Ordinal);
        Assert.DoesNotContain("unsafe-inline", policy, StringComparison.Ordinal);
        Assert.DoesNotContain("script-src", policy, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Cors_is_not_enabled()
    {
        await using var harness = await SetupAssistantHarness.StartAsync();

        using var request = new HttpRequestMessage(HttpMethod.Get, "/");
        request.Headers.Add("Origin", "http://attacker.invalid");
        using var response = await harness.Client.SendAsync(request, TestContext.Current.CancellationToken);

        Assert.False(response.Headers.Contains("Access-Control-Allow-Origin"));
        Assert.False(response.Headers.Contains("Access-Control-Allow-Credentials"));
    }

    [Theory]
    [InlineData("attacker.invalid")]
    [InlineData("attacker.invalid:80")]
    [InlineData("192.168.1.10:5280")]
    [InlineData("localhost")]
    public async Task Host_header_mismatch_is_rejected(string host)
    {
        await using var harness = await SetupAssistantHarness.StartAsync();

        using var request = new HttpRequestMessage(HttpMethod.Get, "/");
        request.Headers.Host = host;
        using var response = await harness.Client.SendAsync(request, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Loopback_hostnames_on_the_bound_port_are_accepted()
    {
        await using var harness = await SetupAssistantHarness.StartAsync();

        foreach (var host in new[] { $"127.0.0.1:{harness.Host.BoundPort}", $"localhost:{harness.Host.BoundPort}" })
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, "/");
            request.Headers.Host = host;
            using var response = await harness.Client.SendAsync(request, TestContext.Current.CancellationToken);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }
    }

    [Fact]
    public async Task A_loopback_host_on_another_port_is_rejected()
    {
        await using var harness = await SetupAssistantHarness.StartAsync();

        using var request = new HttpRequestMessage(HttpMethod.Get, "/");
        request.Headers.Host = $"127.0.0.1:{harness.Host.BoundPort + 1}";
        using var response = await harness.Client.SendAsync(request, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task A_state_change_without_an_origin_header_is_rejected()
    {
        await using var harness = await SetupAssistantHarness.StartAsync();

        using var response = await harness.PostAsync(
            "/token",
            [new KeyValuePair<string, string>("one_time_token", harness.Sessions.OneTimeTokenText)],
            includeOrigin: false);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Theory]
    [InlineData("http://attacker.invalid")]
    [InlineData("https://127.0.0.1")]
    [InlineData("null")]
    public async Task A_state_change_from_a_foreign_origin_is_rejected(string origin)
    {
        await using var harness = await SetupAssistantHarness.StartAsync();

        using var response = await harness.PostAsync(
            "/token",
            [new KeyValuePair<string, string>("one_time_token", harness.Sessions.OneTimeTokenText)],
            origin: origin);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task A_cross_origin_post_never_advances_the_workflow()
    {
        await using var harness = await SetupAssistantHarness.StartAsync();
        await harness.RedeemTokenAsync();
        var token = SetupAssistantHarness.ExtractCsrfToken(await harness.ReadCurrentPageAsync());

        using var response = await harness.PostAsync(
            "/welcome",
            [],
            csrfToken: token,
            origin: "http://attacker.invalid");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Contains("ようこそ", await harness.ReadCurrentPageAsync(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_post_without_a_csrf_token_is_rejected()
    {
        await using var harness = await SetupAssistantHarness.StartAsync();
        await harness.RedeemTokenAsync();

        using var response = await harness.PostAsync("/welcome");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("ようこそ", await harness.ReadCurrentPageAsync(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_post_with_a_forged_csrf_token_is_rejected()
    {
        await using var harness = await SetupAssistantHarness.StartAsync();
        await harness.RedeemTokenAsync();
        var real = SetupAssistantHarness.ExtractCsrfToken(await harness.ReadCurrentPageAsync())!;

        using var response = await harness.PostAsync(
            "/welcome",
            [],
            csrfToken: new string('a', real.Length));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("ようこそ", await harness.ReadCurrentPageAsync(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("/token")]
    [InlineData("/welcome")]
    [InlineData("/mode")]
    [InlineData("/confirm")]
    [InlineData("/verify")]
    [InlineData("/admin-bootstrap")]
    [InlineData("/finish")]
    [InlineData("/cancel")]
    public async Task State_changing_routes_reject_get(string path)
    {
        await using var harness = await SetupAssistantHarness.StartAsync();
        await harness.RedeemTokenAsync();

        using var response = await harness.GetAsync(path);

        Assert.Equal(HttpStatusCode.MethodNotAllowed, response.StatusCode);
        Assert.Equal(SetupAssistantShutdownReason.None, harness.Sessions.ShutdownReason);
    }

    [Fact]
    public async Task No_screen_references_an_external_resource()
    {
        await using var harness = await SetupAssistantHarness.StartAsync();
        await harness.RedeemTokenAsync();

        // ReadCurrentPageAsync applies the same guard to every screen every test walks through.
        SetupAssistantHarness.AssertNoExternalResource(await harness.ReadCurrentPageAsync());

        using var styleSheet = await harness.GetAsync(SetupAssistantPages.StyleSheetPath);
        var css = await styleSheet.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        SetupAssistantHarness.AssertNoExternalResource(css);
        Assert.DoesNotContain("url(", css, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task The_session_cookie_is_http_only_and_same_site_strict()
    {
        await using var harness = await SetupAssistantHarness.StartAsync();

        using var response = await harness.RedeemTokenAsync();

        var cookie = Assert.Single(
            response.Headers.GetValues("Set-Cookie"),
            value => value.StartsWith(SetupAssistantSecurity.SessionCookieName, StringComparison.Ordinal));
        Assert.Contains("httponly", cookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("samesite=strict", cookie, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("expires=", cookie, StringComparison.OrdinalIgnoreCase);
    }
}
