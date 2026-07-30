using System.Net;
using Amane.Mailer.Setup.Assistant;

namespace Amane.Mailer.Tests.Setup.Assistant;

/// <summary>
/// One-time token exchange, single-session enforcement, timeouts, and process stop conditions.
/// </summary>
public sealed class SetupAssistantSessionTests
{
    [Fact]
    public async Task One_time_token_is_exchanged_for_a_session()
    {
        await using var harness = await SetupAssistantHarness.StartAsync();

        using var response = await harness.RedeemTokenAsync();

        Assert.Equal(HttpStatusCode.SeeOther, response.StatusCode);
        Assert.Equal("/", response.Headers.Location?.OriginalString);

        var page = await harness.ReadCurrentPageAsync();
        Assert.Contains("ようこそ", page, StringComparison.Ordinal);
        Assert.NotNull(SetupAssistantHarness.ExtractCsrfToken(page));
    }

    [Fact]
    public async Task Landing_page_never_embeds_the_one_time_token()
    {
        await using var harness = await SetupAssistantHarness.StartAsync();

        var landing = await harness.ReadCurrentPageAsync();

        Assert.DoesNotContain(harness.Sessions.OneTimeTokenText, landing, StringComparison.Ordinal);
        Assert.Contains("type=\"password\"", landing, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Replaying_the_one_time_token_is_rejected()
    {
        await using var harness = await SetupAssistantHarness.StartAsync();
        using var first = await harness.RedeemTokenAsync();
        Assert.Equal(HttpStatusCode.SeeOther, first.StatusCode);

        using var replay = await harness.RedeemTokenAsync();

        Assert.Equal(HttpStatusCode.Forbidden, replay.StatusCode);
        Assert.Contains(
            "既に使用済み",
            await replay.Content.ReadAsStringAsync(TestContext.Current.CancellationToken),
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_wrong_token_is_rejected_and_leaves_the_real_token_usable()
    {
        await using var harness = await SetupAssistantHarness.StartAsync();

        using var wrong = await harness.RedeemTokenAsync("not-the-real-token");
        Assert.Equal(HttpStatusCode.Forbidden, wrong.StatusCode);

        using var correct = await harness.RedeemTokenAsync();
        Assert.Equal(HttpStatusCode.SeeOther, correct.StatusCode);
    }

    [Fact]
    public async Task An_expired_one_time_token_cannot_be_exchanged()
    {
        var options = new SetupAssistantOptions { OneTimeTokenLifetime = TimeSpan.FromMinutes(10) };
        await using var harness = await SetupAssistantHarness.StartAsync(options: options);

        harness.Time.Advance(TimeSpan.FromMinutes(11));

        using var response = await harness.RedeemTokenAsync();

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Contains(
            "有効期限",
            await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken),
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_second_concurrent_session_is_refused()
    {
        await using var harness = await SetupAssistantHarness.StartAsync();
        using var first = await harness.RedeemTokenAsync();
        Assert.Equal(HttpStatusCode.SeeOther, first.StatusCode);

        // A second browser presents the same printed token from a separate cookie jar.
        using var otherHandler = new HttpClientHandler { AllowAutoRedirect = false, UseCookies = true };
        using var otherBrowser = new HttpClient(otherHandler)
        {
            BaseAddress = new Uri(harness.Host.BaseAddress),
        };
        using var request = new HttpRequestMessage(HttpMethod.Post, "/token")
        {
            Content = new FormUrlEncodedContent(
                [new KeyValuePair<string, string>("one_time_token", harness.Sessions.OneTimeTokenText)]),
        };
        request.Headers.Add("Origin", harness.Origin);

        using var response = await otherBrowser.SendAsync(request, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task A_foreign_session_cookie_never_resolves_a_session()
    {
        await using var harness = await SetupAssistantHarness.StartAsync();
        await harness.RedeemTokenAsync();

        using var handler = new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false };
        using var client = new HttpClient(handler) { BaseAddress = new Uri(harness.Host.BaseAddress) };
        using var request = new HttpRequestMessage(HttpMethod.Get, "/");
        request.Headers.Add("Cookie", $"{SetupAssistantSecurity.SessionCookieName}=forged-session-id");

        using var response = await client.SendAsync(request, TestContext.Current.CancellationToken);

        Assert.Contains(
            "トークンを貼り付け",
            await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken),
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task Idle_timeout_stops_the_local_server_and_clears_the_session()
    {
        var options = new SetupAssistantOptions { IdleTimeout = TimeSpan.FromMinutes(15) };
        await using var harness = await SetupAssistantHarness.StartAsync(options: options);
        await harness.RedeemTokenAsync();

        harness.Time.Advance(TimeSpan.FromMinutes(16));
        harness.Sessions.EvaluateDeadlines();

        Assert.True(harness.Sessions.ShutdownToken.IsCancellationRequested);
        Assert.Equal(SetupAssistantShutdownReason.IdleTimeout, harness.Sessions.ShutdownReason);
        Assert.Null(harness.Sessions.TryResolve("anything"));
    }

    [Fact]
    public async Task Absolute_lifetime_stops_the_local_server_even_while_active()
    {
        var options = new SetupAssistantOptions
        {
            IdleTimeout = TimeSpan.FromHours(4),
            AbsoluteLifetime = TimeSpan.FromHours(2),
        };
        await using var harness = await SetupAssistantHarness.StartAsync(options: options);
        await harness.RedeemTokenAsync();

        // Stay active by touching the session before the absolute deadline passes.
        harness.Time.Advance(TimeSpan.FromMinutes(90));
        await harness.ReadCurrentPageAsync();
        harness.Time.Advance(TimeSpan.FromMinutes(31));
        harness.Sessions.EvaluateDeadlines();

        Assert.Equal(SetupAssistantShutdownReason.AbsoluteTimeout, harness.Sessions.ShutdownReason);
    }

    [Fact]
    public async Task An_unclaimed_token_expires_and_stops_the_local_server()
    {
        var options = new SetupAssistantOptions { OneTimeTokenLifetime = TimeSpan.FromMinutes(10) };
        await using var harness = await SetupAssistantHarness.StartAsync(options: options);

        harness.Time.Advance(TimeSpan.FromMinutes(11));
        harness.Sessions.EvaluateDeadlines();

        Assert.Equal(
            SetupAssistantShutdownReason.UnclaimedTokenExpired,
            harness.Sessions.ShutdownReason);
    }

    [Fact]
    public async Task Cancelling_stops_the_local_server()
    {
        await using var harness = await SetupAssistantHarness.StartAsync();
        await harness.RedeemTokenAsync();

        using var response = await harness.PostStepAsync("/cancel");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(SetupAssistantShutdownReason.Cancelled, harness.Sessions.ShutdownReason);
        Assert.True(harness.Sessions.ShutdownToken.IsCancellationRequested);
    }

    [Fact]
    public async Task Waiting_for_shutdown_returns_the_stop_reason()
    {
        await using var harness = await SetupAssistantHarness.StartAsync();
        await harness.RedeemTokenAsync();

        var wait = harness.Host.WaitForShutdownAsync(TestContext.Current.CancellationToken);
        harness.Sessions.Stop(SetupAssistantShutdownReason.Completed);

        Assert.Equal(SetupAssistantShutdownReason.Completed, await wait);
    }
}
