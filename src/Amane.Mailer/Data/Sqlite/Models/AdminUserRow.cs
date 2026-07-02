namespace Amane.Mailer.Data.Sqlite.Models;

public sealed record AdminUserRow(
    long Id,
    string Username,
    string PasswordHash,
    bool Disabled,
    int CredentialEpoch,
    bool IsBreakGlass);
