using Amane.Mailer.Data.Sqlite;
using Amane.Mailer.Data.Sqlite.Models;

namespace Amane.Mailer.Operations;

public sealed class MailerRuntimeMetrics
{
    private long _acceptedTotal;
    private long _attachmentRequestsAcceptedTotal;
    private long _retriesTotal;
    private long _finalizeSkippedTotal;
    private long _webhookFinalizeSkippedTotal;
    private long _bounceEventsTotal;
    private long _bounceUnmatchedTotal;
    private long _bounceRecipientMismatchTotal;
    private long _suppressedSendsTotal;
    private long _providerQueuePollFailedTotal;
    private long _providerQueuePayloadInvalidTotal;
    private long _providerQueuePoisonedTotal;
    private int _ready;
    private bool _readinessObserved;
    private string? _readinessFailureReason;
    private readonly object _gate = new();
    private readonly Dictionary<(string Result, string Provider), long> _deliveries = new();
    private readonly Dictionary<string, DeliveryDurationHistogram> _durations = new(StringComparer.Ordinal);
    private readonly Dictionary<string, long> _attachmentValidationRejected = new(StringComparer.Ordinal);
    private readonly Dictionary<string, long> _attachmentSpoolCleanups = new(StringComparer.Ordinal);

    public void RecordRequestAccepted() =>
        Interlocked.Increment(ref _acceptedTotal);

    /// <summary>ADR 0022 D-04: a mail request with one or more attachments was accepted (spool
    /// committed, DB row inserted). Never carries filename or content -- count only.</summary>
    public void RecordAttachmentRequestAccepted() =>
        Interlocked.Increment(ref _attachmentRequestsAcceptedTotal);

    /// <summary>ADR 0022 D-04/D-12: an attachment-bearing request was rejected at acceptance
    /// time. <paramref name="reasonCode"/> must be a fixed <see cref="MailerErrorCodes"/>-style
    /// value, never a raw filename or provider message.</summary>
    public void RecordAttachmentValidationRejected(string reasonCode)
    {
        lock (_gate)
        {
            _attachmentValidationRejected.TryGetValue(reasonCode, out var count);
            _attachmentValidationRejected[reasonCode] = count + 1;
        }
    }

    /// <summary>ADR 0022 D-06/D-09 spool lifecycle: a committed or staging spool entry was
    /// removed. <paramref name="kind"/> is a fixed category (e.g. "committed", "staging"),
    /// never an opaque spool key or filename.</summary>
    public void RecordAttachmentSpoolCleanup(string kind)
    {
        lock (_gate)
        {
            _attachmentSpoolCleanups.TryGetValue(kind, out var count);
            _attachmentSpoolCleanups[kind] = count + 1;
        }
    }

    public void RecordFinalizeSkipped() =>
        Interlocked.Increment(ref _finalizeSkippedTotal);

    public void RecordWebhookFinalizeSkipped() =>
        Interlocked.Increment(ref _webhookFinalizeSkippedTotal);

    public void RecordBounceEvent() =>
        Interlocked.Increment(ref _bounceEventsTotal);

    public void RecordBounceUnmatched() =>
        Interlocked.Increment(ref _bounceUnmatchedTotal);

    public void RecordBounceRecipientMismatch() =>
        Interlocked.Increment(ref _bounceRecipientMismatchTotal);

    public void RecordSuppressedSend() =>
        Interlocked.Increment(ref _suppressedSendsTotal);

    public void RecordProviderQueuePollFailed() =>
        Interlocked.Increment(ref _providerQueuePollFailedTotal);

    public void RecordProviderQueuePayloadInvalid() =>
        Interlocked.Increment(ref _providerQueuePayloadInvalidTotal);

    public void RecordProviderQueuePoisoned() =>
        Interlocked.Increment(ref _providerQueuePoisonedTotal);

    /// <summary>
    /// Updates readiness gauges. <paramref name="failureReason"/> must be a fixed
    /// <see cref="MailerReadinessReasons"/> value (or null when ready).
    /// </summary>
    public void SetReadiness(bool ready, string? failureReason)
    {
        lock (_gate)
        {
            _readinessObserved = true;
            _ready = ready ? 1 : 0;
            _readinessFailureReason = ready ? null : failureReason;
        }
    }

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
                Interlocked.Read(ref _attachmentRequestsAcceptedTotal),
                Interlocked.Read(ref _retriesTotal),
                Interlocked.Read(ref _finalizeSkippedTotal),
                Interlocked.Read(ref _webhookFinalizeSkippedTotal),
                Interlocked.Read(ref _bounceEventsTotal),
                Interlocked.Read(ref _bounceUnmatchedTotal),
                Interlocked.Read(ref _bounceRecipientMismatchTotal),
                Interlocked.Read(ref _suppressedSendsTotal),
                Interlocked.Read(ref _providerQueuePollFailedTotal),
                Interlocked.Read(ref _providerQueuePayloadInvalidTotal),
                Interlocked.Read(ref _providerQueuePoisonedTotal),
                _readinessObserved,
                _ready == 1,
                _readinessFailureReason,
                _deliveries
                    .Select(entry => (entry.Key.Result, entry.Key.Provider, entry.Value))
                    .ToArray(),
                _durations.ToDictionary(
                    entry => entry.Key,
                    entry => entry.Value.CaptureSnapshot(),
                    StringComparer.Ordinal),
                _attachmentValidationRejected
                    .Select(entry => (entry.Key, entry.Value))
                    .ToArray(),
                _attachmentSpoolCleanups
                    .Select(entry => (entry.Key, entry.Value))
                    .ToArray());
        }
    }

    internal void ClearForTests()
    {
        Interlocked.Exchange(ref _acceptedTotal, 0);
        Interlocked.Exchange(ref _attachmentRequestsAcceptedTotal, 0);
        Interlocked.Exchange(ref _retriesTotal, 0);
        Interlocked.Exchange(ref _finalizeSkippedTotal, 0);
        Interlocked.Exchange(ref _webhookFinalizeSkippedTotal, 0);
        Interlocked.Exchange(ref _bounceEventsTotal, 0);
        Interlocked.Exchange(ref _bounceUnmatchedTotal, 0);
        Interlocked.Exchange(ref _bounceRecipientMismatchTotal, 0);
        Interlocked.Exchange(ref _suppressedSendsTotal, 0);
        Interlocked.Exchange(ref _providerQueuePollFailedTotal, 0);
        Interlocked.Exchange(ref _providerQueuePayloadInvalidTotal, 0);
        Interlocked.Exchange(ref _providerQueuePoisonedTotal, 0);

        lock (_gate)
        {
            _readinessObserved = false;
            _ready = 0;
            _readinessFailureReason = null;
            _deliveries.Clear();
            _durations.Clear();
            _attachmentValidationRejected.Clear();
            _attachmentSpoolCleanups.Clear();
        }
    }

    internal void ClearReadinessForTests()
    {
        lock (_gate)
        {
            _readinessObserved = false;
            _ready = 0;
            _readinessFailureReason = null;
        }
    }

    internal static string MapDeliveryResult(MailRequestState status) => status switch
    {
        MailRequestState.Delivered => "delivered",
        MailRequestState.Failed => "failed",
        MailRequestState.DeadLettered => "dead_lettered",
        MailRequestState.DeliveryUnknown => "delivery_unknown",
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
    long AttachmentRequestsAcceptedTotal,
    long RetriesTotal,
    long FinalizeSkippedTotal,
    long WebhookFinalizeSkippedTotal,
    long BounceEventsTotal,
    long BounceUnmatchedTotal,
    long BounceRecipientMismatchTotal,
    long SuppressedSendsTotal,
    long ProviderQueuePollFailedTotal,
    long ProviderQueuePayloadInvalidTotal,
    long ProviderQueuePoisonedTotal,
    bool ReadinessObserved,
    bool Ready,
    string? ReadinessFailureReason,
    (string Result, string Provider, long Count)[] Deliveries,
    IReadOnlyDictionary<string, DeliveryDurationSnapshot> Durations,
    (string ReasonCode, long Count)[] AttachmentValidationRejected,
    (string Kind, long Count)[] AttachmentSpoolCleanups);

public sealed record DeliveryDurationSnapshot(
    long[] BucketCounts,
    double SumSeconds,
    long Count);
