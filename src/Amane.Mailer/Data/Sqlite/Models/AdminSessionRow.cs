namespace Amane.Mailer.Data.Sqlite.Models;

public sealed record AdminSessionRow(
    string SessionId,
    string Actor,
    DateTimeOffset IssuedAt,
    DateTimeOffset LastSeenAt,
    DateTimeOffset AbsoluteExpiresAt,
    DateTimeOffset IdleExpiresAt,
    DateTimeOffset? RevokedAt,
    string? RevokeReason,
    int CredentialEpoch);
