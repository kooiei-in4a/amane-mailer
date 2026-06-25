using System.Text.Json.Serialization;
using Mailer.Contracts.MailRequests;

namespace Mailer.Contracts.Json;

[JsonSerializable(typeof(MailRequestCreateRequest))]
[JsonSerializable(typeof(MailRequestCreateResponse))]
[JsonSerializable(typeof(MailRecipientDto))]
[JsonSerializable(typeof(MailRecipientDto[]))]
[JsonSerializable(typeof(Dictionary<string, string>))]
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
public partial class MailerContractsJsonContext : JsonSerializerContext;
