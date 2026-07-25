namespace Amane.Mailer.Worker;

public sealed class InflightTracker
{
    private readonly object _gate = new();
    private int _inflightCount;
    private TaskCompletionSource _zeroSignal = CreateCompletedSignal();

    public int InflightCount
    {
        get
        {
            lock (_gate)
            {
                return _inflightCount;
            }
        }
    }

    public InflightScope Enter()
    {
        lock (_gate)
        {
            if (_inflightCount++ == 0)
            {
                // 0 → 1: arm a fresh incomplete signal for waiters.
                _zeroSignal = CreatePendingSignal();
            }
        }

        return new InflightScope(this);
    }

    public async Task WaitForZeroAsync(
        TimeSpan timeout,
        TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(timeProvider);

        Task zeroTask;
        lock (_gate)
        {
            if (_inflightCount == 0)
            {
                return;
            }

            zeroTask = _zeroSignal.Task;
        }

        try
        {
            await zeroTask.WaitAsync(timeout, timeProvider, cancellationToken).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            // Match prior polling behavior: return when the drain budget elapses.
        }
    }

    private void Leave()
    {
        TaskCompletionSource? toComplete = null;
        lock (_gate)
        {
            if (_inflightCount <= 0)
            {
                return;
            }

            if (--_inflightCount == 0)
            {
                // 1 → 0: release all current waiters.
                toComplete = _zeroSignal;
            }
        }

        toComplete?.TrySetResult();
    }

    private static TaskCompletionSource CreatePendingSignal() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private static TaskCompletionSource CreateCompletedSignal()
    {
        var signal = CreatePendingSignal();
        signal.SetResult();
        return signal;
    }

    /// <summary>
    /// One in-flight lease. Idempotent only for the same local/variable used with
    /// <c>using</c>; do not copy, store in a field, or pass by value — copies have
    /// independent dispose state and a second Dispose would over-decrement.
    /// </summary>
    public struct InflightScope : IDisposable
    {
        private InflightTracker? _tracker;
        private int _disposed;

        internal InflightScope(InflightTracker tracker)
        {
            _tracker = tracker;
            _disposed = 0;
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
            {
                return;
            }

            var tracker = _tracker;
            _tracker = null;
            tracker?.Leave();
        }
    }
}
