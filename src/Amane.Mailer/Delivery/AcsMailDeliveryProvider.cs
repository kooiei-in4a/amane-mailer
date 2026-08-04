using Azure;
using Amane.Mailer.Configuration;
using Amane.Mailer.Contracts.MailRequests;
using AcsEmailAddress = Azure.Communication.Email.EmailAddress;
using AcsEmailAttachment = Azure.Communication.Email.EmailAttachment;
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
                MailDeliveryErrorCodes.AcsNotConfigured,
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

            if (job.Attachments is { Count: > 0 })
            {
                foreach (var attachment in job.Attachments)
                {
                    var bytes = await File.ReadAllBytesAsync(attachment.FilePath, cancellationToken);
                    message.Attachments.Add(new AcsEmailAttachment(
                        attachment.FileName,
                        attachment.ContentType,
                        BinaryData.FromBytes(bytes)));
                }
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
            return MailDeliveryResult.Failure(
                MailDeliveryErrorCodes.AcsSendFailed,
                status,
                retryable: false);
        }
        catch (RequestFailedException ex)
        {
            var retryable = ex.Status is 408 or 429 or >= 500;
            return MailDeliveryResult.Failure(
                MailDeliveryErrorCodes.AcsRequestFailed,
                ProviderErrorSanitizer.Sanitize(ex.Message),
                retryable);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            var (errorCode, retryable) = ProviderErrorClassifier.Classify(ex);
            return MailDeliveryResult.Failure(
                errorCode,
                ProviderErrorSanitizer.Sanitize(ex.Message),
                retryable);
        }
    }
}
