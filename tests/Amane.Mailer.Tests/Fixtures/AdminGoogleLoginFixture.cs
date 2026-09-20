using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
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
    public const string DefaultLegacyId = "google-legacy-id-not-sub";

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

    public string Id { get; set; } = AdminGoogleLoginFixture.DefaultLegacyId;

    public bool IncludeSub { get; set; } = true;

    public bool IncludeId { get; set; }

    public bool FailTokenExchange { get; set; }

    public void ResetUserinfo()
    {
        Subject = AdminGoogleLoginFixture.DefaultSubject;
        Email = AdminGoogleLoginFixture.DefaultEmail;
        Id = AdminGoogleLoginFixture.DefaultLegacyId;
        IncludeSub = true;
        IncludeId = false;
        FailTokenExchange = false;
    }

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
            return Task.FromResult(JsonResponse(BuildUserinfoJson()));

        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
    }

    internal string BuildUserinfoJson()
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            if (IncludeId)
                writer.WriteString("id", Id);
            if (IncludeSub)
                writer.WriteString("sub", Subject);
            writer.WriteString("name", "Amane Test User");
            writer.WriteString("given_name", "Amane");
            writer.WriteString("family_name", "Test");
            writer.WriteString("picture", "https://example.invalid/google-photo.jpg");
            writer.WriteString("email", Email);
            writer.WriteBoolean("email_verified", true);
            writer.WriteString("locale", "en");
            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }

    private static HttpResponseMessage JsonResponse(string json) =>
        new(HttpStatusCode.OK)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json"),
        };
}
