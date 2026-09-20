namespace Amane.Mailer.Data.Sqlite.Models;

public sealed record AdminGoogleIdentityRow(
    long AdminUserId,
    string Issuer,
    string Subject,
    DateTimeOffset CreatedAt);
