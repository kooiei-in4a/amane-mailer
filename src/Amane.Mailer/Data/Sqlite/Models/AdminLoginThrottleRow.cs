namespace Amane.Mailer.Data.Sqlite.Models;

public sealed record AdminLoginThrottleRow(
    string ThrottleKey,
    int FailureCount,
    DateTimeOffset? LockedUntil,
    DateTimeOffset UpdatedAt);
