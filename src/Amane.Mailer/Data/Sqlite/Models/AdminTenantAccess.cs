namespace Amane.Mailer.Data.Sqlite.Models;

public sealed record AdminTenantAccess(
    string Username,
    bool IsBreakGlass,
    IReadOnlySet<Guid> TenantIds)
{
    public IReadOnlySet<Guid>? AllowedTenantIdsForQuery =>
        IsBreakGlass ? null : TenantIds;

    public bool CanAccessTenant(Guid tenantId) =>
        IsBreakGlass || TenantIds.Contains(tenantId);

    public bool HasAllTenantScopes(IReadOnlyCollection<Guid> tenantIds)
    {
        if (IsBreakGlass)
            return true;

        foreach (var tenantId in tenantIds)
        {
            if (!TenantIds.Contains(tenantId))
                return false;
        }

        return true;
    }
}
