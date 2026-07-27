using System.Net.Sockets;
using Azure;
using Amane.Mailer.Contracts.MailRequests;
using Amane.Mailer.Delivery;

namespace Amane.Mailer.Operations.AcsTestSend;

/// <summary>
/// Maps ACS transport exceptions to canonical <see cref="AdminProviderTestAcsSendResultCodes"/>
/// without exposing provider raw text. Shared so unit tests can exercise classification without
/// constructing a live <c>EmailClient</c>.
/// </summary>
public static class AcsTestSendFailureMapper
{
    public static AcsTestSendOutcome MapRequestFailed(RequestFailedException ex)
    {
        _ = ProviderErrorSanitizer.Sanitize(ex.Message);

        if (ex.Status is 401 or 403)
        {
            return AcsTestSendOutcome.Failed(AdminProviderTestAcsSendResultCodes.FailedAcsAuthentication);
        }

        if (ex.Status is 408 or 429 or >= 500)
        {
            return AcsTestSendOutcome.Failed(
                AdminProviderTestAcsSendResultCodes.FailedAcsTimeout,
                authenticationSucceeded: true);
        }

        if ((ex.Status is 400 or 422) && LooksLikeSenderOrDomainRejection(ex))
        {
            return AcsTestSendOutcome.Failed(
                AdminProviderTestAcsSendResultCodes.FailedAcsSenderRejected,
                authenticationSucceeded: true);
        }

        return AcsTestSendOutcome.Failed(
            AdminProviderTestAcsSendResultCodes.FailedAcsSendRequest,
            authenticationSucceeded: true);
    }

    public static AcsTestSendOutcome MapException(Exception ex)
    {
        _ = ProviderErrorSanitizer.Sanitize(ex.Message);
        var (errorCode, _) = ProviderErrorClassifier.Classify(ex);

        if (string.Equals(errorCode, MailDeliveryErrorCodes.ProviderAuth, StringComparison.Ordinal))
        {
            return AcsTestSendOutcome.Failed(AdminProviderTestAcsSendResultCodes.FailedAcsAuthentication);
        }

        if (string.Equals(errorCode, MailDeliveryErrorCodes.ProviderNetwork, StringComparison.Ordinal)
            || ex is SocketException or IOException or HttpRequestException)
        {
            return AcsTestSendOutcome.Failed(AdminProviderTestAcsSendResultCodes.FailedAcsNetwork);
        }

        if (string.Equals(errorCode, MailDeliveryErrorCodes.ProviderTimeout, StringComparison.Ordinal)
            || ex is TimeoutException)
        {
            return AcsTestSendOutcome.Failed(
                AdminProviderTestAcsSendResultCodes.FailedAcsTimeout,
                authenticationSucceeded: true);
        }

        return AcsTestSendOutcome.Failed(AdminProviderTestAcsSendResultCodes.FailedAcsSendRequest);
    }

    /// <summary>
    /// Only classify as sender/domain rejection when ACS returns a structured error code that
    /// clearly names sender or domain. Generic 400/404/422 stay as send-request failures.
    /// </summary>
    internal static bool LooksLikeSenderOrDomainRejection(RequestFailedException ex)
    {
        var code = ex.ErrorCode;
        if (string.IsNullOrWhiteSpace(code))
        {
            return false;
        }

        return code.Contains("Sender", StringComparison.OrdinalIgnoreCase)
            || code.Contains("Domain", StringComparison.OrdinalIgnoreCase)
            || code.Contains("FromAddress", StringComparison.OrdinalIgnoreCase)
            || code.Contains("MailFrom", StringComparison.OrdinalIgnoreCase);
    }
}
