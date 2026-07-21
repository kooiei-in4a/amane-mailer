using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Amane.Mailer.Webhooks;

namespace Amane.Mailer.Tests;

public sealed class WebhookTlsPinningTests
{
    [Fact]
    public async Task ConnectAsync_dials_pinned_ip_without_resolving_hostname()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;

        using var cancellationTokenSource = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var cancellationToken = cancellationTokenSource.Token;
        var serverTask = ServeSingleHttpRequestAsync(listener, cancellationToken);

        using var handler = new SocketsHttpHandler
        {
            ConnectCallback = WebhookConnectCallback.ConnectAsync,
        };

        using var client = new HttpClient(handler)
        {
            Timeout = TimeSpan.FromSeconds(5),
        };

        using var request = new HttpRequestMessage(
            HttpMethod.Get,
            $"http://webhook-consumer.example.internal:{port}/webhook");
        request.Options.Set(WebhookConnectionOptions.PinnedConnectAddress, IPAddress.Loopback);

        using var response = await client.SendAsync(request, cancellationToken);
        await serverTask;

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Tls_validation_uses_original_hostname_when_tcp_connects_to_pinned_ip()
    {
        const string host = "webhook-consumer.example.internal";
        using var certificate = CreateSelfSignedCertificate(host);
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;

        using var cancellationTokenSource = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var cancellationToken = cancellationTokenSource.Token;
        var acceptTask = listener.AcceptTcpClientAsync(cancellationToken);

        using var tcpClient = new TcpClient();
        await tcpClient.ConnectAsync(IPAddress.Loopback, port, cancellationToken);

        using var serverTcpClient = await acceptTask;
        var serverTask = CompleteTlsServerExchangeAsync(serverTcpClient, certificate, cancellationToken);

        SslPolicyErrors capturedErrors = SslPolicyErrors.None;
        using var networkStream = tcpClient.GetStream();
        using var sslStream = new SslStream(
            networkStream,
            leaveInnerStreamOpen: false,
            (_, _, _, errors) =>
            {
                capturedErrors = errors;
                return (errors & ~SslPolicyErrors.RemoteCertificateChainErrors) == SslPolicyErrors.None;
            });

        var clientTask = sslStream.AuthenticateAsClientAsync(
            new SslClientAuthenticationOptions
            {
                TargetHost = host,
                EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
            },
            cancellationToken);

        await Task.WhenAll(serverTask, clientTask);

        Assert.False(
            capturedErrors.HasFlag(SslPolicyErrors.RemoteCertificateNameMismatch),
            $"Expected certificate validation against {host}, but got {capturedErrors}.");
    }

    private static X509Certificate2 CreateSelfSignedCertificate(string dnsName)
    {
        using var key = RSA.Create(2048);
        var request = new CertificateRequest(
            $"CN={dnsName}",
            key,
            HashAlgorithmName.SHA256,
            RSASignaturePadding.Pkcs1);

        request.CertificateExtensions.Add(
            new X509BasicConstraintsExtension(false, false, 0, false));
        request.CertificateExtensions.Add(
            new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment, false));
        request.CertificateExtensions.Add(
            new X509SubjectKeyIdentifierExtension(request.PublicKey, false));

        var subjectAlternativeName = new SubjectAlternativeNameBuilder();
        subjectAlternativeName.AddDnsName(dnsName);
        request.CertificateExtensions.Add(subjectAlternativeName.Build());

        using var ephemeralCertificate = request.CreateSelfSigned(
            DateTimeOffset.UtcNow.AddDays(-1),
            DateTimeOffset.UtcNow.AddDays(1));

        return X509CertificateLoader.LoadPkcs12(
            ephemeralCertificate.Export(X509ContentType.Pkcs12),
            password: null,
            X509KeyStorageFlags.UserKeySet | X509KeyStorageFlags.Exportable);
    }

    private static async Task ServeSingleHttpRequestAsync(
        TcpListener listener,
        CancellationToken cancellationToken)
    {
        using var tcpClient = await listener.AcceptTcpClientAsync(cancellationToken);
        using var stream = tcpClient.GetStream();
        var buffer = new byte[4096];
        var totalRead = 0;
        while (totalRead < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(totalRead), cancellationToken);
            if (read == 0)
            {
                break;
            }

            totalRead += read;
            if (ContainsHttpHeaderTerminator(buffer.AsSpan(0, totalRead)))
            {
                break;
            }
        }

        var response = "HTTP/1.1 200 OK\r\nContent-Length: 0\r\nConnection: close\r\n\r\n"u8.ToArray();
        await stream.WriteAsync(response, cancellationToken);
    }

    private static async Task CompleteTlsServerExchangeAsync(
        TcpClient tcpClient,
        X509Certificate2 certificate,
        CancellationToken cancellationToken)
    {
        using (tcpClient)
        {
            using var networkStream = tcpClient.GetStream();
            using var sslStream = new SslStream(networkStream, leaveInnerStreamOpen: false);
            await sslStream.AuthenticateAsServerAsync(
                new SslServerAuthenticationOptions
                {
                    ServerCertificate = certificate,
                    EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
                },
                cancellationToken);
        }
    }

    private static bool ContainsHttpHeaderTerminator(ReadOnlySpan<byte> data)
    {
        for (var index = 0; index <= data.Length - 4; index++)
        {
            if (data[index] == (byte)'\r'
                && data[index + 1] == (byte)'\n'
                && data[index + 2] == (byte)'\r'
                && data[index + 3] == (byte)'\n')
            {
                return true;
            }
        }

        return false;
    }
}
