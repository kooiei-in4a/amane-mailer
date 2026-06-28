using System.Security.Claims;
using Amane.Mailer.Data.Sqlite;
using Amane.Mailer.Data.Sqlite.Models;

namespace Amane.Mailer.Admin;

/// <summary>
/// Shared helpers and vocabulary for the admin audit trail (ADR 0013 D-08).
///
/// Audit events are persisted to SQLite (<see cref="AdminAuditRepository"/>) as the
/// source of truth and mirrored to a structured stdout log as a secondary channel.
/// No recipient, subject, body, metadata value, or payload JSON is ever recorded.
/// </summary>
public static class AdminAuditLog
{
    public const string LoggerCategory = "Amane.Mailer.Admin.Audit";

    private const int UserAgentSummaryMaxLength = 120;
    private const int ActorMaxLength = 256;

    private static readonly EventId AuditEvent = new(1010, "AdminAuditEvent");
    private static readonly EventId AuditWriteFailedEvent = new(1011, "AdminAuditEventWriteFailed");

    public static class EventTypes
    {
        public const string MailRequestBodyViewed = "mail_request.body_viewed";
        public const string LoginSucceeded = "auth.login_succeeded";
        public const string LoginFailed = "auth.login_failed";
        public const string Logout = "auth.logout";
        public const string SessionExpired = "auth.session_expired";
        public const string AccountTemporarilyLocked = "auth.account_temporarily_locked";
        public const string LoginRateLimited = "auth.login_rate_limited";
    }

    public static class Results
    {
        public const string Success = "success";
        public const string Failure = "failure";
    }

    public static class TargetTypes
    {
        public const string MailRequest = "mail_request";
        public const string AdminSession = "admin_session";
    }

    public static string ResolveActor(HttpContext context) =>
        context.User.FindFirstValue(ClaimTypes.Name)
        ?? context.User.Identity?.Name
        ?? "unknown";

    public static string? ResolveSourceIp(HttpContext context) =>
        context.Connection.RemoteIpAddress?.ToString();

    public static string? SummarizeUserAgent(HttpContext context) =>
        SummarizeUserAgent(context.Request.Headers.UserAgent.ToString());

    /// <summary>
    /// Collapses whitespace and truncates a User-Agent down to a short summary so
    /// the full header (a long-term identifier) is not retained verbatim.
    /// </summary>
    internal static string? SummarizeUserAgent(string? userAgent)
    {
        if (string.IsNullOrWhiteSpace(userAgent))
            return null;

        var collapsed = string.Join(' ', userAgent.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return collapsed.Length <= UserAgentSummaryMaxLength
            ? collapsed
            : collapsed[..UserAgentSummaryMaxLength];
    }

    /// <summary>
    /// Strips CR/LF and other control characters from audit field values so
    /// user-supplied login input cannot forge stdout log lines (CWE-117).
    /// </summary>
    internal static string? SanitizeAuditLogValue(string? value)
    {
        if (value is null)
            return null;

        var needsWork = false;
        foreach (var ch in value)
        {
            if (ch is '\r' or '\n' or '\t' || char.IsControl(ch))
            {
                needsWork = true;
                break;
            }
        }

        if (!needsWork)
            return value;

        var chars = new char[value.Length];
        var length = 0;
        foreach (var ch in value)
        {
            if (ch is '\r' or '\n' or '\t')
                chars[length++] = ' ';
            else if (!char.IsControl(ch))
                chars[length++] = ch;
        }

        return length == 0 ? string.Empty : new string(chars, 0, length);
    }

    /// <summary>
    /// Bounds an actor value so an attacker-supplied login name cannot bloat the
    /// audit row or forge log lines. Never used for PII fields.
    /// </summary>
    internal static string NormalizeActor(string? actor)
    {
        if (string.IsNullOrWhiteSpace(actor))
            return "unknown";

        var sanitized = SanitizeAuditLogValue(actor.Trim());
        if (string.IsNullOrWhiteSpace(sanitized))
            return "unknown";

        return sanitized.Length <= ActorMaxLength ? sanitized : sanitized[..ActorMaxLength];
    }

    internal static AdminAuditEvent SanitizeForOutput(AdminAuditEvent auditEvent) =>
        auditEvent with
        {
            Actor = NormalizeActor(auditEvent.Actor),
            SourceIp = SanitizeAuditLogValue(auditEvent.SourceIp),
            UserAgentSummary = auditEvent.UserAgentSummary is null
                ? null
                : SummarizeUserAgent(auditEvent.UserAgentSummary),
            TargetId = SanitizeAuditLogValue(auditEvent.TargetId),
            FieldName = SanitizeAuditLogValue(auditEvent.FieldName),
            ErrorCode = SanitizeAuditLogValue(auditEvent.ErrorCode),
        };

    /// <summary>
    /// Writes an audit event best-effort: always emit the structured stdout log,
    /// then persist to SQLite. A persistence failure is logged but never propagated,
    /// so authentication flows are not converted into an availability outage.
    /// Body-view persistence does not use this path — it must fail closed.
    /// </summary>
    public static async Task WriteBestEffortAsync(
        AdminAuditRepository repository,
        ILogger logger,
        AdminAuditEvent auditEvent,
        CancellationToken cancellationToken)
    {
        LogToStdout(logger, auditEvent);
        var sanitized = SanitizeForOutput(auditEvent);

        try
        {
            await repository.WriteAsync(sanitized, cancellationToken);
        }
        catch (Exception ex)
        {
            logger.LogError(
                AuditWriteFailedEvent,
                ex,
                "Admin audit event {EventType} could not be persisted to the audit store.",
                auditEvent.EventType);
        }
    }

    public static void LogToStdout(ILogger logger, AdminAuditEvent auditEvent)
    {
        // NormalizeActor / SanitizeAuditLogValue already strip all control chars,
        // but CodeQL does not recognise them as sanitizers. The chained Replace
        // calls are a no-op at runtime — they exist solely as a CodeQL-recognised
        // barrier for cs/log-forging (CWE-117).
        var actor = NormalizeActor(auditEvent.Actor)
            .Replace("\r", " ").Replace("\n", " ");
        var sourceIp = SanitizeAuditLogValue(auditEvent.SourceIp)
            ?.Replace("\r", " ").Replace("\n", " ");
        var targetId = SanitizeAuditLogValue(auditEvent.TargetId)
            ?.Replace("\r", " ").Replace("\n", " ");
        var fieldName = SanitizeAuditLogValue(auditEvent.FieldName)
            ?.Replace("\r", " ").Replace("\n", " ");

        logger.LogInformation(
            AuditEvent,
            "Admin audit event {EventType} by {Actor} result {Result} target {TargetType}/{TargetId} field {FieldName} from {SourceIp}.",
            auditEvent.EventType,
            actor,
            auditEvent.Result,
            auditEvent.TargetType,
            targetId,
            fieldName,
            sourceIp);
    }
}
