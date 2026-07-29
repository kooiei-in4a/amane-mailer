using System.Collections.Concurrent;

namespace Amane.Mailer.Operations.AcsSetup;

/// <summary>
/// Process-shared Assistant session limit for Staging verification test sends.
/// Counts are memory-only and expire to keep the session inventory bounded.
/// Direct CLI adapters omit a session id and are not limited (#451 non-goal).
/// </summary>
public sealed class AcsSessionTestSendLimiter
{
    public const int DefaultMaxAttemptsPerSession = 5;
    public static readonly TimeSpan DefaultSessionLifetime = TimeSpan.FromHours(8);
    public const int DefaultMaxTrackedSessions = 4096;

    public static AcsSessionTestSendLimiter Shared { get; } = new();

    private readonly ConcurrentDictionary<string, SessionCounter> _counts =
        new(StringComparer.Ordinal);
    private readonly int _maxAttempts;
    private readonly TimeSpan _sessionLifetime;
    private readonly int _maxTrackedSessions;
    private readonly TimeProvider _timeProvider;

    public AcsSessionTestSendLimiter(
        int maxAttemptsPerSession = DefaultMaxAttemptsPerSession,
        TimeSpan? sessionLifetime = null,
        int maxTrackedSessions = DefaultMaxTrackedSessions,
        TimeProvider? timeProvider = null)
    {
        if (maxAttemptsPerSession < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(maxAttemptsPerSession));
        }

        if (sessionLifetime is { } lifetime && lifetime <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(sessionLifetime));
        }

        if (maxTrackedSessions < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(maxTrackedSessions));
        }

        _maxAttempts = maxAttemptsPerSession;
        _sessionLifetime = sessionLifetime ?? DefaultSessionLifetime;
        _maxTrackedSessions = maxTrackedSessions;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public bool TryAcquire(string assistantSessionId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(assistantSessionId);
        var now = _timeProvider.GetUtcNow();
        Cleanup(now);

        while (true)
        {
            if (_counts.TryGetValue(assistantSessionId, out var existing))
            {
                if (existing.ExpiresAt <= now)
                {
                    _counts.TryRemove(
                        new KeyValuePair<string, SessionCounter>(assistantSessionId, existing));
                    continue;
                }

                if (existing.Count >= _maxAttempts)
                {
                    return false;
                }

                var updated = existing with { Count = existing.Count + 1 };
                if (_counts.TryUpdate(assistantSessionId, updated, existing))
                {
                    return true;
                }

                continue;
            }

            if (_counts.Count >= _maxTrackedSessions)
            {
                return false;
            }

            if (_counts.TryAdd(
                    assistantSessionId,
                    new SessionCounter(1, now + _sessionLifetime)))
            {
                return true;
            }
        }
    }

    public int GetAttemptCount(string assistantSessionId) =>
        _counts.TryGetValue(assistantSessionId, out var counter)
        && counter.ExpiresAt > _timeProvider.GetUtcNow()
            ? counter.Count
            : 0;

    private void Cleanup(DateTimeOffset now)
    {
        foreach (var entry in _counts)
        {
            if (entry.Value.ExpiresAt <= now)
            {
                _counts.TryRemove(entry);
            }
        }
    }

    private sealed record SessionCounter(int Count, DateTimeOffset ExpiresAt);
}
