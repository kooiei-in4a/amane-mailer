using System.Net;
using System.Text;
using Amane.Mailer.Configuration;
using Amane.Mailer.Contracts.MailRequests;

namespace Amane.Mailer.Webhooks;

public sealed class WebhookDeliveryClient(
    IHttpClientFactory httpClientFactory,
    WebhookSignatureService signatureService,
    WebhookUrlValidator urlValidator,
    TimeProvider timeProvider,
    ILogger<WebhookDeliveryClient> logger)
{
    public async Task<WebhookDeliveryResult> DeliverAsync(
        MailerTenant tenant,
        string webhookSecret,
        MailDeliveryEventPayload payload,
        string payloadJson,
        CancellationToken cancellationToken)
    {
        var webhook = tenant.Webhook
            ?? throw new InvalidOperationException("Tenant webhook configuration is required.");

        var validation = await urlValidator.ValidateAsync(webhook, cancellationToken);
        if (!validation.IsValid)
        {
            logger.LogWarning(
                "Webhook URL validation failed for tenant {TenantId}. ErrorCode={ErrorCode}",
                tenant.TenantId,
                validation.ErrorCode);
            return WebhookDeliveryResult.Failure(
                validation.ErrorCode ?? "WEBHOOK_URL_INVALID",
                retryable: false);
        }

        var timestamp = timeProvider.GetUtcNow().ToUnixTimeSeconds();
        var bodyBytes = Encoding.UTF8.GetBytes(payloadJson);
        var headers = signatureService.CreateHeaders(payload.EventId, timestamp, bodyBytes, webhookSecret);

        using var request = new HttpRequestMessage(HttpMethod.Post, validation.Uri!);
        if (validation.ConnectAddress is not null)
        {
            request.Options.Set(WebhookConnectionOptions.PinnedConnectAddress, validation.ConnectAddress);
        }

        request.Content = new ByteArrayContent(bodyBytes);
        request.Content.Headers.ContentType = new("application/json");
        request.Headers.TryAddWithoutValidation(WebhookSignatureService.EventIdHeaderName, headers.EventId.ToString("D"));
        request.Headers.TryAddWithoutValidation(WebhookSignatureService.TimestampHeaderName, headers.Timestamp);
        request.Headers.TryAddWithoutValidation(WebhookSignatureService.SignatureHeaderName, headers.Signature);

        try
        {
            var client = httpClientFactory.CreateClient(WebhookHttpClientRegistration.ClientName);
            using var response = await client.SendAsync(request, cancellationToken);
            if (response.IsSuccessStatusCode)
            {
                return WebhookDeliveryResult.Success();
            }

            var retryable = response.StatusCode is HttpStatusCode.RequestTimeout
                or HttpStatusCode.TooManyRequests
                or >= HttpStatusCode.InternalServerError;

            return WebhookDeliveryResult.Failure(
                $"WEBHOOK_HTTP_{(int)response.StatusCode}",
                retryable);
        }
        catch (OperationCanceledException)
        {
            return WebhookDeliveryResult.Failure("WEBHOOK_TIMEOUT", retryable: true);
        }
        catch (HttpRequestException)
        {
            return WebhookDeliveryResult.Failure("WEBHOOK_TRANSPORT_ERROR", retryable: true);
        }
    }
}

public sealed record WebhookDeliveryResult(bool Succeeded, string? ErrorCode, bool Retryable)
{
    public static WebhookDeliveryResult Success() => new(true, null, false);

    public static WebhookDeliveryResult Failure(string errorCode, bool retryable) =>
        new(false, errorCode, retryable);
}
