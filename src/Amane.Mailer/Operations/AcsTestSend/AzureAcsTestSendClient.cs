using Azure;
using Amane.Mailer.Delivery;

namespace Amane.Mailer.Operations.AcsTestSend;

/// <summary>
/// ACS Email send wrapper for standalone Staging verification. Does not touch DB, tenant JSON,
/// or platform-sender files. Provider raw exception text is never returned; only canonical
/// failure codes are exposed on <see cref="AcsTestSendOutcome"/>.
/// </summary>
public sealed class AzureAcsTestSendClient(IAcsEmailSendTransport? transport = null) : IAcsTestSendClient
{
    private readonly IAcsEmailSendTransport _transport = transport ?? new AzureAcsEmailSendTransport();

    public async Task<AcsTestSendOutcome> SendAsync(
        AcsTestSendRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        try
        {
            var result = await _transport.SendAndWaitAsync(request, cancellationToken);
            if (result.Succeeded)
            {
                return AcsTestSendOutcome.Succeeded(result.OperationId);
            }

            return AcsTestSendOutcome.Failed(
                AdminProviderTestAcsSendResultCodes.FailedAcsOperation,
                authenticationState: AcsEvaluationState.Succeeded,
                sendRequestAccepted: true,
                providerMessageId: result.OperationId);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (RequestFailedException ex)
        {
            return AcsTestSendFailureMapper.MapRequestFailed(ex);
        }
        catch (OperationCanceledException)
        {
            return AcsTestSendOutcome.Failed(
                AdminProviderTestAcsSendResultCodes.FailedAcsTimeout,
                authenticationState: AcsEvaluationState.Succeeded);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Invalid connection-string construction and other pre-send faults land here.
            _ = ProviderErrorSanitizer.Sanitize(ex.Message);
            return AcsTestSendFailureMapper.MapException(ex);
        }
    }
}
