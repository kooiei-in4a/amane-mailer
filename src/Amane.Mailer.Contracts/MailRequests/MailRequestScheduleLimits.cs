namespace Amane.Mailer.Contracts.MailRequests;

/// <summary>
/// Limits for scheduled send. API times are interpreted as UTC.
/// </summary>
public static class MailRequestScheduleLimits
{
    /// <summary>
    /// Maximum how far ahead <c>scheduled_at</c> may be from acceptance / reschedule time.
    /// </summary>
    public static readonly TimeSpan MaxScheduledAhead = TimeSpan.FromDays(30);
}
