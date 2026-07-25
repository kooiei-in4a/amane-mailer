using System.Net;
using Amane.Mailer.Data.Sqlite.Models;
using Amane.Mailer.Operations;
using Amane.Mailer.Tests.Fixtures;
using Microsoft.Extensions.Logging;

namespace Amane.Mailer.Tests;

/// <summary>
/// Coverage for #330: /readyz keeps the public HTTP contract while recording
/// a fixed primary failure reason via transition-only logs and readiness gauges.
/// </summary>
public sealed class ReadyzObservabilityTests
{
    [Fact]
    public async Task Readyz_records_worker_not_running_without_changing_http_contract()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var harness = await ReadyzObservabilityHarness.CreateAsync(ct);

        harness.ServiceStatus.SetWorkerRunning(false);
        harness.ServiceStatus.SetSweepRunning(true);

        using var response = await harness.Client.GetAsync("/readyz", ct);
        var body = await response.Content.ReadAsStringAsync(ct);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        ReadyzAssertionHelpers.AssertReadyBody(body, ready: false);
        ReadyzAssertionHelpers.AssertPrimaryReason(harness, MailerReadinessReasons.WorkerNotRunning);
        ReadyzAssertionHelpers.AssertSingleNotReadyWarning(harness, MailerReadinessReasons.WorkerNotRunning);
    }

    [Fact]
    public async Task Readyz_records_sweep_not_running()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var harness = await ReadyzObservabilityHarness.CreateAsync(ct);

        harness.ServiceStatus.SetWorkerRunning(true);
        harness.ServiceStatus.SetSweepRunning(false);

        using var response = await harness.Client.GetAsync("/readyz", ct);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        ReadyzAssertionHelpers.AssertPrimaryReason(harness, MailerReadinessReasons.SweepNotRunning);
    }

    [Fact]
    public async Task Readyz_records_heartbeat_missing()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var harness = await ReadyzObservabilityHarness.CreateAsync(ct);

        harness.ServiceStatus.SetWorkerRunning(true);
        harness.ServiceStatus.SetSweepRunning(true);

        using var response = await harness.Client.GetAsync("/readyz", ct);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        ReadyzAssertionHelpers.AssertPrimaryReason(harness, MailerReadinessReasons.HeartbeatMissing);
    }

    [Fact]
    public async Task Readyz_records_worker_heartbeat_stale()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var harness = await ReadyzObservabilityHarness.CreateAsync(ct);

        var now = DateTimeOffset.UtcNow;
        await harness.Repository.UpsertHeartbeatAsync("worker", now.AddMinutes(-10), ct);
        await harness.Repository.UpsertHeartbeatAsync("sweep", now, ct);
        harness.ServiceStatus.SetWorkerRunning(true);
        harness.ServiceStatus.SetSweepRunning(true);

        using var response = await harness.Client.GetAsync("/readyz", ct);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        ReadyzAssertionHelpers.AssertPrimaryReason(harness, MailerReadinessReasons.HeartbeatStale);
    }

    [Fact]
    public async Task Readyz_records_sweep_heartbeat_stale()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var harness = await ReadyzObservabilityHarness.CreateAsync(ct);

        var now = DateTimeOffset.UtcNow;
        await harness.Repository.UpsertHeartbeatAsync("worker", now, ct);
        await harness.Repository.UpsertHeartbeatAsync("sweep", now.AddMinutes(-10), ct);
        harness.ServiceStatus.SetWorkerRunning(true);
        harness.ServiceStatus.SetSweepRunning(true);

        using var response = await harness.Client.GetAsync("/readyz", ct);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        ReadyzAssertionHelpers.AssertPrimaryReason(harness, MailerReadinessReasons.HeartbeatStale);
    }

    [Fact]
    public async Task Readyz_recovers_to_ready_and_clears_failure_gauge()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var harness = await ReadyzObservabilityHarness.CreateAsync(ct);

        harness.ServiceStatus.SetWorkerRunning(false);
        harness.ServiceStatus.SetSweepRunning(true);
        using (var notReady = await harness.Client.GetAsync("/readyz", ct))
        {
            Assert.Equal(HttpStatusCode.ServiceUnavailable, notReady.StatusCode);
        }

        harness.LogCapture.Clear();
        var now = DateTimeOffset.UtcNow;
        await harness.Repository.UpsertHeartbeatAsync("worker", now, ct);
        await harness.Repository.UpsertHeartbeatAsync("sweep", now, ct);
        harness.ServiceStatus.SetWorkerRunning(true);
        harness.ServiceStatus.SetSweepRunning(true);

        using var ready = await harness.Client.GetAsync("/readyz", ct);
        var body = await ready.Content.ReadAsStringAsync(ct);

        Assert.Equal(HttpStatusCode.OK, ready.StatusCode);
        ReadyzAssertionHelpers.AssertReadyBody(body, ready: true);
        ReadyzAssertionHelpers.AssertReadyMetrics(harness);
        Assert.Contains(
            harness.LogCapture.Snapshot(),
            static entry =>
                entry.Level == LogLevel.Information &&
                entry.FormattedMessage.Contains("Mailer readiness recovered", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Readyz_suppresses_repeated_warning_for_same_reason()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var harness = await ReadyzObservabilityHarness.CreateAsync(ct);

        harness.ServiceStatus.SetWorkerRunning(false);
        harness.ServiceStatus.SetSweepRunning(true);

        using var first = await harness.Client.GetAsync("/readyz", ct);
        using var second = await harness.Client.GetAsync("/readyz", ct);
        using var third = await harness.Client.GetAsync("/readyz", ct);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, first.StatusCode);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, second.StatusCode);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, third.StatusCode);
        ReadyzAssertionHelpers.AssertSingleNotReadyWarning(harness, MailerReadinessReasons.WorkerNotRunning);
    }

    [Fact]
    public async Task Readyz_logs_again_when_primary_reason_changes()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var harness = await ReadyzObservabilityHarness.CreateAsync(ct);

        harness.ServiceStatus.SetWorkerRunning(false);
        harness.ServiceStatus.SetSweepRunning(true);
        using (var first = await harness.Client.GetAsync("/readyz", ct))
        {
            Assert.Equal(HttpStatusCode.ServiceUnavailable, first.StatusCode);
        }

        harness.ServiceStatus.SetWorkerRunning(true);
        harness.ServiceStatus.SetSweepRunning(false);
        using (var second = await harness.Client.GetAsync("/readyz", ct))
        {
            Assert.Equal(HttpStatusCode.ServiceUnavailable, second.StatusCode);
        }

        var warnings = harness.LogCapture.Snapshot()
            .Where(static entry =>
                entry.Level == LogLevel.Warning &&
                entry.FormattedMessage.Contains("Mailer readiness not ready", StringComparison.Ordinal))
            .ToList();
        Assert.Equal(2, warnings.Count);
        Assert.Equal(MailerReadinessReasons.WorkerNotRunning, warnings[0].State["Reason"]);
        Assert.Equal(MailerReadinessReasons.SweepNotRunning, warnings[1].State["Reason"]);
        ReadyzAssertionHelpers.AssertPrimaryReason(harness, MailerReadinessReasons.SweepNotRunning);
    }

    [Fact]
    public async Task Readyz_when_worker_disabled_skips_worker_sweep_and_heartbeat_checks()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var harness = await ReadyzObservabilityHarness.CreateAsync(
            ct,
            workerEnabled: false);

        harness.ServiceStatus.SetWorkerRunning(false);
        harness.ServiceStatus.SetSweepRunning(false);

        using var response = await harness.Client.GetAsync("/readyz", ct);
        var body = await response.Content.ReadAsStringAsync(ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        ReadyzAssertionHelpers.AssertReadyBody(body, ready: true);
        ReadyzAssertionHelpers.AssertReadyMetrics(harness);
        Assert.DoesNotContain(
            harness.LogCapture.Snapshot(),
            static entry => entry.FormattedMessage.Contains("Mailer readiness not ready", StringComparison.Ordinal));
    }

    [Fact]
    public void WorkerHeartbeatFreshness_distinguishes_missing_and_stale()
    {
        var now = new DateTimeOffset(2026, 7, 24, 12, 0, 0, TimeSpan.Zero);
        var maxStaleness = TimeSpan.FromSeconds(300);

        Assert.Equal(
            MailerReadinessReasons.HeartbeatMissing,
            WorkerHeartbeatFreshness.GetFailureReason([], maxStaleness, now));
        Assert.Equal(
            MailerReadinessReasons.HeartbeatMissing,
            WorkerHeartbeatFreshness.GetFailureReason(
                [new WorkerHeartbeat("worker", now)],
                maxStaleness,
                now));
        Assert.Equal(
            MailerReadinessReasons.HeartbeatStale,
            WorkerHeartbeatFreshness.GetFailureReason(
                [
                    new WorkerHeartbeat("worker", now.AddMinutes(-10)),
                    new WorkerHeartbeat("sweep", now),
                ],
                maxStaleness,
                now));
        Assert.Null(
            WorkerHeartbeatFreshness.GetFailureReason(
                [
                    new WorkerHeartbeat("worker", now.AddSeconds(-30)),
                    new WorkerHeartbeat("sweep", now.AddSeconds(-45)),
                ],
                maxStaleness,
                now));
    }
}
