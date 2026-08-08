using System.Text.Json.Serialization;
using Amane.Mailer.Configuration;
using Amane.Mailer.Contracts.MailRequests;
using Amane.Mailer.Bounce;
using Amane.Mailer.Contracts.Json;

namespace Amane.Mailer.Json;

[JsonSerializable(typeof(MailerErrorResponse))]
[JsonSerializable(typeof(MailerValidationErrorResponse))]
[JsonSerializable(typeof(MailerServiceUnavailableResponse))]
[JsonSerializable(typeof(HealthStatusResponse))]
[JsonSerializable(typeof(ReadyStatusResponse))]
[JsonSerializable(typeof(MailerTenantsFile))]
[JsonSerializable(typeof(MailerTenant))]
[JsonSerializable(typeof(MailerAddress))]
[JsonSerializable(typeof(MailerRetryOptions))]
[JsonSerializable(typeof(MailerWebhookConfig))]
[JsonSerializable(typeof(List<MailerTenant>))]
[JsonSerializable(typeof(MailDeliveryEventPayload))]
[JsonSerializable(typeof(PlatformSenderFile))]
[JsonSerializable(typeof(PlatformSenderAddress))]
[JsonSerializable(typeof(MailRequestCreateRequest))]
[JsonSerializable(typeof(MailRequestCreateResponse))]
[JsonSerializable(typeof(MailRequestStatusResponse))]
[JsonSerializable(typeof(MailRequestRescheduleRequest))]
[JsonSerializable(typeof(MailRecipientDto))]
[JsonSerializable(typeof(MailRecipientDto[]))]
[JsonSerializable(typeof(MailAttachmentDto))]
[JsonSerializable(typeof(MailAttachmentDto[]))]
[JsonSerializable(typeof(Dictionary<string, string>))]
[JsonSerializable(typeof(AcsEventGridEventDto))]
[JsonSerializable(typeof(AcsEmailDeliveryReportDataDto))]
[JsonSerializable(typeof(AcsDeliveryStatusDetailsDto))]
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
public partial class MailerJsonContext : JsonSerializerContext;

public sealed record MailerErrorResponse(string Code);

public sealed record MailerValidationErrorResponse(string Code, string Message);

public sealed record MailerServiceUnavailableResponse(string Code, bool Retryable);

public sealed record HealthStatusResponse(bool Healthy);

public sealed record ReadyStatusResponse(bool Ready);
