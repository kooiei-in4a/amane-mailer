using System.Net;
using Amane.Mailer.Admin;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;

namespace Amane.Mailer.Tests.Fixtures;

/// <summary>
/// Admin fixture with MAILER_ADMIN_PII_LIST_MODE=visible for suppressions list-unmasked audits.
/// </summary>
public sealed class MailerAdminUnmaskedListFixture() : MailerWebApplicationFixtureBase(workerEnabled: false)
{
    public const string Username = MailerAdminFixture.Username;
    public const string Password = MailerAdminFixture.Password;
    public static readonly string PasswordHash = MailerAdminFixture.PasswordHash;

    protected override IReadOnlyDictionary<string, string?> ExtraConfiguration =>
        new Dictionary<string, string?>
        {
            ["AMANE_ADMIN_ENABLED"] = "true",
            ["AMANE_ADMIN_USERNAME"] = Username,
            ["AMANE_ADMIN_PASSWORD_HASH"] = PasswordHash,
            ["AMANE_ADMIN_ALLOWED_LOCAL_ADDRESS"] = "127.0.0.1",
            ["MAILER_ADMIN_PII_LIST_MODE"] = "visible",
            ["AMANE_ADMIN_MASK_RECIPIENTS"] = "true",
            ["AMANE_ADMIN_MASK_SUBJECTS"] = "true",
        };

    protected override void ConfigureMailerServices(IServiceCollection services)
    {
        services.AddSingleton<IStartupFilter>(new TestLocalAddressStartupFilter(IPAddress.Loopback));
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
