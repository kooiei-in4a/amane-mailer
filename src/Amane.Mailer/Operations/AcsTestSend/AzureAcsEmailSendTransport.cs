using Azure;
using AcsEmailClient = Azure.Communication.Email.EmailClient;
using AcsEmailContent = Azure.Communication.Email.EmailContent;
using AcsEmailMessage = Azure.Communication.Email.EmailMessage;
using AcsEmailSendStatus = Azure.Communication.Email.EmailSendStatus;

namespace Amane.Mailer.Operations.AcsTestSend;

/// <summary>
/// Default ACS EmailClient transport for standalone Staging verification.
/// </summary>
public sealed class AzureAcsEmailSendTransport : IAcsEmailSendTransport
{
    public async Task<AcsEmailSendTransportResult> SendAndWaitAsync(
        AcsTestSendRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var client = new AcsEmailClient(request.ConnectionString);
        var content = new AcsEmailContent(request.Subject)
        {
            PlainText = request.PlainTextBody,
        };

        var message = new AcsEmailMessage(
            senderAddress: request.SenderEmail,
            recipientAddress: request.RecipientEmail,
            content: content);

        var operation = await client.SendAsync(
            WaitUntil.Started,
            message,
            request.OperationId,
            cancellationToken);

        await operation.WaitForCompletionAsync(cancellationToken);

        var succeeded = operation.HasValue && operation.Value.Status == AcsEmailSendStatus.Succeeded;
        return new AcsEmailSendTransportResult
        {
            OperationId = operation.Id,
            Succeeded = succeeded,
        };
    }
}
