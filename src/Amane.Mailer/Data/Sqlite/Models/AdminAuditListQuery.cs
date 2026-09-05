namespace Amane.Mailer.Data.Sqlite.Models;

public sealed record AdminAuditListQuery
{
    public string? EventType { get; init; }

    public string? Actor { get; init; }

    public DateTimeOffset? OccurredFrom { get; init; }

    public DateTimeOffset? OccurredToExclusive { get; init; }

    public IReadOnlySet<Guid>? AllowedTenantIds { get; init; }

    /// <summary>
    /// Managed Sender/API-key/instance-configuration events are owner-only. Internal callers
    /// that already have unrestricted audit access keep the historical default; Admin pages
    /// set this explicitly from the authenticated instance-owner state.
    /// </summary>
    public bool IncludeManagedConfiguration { get; init; } = true;

    public string? CursorOccurredAt { get; init; }

    public long? CursorId { get; init; }

    public int PageSize { get; init; } = 50;
}
