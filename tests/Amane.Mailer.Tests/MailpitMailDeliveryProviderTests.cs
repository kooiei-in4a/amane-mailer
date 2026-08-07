using System.Net;
using System.Net.Sockets;
using System.Text;
using Amane.Mailer.Configuration;
using Amane.Mailer.Contracts.MailRequests;
using Amane.Mailer.Delivery;
using Amane.Mailer.Tests.Fixtures;
using MailKit.Security;
using Microsoft.Extensions.Logging.Abstractions;
using MimeKit;

namespace Amane.Mailer.Tests;

public sealed class MailpitMailDeliveryProviderTests
{
    [Fact]
    public async Task SendAsync_includes_bcc_in_smtp_envelope_but_never_in_transmitted_data()
    {
        // Real MailKitSmtpClient (production path) against a minimal in-process SMTP server, so
        // this verifies actual wire behavior rather than a stub -- see Issue #546: Bcc must reach
        // the SMTP envelope (RCPT TO) so the recipient is delivered to, but must never appear in
        // the literal transmitted DATA (the "Bcc:" header MimeMessage.WriteTo would otherwise
        // include -- see OutboundMimeMessageFactoryRecipientTests for that in-memory distinction).
        var ct = TestContext.Current.CancellationToken;
        using var server = await FakeSmtpServer.StartAsync(ct);

        var provider = new MailpitMailDeliveryProvider(new MailerOptions
        {
            MailpitSmtpHost = "127.0.0.1",
            MailpitSmtpPort = server.Port,
            MailpitUseSsl = false,
        });

        var job = new MailSendJob(
            Guid.NewGuid(),
            "example-service",
            "Subject",
            HtmlBody: null,
            TextBody: "body",
            ReplyTo: null,
            To: [new MailSendRecipient("to@example.com", null)],
            Cc: [new MailSendRecipient("cc@example.com", null)],
            Bcc: [new MailSendRecipient("bcc-secret@example.com", null)]);

        var result = await provider.SendAsync(job, CreateTenant(), "mailpit", ct);
        Assert.True(result.Succeeded);

        var session = await server.WaitForSessionAsync(ct);
        Assert.Contains(session.RcptToCommands, cmd => cmd.Contains("bcc-secret@example.com", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(session.RcptToCommands, cmd => cmd.Contains("to@example.com", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(session.RcptToCommands, cmd => cmd.Contains("cc@example.com", StringComparison.OrdinalIgnoreCase));

        Assert.DoesNotContain("bcc-secret@example.com", session.Data, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Bcc:", session.Data, StringComparison.Ordinal);
        Assert.Contains("to@example.com", session.Data, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("cc@example.com", session.Data, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task SendAsync_uses_mailkit_and_returns_retryable_failure_for_unreachable_host()
    {
        var provider = new MailpitMailDeliveryProvider(new MailerOptions
        {
            MailpitSmtpHost = "127.0.0.1",
            MailpitSmtpPort = 1,
            MailpitUseSsl = false,
        });

        var result = await provider.SendAsync(
            CreateJob(),
            CreateTenant(),
            "mailpit",
            TestContext.Current.CancellationToken);

        Assert.False(result.Succeeded);
        Assert.True(result.Retryable);
        Assert.Equal(MailDeliveryErrorCodes.ProviderNetwork, result.ErrorCode);
        Assert.False(string.IsNullOrWhiteSpace(result.ErrorMessage));
    }

    [Fact]
    public async Task SendAsync_treats_disconnect_failure_after_accepted_send_as_success()
    {
        var smtp = new ScriptedMailpitSmtpClient(
            connect: null,
            send: null,
            disconnect: new InvalidOperationException("disconnect failed after DATA"));

        var provider = new MailpitMailDeliveryProvider(
            new MailerOptions
            {
                MailpitSmtpHost = "127.0.0.1",
                MailpitSmtpPort = 1025,
                MailpitUseSsl = false,
            },
            NullLogger<MailpitMailDeliveryProvider>.Instance,
            () => smtp);

        var result = await provider.SendAsync(
            CreateJob(),
            CreateTenant(),
            "mailpit",
            TestContext.Current.CancellationToken);

        Assert.True(result.Succeeded);
        Assert.Equal(1, smtp.ConnectCount);
        Assert.Equal(1, smtp.SendCount);
        Assert.Equal(1, smtp.DisconnectCount);
    }

    [Fact]
    public async Task SendAsync_treats_disconnect_cancellation_after_accepted_send_as_success()
    {
        var smtp = new ScriptedMailpitSmtpClient(
            connect: null,
            send: null,
            disconnect: new OperationCanceledException());

        var provider = new MailpitMailDeliveryProvider(
            new MailerOptions
            {
                MailpitSmtpHost = "127.0.0.1",
                MailpitSmtpPort = 1025,
                MailpitUseSsl = false,
            },
            NullLogger<MailpitMailDeliveryProvider>.Instance,
            () => smtp);

        var result = await provider.SendAsync(
            CreateJob(),
            CreateTenant(),
            "mailpit",
            TestContext.Current.CancellationToken);

        Assert.True(result.Succeeded);
        Assert.Equal(1, smtp.ConnectCount);
        Assert.Equal(1, smtp.SendCount);
        Assert.Equal(1, smtp.DisconnectCount);
    }

    [Fact]
    public async Task SendAsync_keeps_retryable_failure_when_send_throws()
    {
        var smtp = new ScriptedMailpitSmtpClient(
            connect: null,
            send: new IOException("SMTP DATA rejected"),
            disconnect: null);

        var provider = new MailpitMailDeliveryProvider(
            new MailerOptions
            {
                MailpitSmtpHost = "127.0.0.1",
                MailpitSmtpPort = 1025,
                MailpitUseSsl = false,
            },
            NullLogger<MailpitMailDeliveryProvider>.Instance,
            () => smtp);

        var result = await provider.SendAsync(
            CreateJob(),
            CreateTenant(),
            "mailpit",
            TestContext.Current.CancellationToken);

        Assert.False(result.Succeeded);
        Assert.True(result.Retryable);
        Assert.Equal(MailDeliveryErrorCodes.ProviderNetwork, result.ErrorCode);
        Assert.Equal(1, smtp.ConnectCount);
        Assert.Equal(1, smtp.SendCount);
        Assert.Equal(0, smtp.DisconnectCount);
    }

    [Fact]
    public async Task SendAsync_maps_unknown_exception_to_provider_unknown_non_retryable()
    {
        var smtp = new ScriptedMailpitSmtpClient(
            connect: null,
            send: new InvalidOperationException("unexpected smtp client failure"),
            disconnect: null);

        var provider = new MailpitMailDeliveryProvider(
            new MailerOptions
            {
                MailpitSmtpHost = "127.0.0.1",
                MailpitSmtpPort = 1025,
                MailpitUseSsl = false,
            },
            NullLogger<MailpitMailDeliveryProvider>.Instance,
            () => smtp);

        var result = await provider.SendAsync(
            CreateJob(),
            CreateTenant(),
            "mailpit",
            TestContext.Current.CancellationToken);

        Assert.False(result.Succeeded);
        Assert.False(result.Retryable);
        Assert.Equal(MailDeliveryErrorCodes.ProviderUnknown, result.ErrorCode);
        Assert.DoesNotContain(
            nameof(InvalidOperationException),
            result.ErrorCode,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task SendAsync_returns_fixed_error_without_the_spool_path_when_attachment_file_is_missing()
    {
        var smtp = new ScriptedMailpitSmtpClient(connect: null, send: null, disconnect: null);
        var provider = new MailpitMailDeliveryProvider(
            new MailerOptions
            {
                MailpitSmtpHost = "127.0.0.1",
                MailpitSmtpPort = 1025,
                MailpitUseSsl = false,
            },
            NullLogger<MailpitMailDeliveryProvider>.Instance,
            () => smtp);

        var missingFilePath = Path.Combine(Path.GetTempPath(), "amane-mailer-mailpit-missing-attachment-tests", Guid.NewGuid().ToString("N") + ".bin");
        var job = CreateJob(missingFilePath);

        var result = await provider.SendAsync(
            job,
            CreateTenant(),
            "mailpit",
            TestContext.Current.CancellationToken);

        Assert.False(result.Succeeded);
        Assert.False(result.Retryable);
        Assert.Equal(MailDeliveryErrorCodes.AttachmentStorageMissing, result.ErrorCode);
        Assert.DoesNotContain(missingFilePath, result.ErrorMessage ?? string.Empty, StringComparison.Ordinal);
        // The factory throws before ever touching the SMTP client.
        Assert.Equal(0, smtp.ConnectCount);
    }

    private static MailSendJob CreateJob(string? missingAttachmentFilePath = null)
    {
        var request = MailRequestTestData.CreateRequest();
        return MailSendJob.ForSingleRecipient(
            request.MailRequestId,
            request.SourceService,
            request.Subject,
            request.HtmlBody,
            request.TextBody,
            request.ReplyTo,
            request.To[0].Email,
            request.To[0].DisplayName,
            attachments: missingAttachmentFilePath is null
                ? null
                : [new MailSendAttachment("notes.txt", "text/plain", 10, missingAttachmentFilePath)]);
    }

    private static MailerTenant CreateTenant() =>
        new()
        {
            TenantId = MailerWebApplicationFixtureBase.TenantId,
            Name = "example-develop",
            SourceServices = [MailerWebApplicationFixtureBase.SourceService],
            DefaultFrom = new MailerAddress
            {
                Email = "noreply@example.com",
                DisplayName = "Example Service",
            },
            TokenEnv = "MAIL_SERVICE_TOKEN",
            Provider = "mailpit",
            Retry = new MailerRetryOptions
            {
                MaxAttempts = 3,
                InitialDelaySeconds = 1,
                MaxDelaySeconds = 2,
            },
        };

    private sealed class ScriptedMailpitSmtpClient(
        Exception? connect,
        Exception? send,
        Exception? disconnect) : IMailpitSmtpClient
    {
        public int ConnectCount { get; private set; }
        public int SendCount { get; private set; }
        public int DisconnectCount { get; private set; }

        public Task ConnectAsync(
            string host,
            int port,
            SecureSocketOptions socketOptions,
            CancellationToken cancellationToken)
        {
            ConnectCount++;
            if (connect is not null)
            {
                throw connect;
            }

            return Task.CompletedTask;
        }

        public Task SendAsync(
            MimeMessage message,
            MailboxAddress sender,
            IReadOnlyList<MailboxAddress> recipients,
            CancellationToken cancellationToken)
        {
            SendCount++;
            if (send is not null)
            {
                throw send;
            }

            return Task.CompletedTask;
        }

        public Task DisconnectAsync(bool quit, CancellationToken cancellationToken)
        {
            DisconnectCount++;
            if (disconnect is not null)
            {
                throw disconnect;
            }

            return Task.CompletedTask;
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    /// <summary>
    /// Minimal in-process SMTP server accepting exactly one session, recording RCPT TO commands
    /// and the raw DATA payload verbatim. Used only to observe real MailKit
    /// SmtpClient.SendAsync(MimeMessage) wire behavior (Issue #546 Bcc envelope/DATA boundary);
    /// not a general-purpose SMTP test double.
    /// </summary>
    private sealed class FakeSmtpServer : IDisposable
    {
        private readonly TcpListener _listener;
        private readonly Task<SmtpSession> _sessionTask;

        private FakeSmtpServer(TcpListener listener, Task<SmtpSession> sessionTask)
        {
            _listener = listener;
            _sessionTask = sessionTask;
        }

        public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

        public static Task<FakeSmtpServer> StartAsync(CancellationToken cancellationToken)
        {
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            var sessionTask = AcceptOneSessionAsync(listener, cancellationToken);
            return Task.FromResult(new FakeSmtpServer(listener, sessionTask));
        }

        public async Task<SmtpSession> WaitForSessionAsync(CancellationToken cancellationToken)
        {
            using var registration = cancellationToken.Register(static state => ((TcpListener)state!).Stop(), _listener);
            return await _sessionTask;
        }

        private static async Task<SmtpSession> AcceptOneSessionAsync(TcpListener listener, CancellationToken cancellationToken)
        {
            using var client = await listener.AcceptTcpClientAsync(cancellationToken);
            await using var stream = client.GetStream();
            using var reader = new StreamReader(stream, Encoding.ASCII);
            await using var writer = new StreamWriter(stream, Encoding.ASCII) { AutoFlush = true, NewLine = "\r\n" };

            var rcptTo = new List<string>();
            var dataLines = new List<string>();

            await writer.WriteLineAsync("220 localhost ESMTP");
            var inData = false;
            string? line;
            while ((line = await reader.ReadLineAsync(cancellationToken)) is not null)
            {
                if (inData)
                {
                    if (line == ".")
                    {
                        inData = false;
                        await writer.WriteLineAsync("250 OK message queued");
                        break;
                    }

                    dataLines.Add(line);
                    continue;
                }

                if (line.StartsWith("EHLO", StringComparison.OrdinalIgnoreCase))
                {
                    await writer.WriteLineAsync("250-localhost");
                    await writer.WriteLineAsync("250 8BITMIME");
                }
                else if (line.StartsWith("MAIL FROM", StringComparison.OrdinalIgnoreCase))
                {
                    await writer.WriteLineAsync("250 OK");
                }
                else if (line.StartsWith("RCPT TO", StringComparison.OrdinalIgnoreCase))
                {
                    rcptTo.Add(line);
                    await writer.WriteLineAsync("250 OK");
                }
                else if (line.StartsWith("DATA", StringComparison.OrdinalIgnoreCase))
                {
                    await writer.WriteLineAsync("354 Start mail input");
                    inData = true;
                }
                else
                {
                    await writer.WriteLineAsync("250 OK");
                }
            }

            return new SmtpSession(rcptTo, string.Join("\n", dataLines));
        }

        public void Dispose() => _listener.Stop();
    }

    private sealed record SmtpSession(IReadOnlyList<string> RcptToCommands, string Data);
}
