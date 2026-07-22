using System.Globalization;
using System.Text;
using Amane.Mailer.Data.Sqlite;

namespace Amane.Mailer.Operations;

public static class PrometheusMetricsFormatter
{
    internal static readonly double[] DurationBucketUpperBounds = [0.1, 0.5, 1, 2.5, 5, 10, 30];

    public static string Format(MailerDbStatsResult stats, MailerRuntimeMetricsSnapshot runtime)
    {
        var builder = new StringBuilder(2048);

        AppendHelpType(builder, "mail_requests_accepted_total", "counter",
            "Total mail requests accepted since process start.");
        AppendCounter(builder, "mail_requests_accepted_total", runtime.AcceptedTotal);

        AppendHelpType(builder, "mail_deliveries_total", "counter",
            "Total completed delivery attempts since process start.");
        foreach (var delivery in runtime.Deliveries.OrderBy(entry => entry.Provider, StringComparer.Ordinal)
                     .ThenBy(entry => entry.Result, StringComparer.Ordinal))
        {
            AppendCounter(
                builder,
                "mail_deliveries_total",
                delivery.Count,
                ("result", delivery.Result),
                ("provider", delivery.Provider));
        }

        AppendHelpType(builder, "mail_delivery_duration_seconds", "histogram",
            "Mail delivery attempt duration in seconds since process start.");
        foreach (var (provider, duration) in runtime.Durations.OrderBy(entry => entry.Key, StringComparer.Ordinal))
        {
            AppendHistogram(builder, "mail_delivery_duration_seconds", provider, duration);
        }

        AppendHelpType(builder, "mail_queue_ready_count", "gauge",
            "Mail requests ready for immediate delivery.");
        AppendGauge(builder, "mail_queue_ready_count", stats.ReadyBacklogCount);

        AppendHelpType(builder, "mail_queue_oldest_age_seconds", "gauge",
            "Age in seconds of the oldest ready queued mail request.");
        AppendGauge(builder, "mail_queue_oldest_age_seconds", stats.OldestQueuedAgeSeconds);

        AppendHelpType(builder, "mail_retries_total", "counter",
            "Total retry delivery attempts (attempt_number > 1) since process start.");
        AppendCounter(builder, "mail_retries_total", runtime.RetriesTotal);

        AppendHelpType(builder, "mail_finalize_skipped_total", "counter",
            "Total times a delivered provider attempt was recorded but request finalize was skipped because the lock expired or was superseded.");
        AppendCounter(builder, "mail_finalize_skipped_total", runtime.FinalizeSkippedTotal);

        AppendHelpType(builder, "mail_dead_letters_total", "gauge",
            "Current number of dead-lettered mail requests.");
        AppendGauge(builder, "mail_dead_letters_total", stats.DeadLetteredCount);

        AppendHelpType(builder, "mail_worker_heartbeat_age_seconds", "gauge",
            "Age in seconds since the last worker heartbeat.");
        if (stats.WorkerHeartbeatAgeSeconds >= 0)
        {
            AppendGauge(
                builder,
                "mail_worker_heartbeat_age_seconds",
                stats.WorkerHeartbeatAgeSeconds,
                ("component", "worker"));
        }

        if (stats.SweepHeartbeatAgeSeconds >= 0)
        {
            AppendGauge(
                builder,
                "mail_worker_heartbeat_age_seconds",
                stats.SweepHeartbeatAgeSeconds,
                ("component", "sweep"));
        }

        return builder.ToString();
    }

    private static void AppendHelpType(StringBuilder builder, string name, string type, string help)
    {
        builder.Append("# HELP ");
        builder.Append(name);
        builder.Append(' ');
        builder.Append(help);
        builder.Append('\n');
        builder.Append("# TYPE ");
        builder.Append(name);
        builder.Append(' ');
        builder.Append(type);
        builder.Append('\n');
    }

    private static void AppendCounter(StringBuilder builder, string name, long value, params (string Key, string Value)[] labels)
    {
        AppendMetricLine(builder, name, FormatInteger(value), labels);
    }

    private static void AppendGauge(StringBuilder builder, string name, long value, params (string Key, string Value)[] labels)
    {
        AppendMetricLine(builder, name, FormatInteger(value), labels);
    }

    private static void AppendHistogram(
        StringBuilder builder,
        string name,
        string provider,
        DeliveryDurationSnapshot duration)
    {
        for (var index = 0; index < DurationBucketUpperBounds.Length; index++)
        {
            AppendMetricLine(
                builder,
                name + "_bucket",
                FormatInteger(duration.BucketCounts[index]),
                ("provider", provider),
                ("le", FormatBucketUpperBound(DurationBucketUpperBounds[index])));
        }

        AppendMetricLine(
            builder,
            name + "_bucket",
            FormatInteger(duration.Count),
            ("provider", provider),
            ("le", "+Inf"));

        AppendMetricLine(
            builder,
            name + "_sum",
            FormatDouble(duration.SumSeconds),
            ("provider", provider));

        AppendMetricLine(
            builder,
            name + "_count",
            FormatInteger(duration.Count),
            ("provider", provider));
    }

    private static void AppendMetricLine(
        StringBuilder builder,
        string name,
        string formattedValue,
        params (string Key, string Value)[] labels)
    {
        builder.Append(name);
        if (labels.Length > 0)
        {
            builder.Append('{');
            for (var index = 0; index < labels.Length; index++)
            {
                if (index > 0)
                {
                    builder.Append(',');
                }

                builder.Append(labels[index].Key);
                builder.Append("=\"");
                builder.Append(EscapeLabelValue(labels[index].Value));
                builder.Append('"');
            }

            builder.Append('}');
        }

        builder.Append(' ');
        builder.Append(formattedValue);
        builder.Append('\n');
    }

    private static string EscapeLabelValue(string value) =>
        value
            .Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("\n", "\\n", StringComparison.Ordinal)
            .Replace("\"", "\\\"", StringComparison.Ordinal);

    private static string FormatInteger(long value) =>
        value.ToString(CultureInfo.InvariantCulture);

    private static string FormatDouble(double value) =>
        value.ToString("R", CultureInfo.InvariantCulture);

    private static string FormatBucketUpperBound(double upperBound) =>
        upperBound.ToString("0.##########", CultureInfo.InvariantCulture);
}
