namespace Amane.Mailer.Identity;

public sealed record SenderIdentity(
    Guid SenderId,
    string Email,
    string? DisplayName,
    bool Enabled,
    DateTimeOffset CreatedAt,
    DateTimeOffset? DisabledAt);

public sealed record SenderSummary(
    Guid SenderId,
    string Email,
    string? DisplayName,
    bool Enabled,
    DateTimeOffset CreatedAt,
    DateTimeOffset? DisabledAt,
    int ApiKeyCount);

public sealed record ApiKeyMetadata(
    Guid KeyId,
    Guid SenderId,
    string Name,
    DateTimeOffset CreatedAt,
    DateTimeOffset? RevokedAt);

public sealed record AuthenticatedApiKey(
    Guid KeyId,
    SenderIdentity Sender);

public sealed record CreatedApiKey(
    Guid KeyId,
    Guid SenderId,
    string Name,
    string Plaintext,
    DateTimeOffset CreatedAt);
