namespace Amane.Mailer.Operations;

public sealed record MailerDbStatsQuery(
    IReadOnlySet<Guid>? AllowedTenantIds,
    int QueuedStaleMinutes = 30,
    int FailureWindowMinutes = 60,
    int StaleProcessingMinutes = 30)
{
    public static MailerDbStatsQuery ForAllTenants(
        int queuedStaleMinutes = 30,
        int failureWindowMinutes = 60,
        int staleProcessingMinutes = 30) =>
        new(null, queuedStaleMinutes, failureWindowMinutes, staleProcessingMinutes);

    public static MailerDbStatsQuery ForSingleTenant(
        Guid tenantId,
        int queuedStaleMinutes = 30,
        int failureWindowMinutes = 60,
        int staleProcessingMinutes = 30) =>
        new(new HashSet<Guid> { tenantId }, queuedStaleMinutes, failureWindowMinutes, staleProcessingMinutes);
}
