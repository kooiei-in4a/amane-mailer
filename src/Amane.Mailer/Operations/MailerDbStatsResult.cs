namespace Amane.Mailer.Operations;

public sealed record MailerDbStatsResult(
    DateTimeOffset AsOfUtc,
    long QueuedCount,
    long ProcessingCount,
    long DeliveredCount,
    long FailedCount,
    long DeadLetteredCount,
    long ReadyBacklogCount,
    long OldestQueuedAgeSeconds,
    long QueuedStaleCount,
    long StaleProcessingCount,
    long ExpiredProcessingCount,
    long RecentFailedCount,
    long RecentDeadLetteredCount,
    long WorkerHeartbeatAgeSeconds,
    long SweepHeartbeatAgeSeconds);

public sealed record MailerProviderAttemptStat(
    string Provider,
    int AttemptStatus,
    long Count);
