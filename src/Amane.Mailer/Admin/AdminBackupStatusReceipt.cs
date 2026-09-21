using System.Text.Json.Serialization;

namespace Amane.Mailer.Admin;

internal sealed record AdminBackupStatusReceipt
{
    public int SchemaVersion { get; init; }

    public string? BackupType { get; init; }

    public string? RecordType { get; init; }

    public string? Status { get; init; }

    public DateTimeOffset StartedAtUtc { get; init; }

    public DateTimeOffset? CompletedAtUtc { get; init; }

    public string? Stage { get; init; }

    public string? OffsiteStatus { get; init; }

    public string? ArtifactName { get; init; }
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(AdminBackupStatusReceipt))]
internal partial class AdminBackupStatusJsonContext : JsonSerializerContext
{
}
