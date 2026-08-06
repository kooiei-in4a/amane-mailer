using System.Text.Json.Serialization;

namespace Amane.Mailer.ReleaseBundle;

[JsonSerializable(typeof(ReleaseBundleManifestDocument))]
[JsonSerializable(typeof(OciIndexDocument))]
[JsonSerializable(typeof(OciManifestDocument))]
[JsonSerializable(typeof(OciDescriptor))]
[JsonSerializable(typeof(OciPlatform))]
[JsonSerializable(typeof(BuildxMetadataDocument))]
[JsonSerializable(typeof(ImageIdentityDocument))]
[JsonSerializable(typeof(CandidateProvenanceDocument))]
[JsonSerializable(typeof(CandidateArchiveProvenance))]
[JsonSerializable(typeof(CandidateArchiveProvenance[]))]
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    WriteIndented = true)]
public partial class ReleaseBundleJsonContext : JsonSerializerContext;

[JsonSerializable(typeof(ReleaseBundleManifestDocument))]
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    WriteIndented = false)]
public partial class ReleaseBundleManifestJsonContext : JsonSerializerContext;
