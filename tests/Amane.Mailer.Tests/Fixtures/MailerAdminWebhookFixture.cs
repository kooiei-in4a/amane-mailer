using System.Net;
using Amane.Mailer.Admin;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Amane.Mailer.Tests.Fixtures;

/// <summary>
/// Admin-enabled host whose tenant also has a webhook configured, so admin mutations actually
/// reach <c>DeliveryEventEnqueuer</c> and insert delivery events. <see cref="MailerAdminFixture"/>
/// has no webhook block, which makes enqueue a no-op there (#390).
/// </summary>
public sealed class MailerAdminWebhookFixture() : MailerWebApplicationFixtureBase(workerEnabled: false)
{
    public const string Username = "admin";
    public const string Password = "correct horse battery staple";
    public const string WebhookSecret = "test-webhook-secret-390";

    public static readonly string PasswordHash = AdminPasswordHasher.Hash(Password);

    public CapturingLoggerProvider LogCapture { get; } = new();

    protected override IReadOnlyDictionary<string, string?> ExtraConfiguration =>
        new Dictionary<string, string?>
        {
            ["AMANE_ADMIN_ENABLED"] = "true",
            ["AMANE_ADMIN_USERNAME"] = Username,
            ["AMANE_ADMIN_PASSWORD_HASH"] = PasswordHash,
            ["AMANE_ADMIN_ALLOWED_LOCAL_ADDRESS"] = "127.0.0.1",
            ["AMANE_ADMIN_MASK_RECIPIENTS"] = "true",
            ["AMANE_ADMIN_MASK_SUBJECTS"] = "true",
            ["TEST_WEBHOOK_SECRET"] = WebhookSecret,
        };

    protected override void ConfigureMailerServices(IServiceCollection services)
    {
        services.AddSingleton<IStartupFilter>(new TestLocalAddressStartupFilter(IPAddress.Loopback));
        services.AddSingleton<ILoggerProvider>(LogCapture);
    }

    protected override string BuildTenantConfigJson() =>
        $$"""
        {
          "version": 1,
          "environment": "develop",
          "tenants": [
            {
              "tenant_id": "{{TenantId}}",
              "name": "example-develop",
              "source_services": ["{{SourceService}}"],
              "default_from": {
                "email": "noreply@example.com",
                "display_name": "Example Service"
              },
              "token_env": "MAIL_SERVICE_TOKEN",
              "provider": "mailpit",
              "live_sending": false,
              "metadata_max_bytes": 4096,
              "retry": {
                "max_attempts": 3,
                "initial_delay_seconds": 1,
                "max_delay_seconds": 2
              },
              "webhook": {
                "url": "https://93.184.216.34/internal/mailer/webhooks",
                "secret_env": "TEST_WEBHOOK_SECRET"
              }
            }
          ]
        }
        """;

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
