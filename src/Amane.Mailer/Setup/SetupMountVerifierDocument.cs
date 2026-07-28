using System.Text.Json.Serialization;

namespace Amane.Mailer.Setup;

/// <summary>
/// Ephemeral mount verifier document passed to one-shot inspect (ADR 0021 D-04).
/// Contains session material for a single inspect invocation only. Must never be logged,
/// written to stdout/stderr, persisted into normal runtime env, or copied into public results.
/// </summary>
public sealed class SetupMountVerifierDocument
{
    public const int CurrentSchemaVersion = 1;

    [JsonPropertyName("schemaVersion")]
    public int SchemaVersion { get; init; } = CurrentSchemaVersion;

    [JsonPropertyName("bundleId")]
    public required string BundleId { get; init; }

    [JsonPropertyName("sessionNonce")]
    public required string SessionNonce { get; init; }

    [JsonPropertyName("sessionKey")]
    public required string SessionKey { get; init; }

    [JsonPropertyName("expiresAtUnix")]
    public long ExpiresAtUnix { get; init; }

    [JsonPropertyName("members")]
    public required IReadOnlyList<SetupMountVerifierMember> Members { get; init; }
}

public sealed class SetupMountVerifierMember
{
    /// <summary>
    /// Fixed member id shared with host verifier generation.
    /// Examples: <c>secrets/acs_connection_string</c>, <c>env:MAIL_SERVICE_TOKEN</c>.
    /// </summary>
    [JsonPropertyName("memberId")]
    public required string MemberId { get; init; }

    [JsonPropertyName("expectedMac")]
    public required string ExpectedMac { get; init; }
}
