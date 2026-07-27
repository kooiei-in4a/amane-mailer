namespace Amane.Mailer.Data.Sqlite.Models;

public sealed class AdminSuppressionListQuery
{
    public Guid? TenantId { get; init; }

    /// <summary>
    /// null = break-glass (all tenants). Empty set = no rows.
    /// </summary>
    public IReadOnlySet<Guid>? AllowedTenantIds { get; init; }

    public string? CursorCreatedAt { get; init; }

    public Guid? CursorId { get; init; }

    public int PageSize { get; init; } = 50;
}

public sealed record AdminSuppressionListRow(
    Guid Id,
    Guid TenantId,
    string RecipientEmail,
    string Reason,
    Guid? SourceBounceEventId,
    DateTimeOffset CreatedAt);

public sealed record AdminSuppressionListPage(
    IReadOnlyList<AdminSuppressionListRow> Items,
    string? NextCursor);
