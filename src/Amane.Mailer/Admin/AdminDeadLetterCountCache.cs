using Amane.Mailer.Data.Sqlite;

namespace Amane.Mailer.Admin;

public sealed class AdminDeadLetterCountCache(TimeProvider timeProvider)
{
    private static readonly TimeSpan CacheDuration = TimeSpan.FromSeconds(60);
    private readonly object _gate = new();
    private readonly Dictionary<string, CacheEntry> _cache = new(StringComparer.Ordinal);

    public async Task<int> GetCountAsync(
        MailRequestRepository repository,
        IReadOnlySet<Guid>? allowedTenantIds = null,
        CancellationToken cancellationToken = default)
    {
        var now = timeProvider.GetUtcNow();
        var cacheKey = BuildCacheKey(allowedTenantIds);
        lock (_gate)
        {
            if (_cache.TryGetValue(cacheKey, out var cached)
                && now - cached.CachedAt < CacheDuration)
            {
                return cached.Count;
            }
        }

        var count = await repository.CountDeadLettersForAdminAsync(allowedTenantIds, cancellationToken);

        lock (_gate)
        {
            _cache[cacheKey] = new CacheEntry(count, now);
        }

        return count;
    }

    internal void ClearForTests()
    {
        lock (_gate)
        {
            _cache.Clear();
        }
    }

    private static string BuildCacheKey(IReadOnlySet<Guid>? allowedTenantIds)
    {
        if (allowedTenantIds is null)
            return "*";

        if (allowedTenantIds.Count == 0)
            return "(none)";

        return string.Join('|', allowedTenantIds.OrderBy(id => id).Select(id => id.ToString("D")));
    }

    private sealed record CacheEntry(int Count, DateTimeOffset CachedAt);
}
