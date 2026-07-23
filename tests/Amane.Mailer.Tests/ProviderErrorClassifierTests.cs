using System.Net.Sockets;
using Amane.Mailer.Contracts.MailRequests;
using Amane.Mailer.Delivery;
using MailKit;
using MailKit.Net.Smtp;
using MailKit.Security;

namespace Amane.Mailer.Tests;

public sealed class ProviderErrorClassifierTests
{
    [Theory]
    [MemberData(nameof(KnownMappings))]
    public void Classify_maps_known_exceptions_to_stable_codes(
        Exception exception,
        string expectedCode,
        bool expectedRetryable)
    {
        var (errorCode, retryable) = ProviderErrorClassifier.Classify(exception);

        Assert.Equal(expectedCode, errorCode);
        Assert.Equal(expectedRetryable, retryable);
        Assert.DoesNotContain(
            exception.GetType().Name,
            errorCode,
            StringComparison.Ordinal);
    }

    [Fact]
    public void Classify_maps_unknown_exception_to_provider_unknown_non_retryable()
    {
        var (errorCode, retryable) = ProviderErrorClassifier.Classify(
            new InvalidOperationException("unexpected provider failure"));

        Assert.Equal(MailDeliveryErrorCodes.ProviderUnknown, errorCode);
        Assert.False(retryable);
    }

    [Fact]
    public void Classify_walks_inner_exception_chain()
    {
        var wrapped = new Exception(
            "outer",
            new SocketException((int)SocketError.ConnectionRefused));

        var (errorCode, retryable) = ProviderErrorClassifier.Classify(wrapped);

        Assert.Equal(MailDeliveryErrorCodes.ProviderNetwork, errorCode);
        Assert.True(retryable);
    }

    public static TheoryData<Exception, string, bool> KnownMappings =>
        new()
        {
            {
                new TimeoutException("timed out"),
                MailDeliveryErrorCodes.ProviderTimeout,
                true
            },
            {
                new SocketException((int)SocketError.TimedOut),
                MailDeliveryErrorCodes.ProviderNetwork,
                true
            },
            {
                new IOException("connection reset"),
                MailDeliveryErrorCodes.ProviderNetwork,
                true
            },
            {
                new HttpRequestException("ACS transport failed"),
                MailDeliveryErrorCodes.ProviderNetwork,
                true
            },
            {
                new ServiceNotConnectedException("not connected"),
                MailDeliveryErrorCodes.ProviderNetwork,
                true
            },
            {
                new SmtpCommandException(
                    SmtpErrorCode.UnexpectedStatusCode,
                    SmtpStatusCode.TransactionFailed,
                    "RCPT rejected"),
                MailDeliveryErrorCodes.ProviderProtocol,
                true
            },
            {
                new SmtpProtocolException("protocol broken"),
                MailDeliveryErrorCodes.ProviderProtocol,
                true
            },
            {
                new SslHandshakeException("TLS handshake failed"),
                MailDeliveryErrorCodes.ProviderAuth,
                false
            },
            {
                new ServiceNotAuthenticatedException("auth required"),
                MailDeliveryErrorCodes.ProviderAuth,
                false
            },
            {
                new MailKit.Security.AuthenticationException("smtp auth failed"),
                MailDeliveryErrorCodes.ProviderAuth,
                false
            },
            {
                new System.Security.Authentication.AuthenticationException("system auth failed"),
                MailDeliveryErrorCodes.ProviderAuth,
                false
            },
        };
}
