namespace Amane.Mailer.Tests.Fixtures;

/// <summary>
/// Lightweight async wake signal for test waits. Each <see cref="Pulse"/> releases
/// current waiters and arms a fresh gate for the next wait.
/// </summary>
internal sealed class AsyncPulse
{
    private readonly object _gate = new();
    private TaskCompletionSource _tcs =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    public void Pulse()
    {
        TaskCompletionSource prior;
        lock (_gate)
        {
            prior = _tcs;
            _tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        }

        prior.TrySetResult();
    }

    public Task WaitAsync(CancellationToken cancellationToken)
    {
        TaskCompletionSource current;
        lock (_gate)
        {
            current = _tcs;
        }

        return current.Task.WaitAsync(cancellationToken);
    }
}
