using Amane.Mailer.Data.Sqlite;
using Amane.Mailer.Data.Sqlite.Models;

namespace Amane.Mailer.Operations;

public sealed class MailerRuntimeMetrics
{
    private long _acceptedTotal;
    private long _retriesTotal;
    private readonly object _gate = new();
    private readonly Dictionary<(string Result, string Provider), long> _deliveries = new();
    private readonly Dictionary<string, DeliveryDurationHistogram> _durations = new(StringComparer.Ordinal);

    public void RecordRequestAccepted() =>
        Interlocked.Increment(ref _acceptedTotal);

    public void RecordAttemptCompleted(MailAttemptInsert attempt)
    {
        var result = MapDeliveryResult(attempt.Status);
        var durationSeconds = Math.Max(0d, (attempt.CompletedAt - attempt.StartedAt).TotalSeconds);

        lock (_gate)
        {
            var deliveryKey = (result, attempt.Provider);
            _deliveries.TryGetValue(deliveryKey, out var deliveryCount);
            _deliveries[deliveryKey] = deliveryCount + 1;

            if (!_durations.TryGetValue(attempt.Provider, out var histogram))
            {
                histogram = new DeliveryDurationHistogram();
                _durations[attempt.Provider] = histogram;
            }

            histogram.Observe(durationSeconds);
        }

        if (attempt.AttemptNumber > 1)
        {
            Interlocked.Increment(ref _retriesTotal);
        }
    }

    public MailerRuntimeMetricsSnapshot CaptureSnapshot()
    {
        lock (_gate)
        {
            return new MailerRuntimeMetricsSnapshot(
                Interlocked.Read(ref _acceptedTotal),
                Interlocked.Read(ref _retriesTotal),
                _deliveries
                    .Select(entry => (entry.Key.Result, entry.Key.Provider, entry.Value))
                    .ToArray(),
                _durations.ToDictionary(
                    entry => entry.Key,
                    entry => entry.Value.CaptureSnapshot(),
                    StringComparer.Ordinal));
        }
    }

    internal static string MapDeliveryResult(MailRequestState status) => status switch
    {
        MailRequestState.Delivered => "delivered",
        MailRequestState.Failed => "failed",
        MailRequestState.DeadLettered => "dead_lettered",
        _ => "unknown",
    };

    private sealed class DeliveryDurationHistogram
    {
        private readonly long[] _bucketCounts = new long[PrometheusMetricsFormatter.DurationBucketUpperBounds.Length];
        private double _sum;
        private long _count;

        public void Observe(double durationSeconds)
        {
            _count++;
            _sum += durationSeconds;

            for (var index = 0; index < PrometheusMetricsFormatter.DurationBucketUpperBounds.Length; index++)
            {
                if (durationSeconds <= PrometheusMetricsFormatter.DurationBucketUpperBounds[index])
                {
                    _bucketCounts[index]++;
                }
            }
        }

        public DeliveryDurationSnapshot CaptureSnapshot() =>
            new(_bucketCounts.ToArray(), _sum, _count);
    }
}

public sealed record MailerRuntimeMetricsSnapshot(
    long AcceptedTotal,
    long RetriesTotal,
    (string Result, string Provider, long Count)[] Deliveries,
    IReadOnlyDictionary<string, DeliveryDurationSnapshot> Durations);

public sealed record DeliveryDurationSnapshot(
    long[] BucketCounts,
    double SumSeconds,
    long Count);
