namespace Amane.Mailer.Worker;

public sealed class MailDeliveryInflightTracker
{
    private int _inflightCount;

    public int InflightCount => Volatile.Read(ref _inflightCount);

    public InflightScope Enter()
    {
        Interlocked.Increment(ref _inflightCount);
        return new InflightScope(this);
    }

    public async Task WaitForZeroAsync(TimeSpan timeout, CancellationToken cancellationToken)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;

        while (Volatile.Read(ref _inflightCount) > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (DateTimeOffset.UtcNow >= deadline)
            {
                return;
            }

            await Task.Delay(50, cancellationToken);
        }
    }

    public readonly struct InflightScope(MailDeliveryInflightTracker tracker) : IDisposable
    {
        public void Dispose() => Interlocked.Decrement(ref tracker._inflightCount);
    }
}
