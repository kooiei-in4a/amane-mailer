using System.Net.Sockets;
using Amane.Mailer.Contracts.MailRequests;
using MailKit;
using MailKit.Net.Smtp;
using MailKit.Security;

namespace Amane.Mailer.Delivery;

/// <summary>
/// Maps provider exceptions to stable <see cref="MailDeliveryErrorCodes"/> buckets
/// so persisted <c>error_code</c> values do not depend on library exception type names.
/// </summary>
public static class ProviderErrorClassifier
{
    public static (string ErrorCode, bool Retryable) Classify(Exception exception)
    {
        for (var current = exception; current is not null; current = current.InnerException)
        {
            var mapped = TryMap(current);
            if (mapped is not null)
            {
                return mapped.Value;
            }
        }

        return (MailDeliveryErrorCodes.ProviderUnknown, false);
    }

    private static (string ErrorCode, bool Retryable)? TryMap(Exception exception) =>
        exception switch
        {
            TimeoutException => (MailDeliveryErrorCodes.ProviderTimeout, true),
            TaskCanceledException => (MailDeliveryErrorCodes.ProviderTimeout, true),
            SocketException => (MailDeliveryErrorCodes.ProviderNetwork, true),
            IOException => (MailDeliveryErrorCodes.ProviderNetwork, true),
            HttpRequestException => (MailDeliveryErrorCodes.ProviderNetwork, true),
            ServiceNotConnectedException => (MailDeliveryErrorCodes.ProviderNetwork, true),
            SmtpCommandException => (MailDeliveryErrorCodes.ProviderProtocol, true),
            SmtpProtocolException => (MailDeliveryErrorCodes.ProviderProtocol, true),
            ProtocolException => (MailDeliveryErrorCodes.ProviderProtocol, true),
            CommandException => (MailDeliveryErrorCodes.ProviderProtocol, true),
            SslHandshakeException => (MailDeliveryErrorCodes.ProviderAuth, false),
            ServiceNotAuthenticatedException => (MailDeliveryErrorCodes.ProviderAuth, false),
            AuthenticationException => (MailDeliveryErrorCodes.ProviderAuth, false),
            System.Security.Authentication.AuthenticationException =>
                (MailDeliveryErrorCodes.ProviderAuth, false),
            _ => null,
        };
}
