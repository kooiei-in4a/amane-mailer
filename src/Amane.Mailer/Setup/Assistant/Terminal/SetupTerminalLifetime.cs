namespace Amane.Mailer.Setup.Assistant.Terminal;

/// <summary>
/// Idle and absolute session deadlines for the terminal wizard, mirroring
/// <see cref="SetupAssistantOptions"/> used by the Web Assistant host.
/// </summary>
internal sealed class SetupTerminalLifetime : IDisposable
{
    private readonly SetupAssistantOptions _options;
    private readonly TimeProvider _timeProvider;
    private readonly CancellationTokenSource _stopRequested = new();
    private readonly Lock _gate = new();

    private DateTimeOffset _createdAt;
    private DateTimeOffset _lastActivity;
    private int _inFlight;
    private bool _disposed;
    private SetupAssistantShutdownReason _stopReason = SetupAssistantShutdownReason.None;

    internal SetupTerminalLifetime(
        SetupAssistantOptions? options = null,
        TimeProvider? timeProvider = null)
    {
        _options = options ?? new SetupAssistantOptions();
        _timeProvider = timeProvider ?? TimeProvider.System;
        var now = _timeProvider.GetUtcNow();
        _createdAt = now;
        _lastActivity = now;
    }

    internal CancellationToken Token => _stopRequested.Token;

    internal SetupAssistantShutdownReason StopReason => _stopReason;

    internal void Touch()
    {
        lock (_gate)
        {
            _lastActivity = _timeProvider.GetUtcNow();
        }
    }

    internal void EnsureNotExpired()
    {
        var reason = ClassifyExpiry(_timeProvider.GetUtcNow());
        if (reason != SetupAssistantShutdownReason.None)
        {
            RequestStop(reason);
            throw new OperationCanceledException(Token);
        }
    }

    internal void BeginOperation()
    {
        lock (_gate)
        {
            _inFlight++;
        }
    }

    internal void EndOperation()
    {
        lock (_gate)
        {
            _inFlight--;
            if (_inFlight == 0)
            {
                _lastActivity = _timeProvider.GetUtcNow();
            }
        }
    }

    internal void RequestStop(SetupAssistantShutdownReason reason)
    {
        lock (_gate)
        {
            if (_disposed || _stopReason != SetupAssistantShutdownReason.None)
            {
                return;
            }

            _stopReason = reason;
        }

        _stopRequested.Cancel();
    }

    internal string DescribeStopReason() => StopReason switch
    {
        SetupAssistantShutdownReason.IdleTimeout =>
            "뿯撽作がないまま時뿯붿が経붿したた뿯ソ、session 뿯ソ破뿯梽します。",
        SetupAssistantShutdownReason.AbsoluteTimeout =>
            "session の上붿時뿯붿に붿したた뿯ソ、session 뿯ソ破뿯梽します。",
        SetupAssistantShutdownReason.Cancelled => "Assistant 뿯ソ中止しました。",
        _ => "Assistant 뿯ソ終뿯亽します。",
    };

    private SetupAssistantShutdownReason ClassifyExpiry(DateTimeOffset now)
    {
        lock (_gate)
        {
            if (now - _createdAt >= _options.AbsoluteLifetime)
            {
                return SetupAssistantShutdownReason.AbsoluteTimeout;
            }

            if (_inFlight > 0)
            {
                return SetupAssistantShutdownReason.None;
            }

            return now - _lastActivity >= _options.IdleTimeout
                ? SetupAssistantShutdownReason.IdleTimeout
                : SetupAssistantShutdownReason.None;
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
        }

        _stopRequested.Dispose();
    }
}
