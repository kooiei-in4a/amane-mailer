using System.Threading.RateLimiting;

namespace Amane.Mailer.Identity;

public sealed class ApiAuthenticationRateLimiter : IDisposable
{
    internal const int PermitLimit = 20;

    private readonly PartitionedRateLimiter<string> _limiter =
        PartitionedRateLimiter.Create<string, string>(key =>
            RateLimitPartition.GetFixedWindowLimiter(
                key,
                static _ => new FixedWindowRateLimiterOptions
                {
                    PermitLimit = PermitLimit,
                    QueueLimit = 0,
                    Window = TimeSpan.FromMinutes(1),
                    AutoReplenishment = true,
                }));

    public bool CanAttempt(HttpContext context)
    {
        using var lease = _limiter.AttemptAcquire(GetPartition(context), 0);
        return lease.IsAcquired;
    }

    public bool TryConsume(HttpContext context)
    {
        using var lease = _limiter.AttemptAcquire(GetPartition(context), 1);
        return lease.IsAcquired;
    }

    private static string GetPartition(HttpContext context) =>
        context.Connection.RemoteIpAddress?.ToString() ?? "unknown";

    public void Dispose() => _limiter.Dispose();
}
