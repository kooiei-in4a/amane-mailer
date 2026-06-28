using System.Collections.Concurrent;

namespace Amane.Mailer.Admin;

/// <summary>
/// Prevents duplicate <c>auth.session_expired</c> audit rows for the same session (ADR 0014 D-04).
/// </summary>
public sealed class AdminSessionExpiredDedupe(TimeProvider timeProvider)
{
    private static readonly TimeSpan DedupeWindow = TimeSpan.FromMinutes(5);
    private readonly ConcurrentDictionary<string, DateTimeOffset> _recent = new(StringComparer.Ordinal);

    public bool ShouldRecord(string sessionId)
    {
        var now = timeProvider.GetUtcNow();
        PruneExpired(now);

        if (_recent.TryGetValue(sessionId, out var recordedAt) && now - recordedAt < DedupeWindow)
            return false;

        _recent[sessionId] = now;
        return true;
    }

    public void Clear() => _recent.Clear();

    private void PruneExpired(DateTimeOffset now)
    {
        foreach (var entry in _recent)
        {
            if (now - entry.Value >= DedupeWindow)
            {
                _recent.TryRemove(entry.Key, out _);
            }
        }
    }
}
