namespace Amane.Mailer.Data.Sqlite.Models;

/// <summary>
/// A persisted admin-operation audit event read back from the database.
/// </summary>
public sealed record AdminAuditEventRow(
    long Id,
    string EventType,
    string Actor,
    DateTimeOffset OccurredAt,
    string? SourceIp,
    string? UserAgentSummary,
    string? TargetType,
    string? TargetId,
    string? FieldName,
    string Result,
    string? ErrorCode);
