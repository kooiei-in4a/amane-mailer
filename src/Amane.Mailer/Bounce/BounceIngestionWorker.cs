using Amane.Mailer.Configuration;
using Amane.Mailer.Data;
using Amane.Mailer.Data.Sqlite;
using Amane.Mailer.Data.Sqlite.Models;
using Amane.Mailer.Operations;
using Amane.Mailer.Worker;

namespace Amane.Mailer.Bounce;

/// <summary>
/// Claims provider_event_inbox rows and correlates / records / suppresses (issue #302).
/// Per-event isolation mirrors WebhookDeliveryWorker (#389); terminal lease converge via sweep (#388).
/// </summary>
public sealed class BounceIngestionWorker(
    ProviderEventInboxRepository inboxRepository,
    BounceIngestionStore ingestionStore,
    MailerBounceIngestionOptions options,
    IBounceIngestionQueue queue,
    MailerRuntimeMetrics runtimeMetrics,
    TimeProvider timeProvider,
    ILogger<BounceIngestionWorker> logger) : BackgroundService
{
    internal const string FailureStageClaim = "claim";
    internal const string FailureStageProcess = "process";
    internal const string FailureStageFinalize = "finalize";

    internal static readonly TimeSpan IsolatedFailureBackoff = TimeSpan.FromSeconds(1);

    private readonly InflightTracker _inflightTracker = new();

    /// <summary>Optional claim/finalize override for isolation tests.</summary>
    internal IBounceIngestionWorkStore? WorkStoreOverride { get; set; }

    private IBounceIngestionWorkStore WorkStore => WorkStoreOverride ?? new RepositoryBounceIngestionWorkStore(inboxRepository);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                bool hasWork;
                try
                {
                    using var wakeTimeout = new CancellationTokenSource(ComputeIdleWakeTimeout());
                    using var linked = CancellationTokenSource.CreateLinkedTokenSource(
                        stoppingToken,
                        wakeTimeout.Token);
                    hasWork = await queue.Reader.WaitToReadAsync(linked.Token);
                }
                catch (OperationCanceledException) when (!stoppingToken.IsCancellationRequested)
                {
                    hasWork = true;
                }
                catch (OperationCanceledException)
                {
                    break;
                }

                if (!hasWork)
                {
                    break;
                }

                while (queue.Reader.TryRead(out _))
                {
                }

                while (!stoppingToken.IsCancellationRequested)
                {
                    try
                    {
                        var claimed = await TryProcessNextEventAsync(stoppingToken);
                        if (!claimed)
                        {
                            break;
                        }
                    }
                    catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                    {
                        break;
                    }
                    catch (Exception ex)
                    {
                        if (ex is not IsolatedBounceFailureException)
                        {
                            LogIsolatedFailure(ex, FailureStageClaim, row: null);
                        }

                        await DelayIsolatedFailureBackoffAsync(stoppingToken);
                        break;
                    }
                }
            }
        }
        finally
        {
            await _inflightTracker.WaitForZeroAsync(
                options.LeaseDuration,
                timeProvider,
                CancellationToken.None);
        }
    }

    internal TimeSpan ComputeIdleWakeTimeout() =>
        TimeSpan.FromSeconds(Math.Max(1, options.InitialDelaySeconds));

    /// <summary>Test hook for processing an already-claimed inbox row (isolated).</summary>
    internal Task ProcessClaimedEventForTestsAsync(
        ProviderEventInboxRow row,
        CancellationToken cancellationToken) =>
        ProcessClaimedEventIsolatedAsync(row, cancellationToken);

    private async Task<bool> TryProcessNextEventAsync(CancellationToken stoppingToken)
    {
        ProviderEventInboxRow? row;
        try
        {
            var now = timeProvider.GetUtcNow();
            row = await WorkStore.TryClaimOneAsync(now, options.LeaseDuration, stoppingToken);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            LogIsolatedFailure(ex, FailureStageClaim, row: null);
            throw new IsolatedBounceFailureException(ex);
        }

        if (row is null)
        {
            return false;
        }

        using var inflight = _inflightTracker.Enter();
        await ProcessClaimedEventIsolatedAsync(row, stoppingToken);
        return true;
    }

    private async Task ProcessClaimedEventIsolatedAsync(
        ProviderEventInboxRow row,
        CancellationToken stoppingToken)
    {
        var stage = FailureStageProcess;
        try
        {
            await ProcessClaimedEventAsync(row, stoppingToken, stageTracker: value => stage = value);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            LogIsolatedFailure(ex, stage, row);
            await DelayIsolatedFailureBackoffAsync(stoppingToken);
        }
    }

    private async Task ProcessClaimedEventAsync(
        ProviderEventInboxRow row,
        CancellationToken stoppingToken,
        Action<string> stageTracker)
    {
        var now = timeProvider.GetUtcNow();

        if (string.IsNullOrWhiteSpace(row.ProviderMessageId)
            || string.IsNullOrWhiteSpace(row.DeliveryStatus))
        {
            stageTracker(FailureStageFinalize);
            await FinalizeDiscardedAsync(
                row,
                now,
                ProviderEventInboxRepository.UnparseableEventErrorCode,
                stoppingToken);
            return;
        }

        stageTracker(FailureStageProcess);
        var result = await ingestionStore.ProcessClaimedAsync(row, now, stoppingToken);
        stageTracker(FailureStageFinalize);
        switch (result)
        {
            case RecipientFeedbackProcessResult.Processed:
                runtimeMetrics.RecordBounceEvent();
                break;
            case RecipientFeedbackProcessResult.Unmatched:
                runtimeMetrics.RecordBounceUnmatched();
                break;
            case RecipientFeedbackProcessResult.RecipientMismatch:
                runtimeMetrics.RecordBounceRecipientMismatch();
                break;
            case RecipientFeedbackProcessResult.FenceFailed:
                logger.LogWarning(
                    "Bounce inbox finalize fencing skipped for event {EventId}. Provider={Provider}; InboxId={InboxId}",
                    row.EventId,
                    row.Provider,
                    row.Id);
                break;
            case RecipientFeedbackProcessResult.Duplicate:
                break;
            default:
                throw new InvalidOperationException("Unknown recipient feedback process result.");
        }
    }

    private async Task FinalizeDiscardedAsync(
        ProviderEventInboxRow row,
        DateTimeOffset now,
        string? lastErrorCode,
        CancellationToken cancellationToken)
    {
        var finalized = await WorkStore.FinalizeAsync(
            row.Id,
            row.LockToken,
            now,
            ProviderEventInboxFinalizeOutcome.Discarded,
            nextAttemptAt: null,
            lastErrorCode,
            cancellationToken);

        if (!finalized)
        {
            logger.LogWarning(
                "Bounce inbox discard finalize fencing skipped for event {EventId}. Provider={Provider}; InboxId={InboxId}",
                row.EventId,
                row.Provider,
                row.Id);
        }
    }

    private async Task DelayIsolatedFailureBackoffAsync(CancellationToken stoppingToken)
    {
        try
        {
            await Task.Delay(IsolatedFailureBackoff, timeProvider, stoppingToken);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
    }

    private void LogIsolatedFailure(Exception ex, string stage, ProviderEventInboxRow? row)
    {
        // Never log recipient / status_message / provider raw text (ADR 0013 / #26).
        if (row is null)
        {
            logger.LogError(
                ex,
                "Bounce ingestion isolated failure at stage {FailureStage}.",
                stage);
            return;
        }

        logger.LogError(
            ex,
            "Bounce ingestion isolated failure at stage {FailureStage}. InboxId={InboxId}; Provider={Provider}; EventId={EventId}; AttemptCount={AttemptCount}",
            stage,
            row.Id,
            row.Provider,
            row.EventId,
            row.AttemptCount);
    }

    private sealed class IsolatedBounceFailureException(Exception inner) : Exception(null, inner);

    private sealed class RepositoryBounceIngestionWorkStore(ProviderEventInboxRepository repository)
        : IBounceIngestionWorkStore
    {
        public Task<ProviderEventInboxRow?> TryClaimOneAsync(
            DateTimeOffset now,
            TimeSpan leaseDuration,
            CancellationToken cancellationToken) =>
            repository.TryClaimOneAsync(now, leaseDuration, cancellationToken);

        public Task<bool> FinalizeAsync(
            Guid id,
            Guid lockToken,
            DateTimeOffset now,
            ProviderEventInboxFinalizeOutcome outcome,
            DateTimeOffset? nextAttemptAt,
            string? lastErrorCode,
            CancellationToken cancellationToken) =>
            repository.FinalizeAsync(id, lockToken, now, outcome, nextAttemptAt, lastErrorCode, cancellationToken);
    }
}

public interface IBounceIngestionWorkStore
{
    Task<ProviderEventInboxRow?> TryClaimOneAsync(
        DateTimeOffset now,
        TimeSpan leaseDuration,
        CancellationToken cancellationToken);

    Task<bool> FinalizeAsync(
        Guid id,
        Guid lockToken,
        DateTimeOffset now,
        ProviderEventInboxFinalizeOutcome outcome,
        DateTimeOffset? nextAttemptAt,
        string? lastErrorCode,
        CancellationToken cancellationToken);
}
