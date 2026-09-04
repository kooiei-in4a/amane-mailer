using Amane.Mailer.Configuration;
using Amane.Mailer.Data.Sqlite;
using Amane.Mailer.Data.Sqlite.Models;
using Amane.Mailer.Operations;
using Amane.Mailer.Queue;

namespace Amane.Mailer.Worker;

public sealed class MailRequestWorker : BackgroundService
{
    private readonly IMailRequestQueue _queue;
    private readonly MailRequestRepository _repository;
    private readonly MailerWorkerOptions _workerOptions;
    private readonly MailerHealthcheckOptions _healthcheckOptions;
    private readonly ExpiredProcessingReaper _expiredProcessingReaper;
    private readonly MailRequestDispatcher _dispatcher;
    private readonly WorkerServiceStatus _serviceStatus;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<MailRequestWorker> _logger;
    private readonly InflightTracker _inflightTracker = new();
    private readonly SemaphoreSlim _sendConcurrency;

    public MailRequestWorker(
        IMailRequestQueue queue,
        MailRequestRepository repository,
        MailerWorkerOptions workerOptions,
        MailerHealthcheckOptions healthcheckOptions,
        ExpiredProcessingReaper expiredProcessingReaper,
        MailRequestDispatcher dispatcher,
        WorkerServiceStatus serviceStatus,
        TimeProvider timeProvider,
        ILogger<MailRequestWorker> logger)
    {
        _queue = queue;
        _repository = repository;
        _workerOptions = workerOptions;
        _healthcheckOptions = healthcheckOptions;
        _expiredProcessingReaper = expiredProcessingReaper;
        _dispatcher = dispatcher;
        _serviceStatus = serviceStatus;
        _timeProvider = timeProvider;
        _logger = logger;
        _sendConcurrency = new SemaphoreSlim(_workerOptions.MaxSendConcurrency);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await WriteHeartbeatAsync(stoppingToken);
        _serviceStatus.SetWorkerRunning(true);
        try
        {
            await StartupRecoveryAsync(stoppingToken);
            await WorkLoopAsync(stoppingToken);
        }
        finally
        {
            _serviceStatus.SetWorkerRunning(false);
            await _inflightTracker.WaitForZeroAsync(
                _workerOptions.ShutdownDrainTimeout,
                _timeProvider,
                CancellationToken.None);

            if (_inflightTracker.InflightCount > 0)
            {
                _logger.LogWarning(
                    "Shutdown grace period elapsed with {InflightCount} in-flight mail deliveries still active.",
                    _inflightTracker.InflightCount);
            }
        }
    }

    private async Task StartupRecoveryAsync(CancellationToken stoppingToken)
    {
        try
        {
            var now = _timeProvider.GetUtcNow();
            await _expiredProcessingReaper.DeadLetterExpiredProcessingAtMaxAttemptsAsync(now, stoppingToken);

            if (await _repository.HasDispatchableWorkAsync(now, stoppingToken))
            {
                if (!_queue.TrySignalWorkAvailable())
                {
                    _logger.LogWarning("WorkAvailable channel is full during startup recovery.");
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            SqliteDatabaseExceptionLogging.LogError(
                _logger,
                ex,
                "Mailer worker startup recovery failed due to SQLite storage full (SQLITE_FULL).",
                "Mailer worker startup recovery failed.");
        }
    }

    private async Task WorkLoopAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            bool hasWork;
            try
            {
                using var heartbeatTimeout = new CancellationTokenSource(_healthcheckOptions.WorkerHeartbeatInterval);
                using var linked = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken, heartbeatTimeout.Token);
                hasWork = await _queue.Reader.WaitToReadAsync(linked.Token);
            }
            catch (OperationCanceledException) when (!stoppingToken.IsCancellationRequested)
            {
                await WriteHeartbeatAsync(stoppingToken);
                continue;
            }
            catch (OperationCanceledException)
            {
                break;
            }

            if (!hasWork)
                break;

            while (_queue.Reader.TryRead(out _)) { }

            await WriteHeartbeatAsync(stoppingToken);

            try
            {
                await DrainAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                SqliteDatabaseExceptionLogging.LogError(
                    _logger,
                    ex,
                    "Mailer worker drain loop failed due to SQLite storage full (SQLITE_FULL).",
                    "Mailer worker drain loop failed.");
            }
        }
    }

    private async Task DrainAsync(CancellationToken stoppingToken)
    {
    drain:
        var batch = new List<MailRequestRow>(_workerOptions.BatchClaimSize);
        var now = _timeProvider.GetUtcNow();
        await _expiredProcessingReaper.DeadLetterExpiredProcessingAtMaxAttemptsAsync(now, stoppingToken);

        for (var i = 0; i < _workerOptions.BatchClaimSize; i++)
        {
            var lockToken = Guid.CreateVersion7(now);
            var claimed = await _repository.TryClaimOneAsync(
                now,
                _workerOptions.LeaseDuration,
                lockToken,
                stoppingToken);

            if (claimed is null)
            {
                break;
            }

            batch.Add(claimed);
        }

        if (batch.Count == 0)
        {
            return;
        }

        var sendTasks = batch.Select(row => DispatchClaimedAsync(row, stoppingToken));
        await Task.WhenAll(sendTasks);

        // Do not start another claim wave after shutdown began; semaphore waiters
        // already cancelled in DispatchClaimedAsync leave Processing for lease reclaim.
        if (stoppingToken.IsCancellationRequested)
        {
            return;
        }

        if (batch.Count == _workerOptions.BatchClaimSize)
        {
            await WriteHeartbeatAsync(stoppingToken);
            goto drain;
        }
    }

    private async Task WriteHeartbeatAsync(CancellationToken cancellationToken)
    {
        try
        {
            var now = _timeProvider.GetUtcNow();
            await _repository.UpsertHeartbeatAsync("worker", now, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            SqliteDatabaseExceptionLogging.LogWarning(
                _logger,
                ex,
                "Failed to update worker heartbeat due to SQLite storage full (SQLITE_FULL).",
                "Failed to update worker heartbeat.");
        }
    }

    private async Task DispatchClaimedAsync(MailRequestRow row, CancellationToken stoppingToken)
    {
        try
        {
            // Honor stoppingToken so later waves waiting on the semaphore do not
            // start new sends after shutdown begins (#271). In-flight sends still
            // use SendTimeout (not linked to stopping) and are drained in finally.
            await _sendConcurrency.WaitAsync(stoppingToken);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Claimed but never started: leave Processing for lease reclaim.
            return;
        }

        using var inflight = _inflightTracker.Enter();
        try
        {
            await _dispatcher.DispatchAsync(row, stoppingToken);
        }
        finally
        {
            _sendConcurrency.Release();
        }
    }

    public override void Dispose()
    {
        _sendConcurrency.Dispose();
        base.Dispose();
    }
}
