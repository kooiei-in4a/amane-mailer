using Amane.Mailer.Data.Sqlite.Models;

namespace Amane.Mailer.Operations;

public static class WorkerHeartbeatFreshness
{
    public const string WorkerName = "worker";
    public const string SweepName = "sweep";

    public static bool AreFresh(
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
            return false;

        if (utcNow - workerHeartbeat.LastHeartbeatAt > maxStaleness)
            return false;

        if (utcNow - sweepHeartbeat.LastHeartbeatAt > maxStaleness)
            return false;

        return true;
    }
}
