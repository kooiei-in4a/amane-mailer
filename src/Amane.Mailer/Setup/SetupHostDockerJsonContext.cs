using System.Text.Json.Serialization;

namespace Amane.Mailer.Setup;

[JsonSerializable(typeof(ReleaseBundleManifestDocument))]
[JsonSerializable(typeof(DockerContextInspectDocument))]
[JsonSerializable(typeof(DockerContextInspectDocument[]))]
[JsonSerializable(typeof(DockerContextInspectEndpoints))]
[JsonSerializable(typeof(DockerContextInspectEndpoint))]
[JsonSerializable(typeof(SetupMountVerifierDocument))]
[JsonSerializable(typeof(SetupMountVerifierMember))]
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    WriteIndented = false)]
public partial class SetupHostDockerJsonContext : JsonSerializerContext;
