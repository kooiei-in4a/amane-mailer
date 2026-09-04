using System.Text.Json.Serialization;
using Amane.Mailer.Contracts.MailRequests;

namespace Amane.Mailer.Contracts.Json;

[JsonSerializable(typeof(MailDeliveryEventPayload))]
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
internal partial class MailerInternalJsonContext : JsonSerializerContext;
