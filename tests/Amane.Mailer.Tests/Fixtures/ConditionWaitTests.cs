namespace Amane.Mailer.Tests.Fixtures;

public sealed class ConditionWaitTests
{
    [Fact]
    public async Task UntilAsync_returns_immediately_when_already_satisfied()
    {
        var ct = TestContext.Current.CancellationToken;
        var probes = 0;

        await ConditionWait.UntilAsync(
            _ =>
            {
                probes++;
                return Task.FromResult(true);
            },
            TimeSpan.FromSeconds(1),
            ct);

        Assert.Equal(1, probes);
    }

    [Fact]
    public async Task UntilAsync_wakes_on_pulse_without_waiting_full_fallback()
    {
        var ct = TestContext.Current.CancellationToken;
        var pulse = new AsyncPulse();
        var ready = 0;

        var waitTask = ConditionWait.UntilAsync(
            _ => Task.FromResult(Volatile.Read(ref ready) == 1),
            TimeSpan.FromSeconds(5),
            ct,
            wake: pulse,
            fallbackDelay: TimeSpan.FromSeconds(2));

        await Task.Delay(30, ct);
        Volatile.Write(ref ready, 1);
        pulse.Pulse();

        var sw = System.Diagnostics.Stopwatch.StartNew();
        await waitTask;
        sw.Stop();

        Assert.True(
            sw.Elapsed < TimeSpan.FromMilliseconds(500),
            $"Expected pulse wake; elapsed {sw.Elapsed.TotalMilliseconds:0}ms.");
    }

    [Fact]
    public async Task UntilAsync_times_out_when_condition_never_holds()
    {
        var ct = TestContext.Current.CancellationToken;

        var ex = await Assert.ThrowsAsync<TimeoutException>(() =>
            ConditionWait.UntilAsync(
                _ => Task.FromResult(false),
                TimeSpan.FromMilliseconds(80),
                ct,
                fallbackDelay: TimeSpan.FromMilliseconds(20)));

        Assert.Contains("Condition was not met", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task UntilAsync_class_probe_returns_matched_value()
    {
        var ct = TestContext.Current.CancellationToken;

        var value = await ConditionWait.UntilAsync(
            _ => Task.FromResult<string?>("ok"),
            static v => v == "ok",
            TimeSpan.FromSeconds(1),
            ct);

        Assert.Equal("ok", value);
    }

    [Fact]
    public async Task AsyncPulse_releases_current_waiters_and_arms_next_gate()
    {
        var ct = TestContext.Current.CancellationToken;
        var pulse = new AsyncPulse();

        var first = pulse.WaitAsync(ct);
        Assert.False(first.IsCompleted);

        pulse.Pulse();
        await first.WaitAsync(ct);

        var second = pulse.WaitAsync(ct);
        Assert.False(second.IsCompleted);
        pulse.Pulse();
        await second.WaitAsync(ct);
    }
}
