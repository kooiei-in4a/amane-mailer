using Amane.Mailer.Configuration;
using Amane.Mailer.Worker;

namespace Amane.Mailer.Tests;

public sealed class InflightTrackerTests
{
    [Fact]
    public async Task WaitForZeroAsync_returns_immediately_when_inflight_is_already_zero()
    {
        var tracker = new InflightTracker();

        var waitTask = tracker.WaitForZeroAsync(TimeSpan.FromSeconds(2), TimeProvider.System, CancellationToken.None);

        Assert.True(waitTask.IsCompletedSuccessfully);
        await waitTask;
        Assert.Equal(0, tracker.InflightCount);
    }

    [Fact]
    public async Task WaitForZeroAsync_returns_when_inflight_reaches_zero()
    {
        var tracker = new InflightTracker();
        var scope = tracker.Enter();
        Assert.Equal(1, tracker.InflightCount);

        var waitTask = tracker.WaitForZeroAsync(TimeSpan.FromSeconds(2), TimeProvider.System, CancellationToken.None);
        Assert.False(waitTask.IsCompleted);

        scope.Dispose();

        await waitTask;
        Assert.Equal(0, tracker.InflightCount);
    }

    [Fact]
    public async Task WaitForZeroAsync_waits_until_all_scopes_are_disposed()
    {
        var tracker = new InflightTracker();
        var first = tracker.Enter();
        var second = tracker.Enter();
        Assert.Equal(2, tracker.InflightCount);

        var waitTask = tracker.WaitForZeroAsync(TimeSpan.FromSeconds(2), TimeProvider.System, CancellationToken.None);
        first.Dispose();
        Assert.False(waitTask.IsCompleted);
        Assert.Equal(1, tracker.InflightCount);

        second.Dispose();
        await waitTask;
        Assert.Equal(0, tracker.InflightCount);
    }

    [Fact]
    public async Task WaitForZeroAsync_completes_all_waiters_when_count_reaches_zero()
    {
        var tracker = new InflightTracker();
        var scope = tracker.Enter();

        var firstWait = tracker.WaitForZeroAsync(TimeSpan.FromSeconds(2), TimeProvider.System, CancellationToken.None);
        var secondWait = tracker.WaitForZeroAsync(TimeSpan.FromSeconds(2), TimeProvider.System, CancellationToken.None);
        Assert.False(firstWait.IsCompleted);
        Assert.False(secondWait.IsCompleted);

        scope.Dispose();

        await Task.WhenAll(firstWait, secondWait);
        Assert.Equal(0, tracker.InflightCount);
    }

    [Fact]
    public async Task WaitForZeroAsync_returns_after_timeout_while_inflight_remains()
    {
        var tracker = new InflightTracker();
        using var scope = tracker.Enter();

        await tracker.WaitForZeroAsync(TimeSpan.FromMilliseconds(50), TimeProvider.System, CancellationToken.None);

        Assert.Equal(1, tracker.InflightCount);
    }

    [Fact]
    public async Task WaitForZeroAsync_throws_when_cancellation_is_requested()
    {
        var tracker = new InflightTracker();
        using var scope = tracker.Enter();
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            tracker.WaitForZeroAsync(TimeSpan.FromSeconds(2), TimeProvider.System, cts.Token));

        Assert.Equal(1, tracker.InflightCount);
    }

    [Fact]
    public async Task WaitForZeroAsync_supports_repeated_zero_one_cycles()
    {
        var tracker = new InflightTracker();

        for (var cycle = 0; cycle < 3; cycle++)
        {
            var scope = tracker.Enter();
            var waitTask = tracker.WaitForZeroAsync(TimeSpan.FromSeconds(2), TimeProvider.System, CancellationToken.None);
            Assert.False(waitTask.IsCompleted);

            scope.Dispose();
            await waitTask;
            Assert.Equal(0, tracker.InflightCount);
        }
    }

    [Fact]
    public async Task Enter_and_Dispose_are_thread_safe_under_concurrent_use()
    {
        var tracker = new InflightTracker();
        const int workers = 32;
        const int iterations = 100;

        var tasks = Enumerable.Range(0, workers).Select(async _ =>
        {
            for (var i = 0; i < iterations; i++)
            {
                using var scope = tracker.Enter();
                await Task.Yield();
            }
        });

        await Task.WhenAll(tasks);
        Assert.Equal(0, tracker.InflightCount);

        await tracker.WaitForZeroAsync(TimeSpan.FromSeconds(1), TimeProvider.System, CancellationToken.None);
    }

    [Fact]
    public void InflightScope_Dispose_is_idempotent()
    {
        var tracker = new InflightTracker();
        var scope = tracker.Enter();
        Assert.Equal(1, tracker.InflightCount);

        scope.Dispose();
        Assert.Equal(0, tracker.InflightCount);

        scope.Dispose();
        Assert.Equal(0, tracker.InflightCount);
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
