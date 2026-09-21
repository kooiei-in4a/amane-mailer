namespace Amane.Mailer.Data.Sqlite.Models;

/// <summary>
/// Secret-free admin user projection for Admin UI listing.
/// Never includes password hash, Google subject, or Google email.
/// </summary>
public sealed record AdminUserSummary(
    long Id,
    string Username,
    bool Disabled,
    bool IsBreakGlass,
    bool IsInstanceOwner,
    bool GoogleLinked,
    IReadOnlyList<Guid> TenantScopes);
