using Azure;
using Amane.Mailer.Configuration;
using AcsEmailAddress = Azure.Communication.Email.EmailAddress;
using AcsEmailClient = Azure.Communication.Email.EmailClient;
using AcsEmailContent = Azure.Communication.Email.EmailContent;
using AcsEmailMessage = Azure.Communication.Email.EmailMessage;
using AcsEmailSendStatus = Azure.Communication.Email.EmailSendStatus;

namespace Amane.Mailer.Delivery;

public sealed class AcsMailDeliveryProvider(MailerOptions options)
{
    private readonly Lazy<AcsEmailClient> _client = new(
        () => new AcsEmailClient(options.AcsConnectionString),
        LazyThreadSafetyMode.ExecutionAndPublication);

    public async Task<MailDeliveryResult> SendAsync(
        MailSendJob job,
        MailerTenant tenant,
        string provider,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(options.AcsConnectionString))
        {
            return MailDeliveryResult.Failure(
                "ACS_NOT_CONFIGURED",
                "ACS_CONNECTION_STRING is required when MAILER_PROVIDER=acs.",
                retryable: false);
        }

        try
        {
            var content = new AcsEmailContent(job.Subject)
            {
                PlainText = job.TextBody,
                Html = job.HtmlBody,
            };

            var message = new AcsEmailMessage(
                senderAddress: tenant.DefaultFrom.Email,
                recipientAddress: job.RecipientEmail,
                content: content);

            if (!string.IsNullOrWhiteSpace(job.ReplyTo))
            {
                message.ReplyTo.Add(new AcsEmailAddress(job.ReplyTo));
            }

            var operationId = AcsOperationIdFactory.Create(
                tenant.TenantId,
                job.SourceService,
                job.MailRequestId);

            var operation = await _client.Value.SendAsync(
                WaitUntil.Completed,
                message,
                operationId,
                cancellationToken);

            if (operation.HasValue && operation.Value.Status == AcsEmailSendStatus.Succeeded)
            {
                return MailDeliveryResult.Success(operation.Id);
            }

            var status = operation.HasValue ? operation.Value.Status.ToString() : "Unknown";
            return MailDeliveryResult.Failure("ACS_SEND_FAILED", status, retryable: false);
        }
        catch (RequestFailedException ex)
        {
            var retryable = ex.Status is 408 or 429 or >= 500;
            return MailDeliveryResult.Failure("ACS_REQUEST_FAILED", ex.Message, retryable);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return MailDeliveryResult.Failure(ex.GetType().Name, ex.Message, retryable: true);
        }
    }
}
