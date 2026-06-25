namespace Amane.Mailer.Data.Sqlite.Models;

public sealed record AdminDeadLetterListPage(
    IReadOnlyList<AdminDeadLetterListRow> Items,
    string? NextCursor);
