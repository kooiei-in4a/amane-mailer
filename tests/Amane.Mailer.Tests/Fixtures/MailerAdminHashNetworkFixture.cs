using System.Net;
using Amane.Mailer.Admin;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;

namespace Amane.Mailer.Tests.Fixtures;

public sealed class MailerAdminHashNetworkFixture() : MailerWebApplicationFixtureBase(workerEnabled: false)
{
    internal static readonly string IdentifierHashKey = Convert.ToBase64String(
    [
        0x01, 0x02, 0x03, 0x04, 0x05, 0x06, 0x07, 0x08,
        0x09, 0x0A, 0x0B, 0x0C, 0x0D, 0x0E, 0x0F, 0x10,
        0x11, 0x12, 0x13, 0x14, 0x15, 0x16, 0x17, 0x18,
        0x19, 0x1A, 0x1B, 0x1C, 0x1D, 0x1E, 0x1F, 0x20,
    ]);

    private static readonly string PasswordHash = AdminPasswordHasher.Hash(MailerAdminFixture.Password);

    protected override IReadOnlyDictionary<string, string?> ExtraConfiguration =>
        new Dictionary<string, string?>
        {
            ["AMANE_ADMIN_ENABLED"] = "true",
            ["AMANE_ADMIN_USERNAME"] = MailerAdminFixture.Username,
            ["AMANE_ADMIN_PASSWORD_HASH"] = PasswordHash,
            ["AMANE_ADMIN_ALLOWED_LOCAL_ADDRESS"] = "127.0.0.1",
            ["AMANE_ADMIN_AUDIT_HASH_NETWORK_IDENTIFIERS"] = "true",
            ["AMANE_ADMIN_AUDIT_IDENTIFIER_HASH_KEY"] = IdentifierHashKey,
        };

    protected override void ConfigureMailerServices(IServiceCollection services)
    {
        services.AddSingleton<IStartupFilter>(new LoopbackAddressStartupFilter());
    }

    private sealed class LoopbackAddressStartupFilter : IStartupFilter
    {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) =>
            app =>
            {
                app.Use(async (context, nextMiddleware) =>
                {
                    context.Connection.LocalIpAddress ??= IPAddress.Loopback;
                    context.Connection.RemoteIpAddress ??= IPAddress.Loopback;
                    await nextMiddleware();
                });

                next(app);
            };
    }
}
