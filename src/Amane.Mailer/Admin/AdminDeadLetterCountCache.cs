using Amane.Mailer.Data.Sqlite;

namespace Amane.Mailer.Admin;

public sealed class AdminDeadLetterCountCache(TimeProvider timeProvider)
{
    private static readonly TimeSpan CacheDuration = TimeSpan.FromSeconds(60);
    private readonly object _gate = new();
    private int? _cachedCount;
    private DateTimeOffset? _cachedAt;

    public async Task<int> GetCountAsync(
        MailRequestRepository repository,
        CancellationToken cancellationToken = default)
    {
        var now = timeProvider.GetUtcNow();
        lock (_gate)
        {
            if (_cachedCount is not null
                && _cachedAt is not null
                && now - _cachedAt.Value < CacheDuration)
            {
                return _cachedCount.Value;
            }
        }

        var count = await repository.CountDeadLettersForAdminAsync(cancellationToken);

        lock (_gate)
        {
            _cachedCount = count;
            _cachedAt = now;
        }

        return count;
    }

    internal void ClearForTests()
    {
        lock (_gate)
        {
            _cachedCount = null;
            _cachedAt = null;
        }
    }
}
