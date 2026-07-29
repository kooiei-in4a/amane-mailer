using System.Text.Json.Serialization;

namespace Amane.Mailer.Setup;

[JsonSerializable(typeof(SetupActivePointer))]
[JsonSerializable(typeof(SetupTransactionStamp))]
[JsonSerializable(typeof(SetupVerificationRecord))]
[JsonSerializable(typeof(SetupRuntimeIdentityBindingStamp))]
[JsonSerializable(typeof(SetupMigrationStatusDocument))]
[JsonSerializable(typeof(SetupApplyResult))]
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    WriteIndented = false)]
public partial class SetupApplyJsonContext : JsonSerializerContext;
