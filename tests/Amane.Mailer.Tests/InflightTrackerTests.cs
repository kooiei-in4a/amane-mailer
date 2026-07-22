using Amane.Mailer.Configuration;
using Amane.Mailer.Worker;

namespace Amane.Mailer.Tests;

public sealed class InflightTrackerTests
{
    [Fact]
    public async Task WaitForZeroAsync_returns_when_inflight_reaches_zero()
    {
        var tracker = new InflightTracker();
        var scope = tracker.Enter();
        Assert.Equal(1, tracker.InflightCount);

        var waitTask = tracker.WaitForZeroAsync(TimeSpan.FromSeconds(2), CancellationToken.None);
        scope.Dispose();

        await waitTask;
        Assert.Equal(0, tracker.InflightCount);
    }

    [Fact]
    public async Task WaitForZeroAsync_returns_after_timeout_while_inflight_remains()
    {
        var tracker = new InflightTracker();
        using var scope = tracker.Enter();

        await tracker.WaitForZeroAsync(TimeSpan.FromMilliseconds(80), CancellationToken.None);

        Assert.Equal(1, tracker.InflightCount);
    }
}

public sealed class MailerWebhookOptionsTests
{
    [Fact]
    public void ShutdownDrainTimeout_is_delivery_plus_finalize()
    {
        var options = new MailerWebhookOptions
        {
            DeliveryTimeoutSeconds = 30,
        };

        Assert.Equal(
            TimeSpan.FromSeconds(30 + MailerWebhookOptions.FinalizeTimeoutSeconds),
            options.ShutdownDrainTimeout);
        Assert.Equal(
            options.ShutdownDrainTimeout + TimeSpan.FromSeconds(MailerWebhookOptions.HostShutdownSlackSeconds),
            options.HostShutdownTimeout);
    }

    [Fact]
    public void Default_webhook_options_are_lease_safe()
    {
        var options = new MailerWebhookOptions();

        options.Validate();
    }
}
