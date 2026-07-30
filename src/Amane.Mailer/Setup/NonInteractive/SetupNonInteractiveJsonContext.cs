using System.Text.Json.Serialization;

namespace Amane.Mailer.Setup.NonInteractive;

[JsonSerializable(typeof(SetupNonInteractiveResult))]
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    WriteIndented = false)]
internal partial class SetupNonInteractiveJsonContext : JsonSerializerContext;
