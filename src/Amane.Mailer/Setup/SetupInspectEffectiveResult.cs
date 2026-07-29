using System.Text.Json.Serialization;

namespace Amane.Mailer.Setup;

/// <summary>
/// Public JSON result for <c>setup inspect-effective --format json</c> (ADR 0021 D-05).
/// Secrets, HMAC, session keys, salt, and private host paths are never included.
/// </summary>
public sealed class SetupInspectEffectiveResult
{
    public const int CurrentSchemaVersion = 1;

    [JsonPropertyName("schemaVersion")]
    public int SchemaVersion { get; init; } = CurrentSchemaVersion;

    [JsonPropertyName("mailerVersion")]
    public required string MailerVersion { get; init; }

    [JsonPropertyName("managed")]
    public required bool Managed { get; init; }

    [JsonPropertyName("recorded")]
    public SetupInspectRecordedSummary? Recorded { get; init; }

    [JsonPropertyName("effective")]
    public required SetupInspectEffectiveSummary Effective { get; init; }

    /// <summary>
    /// Container-side mount attestation only (ADR 0021 D-04 step 2).
    /// </summary>
    [JsonPropertyName("mountAttestation")]
    public required SetupInspectAttestationSummary MountAttestation { get; init; }

    /// <summary>
    /// Provisional integrity visible to host. One-shot never emits final
    /// <see cref="SetupInspectIntegrityResult.Matched"/>; host at-rest must still integrate (D-04).
    /// </summary>
    [JsonPropertyName("bundleIntegrity")]
    public required SetupInspectAttestationSummary BundleIntegrity { get; init; }

    [JsonPropertyName("tenantConfigurationSource")]
    public required string TenantConfigurationSource { get; init; }

    [JsonPropertyName("credentialSource")]
    public required string CredentialSource { get; init; }

    [JsonPropertyName("reason")]
    public string? Reason { get; init; }
}

public sealed class SetupInspectRecordedSummary
{
    [JsonPropertyName("setupBundleId")]
    public required string SetupBundleId { get; init; }

    [JsonPropertyName("configurationFingerprint")]
    public required string ConfigurationFingerprint { get; init; }

    [JsonPropertyName("mode")]
    public string? Mode { get; init; }

    [JsonPropertyName("schemaVersion")]
    public int SchemaVersion { get; init; }
}

public sealed class SetupInspectEffectiveSummary
{
    [JsonPropertyName("configurationFingerprint")]
    public string? ConfigurationFingerprint { get; init; }

    [JsonPropertyName("providerSummary")]
    public string? ProviderSummary { get; init; }

    [JsonPropertyName("liveSendingEnabled")]
    public bool? LiveSendingEnabled { get; init; }

    [JsonPropertyName("credentialStatus")]
    public required string CredentialStatus { get; init; }

    [JsonPropertyName("fingerprintsMatchRecorded")]
    public bool? FingerprintsMatchRecorded { get; init; }
}

public sealed class SetupInspectAttestationSummary
{
    [JsonPropertyName("result")]
    public required string Result { get; init; }

    [JsonPropertyName("reason")]
    public string? Reason { get; init; }

    [JsonPropertyName("scope")]
    public string? Scope { get; init; }
}
