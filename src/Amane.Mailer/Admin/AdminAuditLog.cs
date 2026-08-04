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
        public const string BreakGlassMailRequestBodyViewed = "mail_request.break_glass_body_viewed";
        public const string ManualRetryRequested = "mail_request.manual_retry_requested";
        public const string ManualCancelRequested = "mail_request.manual_cancel_requested";
        public const string LoginSucceeded = "auth.login_succeeded";
        public const string BreakGlassLoginSucceeded = "auth.break_glass_login_succeeded";
        public const string LoginFailed = "auth.login_failed";
        public const string Logout = "auth.logout";
        public const string SessionExpired = "auth.session_expired";
        public const string AccountTemporarilyLocked = "auth.account_temporarily_locked";
        public const string LoginRateLimited = "auth.login_rate_limited";
        public const string DbCheckpointRequested = "db_ops.checkpoint_requested";
        public const string DbBackupRequested = "db_ops.backup_requested";
        public const string DbBackupCompleted = "db_ops.backup_completed";
        public const string DbBackupFailed = "db_ops.backup_failed";
        public const string MailSuppressionsListUnmasked = "mail_suppressions.list_unmasked";
        public const string MailSuppressionsRemoved = "mail_suppressions.removed";
        public const string MailSuppressionsRemoveFailed = "mail_suppressions.remove_failed";

        public static IReadOnlyList<string> All { get; } =
        [
            MailRequestBodyViewed,
            BreakGlassMailRequestBodyViewed,
            ManualRetryRequested,
            ManualCancelRequested,
            LoginSucceeded,
            BreakGlassLoginSucceeded,
            LoginFailed,
            Logout,
            SessionExpired,
            AccountTemporarilyLocked,
            LoginRateLimited,
            DbCheckpointRequested,
            DbBackupRequested,
            DbBackupCompleted,
            DbBackupFailed,
            MailSuppressionsListUnmasked,
            MailSuppressionsRemoved,
            MailSuppressionsRemoveFailed,
        ];
    }

    public static class ErrorCodes
    {
        public const string NotFound = "not_found";
        public const string InvalidState = "invalid_state";
        public const string LockHeld = "lock_held";
        public const string OperationFailed = "operation_failed";

        /// <summary>ADR 0022 D-08/D-12 fixed reason code (kept uppercase to match the ADR verbatim).</summary>
        public const string AttachmentManualRetryNotSupported = "ATTACHMENT_MANUAL_RETRY_NOT_SUPPORTED";
    }

    public static class FieldNames
    {
        public const string ConfiguredBackupDirectory = "configured_backup_directory";
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
        public const string DbOps = "db_ops";
        public const string MailSuppressions = "mail_suppressions";

        /// <summary>
        /// Targets whose rows carry a mail tenant_id and must be filtered for scoped admins.
        /// Auth/session/db_ops remain service-wide (no mail-tenant PII in those events).
        /// </summary>
        public static IReadOnlyList<string> TenantScoped { get; } =
        [
            MailRequest,
            MailSuppressions,
        ];
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
