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
        return new MailSendJob(
            request.MailRequestId,
            request.SourceService,
            request.Subject,
            request.HtmlBody,
            request.TextBody,
            request.ReplyTo,
            request.To[0].Email,
            request.To[0].DisplayName,
            Attachments: missingAttachmentFilePath is null
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

        public Task SendAsync(MimeMessage message, CancellationToken cancellationToken)
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
}
