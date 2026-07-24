namespace Amane.Mailer.Tests.Fixtures;

/// <summary>
/// Condition waiter that prefers event/TCS wake hints over fixed wall-clock spin.
/// A short fallback delay remains only when no wake fires (e.g. lease-reaper paths).
/// </summary>
internal static class ConditionWait
{
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(15);
    public static readonly TimeSpan DefaultFallbackDelay = TimeSpan.FromMilliseconds(20);

    public static async Task UntilAsync(
        Func<CancellationToken, Task<bool>> isSatisfied,
        TimeSpan timeout,
        CancellationToken cancellationToken,
        AsyncPulse? wake = null,
        TimeSpan? fallbackDelay = null)
    {
        ArgumentNullException.ThrowIfNull(isSatisfied);

        await UntilCoreAsync(
            async ct => await isSatisfied(ct).ConfigureAwait(false),
            timeout,
            cancellationToken,
            wake,
            fallbackDelay,
            timeoutMessage: null).ConfigureAwait(false);
    }

    public static async Task<T> UntilAsync<T>(
        Func<CancellationToken, Task<T?>> probe,
        Func<T, bool> isSatisfied,
        TimeSpan timeout,
        CancellationToken cancellationToken,
        AsyncPulse? wake = null,
        TimeSpan? fallbackDelay = null)
        where T : class
    {
        ArgumentNullException.ThrowIfNull(probe);
        ArgumentNullException.ThrowIfNull(isSatisfied);

        T? lastValue = null;
        await UntilCoreAsync(
            async ct =>
            {
                lastValue = await probe(ct).ConfigureAwait(false);
                return lastValue is not null && isSatisfied(lastValue);
            },
            timeout,
            cancellationToken,
            wake,
            fallbackDelay,
            () => lastValue is null
                ? $"Condition was not met within {timeout.TotalSeconds:0.###}s (no value observed)."
                : $"Condition was not met within {timeout.TotalSeconds:0.###}s. Last value: {lastValue}.").ConfigureAwait(false);

        return lastValue!;
    }

    private static async Task UntilCoreAsync(
        Func<CancellationToken, Task<bool>> isSatisfied,
        TimeSpan timeout,
        CancellationToken cancellationToken,
        AsyncPulse? wake,
        TimeSpan? fallbackDelay,
        Func<string>? timeoutMessage)
    {
        var delay = fallbackDelay ?? DefaultFallbackDelay;
        if (delay < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(fallbackDelay));
        }

        var deadline = DateTimeOffset.UtcNow + timeout;

        while (DateTimeOffset.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (await isSatisfied(cancellationToken).ConfigureAwait(false))
            {
                return;
            }

            var remaining = deadline - DateTimeOffset.UtcNow;
            if (remaining <= TimeSpan.Zero)
            {
                break;
            }

            if (wake is null)
            {
                var wait = remaining < delay ? remaining : delay;
                await Task.Delay(wait, cancellationToken).ConfigureAwait(false);
                continue;
            }

            // Wake on provider/handler activity; settle briefly if the DB write lags the signal.
            var settle = remaining < delay ? remaining : delay;
            using var settleCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            settleCts.CancelAfter(settle);
            try
            {
                await wake.WaitAsync(settleCts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                // Settle timeout — re-probe.
            }
        }

        throw new TimeoutException(
            timeoutMessage?.Invoke()
            ?? $"Condition was not met within {timeout.TotalSeconds:0.###}s.");
    }
}
