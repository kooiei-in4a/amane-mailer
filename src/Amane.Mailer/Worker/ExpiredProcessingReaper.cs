using Amane.Mailer.Configuration;
using Amane.Mailer.Data.Sqlite;
using Amane.Mailer.Data.Sqlite.Models;
using Amane.Mailer.Webhooks;

namespace Amane.Mailer.Worker;

public sealed class ExpiredProcessingReaper(
    MailRequestRepository repository,
    MailerWorkerOptions workerOptions,
    DeliveryEventEnqueuer deliveryEventEnqueuer,
    ILogger<ExpiredProcessingReaper> logger)
{
    public async Task DeadLetterExpiredProcessingAtMaxAttemptsAsync(
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            var deadLettered = await repository.DeadLetterExpiredProcessingAtMaxAttemptsAsync(
                now,
                workerOptions.BatchClaimSize,
                cancellationToken);

            foreach (var request in deadLettered)
            {
                LogExpiredProcessingDeadLetter(request);
                await deliveryEventEnqueuer.TryEnqueueForInternalRequestAsync(request.Id, cancellationToken);
            }

            if (deadLettered.Count < workerOptions.BatchClaimSize)
            {
                return;
            }
        }
    }

    private void LogExpiredProcessingDeadLetter(ExpiredProcessingDeadLetteredRequest request)
    {
        logger.LogError(
            "Mail request {MailRequestId} was dead-lettered after its processing lease expired at attempt {AttemptNumber}. ErrorCode={ErrorCode}; ErrorMessage={ErrorMessage}",
            request.MailRequestId,
            request.AttemptNumber,
            request.ErrorCode,
            request.ErrorMessage);
    }
}
