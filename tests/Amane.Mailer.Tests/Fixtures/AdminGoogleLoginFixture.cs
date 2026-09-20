using System.Net;
using System.Net.Http;
using System.Text;
using Microsoft.AspNetCore.Authentication.Google;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;

namespace Amane.Mailer.Tests.Fixtures;

public sealed class AdminGoogleLoginFixture() : MailerWebApplicationFixtureBase(workerEnabled: false)
{
    public const string ClientId = "amane-mailer-test-google-client-id.apps.googleusercontent.com";
    public const string ClientSecret = "amane-mailer-test-google-client-secret-not-real";
    public const string DefaultSubject = "google-subject-test-001";
    public const string DefaultEmail = "unlinked-google-user@example.invalid";

    public GoogleBackchannelStub Backchannel { get; } = new();

    protected override IReadOnlyDictionary<string, string?> ExtraConfiguration =>
        new Dictionary<string, string?>
        {
            ["AMANE_ADMIN_ENABLED"] = "true",
            ["AMANE_ADMIN_USERNAME"] = MailerAdminFixture.Username,
            ["AMANE_ADMIN_PASSWORD_HASH"] = MailerAdminFixture.PasswordHash,
            ["AMANE_ADMIN_ALLOWED_LOCAL_ADDRESS"] = "127.0.0.1",
            ["AMANE_ADMIN_MASK_RECIPIENTS"] = "true",
            ["AMANE_ADMIN_MASK_SUBJECTS"] = "true",
            ["AMANE_ADMIN_GOOGLE_CLIENT_ID"] = ClientId,
            ["AMANE_ADMIN_GOOGLE_CLIENT_SECRET"] = ClientSecret,
        };

    protected override void ConfigureMailerServices(IServiceCollection services)
    {
        services.AddSingleton<IStartupFilter>(new TestLocalAddressStartupFilter(IPAddress.Loopback));
        services.PostConfigure<GoogleOptions>(GoogleDefaults.AuthenticationScheme, options =>
        {
            options.BackchannelHttpHandler = Backchannel;
            options.Backchannel = new HttpClient(Backchannel, disposeHandler: false)
            {
                Timeout = TimeSpan.FromSeconds(30),
                MaxResponseContentBufferSize = 1024 * 1024,
            };
        });
    }

    private sealed class TestLocalAddressStartupFilter(IPAddress localAddress) : IStartupFilter
    {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) =>
            app =>
            {
                app.Use(async (context, nextMiddleware) =>
                {
                    context.Connection.LocalIpAddress ??= localAddress;
                    await nextMiddleware();
                });

                next(app);
            };
    }
}

public sealed class GoogleBackchannelStub : HttpMessageHandler
{
    public string Subject { get; set; } = AdminGoogleLoginFixture.DefaultSubject;

    public string Email { get; set; } = AdminGoogleLoginFixture.DefaultEmail;

    public bool FailTokenExchange { get; set; }

    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        var uri = request.RequestUri?.AbsoluteUri ?? string.Empty;
        if (uri.Contains("/token", StringComparison.OrdinalIgnoreCase))
        {
            if (FailTokenExchange)
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.BadGateway)
                {
                    Content = new StringContent("{\"error\":\"temporarily_unavailable\"}", Encoding.UTF8, "application/json"),
                });
            }

            return Task.FromResult(JsonResponse(
                """{"access_token":"amane-mailer-test-google-access-token-not-real","token_type":"Bearer","expires_in":3600}"""));
        }

        if (uri.Contains("userinfo", StringComparison.OrdinalIgnoreCase))
        {
            var payload =
                "{\"id\":\"" + Subject + "\",\"sub\":\"" + Subject + "\",\"email\":\"" + Email + "\"}";
            return Task.FromResult(JsonResponse(payload));
        }

        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
    }

    private static HttpResponseMessage JsonResponse(string json) =>
        new(HttpStatusCode.OK)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json"),
        };
}
