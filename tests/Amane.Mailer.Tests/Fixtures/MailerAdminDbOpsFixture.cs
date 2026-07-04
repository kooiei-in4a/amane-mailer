using System.Net;
using Amane.Mailer.Admin;
using Amane.Mailer.Tests.Fixtures;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;

namespace Amane.Mailer.Tests.Fixtures;

public sealed class MailerAdminDbOpsFixture() : MailerWebApplicationFixtureBase(workerEnabled: false)
{
    public const string Username = MailerAdminFixture.Username;
    public const string Password = MailerAdminFixture.Password;

    private static readonly string PasswordHash = AdminPasswordHasher.Hash(Password);

    public string BackupDirectory =>
        Path.Combine(Path.GetDirectoryName(DatabaseFilePath)!, "backups");

    private string DatabaseFilePath =>
        ConnectionString["Data Source=".Length..];

    protected override IReadOnlyDictionary<string, string?> ExtraConfiguration =>
        new Dictionary<string, string?>
        {
            ["AMANE_ADMIN_ENABLED"] = "true",
            ["AMANE_ADMIN_USERNAME"] = Username,
            ["AMANE_ADMIN_PASSWORD_HASH"] = PasswordHash,
            ["AMANE_ADMIN_ALLOWED_LOCAL_ADDRESS"] = "127.0.0.1",
            ["AMANE_ADMIN_MASK_RECIPIENTS"] = "true",
            ["AMANE_ADMIN_MASK_SUBJECTS"] = "true",
            ["AMANE_ADMIN_DB_OPS_ENABLED"] = "true",
            ["AMANE_ADMIN_DB_BACKUP_DIRECTORY"] = BackupDirectory,
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
