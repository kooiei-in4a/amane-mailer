namespace Amane.Mailer.Tests;

/// <summary>
/// Controllable <see cref="TimeProvider"/> for tests that need to advance virtual time while a
/// SQLite write lock is held (contention-window fencing), without waiting on wall clock.
/// </summary>
internal sealed class ControllableTimeProvider(DateTimeOffset utcNow) : TimeProvider
{
    private DateTimeOffset _utcNow = utcNow;

    public override DateTimeOffset GetUtcNow() => _utcNow;

    public void SetUtcNow(DateTimeOffset value) => _utcNow = value;

    public void Advance(TimeSpan by) => _utcNow = _utcNow.Add(by);
}
