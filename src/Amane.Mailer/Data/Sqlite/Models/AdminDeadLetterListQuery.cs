namespace Amane.Mailer.Data.Sqlite.Models;

public sealed record AdminDeadLetterListQuery
{
    public string? CursorCompletedAt { get; init; }

    public Guid? CursorId { get; init; }

    public IReadOnlySet<Guid>? AllowedTenantIds { get; init; }

    public int PageSize { get; init; } = 50;
}
