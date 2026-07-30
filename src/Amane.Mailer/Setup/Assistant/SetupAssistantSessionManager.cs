using System.Security.Cryptography;
using System.Text;

namespace Amane.Mailer.Setup.Assistant;

internal enum SetupAssistantTokenExchange
{
    Redeemed = 0,
    InvalidToken = 1,
    AlreadyRedeemed = 2,
    TokenExpired = 3,
    SessionAlreadyActive = 4,
}

internal enum SetupAssistantShutdownReason
{
    None = 0,
    Completed = 1,
    Cancelled = 2,
    IdleTimeout = 3,
    AbsoluteTimeout = 4,
    UnclaimedTokenExpired = 5,
    OperationFault = 6,
}

/// <summary>
/// Owns the single one-time token, the single assistant session, and the process stop signal.
/// Only one session may exist for the lifetime of the host; the token cannot be replayed; and
/// completion, cancellation, or any timeout terminates the local server and clears session memory.
/// </summary>
internal sealed class SetupAssistantSessionManager : IDisposable
{
    private readonly SetupAssistantOptions _options;
    private readonly TimeProvider _timeProvider;
    private readonly CancellationTokenSource _shutdown = new();
    private readonly Lock _gate = new();
    private readonly byte[] _oneTimeToken;
    private readonly DateTimeOffset _tokenIssuedAt;

    private readonly SemaphoreSlim _stepGate = new(1, 1);

    private SetupAssistantSession? _session;
    private bool _tokenRedeemed;
    private bool _disposed;
    private int _inFlight;
    private bool _disposeWhenIdle;

    internal SetupAssistantSessionManager(
        SetupAssistantOptions options,
        TimeProvider? timeProvider = null)
    {
        _options = options;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _oneTimeToken = RandomNumberGenerator.GetBytes(32);
        _tokenIssuedAt = _timeProvider.GetUtcNow();
    }

    /// <summary>
    /// The token the operator copies from the terminal. It is printed to stdout only and is never
    /// placed in a URL, query string, cookie, log record, or persisted artifact.
    /// </summary>
    internal string OneTimeTokenText => Base64Url(_oneTimeToken);

    internal CancellationToken ShutdownToken => _shutdown.Token;

    internal SetupAssistantShutdownReason ShutdownReason { get; private set; }

    internal SetupAssistantTokenExchange TryRedeem(
        string? presentedToken,
        out SetupAssistantSession? session)
    {
        session = null;
        var now = _timeProvider.GetUtcNow();

        lock (_gate)
        {
            if (!MatchesOneTimeToken(presentedToken))
            {
                return SetupAssistantTokenExchange.InvalidToken;
            }

            if (_tokenRedeemed)
            {
                return SetupAssistantTokenExchange.AlreadyRedeemed;
            }

            if (now - _tokenIssuedAt >= _options.OneTimeTokenLifetime)
            {
                return SetupAssistantTokenExchange.TokenExpired;
            }

            if (_session is not null)
            {
                return SetupAssistantTokenExchange.SessionAlreadyActive;
            }

            _tokenRedeemed = true;
            _session = new SetupAssistantSession(
                Base64Url(RandomNumberGenerator.GetBytes(32)),
                Base64Url(RandomNumberGenerator.GetBytes(32)),
                now);
            session = _session;
            return SetupAssistantTokenExchange.Redeemed;
        }
    }

    /// <summary>
    /// Resolves the session for a request. An unknown, foreign, or expired session identifier
    /// never yields a session, and an expired session immediately stops the host.
    /// </summary>
    internal SetupAssistantSession? TryResolve(string? presentedSessionId)
    {
        var now = _timeProvider.GetUtcNow();
        SetupAssistantShutdownReason expiry;

        lock (_gate)
        {
            if (_session is null || string.IsNullOrEmpty(presentedSessionId))
            {
                return null;
            }

            if (!FixedTimeTextEquals(_session.SessionId, presentedSessionId))
            {
                return null;
            }

            expiry = ClassifyExpiry(_session, now);
            if (expiry == SetupAssistantShutdownReason.None)
            {
                _session.Touch(now);
                return _session;
            }
        }

        Stop(expiry);
        return null;
    }

    /// <summary>
    /// Takes the single step slot and marks the session busy until <see cref="EndStep"/> runs. Two
    /// requests can therefore never mutate the session at the same time, and a typed operation that
    /// takes minutes is never mistaken for an idle operator.
    /// </summary>
    internal async Task<SetupAssistantSession?> TryBeginStepAsync(
        string? presentedSessionId,
        CancellationToken cancellationToken)
    {
        await _stepGate.WaitAsync(cancellationToken).ConfigureAwait(false);

        var session = TryResolve(presentedSessionId);
        if (session is null)
        {
            _stepGate.Release();
            return null;
        }

        lock (_gate)
        {
            if (_disposed || _shutdown.IsCancellationRequested)
            {
                _stepGate.Release();
                return null;
            }

            _inFlight++;
        }

        return session;
    }

    /// <summary>
    /// Releases the step slot, restarts the idle clock, and performs any session disposal that was
    /// deferred because an operation was still running.
    /// </summary>
    internal void EndStep()
    {
        SetupAssistantSession? deferred = null;

        lock (_gate)
        {
            _inFlight--;
            if (_inFlight > 0)
            {
                // Defensive: the gate admits one step at a time.
            }
            else if (_disposeWhenIdle)
            {
                deferred = _session;
                _session = null;
                _disposeWhenIdle = false;
            }
            else
            {
                _session?.Touch(_timeProvider.GetUtcNow());
            }
        }

        deferred?.Dispose();
        _stepGate.Release();
    }

    /// <summary>
    /// Enforces idle, absolute, and unclaimed-token deadlines without requiring a request. The
    /// host calls this on a timer so an abandoned browser still terminates the local server.
    /// </summary>
    internal void EvaluateDeadlines()
    {
        var now = _timeProvider.GetUtcNow();
        SetupAssistantShutdownReason reason;

        lock (_gate)
        {
            if (_disposed || _shutdown.IsCancellationRequested)
            {
                return;
            }

            if (_session is null)
            {
                reason = !_tokenRedeemed && now - _tokenIssuedAt >= _options.OneTimeTokenLifetime
                    ? SetupAssistantShutdownReason.UnclaimedTokenExpired
                    : SetupAssistantShutdownReason.None;
            }
            else
            {
                reason = ClassifyExpiry(_session, now);
            }
        }

        if (reason != SetupAssistantShutdownReason.None)
        {
            Stop(reason);
        }
    }

    internal void Stop(SetupAssistantShutdownReason reason)
    {
        SetupAssistantSession? expired = null;

        lock (_gate)
        {
            if (_shutdown.IsCancellationRequested)
            {
                return;
            }

            ShutdownReason = reason;

            // A running typed operation still holds this session. Cancellation is signalled now,
            // but the secrets are not cleared underneath the operation; disposal waits for it.
            if (_inFlight > 0)
            {
                _disposeWhenIdle = true;
            }
            else
            {
                expired = _session;
                _session = null;
            }
        }

        expired?.Dispose();
        _shutdown.Cancel();
    }

    private SetupAssistantShutdownReason ClassifyExpiry(
        SetupAssistantSession session,
        DateTimeOffset now)
    {
        if (now - session.CreatedAt >= _options.AbsoluteLifetime)
        {
            return SetupAssistantShutdownReason.AbsoluteTimeout;
        }

        // A step that is still running is not an idle browser, so the idle clock only applies
        // between requests.
        if (_inFlight > 0)
        {
            return SetupAssistantShutdownReason.None;
        }

        return now - session.LastSeenAt >= _options.IdleTimeout
            ? SetupAssistantShutdownReason.IdleTimeout
            : SetupAssistantShutdownReason.None;
    }

    private bool MatchesOneTimeToken(string? presentedToken)
    {
        if (string.IsNullOrEmpty(presentedToken))
        {
            return false;
        }

        Span<byte> presented = stackalloc byte[48];
        if (!TryDecodeBase64Url(presentedToken, presented, out var written)
            || written != _oneTimeToken.Length)
        {
            return false;
        }

        return CryptographicOperations.FixedTimeEquals(_oneTimeToken, presented[..written]);
    }

    internal static bool FixedTimeTextEquals(string expected, string presented) =>
        CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(expected),
            Encoding.UTF8.GetBytes(presented));

    private static string Base64Url(byte[] value) =>
        Convert.ToBase64String(value).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static bool TryDecodeBase64Url(string value, Span<byte> destination, out int written)
    {
        written = 0;
        if (value.Length > 64)
        {
            return false;
        }

        var padded = value.Replace('-', '+').Replace('_', '/');
        padded = (padded.Length % 4) switch
        {
            2 => padded + "==",
            3 => padded + "=",
            0 => padded,
            _ => string.Empty,
        };

        return padded.Length != 0 && Convert.TryFromBase64String(padded, destination, out written);
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
            _session?.Dispose();
            _session = null;
        }

        _shutdown.Dispose();
        _stepGate.Dispose();
        _oneTimeToken.AsSpan().Clear();
    }
}
