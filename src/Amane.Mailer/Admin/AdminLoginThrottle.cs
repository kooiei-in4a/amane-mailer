using System.Collections.Concurrent;
using Amane.Mailer.Data.Sqlite;
using Amane.Mailer.Data.Sqlite.Models;

namespace Amane.Mailer.Admin;

public sealed class AdminLoginThrottle(
    TimeProvider timeProvider,
    AdminLoginThrottleRepository repository,
    MailerAdminOptions options)
{
    private readonly AdminNetworkIdentifierHasher? _networkIdentifierHasher =
        options.HashNetworkIdentifiers && options.AuditIdentifierHashKey is not null
            ? new AdminNetworkIdentifierHasher(options.AuditIdentifierHashKey)
            : null;
    private readonly ConcurrentDictionary<string, LoginFailureState> _cache = new(StringComparer.Ordinal);

    public void Clear() => _cache.Clear();

    public async Task<(bool IsLocked, TimeSpan RetryAfter)> IsLockedWithRetryAfterAsync(
        string username,
        string remoteAddress,
        CancellationToken cancellationToken)
    {
        var key = BuildKey(username, remoteAddress);
        var now = timeProvider.GetUtcNow();
        var state = await GetStateAsync(key, now, cancellationToken);

        if (state.LockedUntil is null || state.LockedUntil <= now)
            return (false, TimeSpan.Zero);

        return (true, state.LockedUntil.Value - now);
    }

    public async Task<(bool IsLocked, TimeSpan RetryAfter, bool LockCreated)> RecordFailureAsync(
        string username,
        string remoteAddress,
        CancellationToken cancellationToken)
    {
        var key = BuildKey(username, remoteAddress);
        var now = timeProvider.GetUtcNow();
        var current = await GetStateAsync(key, now, cancellationToken);

        var count = current.LockedUntil is not null && current.LockedUntil <= now
            ? 1
            : current.Count + 1;
        var wasLocked = current.LockedUntil is not null && current.LockedUntil > now;
        var lockCreated = false;
        DateTimeOffset? lockedUntil = current.LockedUntil;

        if (count >= options.LoginFailureLimit)
        {
            if (!wasLocked)
            {
                lockCreated = true;
            }

            lockedUntil = now + options.LoginCooldown;
        }
        else if (current.LockedUntil is not null && current.LockedUntil <= now)
        {
            lockedUntil = null;
        }

        var next = new LoginFailureState(count, lockedUntil);
        _cache[key] = next;
        await repository.UpsertAsync(
            new AdminLoginThrottleRow(key, count, lockedUntil, now),
            cancellationToken);

        if (lockedUntil is null || lockedUntil <= now)
            return (false, TimeSpan.Zero, lockCreated);

        return (true, lockedUntil.Value - now, lockCreated);
    }

    public async Task ResetAsync(
        string username,
        string remoteAddress,
        CancellationToken cancellationToken)
    {
        var key = BuildKey(username, remoteAddress);
        _cache.TryRemove(key, out _);
        await repository.DeleteAsync(key, cancellationToken);
    }

    private async Task<LoginFailureState> GetStateAsync(
        string key,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        if (_cache.TryGetValue(key, out var cached))
        {
            if (cached.LockedUntil is null || cached.LockedUntil > now)
                return cached;

            _cache.TryRemove(key, out _);
        }

        var row = await repository.GetAsync(key, cancellationToken);
        if (row is null)
            return new LoginFailureState(0, null);

        if (row.LockedUntil is not null && row.LockedUntil <= now)
        {
            await repository.DeleteAsync(key, cancellationToken);
            return new LoginFailureState(0, null);
        }

        var state = new LoginFailureState(row.FailureCount, row.LockedUntil);
        _cache[key] = state;
        return state;
    }

    internal string BuildKey(string username, string remoteAddress)
    {
        var sourceIdentifier = ResolveSourceIdentifier(remoteAddress);
        return $"{username.Trim().ToUpperInvariant()}|{sourceIdentifier}";
    }

    private string ResolveSourceIdentifier(string remoteAddress)
    {
        if (_networkIdentifierHasher is null)
            return remoteAddress;

        return _networkIdentifierHasher.HashIdentifier(remoteAddress);
    }

    private sealed record LoginFailureState(int Count, DateTimeOffset? LockedUntil);
}
