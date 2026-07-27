namespace Amane.Mailer.Admin;

/// <summary>
/// Internal Admin session touch interval policy (#391).
/// Not a public configuration surface; derived from <see cref="MailerAdminOptions.SessionIdleTimeout"/>.
/// </summary>
internal static class AdminSessionTouch
{
    private static readonly TimeSpan MaxTouchInterval = TimeSpan.FromMinutes(1);

    /// <summary>
    /// Returns <c>min(1 minute, SessionIdleTimeout / 4)</c> so short idle timeouts
    /// still touch before the idle window elapses.
    /// </summary>
    internal static TimeSpan ResolveInterval(TimeSpan sessionIdleTimeout)
    {
        if (sessionIdleTimeout <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(sessionIdleTimeout));

        var quarter = TimeSpan.FromTicks(sessionIdleTimeout.Ticks / 4);
        return quarter < MaxTouchInterval ? quarter : MaxTouchInterval;
    }
}
