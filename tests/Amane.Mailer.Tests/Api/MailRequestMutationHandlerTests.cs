using System.Text.Json;
using Amane.Mailer.Api;
using Amane.Mailer.Contracts.MailRequests;
using Amane.Mailer.Data.Sqlite;
using Amane.Mailer.Data.Sqlite.Models;
using Amane.Mailer.Queue;
using Microsoft.AspNetCore.Http;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;

namespace Amane.Mailer.Tests.Api;

public sealed class MailRequestMutationHandlerTests
{
    [Fact]
    public async Task CancelAsync_returns_404_when_not_found()
    {
        var repository = new StubMailRequestRepository
        {
            CancelResult = new ConsumerMailRequestMutationResult(ManualMailRequestMutationStatus.NotFound),
        };

        var result = await MailRequestMutationHandler.CancelAsync(
            Guid.NewGuid(),
            "example-service",
            Guid.NewGuid(),
            repository,
            deliveryEventEnqueuer: null!,
            TimeProvider.System,
            CancellationToken.None);

        var (statusCode, body) = MailRequestHttpResultAssertions.Inspect(result);
        Assert.Equal(StatusCodes.Status404NotFound, statusCode);
        Assert.Contains(MailerErrorCodes.NotFound, body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CancelAsync_returns_422_for_invalid_state()
    {
        var repository = new StubMailRequestRepository
        {
            CancelResult = new ConsumerMailRequestMutationResult(ManualMailRequestMutationStatus.InvalidState),
        };

        var result = await MailRequestMutationHandler.CancelAsync(
            Guid.NewGuid(),
            "example-service",
            Guid.NewGuid(),
            repository,
            deliveryEventEnqueuer: null!,
            TimeProvider.System,
            CancellationToken.None);

        var (statusCode, body) = MailRequestHttpResultAssertions.Inspect(result);
        Assert.Equal(StatusCodes.Status422UnprocessableEntity, statusCode);
        Assert.Contains(MailerErrorCodes.InvalidState, body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CancelAsync_returns_status_snapshot_without_requiring_enqueue_when_internal_id_missing()
    {
        var mailRequestId = Guid.NewGuid();
        var snapshot = new MailRequestStatusRow(
            MailRequestId: mailRequestId,
            Status: MailRequestState.Cancelled,
            AttemptCount: 0,
            MaxAttempts: 3,
            NextAttemptAt: null,
            ScheduledAt: null,
            AcceptedAt: DateTimeOffset.UtcNow,
            DeliveredAt: null,
            LastErrorCode: null);
        var repository = new StubMailRequestRepository
        {
            CancelResult = new ConsumerMailRequestMutationResult(
                ManualMailRequestMutationStatus.Succeeded,
                InternalRequestId: null,
                StatusSnapshot: snapshot),
        };

        var result = await MailRequestMutationHandler.CancelAsync(
            Guid.NewGuid(),
            "example-service",
            mailRequestId,
            repository,
            deliveryEventEnqueuer: null!,
            TimeProvider.System,
            CancellationToken.None);

        var (statusCode, _) = MailRequestHttpResultAssertions.Inspect(result);
        var response = MailRequestHttpResultAssertions.Value<MailRequestStatusResponse>(result);
        Assert.Equal(StatusCodes.Status200OK, statusCode);
        Assert.Equal(MailRequestStatus.Cancelled, response.Status);
    }

    [Fact]
    public async Task CancelAsync_maps_storage_full()
    {
        var repository = new StubMailRequestRepository
        {
            CancelException = new SqliteException(
                "database or disk is full",
                SqliteDatabaseExceptionClassifier.SqliteFull),
        };

        var result = await MailRequestMutationHandler.CancelAsync(
            Guid.NewGuid(),
            "example-service",
            Guid.NewGuid(),
            repository,
            deliveryEventEnqueuer: null!,
            TimeProvider.System,
            CancellationToken.None);

        var (statusCode, body) = MailRequestHttpResultAssertions.Inspect(result);
        Assert.Equal(StatusCodes.Status503ServiceUnavailable, statusCode);
        Assert.Contains(MailerErrorCodes.StorageFull, body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RescheduleAsync_returns_422_for_scheduled_at_in_past()
    {
        var result = await MailRequestMutationHandler.RescheduleAsync(
            Guid.NewGuid(),
            "example-service",
            Guid.NewGuid(),
            new MailRequestRescheduleRequest
            {
                ScheduledAt = DateTimeOffset.UtcNow.AddMinutes(-1),
            },
            new StubMailRequestRepository(),
            new MailRequestQueue(),
            TimeProvider.System,
            NullLoggerFactory.Instance,
            CancellationToken.None);

        var (statusCode, body) = MailRequestHttpResultAssertions.Inspect(result);
        Assert.Equal(StatusCodes.Status422UnprocessableEntity, statusCode);
        Assert.Contains(MailerErrorCodes.ScheduledAtInPast, body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RescheduleAsync_returns_status_snapshot_on_success()
    {
        var mailRequestId = Guid.NewGuid();
        var scheduledAt = DateTimeOffset.UtcNow.AddHours(1);
        var snapshot = new MailRequestStatusRow(
            MailRequestId: mailRequestId,
            Status: MailRequestState.Queued,
            AttemptCount: 0,
            MaxAttempts: 3,
            NextAttemptAt: null,
            ScheduledAt: scheduledAt,
            AcceptedAt: DateTimeOffset.UtcNow,
            DeliveredAt: null,
            LastErrorCode: null);
        var repository = new StubMailRequestRepository
        {
            RescheduleResult = new ConsumerMailRequestMutationResult(
                ManualMailRequestMutationStatus.Succeeded,
                StatusSnapshot: snapshot),
        };

        var result = await MailRequestMutationHandler.RescheduleAsync(
            Guid.NewGuid(),
            "example-service",
            mailRequestId,
            new MailRequestRescheduleRequest { ScheduledAt = scheduledAt },
            repository,
            new MailRequestQueue(),
            TimeProvider.System,
            NullLoggerFactory.Instance,
            CancellationToken.None);

        var (statusCode, _) = MailRequestHttpResultAssertions.Inspect(result);
        var response = MailRequestHttpResultAssertions.Value<MailRequestStatusResponse>(result);
        Assert.Equal(StatusCodes.Status200OK, statusCode);
        Assert.Equal(MailRequestStatus.Queued, response.Status);
    }

    private sealed class StubMailRequestRepository : MailRequestRepository
    {
        public StubMailRequestRepository()
            : base(
                claimStore: null!,
                acceptStore: null!,
                consumerMutations: null!,
                adminQueries: null!,
                heartbeatStore: null!,
                attachmentStore: null!,
                attachmentSubmissionStore: null!)
        {
        }

        public ConsumerMailRequestMutationResult CancelResult { get; init; } =
            new(ManualMailRequestMutationStatus.NotFound);

        public ConsumerMailRequestMutationResult RescheduleResult { get; init; } =
            new(ManualMailRequestMutationStatus.NotFound);

        public Exception? CancelException { get; init; }

        public override Task<ConsumerMailRequestMutationResult> TryConsumerCancelAsync(
            Guid tenantId,
            string sourceService,
            Guid mailRequestId,
            DateTimeOffset now,
            CancellationToken cancellationToken = default)
        {
            if (CancelException is not null)
            {
                throw CancelException;
            }

            return Task.FromResult(CancelResult);
        }

        public override Task<ConsumerMailRequestMutationResult> TryRescheduleAsync(
            Guid tenantId,
            string sourceService,
            Guid mailRequestId,
            DateTimeOffset? scheduledAt,
            DateTimeOffset now,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(RescheduleResult);
    }
}
