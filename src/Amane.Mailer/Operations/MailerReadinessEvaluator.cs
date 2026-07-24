using Amane.Mailer.Configuration;
using Amane.Mailer.Data.Sqlite;
using Amane.Mailer.Data.Sqlite.Models;
using Amane.Mailer.Worker;
using Microsoft.Data.Sqlite;

namespace Amane.Mailer.Operations;

/// <summary>
/// Evaluates process readiness for <c>/readyz</c>, records a primary failure reason,
/// and emits transition-only logs plus readiness gauges (#330).
/// </summary>
public sealed class MailerReadinessEvaluator
{
    private readonly MailerRuntimeMetrics _metrics;
    private readonly ILogger<MailerReadinessEvaluator> _logger;
    private readonly object _gate = new();
    private string? _observedReason;
    private bool _hasObservation;

    public MailerReadinessEvaluator(
        MailerRuntimeMetrics metrics,
        ILogger<MailerReadinessEvaluator> logger)
    {
        _metrics = metrics;
        _logger = logger;
    }

    public Task<MailerReadinessResult> EvaluateAsync(
        SqlMigrationRunner migrationRunner,
        WorkerServiceStatus serviceStatus,
        MailRequestRepository repository,
        MailerHealthcheckOptions healthcheckOptions,
        bool workerEnabled,
        CancellationToken cancellationToken) =>
        EvaluateCoreAsync(
            isSchemaReadyAsync: ct => migrationRunner.IsCurrentSchemaReadyAsync(ct),
            isWorkerRunning: () => serviceStatus.IsWorkerRunning,
            isSweepRunning: () => serviceStatus.IsSweepRunning,
            getHeartbeatsAsync: ct => repository.GetHeartbeatsAsync(ct),
            maxHeartbeatStaleness: healthcheckOptions.MaxHeartbeatStaleness,
            workerEnabled,
            cancellationToken);

    /// <summary>
    /// Testable evaluation core. Keeps HTTP callers on the concrete service overload.
    /// </summary>
    internal async Task<MailerReadinessResult> EvaluateCoreAsync(
        Func<CancellationToken, Task<bool>> isSchemaReadyAsync,
        Func<bool> isWorkerRunning,
        Func<bool> isSweepRunning,
        Func<CancellationToken, Task<IReadOnlyList<WorkerHeartbeat>>> getHeartbeatsAsync,
        TimeSpan maxHeartbeatStaleness,
        bool workerEnabled,
        CancellationToken cancellationToken)
    {
        try
        {
            var schemaReady = await isSchemaReadyAsync(cancellationToken);
            if (!schemaReady)
            {
                return Observe(MailerReadinessResult.NotReady(MailerReadinessReasons.SchemaNotReady));
            }

            if (workerEnabled)
            {
                if (!isWorkerRunning())
                {
                    return Observe(MailerReadinessResult.NotReady(MailerReadinessReasons.WorkerNotRunning));
                }

                if (!isSweepRunning())
                {
                    return Observe(MailerReadinessResult.NotReady(MailerReadinessReasons.SweepNotRunning));
                }

                var heartbeats = await getHeartbeatsAsync(cancellationToken);
                var heartbeatReason = WorkerHeartbeatFreshness.GetFailureReason(
                    heartbeats,
                    maxHeartbeatStaleness);
                if (heartbeatReason is not null)
                {
                    return Observe(MailerReadinessResult.NotReady(heartbeatReason));
                }
            }

            return Observe(MailerReadinessResult.Ready());
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Probe cancellation is not an application readiness failure. Keep the
            // previous observation (logs/gauges) and still report not-ready to the
            // caller so the HTTP path stays 503-compatible with prior catch-all behavior.
            return MailerReadinessResult.NotReady(MailerReadinessReasons.UnexpectedError);
        }
        catch (Exception exception)
        {
            return Observe(MailerReadinessResult.NotReady(ClassifyException(exception)));
        }
    }

    /// <summary>
    /// Publishes readiness state to metrics and logs only on reason transitions.
    /// </summary>
    public MailerReadinessResult Observe(MailerReadinessResult result)
    {
        var nextReason = result.IsReady ? null : result.FailureReason;
        bool shouldLog;
        bool previouslyObserved;

        lock (_gate)
        {
            previouslyObserved = _hasObservation;
            var previousReason = _observedReason;
            shouldLog = !previouslyObserved || !string.Equals(previousReason, nextReason, StringComparison.Ordinal);
            _observedReason = nextReason;
            _hasObservation = true;
        }

        _metrics.SetReadiness(result.IsReady, nextReason);

        if (shouldLog)
        {
            if (result.IsReady)
            {
                if (previouslyObserved)
                {
                    _logger.LogInformation("Mailer readiness recovered.");
                }
            }
            else
            {
                _logger.LogWarning("Mailer readiness not ready. Reason={Reason}", nextReason);
            }
        }

        return result;
    }

    internal static string ClassifyException(Exception exception)
    {
        for (var current = exception; current is not null; current = current.InnerException)
        {
            if (current is SqliteException)
            {
                return MailerReadinessReasons.DatabaseError;
            }
        }

        return MailerReadinessReasons.UnexpectedError;
    }

    internal void ClearForTests()
    {
        lock (_gate)
        {
            _observedReason = null;
            _hasObservation = false;
        }

        _metrics.ClearReadinessForTests();
    }
}
