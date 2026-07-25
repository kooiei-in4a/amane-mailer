using Amane.Mailer.Contracts.MailRequests;
using Amane.Mailer.Data.Sqlite;
using Amane.Mailer.Queue;
using Amane.Mailer.Webhooks;

namespace Amane.Mailer.Api;

/// <summary>
/// Cancel / reschedule mail-request use-cases. Post-commit side effects must not turn
/// a committed mutation into an HTTP failure (#269, #327).
/// </summary>
public static class MailRequestMutationHandler
{
    public static async Task<IResult> CancelAsync(
        Guid tenantId,
        string sourceService,
        Guid mailRequestId,
        MailRequestRepository repository,
        DeliveryEventEnqueuer deliveryEventEnqueuer,
        TimeProvider timeProvider,
        ILoggerFactory loggerFactory,
        CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        ConsumerMailRequestMutationResult result;
        try
        {
            result = await repository.TryConsumerCancelAsync(
                tenantId,
                sourceService,
                mailRequestId,
                now,
                cancellationToken);
        }
        catch (Exception ex) when (MailRequestHttpErrorMapper.IsStorageFullDatabaseException(ex))
        {
            return MailRequestHttpErrorMapper.StorageFull();
        }
        catch (Exception ex) when (MailRequestHttpErrorMapper.IsTransientDatabaseException(ex))
        {
            return MailRequestHttpErrorMapper.ServiceUnavailable();
        }

        if (result.Status == ManualMailRequestMutationStatus.NotFound)
        {
            return MailRequestHttpErrorMapper.Error(
                StatusCodes.Status404NotFound,
                MailerErrorCodes.NotFound);
        }

        if (result.Status != ManualMailRequestMutationStatus.Succeeded)
        {
            return MailRequestHttpErrorMapper.Error(
                StatusCodes.Status422UnprocessableEntity,
                MailerErrorCodes.InvalidState);
        }

        // Cancel is already committed. Webhook enqueue is best-effort; reconcile covers gaps (#269).
        if (result.InternalRequestId is Guid internalId)
        {
            try
            {
                await deliveryEventEnqueuer.TryEnqueueForInternalRequestAsync(internalId, cancellationToken);
            }
            catch (Exception ex)
            {
                var logger = loggerFactory.CreateLogger("MailRequestEndpoints");
                logger.LogWarning(
                    ex,
                    "Failed to enqueue cancel delivery event for mail request {MailRequestId}.",
                    mailRequestId);
            }
        }

        // Prefer the committed snapshot over a post-commit re-read so non-transient re-read
        // faults cannot turn a successful cancel into HTTP 5xx (#269).
        return MailRequestHttpErrorMapper.StatusOk(result.StatusSnapshot!);
    }

    public static async Task<IResult> RescheduleAsync(
        Guid tenantId,
        string sourceService,
        Guid mailRequestId,
        MailRequestRescheduleRequest request,
        MailRequestRepository repository,
        IMailRequestQueue queue,
        TimeProvider timeProvider,
        ILoggerFactory loggerFactory,
        CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        var scheduleError = MailRequestScheduleValidator.ValidateScheduledAt(request.ScheduledAt, now);
        if (scheduleError is not null)
        {
            return scheduleError;
        }

        var scheduledAtUtc = request.ScheduledAt?.ToUniversalTime();
        ConsumerMailRequestMutationResult result;
        try
        {
            result = await repository.TryRescheduleAsync(
                tenantId,
                sourceService,
                mailRequestId,
                scheduledAtUtc,
                now,
                cancellationToken);
        }
        catch (Exception ex) when (MailRequestHttpErrorMapper.IsStorageFullDatabaseException(ex))
        {
            return MailRequestHttpErrorMapper.StorageFull();
        }
        catch (Exception ex) when (MailRequestHttpErrorMapper.IsTransientDatabaseException(ex))
        {
            return MailRequestHttpErrorMapper.ServiceUnavailable();
        }

        if (result.Status == ManualMailRequestMutationStatus.NotFound)
        {
            return MailRequestHttpErrorMapper.Error(
                StatusCodes.Status404NotFound,
                MailerErrorCodes.NotFound);
        }

        if (result.Status != ManualMailRequestMutationStatus.Succeeded)
        {
            return MailRequestHttpErrorMapper.Error(
                StatusCodes.Status422UnprocessableEntity,
                MailerErrorCodes.InvalidState);
        }

        if (MailRequestCreateHandler.IsImmediatelyDispatchable(scheduledAtUtc, now))
        {
            var logger = loggerFactory.CreateLogger("MailRequestEndpoints");
            if (!queue.TrySignalWorkAvailable())
            {
                logger.LogWarning(
                    "WorkAvailable channel is full after reschedule for request {MailRequestId}.",
                    mailRequestId);
            }
        }

        // Prefer the committed snapshot over a post-commit re-read so non-transient re-read
        // faults cannot turn a successful reschedule into HTTP 5xx (#327).
        return MailRequestHttpErrorMapper.StatusOk(result.StatusSnapshot!);
    }
}
