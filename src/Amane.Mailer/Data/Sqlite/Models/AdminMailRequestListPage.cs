namespace Amane.Mailer.Data.Sqlite.Models;

public sealed record AdminMailRequestListPage(
    IReadOnlyList<AdminMailRequestListRow> Items,
    string? NextCursor);
