using System.Text.Json.Serialization;
using Amane.Mailer.Contracts.MailRequests;

namespace Amane.Mailer.Contracts.Json;

[JsonSerializable(typeof(MailRequestCreateRequest))]
[JsonSerializable(typeof(MailRequestCreateResponse))]
[JsonSerializable(typeof(MailRecipientDto))]
[JsonSerializable(typeof(MailRecipientDto[]))]
[JsonSerializable(typeof(Dictionary<string, string>))]
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
public partial class MailerContractsJsonContext : JsonSerializerContext;
