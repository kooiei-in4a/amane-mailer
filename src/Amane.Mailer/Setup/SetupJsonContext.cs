using System.Text.Json.Serialization;
using Amane.Mailer.Configuration;

namespace Amane.Mailer.Setup;

[JsonSerializable(typeof(SetupRecordedMetadata))]
[JsonSerializable(typeof(SetupAdminBootstrapExpectation))]
[JsonSerializable(typeof(SetupAdminDatabaseExpectationState))]
[JsonSerializable(typeof(MailerTenantsFile))]
[JsonSerializable(typeof(PlatformSenderFile))]
[JsonSerializable(typeof(PlatformSenderAddress))]
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    WriteIndented = true)]
public partial class SetupJsonContext : JsonSerializerContext;

[JsonSerializable(typeof(SetupInspectEffectiveResult))]
[JsonSerializable(typeof(SetupInspectRecordedSummary))]
[JsonSerializable(typeof(SetupInspectEffectiveSummary))]
[JsonSerializable(typeof(SetupInspectAttestationSummary))]
[JsonSerializable(typeof(SetupMountVerifierDocument))]
[JsonSerializable(typeof(SetupMountVerifierMember))]
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    WriteIndented = false)]
public partial class SetupInspectJsonContext : JsonSerializerContext;
