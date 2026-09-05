namespace Amane.Mailer.Data.Sqlite.Models;

public sealed record AdminTenantAccess(
    string Username,
    bool IsBreakGlass,
    IReadOnlySet<Guid> TenantIds,
    bool IsInstanceOwner = false)
{
    public IReadOnlySet<Guid>? AllowedTenantIdsForQuery =>
        IsBreakGlass || IsInstanceOwner ? null : TenantIds;

    public bool CanAccessTenant(Guid tenantId) =>
        IsBreakGlass || IsInstanceOwner || TenantIds.Contains(tenantId);

    public bool HasAllTenantScopes(IReadOnlyCollection<Guid> tenantIds)
    {
        if (IsBreakGlass || IsInstanceOwner)
            return true;

        foreach (var tenantId in tenantIds)
        {
            if (!TenantIds.Contains(tenantId))
                return false;
        }

        return true;
    }
}
