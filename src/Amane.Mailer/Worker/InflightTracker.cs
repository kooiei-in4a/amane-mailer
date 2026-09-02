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

    /// <summary>One in-flight lease. Dispose is idempotent across reference aliases.</summary>
    public sealed class InflightScope : IDisposable
    {
        private InflightTracker? _tracker;

        internal InflightScope(InflightTracker tracker)
        {
            _tracker = tracker;
        }

        public void Dispose() => Interlocked.Exchange(ref _tracker, null)?.Leave();
    }
}
