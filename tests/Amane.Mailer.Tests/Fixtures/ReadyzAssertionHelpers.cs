using System.Text.Json;
using Amane.Mailer.Data.Sqlite;
using Amane.Mailer.Operations;
using Microsoft.Extensions.Logging;

namespace Amane.Mailer.Tests.Fixtures;

public static class ReadyzAssertionHelpers
{
    public static void AssertReadyBody(string body, bool ready)
    {
        using var document = JsonDocument.Parse(body);
        Assert.Equal(JsonValueKind.Object, document.RootElement.ValueKind);
        Assert.Single(document.RootElement.EnumerateObject());
        Assert.Equal(ready, document.RootElement.GetProperty("ready").GetBoolean());
    }

    public static void AssertPrimaryReason(ReadyzObservabilityHarness harness, string reason) =>
        AssertPrimaryReason(harness.RuntimeMetrics, reason);

    public static void AssertPrimaryReason(MailerRuntimeMetrics metrics, string reason)
    {
        var snapshot = metrics.CaptureSnapshot();
        Assert.False(snapshot.Ready);
        Assert.Equal(reason, snapshot.ReadinessFailureReason);

        var text = PrometheusMetricsFormatter.Format(EmptyStats(), snapshot);
        Assert.Contains("mail_ready 0", text, StringComparison.Ordinal);
        Assert.Contains($"mail_readiness_failure{{reason=\"{reason}\"}} 1", text, StringComparison.Ordinal);
        foreach (var other in MailerReadinessReasons.All.Where(value => value != reason))
        {
            Assert.Contains($"mail_readiness_failure{{reason=\"{other}\"}} 0", text, StringComparison.Ordinal);
        }
    }

    public static void AssertReadyMetrics(ReadyzObservabilityHarness harness) =>
        AssertReadyMetrics(harness.RuntimeMetrics);

    public static void AssertReadyMetrics(MailerRuntimeMetrics metrics)
    {
        var snapshot = metrics.CaptureSnapshot();
        Assert.True(snapshot.Ready);
        Assert.Null(snapshot.ReadinessFailureReason);

        var text = PrometheusMetricsFormatter.Format(EmptyStats(), snapshot);
        Assert.Contains("mail_ready 1", text, StringComparison.Ordinal);
        foreach (var reason in MailerReadinessReasons.All)
        {
            Assert.Contains($"mail_readiness_failure{{reason=\"{reason}\"}} 0", text, StringComparison.Ordinal);
        }
    }

    public static void AssertSingleNotReadyWarning(ReadyzObservabilityHarness harness, string reason)
    {
        var warnings = harness.LogCapture.Snapshot()
            .Where(static entry =>
                entry.Level == LogLevel.Warning &&
                entry.FormattedMessage.Contains("Mailer readiness not ready", StringComparison.Ordinal))
            .ToList();
        Assert.Single(warnings);
        Assert.Equal(reason, warnings[0].State["Reason"]);
    }

    private static MailerDbStatsResult EmptyStats() =>
        new(
            AsOfUtc: DateTimeOffset.UtcNow,
            QueuedCount: 0,
            ProcessingCount: 0,
            DeliveredCount: 0,
            FailedCount: 0,
            DeadLetteredCount: 0,
            ReadyBacklogCount: 0,
            OldestQueuedAgeSeconds: 0,
            QueuedStaleCount: 0,
            StaleProcessingCount: 0,
            ExpiredProcessingCount: 0,
            RecentFailedCount: 0,
            RecentDeadLetteredCount: 0,
            WorkerHeartbeatAgeSeconds: -1,
            SweepHeartbeatAgeSeconds: -1);
}
