namespace Amane.Mailer.Data.Sqlite.Models;

public sealed record AdminAuditListQuery
{
    public string? EventType { get; init; }

    public string? Actor { get; init; }

    public DateTimeOffset? OccurredFrom { get; init; }

    public DateTimeOffset? OccurredToExclusive { get; init; }

    public IReadOnlySet<Guid>? AllowedTenantIds { get; init; }

    public string? CursorOccurredAt { get; init; }

    public long? CursorId { get; init; }

    public int PageSize { get; init; } = 50;
}
