namespace Amane.Mailer.Identity;

/// <summary>
/// The only boundary translating v2 identities into the retained v1 physical persistence graph.
/// These values are not public API or provider identities.
/// </summary>
public static class V2PersistenceCompatibility
{
    internal const string SourceService = "amane-v2-internal";

    // Stable instance-wide identity used only by the physical tenant_id column on suppressions.
    public static readonly Guid SuppressionScopeId =
        Guid.Parse("8ec85038-100e-5b84-bb9d-51bfd1ba74ce");

    public static Guid ToPhysicalTenantId(Guid senderId) => senderId;
}
