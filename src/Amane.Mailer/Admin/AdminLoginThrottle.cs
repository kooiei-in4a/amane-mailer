using System.Collections.Concurrent;

namespace Amane.Mailer.Admin;

public sealed class AdminLoginThrottle(TimeProvider timeProvider)
{
    private readonly ConcurrentDictionary<string, LoginFailureState> _failures = new(StringComparer.Ordinal);

    public bool IsLocked(string username, string remoteAddress, MailerAdminOptions options, out TimeSpan retryAfter)
    {
        var key = BuildKey(username, remoteAddress);
        var now = timeProvider.GetUtcNow();
        retryAfter = TimeSpan.Zero;

        if (!_failures.TryGetValue(key, out var state) || state.LockedUntil is null)
            return false;

        if (state.LockedUntil <= now)
        {
            _failures.TryRemove(key, out _);
            return false;
        }

        retryAfter = state.LockedUntil.Value - now;
        return true;
    }

    public bool RecordFailure(
        string username,
        string remoteAddress,
        MailerAdminOptions options,
        out TimeSpan retryAfter)
    {
        var key = BuildKey(username, remoteAddress);
        var now = timeProvider.GetUtcNow();

        var next = _failures.AddOrUpdate(
            key,
            _ => new LoginFailureState(1, null),
            (_, current) =>
            {
                if (current.LockedUntil is not null && current.LockedUntil <= now)
                    return new LoginFailureState(1, null);

                var count = current.Count + 1;
                var lockedUntil = count >= options.LoginFailureLimit
                    ? now + options.LoginCooldown
                    : current.LockedUntil;
                return new LoginFailureState(count, lockedUntil);
            });

        if (next.LockedUntil is null || next.LockedUntil <= now)
        {
            retryAfter = TimeSpan.Zero;
            return false;
        }

        retryAfter = next.LockedUntil.Value - now;
        return true;
    }

    public void Reset(string username, string remoteAddress)
    {
        _failures.TryRemove(BuildKey(username, remoteAddress), out _);
    }

    public void Clear()
    {
        _failures.Clear();
    }

    private static string BuildKey(string username, string remoteAddress) =>
        $"{username.Trim().ToUpperInvariant()}|{remoteAddress}";

    private sealed record LoginFailureState(int Count, DateTimeOffset? LockedUntil);
}
