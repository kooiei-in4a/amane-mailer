namespace Amane.Mailer.Data.Sqlite.Models;

public sealed record AdminMailRequestListQuery
{
    public int? Status { get; init; }

    public Guid? TenantId { get; init; }

    public string? SourceService { get; init; }

    public string? CursorUpdatedAt { get; init; }

    public Guid? CursorId { get; init; }

    public int PageSize { get; init; } = 50;
}
