using Azure;
using Amane.Mailer.Configuration;
using Amane.Mailer.Contracts.MailRequests;
using AcsEmailAddress = Azure.Communication.Email.EmailAddress;
using AcsEmailAttachment = Azure.Communication.Email.EmailAttachment;
using AcsEmailClient = Azure.Communication.Email.EmailClient;
using AcsEmailContent = Azure.Communication.Email.EmailContent;
using AcsEmailMessage = Azure.Communication.Email.EmailMessage;
using AcsEmailRecipients = Azure.Communication.Email.EmailRecipients;
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

            // Global provider order To -> Cc -> Bcc (ADR 0023 D-01). ACS's EmailRecipients keeps
            // To/Cc/Bcc as distinct lists -- there is no shared header to leak Bcc into, unlike
            // SMTP; ACS never sees a merged recipient list.
            var recipients = new AcsEmailRecipients(
                to: job.To.Select(ToAcsAddress),
                cc: job.Cc.Count > 0 ? job.Cc.Select(ToAcsAddress) : null,
                bcc: job.Bcc.Count > 0 ? job.Bcc.Select(ToAcsAddress) : null);

            var message = new AcsEmailMessage(
                senderAddress: tenant.DefaultFrom.Email,
                recipients: recipients,
                content: content);

            if (!string.IsNullOrWhiteSpace(job.ReplyTo))
            {
                message.ReplyTo.Add(new AcsEmailAddress(job.ReplyTo));
            }

            if (job.Attachments is { Count: > 0 })
            {
                foreach (var attachment in job.Attachments)
                {
                    byte[] bytes;
                    try
                    {
                        bytes = await File.ReadAllBytesAsync(attachment.FilePath, cancellationToken);
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    {
                        // A file-not-found/access exception message embeds the private spool
                        // path (ADR 0022 D-08/D-14); never let it reach the generic sanitizer
                        // below, which only masks credentials/URLs/tokens/emails, not paths.
                        return MailDeliveryResult.Failure(
                            MailDeliveryErrorCodes.AttachmentStorageMissing,
                            "Attachment spool file could not be read.",
                            retryable: false);
                    }

                    message.Attachments.Add(new AcsEmailAttachment(
                        attachment.FileName,
                        attachment.ContentType,
                        BinaryData.FromBytes(bytes)));
                }
            }

            var operationId = AcsOperationIdFactory.Create(
                tenant.TenantId,
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

    private static AcsEmailAddress ToAcsAddress(MailSendRecipient recipient) =>
        string.IsNullOrWhiteSpace(recipient.DisplayName)
            ? new AcsEmailAddress(recipient.Address)
            : new AcsEmailAddress(recipient.Address, recipient.DisplayName);
}
