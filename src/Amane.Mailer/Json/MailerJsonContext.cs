using System.Text.Json.Serialization;
using Amane.Mailer.Configuration;
using Amane.Mailer.Contracts.MailRequests;
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
[JsonSerializable(typeof(List<MailerTenant>))]
[JsonSerializable(typeof(MailRequestCreateRequest))]
[JsonSerializable(typeof(MailRequestCreateResponse))]
[JsonSerializable(typeof(MailRecipientDto))]
[JsonSerializable(typeof(MailRecipientDto[]))]
[JsonSerializable(typeof(Dictionary<string, string>))]
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
public partial class MailerJsonContext : JsonSerializerContext;

public sealed record MailerErrorResponse(string Code);

public sealed record MailerValidationErrorResponse(string Code, string Message);

public sealed record MailerServiceUnavailableResponse(string Code, bool Retryable);

public sealed record HealthStatusResponse(bool Healthy);

public sealed record ReadyStatusResponse(bool Ready);
