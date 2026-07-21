using Amane.Mailer.Configuration;

namespace Amane.Mailer.Operations;

public static class MailerMetricsEndpoint
{
    public static async Task<IResult> HandleAsync(
        HttpContext context,
        MailerMetricsOptions options,
        MailerDbStatsReader statsReader,
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

        if (!await statsReader.CanReadMigratedSchemaAsync(cancellationToken))
        {
            return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
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

        var body = PrometheusMetricsFormatter.Format(stats, runtimeMetrics.CaptureSnapshot());
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
            && string.Equals(token, options.BearerToken, StringComparison.Ordinal);
    }
}
