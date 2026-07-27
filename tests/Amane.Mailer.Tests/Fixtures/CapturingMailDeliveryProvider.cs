using System.Collections.Concurrent;
using System.Threading.Tasks;
using Amane.Mailer.Configuration;
using Amane.Mailer.Delivery;

namespace Amane.Mailer.Tests.Fixtures;

public sealed class CapturingMailDeliveryProvider : IMailDeliveryProvider
{
    private readonly ConcurrentQueue<CapturedMail> _sent = new();
    private readonly ConcurrentQueue<MailDeliveryResult> _results = new();
    private readonly object _holdGate = new();
    private readonly AsyncPulse _activity = new();
    private TaskCompletionSource? _holdCompletion;
    private bool _ignoreHoldCancellation;
    private bool _holdConsumed;
    private TimeSpan? _sendDelay;

    public IReadOnlyCollection<CapturedMail> Sent => _sent.ToArray();

    /// <summary>
    /// Pulsed when a send starts or completes. Used as a wake hint for DB status waits.
    /// </summary>
    internal AsyncPulse Activity => _activity;

    public void Reset()
    {
        while (_sent.TryDequeue(out _))
        {
        }

        while (_results.TryDequeue(out _))
        {
        }

        lock (_holdGate)
        {
            _holdCompletion = null;
            _ignoreHoldCancellation = false;
            _holdConsumed = false;
            _sendDelay = null;
        }

        _activity.Pulse();
    }

    public void HoldNextSend()
    {
        lock (_holdGate)
        {
            _holdCompletion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _ignoreHoldCancellation = false;
            _holdConsumed = false;
        }
    }

    public void HoldNextSendIgnoringCancellation()
    {
        lock (_holdGate)
        {
            _holdCompletion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _ignoreHoldCancellation = true;
            _holdConsumed = false;
        }
    }

    public void ReleaseHeldSend()
    {
        lock (_holdGate)
        {
            _holdCompletion?.TrySetResult();
            _holdCompletion = null;
            _ignoreHoldCancellation = false;
            _holdConsumed = false;
        }

        _activity.Pulse();
    }

    /// <summary>
    /// Waits until a held <see cref="SendAsync"/> has entered the hold gate.
    /// Use before host shutdown in tests that rely on an in-flight held send.
    /// </summary>
    public async Task WaitUntilHoldConsumedAsync(CancellationToken cancellationToken)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(15);
        while (DateTimeOffset.UtcNow < deadline)
        {
            lock (_holdGate)
            {
                if (_holdConsumed)
                {
                    return;
                }
            }

            using var delayCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            delayCts.CancelAfter(TimeSpan.FromMilliseconds(50));
            try
            {
                await _activity.WaitAsync(delayCts.Token);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                // Poll interval elapsed; re-check _holdConsumed.
            }
        }

        throw new TimeoutException("Timed out waiting for the delivery provider hold to be consumed.");
    }

    public void SetSendDelay(TimeSpan delay)
    {
        lock (_holdGate)
        {
            _sendDelay = delay;
        }
    }

    public void QueueResult(MailDeliveryResult result)
    {
        _results.Enqueue(result);
    }

    public async Task<MailDeliveryResult> SendAsync(
        MailSendJob job,
        MailerTenant tenant,
        string provider,
        CancellationToken cancellationToken)
    {
        TaskCompletionSource? hold;
        bool ignoreHoldCancellation;
        TimeSpan? delay;
        lock (_holdGate)
        {
            hold = _holdConsumed ? null : _holdCompletion;
            ignoreHoldCancellation = _ignoreHoldCancellation;
            if (hold is not null)
            {
                _holdConsumed = true;
            }

            delay = _sendDelay;
        }

        _activity.Pulse();

        try
        {
            if (hold is not null)
            {
                if (ignoreHoldCancellation)
                {
                    await hold.Task;
                }
                else
                {
                    await hold.Task.WaitAsync(cancellationToken);
                }
            }

            if (delay is not null)
            {
                await Task.Delay(delay.Value, cancellationToken);
            }

            _sent.Enqueue(new CapturedMail(job.MailRequestId, job.RecipientEmail, job.Subject, provider));
            return _results.TryDequeue(out var result)
                ? result
                : MailDeliveryResult.Success($"stub-{job.MailRequestId:N}");
        }
        finally
        {
            _activity.Pulse();
        }
    }
}

public sealed record CapturedMail(
    Guid MailRequestId,
    string To,
    string Subject,
    string Provider);
