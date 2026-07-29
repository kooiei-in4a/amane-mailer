using System.Collections.Concurrent;

namespace Amane.Mailer.Operations.AcsSetup;

/// <summary>
/// In-process Assistant session limit for Staging verification test sends.
/// Direct CLI adapters do not supply a session id and are therefore not limited (#451 non-goal).
/// Counts live only in session memory; never persisted.
/// </summary>
public sealed class AcsSessionTestSendLimiter
{
    public const int DefaultMaxAttemptsPerSession = 5;

    private readonly ConcurrentDictionary<string, int> _counts = new(StringComparer.Ordinal);
    private readonly int _maxAttempts;

    public AcsSessionTestSendLimiter(int maxAttemptsPerSession = DefaultMaxAttemptsPerSession)
    {
        if (maxAttemptsPerSession < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(maxAttemptsPerSession));
        }

        _maxAttempts = maxAttemptsPerSession;
    }

    /// <summary>
    /// Attempts to consume one Staging verification slot for the session.
    /// Returns false when the session has already reached the limit.
    /// </summary>
    public bool TryAcquire(string assistantSessionId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(assistantSessionId);

        while (true)
        {
            var current = _counts.GetOrAdd(assistantSessionId, 0);
            if (current >= _maxAttempts)
            {
                return false;
            }

            if (_counts.TryUpdate(assistantSessionId, current + 1, current))
            {
                return true;
            }
        }
    }

    public int GetAttemptCount(string assistantSessionId) =>
        _counts.TryGetValue(assistantSessionId, out var count) ? count : 0;
}
