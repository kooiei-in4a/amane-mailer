namespace Amane.Mailer.Operations;

/// <summary>
/// Fixed, low-cardinality readiness failure reasons for internal logs and metrics (#330).
/// Never include tenant IDs, request IDs, or exception messages in metric labels.
/// </summary>
public static class MailerReadinessReasons
{
    public const string SchemaNotReady = "schema_not_ready";
    public const string WorkerNotRunning = "worker_not_running";
    public const string SweepNotRunning = "sweep_not_running";
    public const string HeartbeatMissing = "heartbeat_missing";
    public const string HeartbeatStale = "heartbeat_stale";
    public const string DatabaseError = "database_error";
    public const string UnexpectedError = "unexpected_error";

    public static readonly string[] All =
    [
        SchemaNotReady,
        WorkerNotRunning,
        SweepNotRunning,
        HeartbeatMissing,
        HeartbeatStale,
        DatabaseError,
        UnexpectedError,
    ];
}
