namespace Amane.Mailer.Data.Sqlite.Models;

public sealed record AdminAuditListPage(
    IReadOnlyList<AdminAuditEventRow> Items,
    string? NextCursor);
