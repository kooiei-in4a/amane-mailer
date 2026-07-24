using System.Security.Cryptography;
using System.Text;
using Amane.Mailer.Configuration;
using Amane.Mailer.Webhooks;

namespace Amane.Mailer.Operations;

public static class MailerMetricsEndpoint
{
    public static async Task<IResult> HandleAsync(
        HttpContext context,
        MailerMetricsOptions options,
        MailerDbStatsReader statsReader,
        DeliveryEventRepository deliveryEventRepository,
        MailerRuntimeMetrics runtimeMetrics,
        TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        if (!options.Enabled)
        {
            return Results.NotFound();
        }

        if (!IsAuthorized(context, options))
        {
            return Results.Unauthorized();
        }

        var now = timeProvider.GetUtcNow();
        var stats = await statsReader.LoadStatsAsync(
            MailerDbStatsQuery.ForAllTenants(),
            now,
            cancellationToken);

        if (stats is null)
        {
            return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
        }

        var webhookCounts = await deliveryEventRepository.CountOperationalAsync(cancellationToken);
        var body = PrometheusMetricsFormatter.Format(
            stats,
            runtimeMetrics.CaptureSnapshot(),
            webhookCounts.PendingCount,
            webhookCounts.DeadLetteredCount);
        context.Response.Headers.CacheControl = "no-store";
        return Results.Content(body, "text/plain; version=0.0.4; charset=utf-8");
    }

    private static bool IsAuthorized(HttpContext context, MailerMetricsOptions options)
    {
        if (string.IsNullOrEmpty(options.BearerToken))
        {
            return true;
        }

        var authorization = context.Request.Headers.Authorization.ToString();
        const string prefix = "Bearer ";
        if (!authorization.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var token = authorization[prefix.Length..].Trim();
        return !string.IsNullOrEmpty(token)
            && ConstantTimeEquals(options.BearerToken, token);
    }

    private static bool ConstantTimeEquals(string expected, string actual)
    {
        var expectedBytes = Encoding.UTF8.GetBytes(expected);
        var actualBytes = Encoding.UTF8.GetBytes(actual);
        var expectedHash = SHA256.HashData(expectedBytes);
        var actualHash = SHA256.HashData(actualBytes);
        return CryptographicOperations.FixedTimeEquals(expectedHash, actualHash);
    }
}
