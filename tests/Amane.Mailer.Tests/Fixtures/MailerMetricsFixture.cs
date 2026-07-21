using Amane.Mailer.Tests.Fixtures;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Amane.Mailer.Tests.Fixtures;

public sealed class MailerMetricsFixture() : MailerWebApplicationFixtureBase(workerEnabled: false)
{
    public static DateTimeOffset FixedNow { get; } = new(2026, 7, 3, 12, 0, 0, TimeSpan.Zero);

    protected override void ConfigureMailerServices(IServiceCollection services)
    {
        services.RemoveAll<TimeProvider>();
        services.AddSingleton<TimeProvider>(_ => new FixedUtcTimeProvider(FixedNow));
    }

    private sealed class FixedUtcTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => utcNow;
    }
}
