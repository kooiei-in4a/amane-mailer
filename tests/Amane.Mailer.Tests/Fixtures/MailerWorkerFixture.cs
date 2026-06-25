using Amane.Mailer.Delivery;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;

namespace Amane.Mailer.Tests.Fixtures;

public sealed class MailerWorkerFixture() : MailerWebApplicationFixtureBase(workerEnabled: true)
{
    public CapturingMailDeliveryProvider DeliveryProvider { get; } = new();

    protected override IReadOnlyDictionary<string, string?> ExtraConfiguration =>
        new Dictionary<string, string?>
        {
            ["Mailer:Worker:SendTimeoutSeconds"] = "2",
            ["Mailer:Worker:LeaseDurationSeconds"] = "30",
        };

    public new async ValueTask InitializeAsync()
    {
        await base.InitializeAsync();
        _ = Factory.CreateClient();
    }

    protected override void ConfigureMailerServices(IServiceCollection services)
    {
        services.RemoveAll<IMailDeliveryProvider>();
        services.AddSingleton<IMailDeliveryProvider>(DeliveryProvider);
    }
}
