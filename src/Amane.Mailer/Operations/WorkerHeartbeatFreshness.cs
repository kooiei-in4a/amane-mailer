using Amane.Mailer.Data.Sqlite.Models;

namespace Amane.Mailer.Operations;

public static class WorkerHeartbeatFreshness
{
    public const string WorkerName = "worker";
    public const string SweepName = "sweep";

    public static bool AreFresh(
        IReadOnlyList<WorkerHeartbeat> heartbeats,
        TimeSpan maxStaleness,
        DateTimeOffset? now = null) =>
        GetFailureReason(heartbeats, maxStaleness, now) is null;

    /// <summary>
    /// Returns a primary readiness failure reason for missing or stale heartbeats,
    /// or null when both worker and sweep heartbeats are present and fresh.
    /// Priority: missing before stale; worker before sweep within each class.
    /// </summary>
    public static string? GetFailureReason(
        IReadOnlyList<WorkerHeartbeat> heartbeats,
        TimeSpan maxStaleness,
        DateTimeOffset? now = null)
    {
        var utcNow = now ?? DateTimeOffset.UtcNow;

        var workerHeartbeat = heartbeats.FirstOrDefault(h =>
            string.Equals(h.Name, WorkerName, StringComparison.Ordinal));
        var sweepHeartbeat = heartbeats.FirstOrDefault(h =>
            string.Equals(h.Name, SweepName, StringComparison.Ordinal));

        if (workerHeartbeat is null || sweepHeartbeat is null)
            return MailerReadinessReasons.HeartbeatMissing;

        if (utcNow - workerHeartbeat.LastHeartbeatAt > maxStaleness
            || utcNow - sweepHeartbeat.LastHeartbeatAt > maxStaleness)
        {
            return MailerReadinessReasons.HeartbeatStale;
        }

        return null;
    }
}
