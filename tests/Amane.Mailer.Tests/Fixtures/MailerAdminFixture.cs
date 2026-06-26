using System.Net;
using Amane.Mailer.Admin;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;

namespace Amane.Mailer.Tests.Fixtures;

public sealed class MailerAdminFixture() : MailerWebApplicationFixtureBase(workerEnabled: false)
{
    public const string Username = "admin";
    public const string Password = "correct horse battery staple";

    private static readonly string PasswordHash = AdminPasswordHasher.Hash(Password);

    protected override IReadOnlyDictionary<string, string?> ExtraConfiguration =>
        new Dictionary<string, string?>
        {
            ["AMANE_ADMIN_ENABLED"] = "true",
            ["AMANE_ADMIN_USERNAME"] = Username,
            ["AMANE_ADMIN_PASSWORD_HASH"] = PasswordHash,
            ["AMANE_ADMIN_ALLOWED_LOCAL_ADDRESS"] = "127.0.0.1",
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
