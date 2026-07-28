using System.Text.Json.Serialization;
using Amane.Mailer.Configuration;

namespace Amane.Mailer.Setup;

[JsonSerializable(typeof(SetupRecordedMetadata))]
[JsonSerializable(typeof(MailerTenantsFile))]
[JsonSerializable(typeof(PlatformSenderFile))]
[JsonSerializable(typeof(PlatformSenderAddress))]
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    WriteIndented = true)]
public partial class SetupJsonContext : JsonSerializerContext;
