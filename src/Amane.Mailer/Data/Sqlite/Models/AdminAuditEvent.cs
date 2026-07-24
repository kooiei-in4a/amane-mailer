namespace Amane.Mailer.Data.Sqlite.Models;

/// <summary>
/// An admin-operation audit event to persist (ADR 0013 D-08).
/// PII (recipient, subject, body, metadata values) is never carried on this type.
/// </summary>
public sealed record AdminAuditEvent
{
    public required string EventType { get; init; }

    public required string Actor { get; init; }

    public required DateTimeOffset OccurredAt { get; init; }

    public string? SourceIp { get; init; }

    public string? UserAgentSummary { get; init; }

    public string? TargetType { get; init; }

    public string? TargetId { get; init; }

    /// <summary>
    /// Tenant scope for mail_request-targeted events. Null for auth/session/db_ops
    /// events and for legacy rows that could not be backfilled after request retention.
    /// </summary>
    public Guid? TenantId { get; init; }

    public string? FieldName { get; init; }

    public required string Result { get; init; }

    public string? ErrorCode { get; init; }
}
