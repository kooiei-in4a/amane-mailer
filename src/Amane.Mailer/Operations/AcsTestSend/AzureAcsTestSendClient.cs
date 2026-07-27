using Azure;
using Amane.Mailer.Contracts.MailRequests;
using Amane.Mailer.Delivery;
using AcsEmailClient = Azure.Communication.Email.EmailClient;
using AcsEmailContent = Azure.Communication.Email.EmailContent;
using AcsEmailMessage = Azure.Communication.Email.EmailMessage;
using AcsEmailSendStatus = Azure.Communication.Email.EmailSendStatus;

namespace Amane.Mailer.Operations.AcsTestSend;

/// <summary>
/// Real ACS EmailClient wrapper for standalone Staging verification. Does not touch DB, tenant
/// JSON, or platform-sender files. Provider raw exception text is never returned; only canonical
/// failure codes are exposed on <see cref="AcsTestSendOutcome"/>. Optional display names are
/// accepted by the CLI for operator workflow parity but are not required on the ACS wire path.
/// </summary>
public sealed class AzureAcsTestSendClient : IAcsTestSendClient
{
    public async Task<AcsTestSendOutcome> SendAsync(
        AcsTestSendRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        AcsEmailClient client;
        try
        {
            client = new AcsEmailClient(request.ConnectionString);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Invalid connection-string shape can throw at construction; treat as auth/config.
            _ = ProviderErrorSanitizer.Sanitize(ex.Message);
            return AcsTestSendOutcome.Failed(AdminProviderTestAcsSendResultCodes.FailedAcsAuthentication);
        }

        try
        {
            var content = new AcsEmailContent(request.Subject)
            {
                PlainText = request.PlainTextBody,
            };

            var message = new AcsEmailMessage(
                senderAddress: request.SenderEmail,
                recipientAddress: request.RecipientEmail,
                content: content);

            // WaitUntil.Started: request accepted (auth + ingress). Then wait for LRO completion.
            var operation = await client.SendAsync(
                WaitUntil.Started,
                message,
                request.OperationId,
                cancellationToken);

            var providerMessageId = operation.Id;

            await operation.WaitForCompletionAsync(cancellationToken);

            if (operation.HasValue && operation.Value.Status == AcsEmailSendStatus.Succeeded)
            {
                return AcsTestSendOutcome.Succeeded(providerMessageId);
            }

            return AcsTestSendOutcome.Failed(
                AdminProviderTestAcsSendResultCodes.FailedAcsOperation,
                authenticationSucceeded: true,
                sendRequestAccepted: true,
                providerMessageId: providerMessageId);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (RequestFailedException ex)
        {
            _ = ProviderErrorSanitizer.Sanitize(ex.Message);
            return MapRequestFailed(ex);
        }
        catch (TimeoutException)
        {
            return AcsTestSendOutcome.Failed(
                AdminProviderTestAcsSendResultCodes.FailedAcsTimeout,
                authenticationSucceeded: true);
        }
        catch (OperationCanceledException)
        {
            return AcsTestSendOutcome.Failed(
                AdminProviderTestAcsSendResultCodes.FailedAcsTimeout,
                authenticationSucceeded: true);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _ = ProviderErrorSanitizer.Sanitize(ex.Message);
            var (errorCode, _) = ProviderErrorClassifier.Classify(ex);
            if (string.Equals(errorCode, MailDeliveryErrorCodes.ProviderAuth, StringComparison.Ordinal)
                || string.Equals(errorCode, MailDeliveryErrorCodes.ProviderNetwork, StringComparison.Ordinal))
            {
                return AcsTestSendOutcome.Failed(AdminProviderTestAcsSendResultCodes.FailedAcsAuthentication);
            }

            if (string.Equals(errorCode, MailDeliveryErrorCodes.ProviderTimeout, StringComparison.Ordinal))
            {
                return AcsTestSendOutcome.Failed(
                    AdminProviderTestAcsSendResultCodes.FailedAcsTimeout,
                    authenticationSucceeded: true);
            }

            return AcsTestSendOutcome.Failed(AdminProviderTestAcsSendResultCodes.FailedAcsSendRequest);
        }
    }

    private static AcsTestSendOutcome MapRequestFailed(RequestFailedException ex)
    {
        // Scrub any provider text before branching so accidental logging cannot retain raw content.
        _ = ProviderErrorSanitizer.Sanitize(ex.Message);

        if (ex.Status is 401 or 403)
        {
            return AcsTestSendOutcome.Failed(AdminProviderTestAcsSendResultCodes.FailedAcsAuthentication);
        }

        if (ex.Status is 400 or 404 or 422)
        {
            return AcsTestSendOutcome.Failed(
                AdminProviderTestAcsSendResultCodes.FailedAcsSenderRejected,
                authenticationSucceeded: true);
        }

        if (ex.Status is 408 or 429 or >= 500)
        {
            return AcsTestSendOutcome.Failed(
                AdminProviderTestAcsSendResultCodes.FailedAcsTimeout,
                authenticationSucceeded: true);
        }

        return AcsTestSendOutcome.Failed(
            AdminProviderTestAcsSendResultCodes.FailedAcsSendRequest,
            authenticationSucceeded: true);
    }
}
