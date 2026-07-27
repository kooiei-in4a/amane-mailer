namespace Amane.Mailer.Data.Sqlite.Models;

/// <summary>
/// Result of an atomic admin session touch. Returned only when the row was updated.
/// </summary>
public sealed record AdminSessionTouchResult(
    DateTimeOffset LastSeenAt,
    DateTimeOffset IdleExpiresAt,
    DateTimeOffset AbsoluteExpiresAt);
