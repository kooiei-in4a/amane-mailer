using Amane.Mailer.Bounce;

namespace Amane.Mailer.Operations.VerifyDeliveryReport;

/// <summary>
/// Read-only poll loop that peeks Storage Queue messages and correlates Delivery Reports
/// by exact <c>data.messageId</c> match (#428 / ADR 0020 D-03).
/// </summary>
public sealed class DeliveryReportQueuePoller
{
    private readonly IAcsEventQueuePeeker _peeker;
    private readonly Func<TimeSpan, CancellationToken, Task> _delayAsync;
    private readonly Func<DateTimeOffset> _utcNow;

    public DeliveryReportQueuePoller(
        IAcsEventQueuePeeker peeker,
        Func<TimeSpan, CancellationToken, Task>? delayAsync = null,
        Func<DateTimeOffset>? utcNow = null)
    {
        _peeker = peeker ?? throw new ArgumentNullException(nameof(peeker));
        _delayAsync = delayAsync ?? ((delay, ct) => Task.Delay(delay, ct));
        _utcNow = utcNow ?? (() => DateTimeOffset.UtcNow);
    }

    public async Task<DeliveryReportPollResult> PollAsync(
        string expectedMessageId,
        TimeSpan timeout,
        TimeSpan pollInterval,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(expectedMessageId);
        if (timeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(timeout));
        }

        if (pollInterval <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(pollInterval));
        }

        var deadline = _utcNow() + timeout;
        var sawMalformed = false;
        var sawOtherDeliveryReport = false;
        int? lastApproximateCount = null;

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();

            IReadOnlyList<PeekedQueueMessageBody> peeked;
            try
            {
                peeked = await _peeker.PeekMessagesAsync(
                    AzureAcsEventQueuePeeker.MaxPeekMessages,
                    cancellationToken);
                lastApproximateCount = await _peeker.GetApproximateMessageCountAsync(cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                return DeliveryReportPollResult.QueueAccessFailed(QueuePeekFailureMapper.Map(ex));
            }

            foreach (var message in peeked)
            {
                foreach (var observation in DeliveryReportEventInspector.InspectBody(message.Body))
                {
                    if (observation.Kind == DeliveryReportPeekKind.Malformed)
                    {
                        sawMalformed = true;
                        continue;
                    }

                    if (observation.Kind != DeliveryReportPeekKind.DeliveryReport)
                    {
                        continue;
                    }

                    if (string.Equals(observation.MessageId, expectedMessageId, StringComparison.Ordinal))
                    {
                        return DeliveryReportPollResult.Correlated(
                            observation.Status ?? string.Empty,
                            lastApproximateCount);
                    }

                    sawOtherDeliveryReport = true;
                }
            }

            if (_utcNow() >= deadline)
            {
                var backlogPreventsConfirmation =
                    lastApproximateCount is int count
                    && count > AzureAcsEventQueuePeeker.MaxPeekMessages;

                return DeliveryReportPollResult.TimedOut(
                    backlogPreventsConfirmation,
                    sawOtherDeliveryReport,
                    sawMalformed,
                    lastApproximateCount);
            }

            var remaining = deadline - _utcNow();
            var delay = remaining < pollInterval ? remaining : pollInterval;
            if (delay > TimeSpan.Zero)
            {
                await _delayAsync(delay, cancellationToken);
            }
        }
    }
}

public sealed class DeliveryReportPollResult
{
    public required DeliveryReportPollOutcome Outcome { get; init; }

    public string? DeliveryStatus { get; init; }

    public string? CanonicalFailureCode { get; init; }

    public bool BacklogPreventsConfirmation { get; init; }

    public bool SawOtherDeliveryReport { get; init; }

    public bool SawMalformed { get; init; }

    public int? ApproximateMessageCount { get; init; }

    public static DeliveryReportPollResult Correlated(string status, int? approximateCount) =>
        new()
        {
            Outcome = DeliveryReportPollOutcome.Correlated,
            DeliveryStatus = status,
            ApproximateMessageCount = approximateCount,
        };

    public static DeliveryReportPollResult TimedOut(
        bool backlogPreventsConfirmation,
        bool sawOtherDeliveryReport,
        bool sawMalformed,
        int? approximateCount) =>
        new()
        {
            Outcome = DeliveryReportPollOutcome.TimedOut,
            BacklogPreventsConfirmation = backlogPreventsConfirmation,
            SawOtherDeliveryReport = sawOtherDeliveryReport,
            SawMalformed = sawMalformed,
            ApproximateMessageCount = approximateCount,
            CanonicalFailureCode = backlogPreventsConfirmation
                ? VerifyDeliveryReportResultCodes.FailedDeliveryReportBacklog
                : VerifyDeliveryReportResultCodes.FailedDeliveryReportTimeout,
        };

    public static DeliveryReportPollResult QueueAccessFailed(string canonicalFailureCode) =>
        new()
        {
            Outcome = DeliveryReportPollOutcome.QueueAccessFailed,
            CanonicalFailureCode = canonicalFailureCode,
        };
}

public enum DeliveryReportPollOutcome
{
    Correlated,
    TimedOut,
    QueueAccessFailed,
}

/// <summary>
/// Classifies ACS delivery status for operator reporting without printing the raw value.
/// </summary>
public static class DeliveryStatusClassifier
{
    public static DeliveryStatusClass Classify(string? status)
    {
        if (string.IsNullOrWhiteSpace(status))
        {
            return DeliveryStatusClass.Unknown;
        }

        if (BounceClassifier.IsDelivered(status))
        {
            return DeliveryStatusClass.Delivered;
        }

        if (BounceClassifier.IsHardBounce(status)
            || string.Equals(status, "Failed", StringComparison.Ordinal))
        {
            return DeliveryStatusClass.Failed;
        }

        return DeliveryStatusClass.Unknown;
    }
}

public enum DeliveryStatusClass
{
    Delivered,
    Failed,
    Unknown,
}
