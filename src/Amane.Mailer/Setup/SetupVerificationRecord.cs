namespace Amane.Mailer.Setup;

/// <summary>
/// Secret-free verification record under verification/last-record.json.
/// </summary>
public sealed class SetupVerificationRecord
{
    public const int CurrentSchemaVersion = 1;
    public const string StatusCommitted = "committed";
    public const string StatusInvalidated = "invalidated";

    public const string FingerprintMatched = "matched";
    public const string FingerprintMismatch = "mismatch";
    public const string FingerprintNotEvaluated = "not-evaluated";

    public const string ReadinessPassed = "passed";
    public const string ReadinessFailed = "failed";
    public const string ReadinessNotEvaluated = "not-evaluated";

    /// <summary>Apply never asserts outbound send readiness, so this is the only value it records.</summary>
    public const string SendReadyNotEvaluated = "not-evaluated";

    public required int SchemaVersion { get; init; }
    public required string Status { get; init; }
    public required string BundleId { get; init; }
    public required long ActivationGeneration { get; init; }
    public required string FingerprintComparison { get; init; }
    public required string HostAtRest { get; init; }
    public required string MountAttestation { get; init; }
    public required string BundleIntegrity { get; init; }
    public string? ImageReference { get; init; }
    public string? ComposeIdentity { get; init; }
    public int? RecordedSchemaVersion { get; init; }
    public required string RuntimeIdentityBinding { get; init; }
    public required string Readiness { get; init; }
    public required string SendReadyEvaluation { get; init; }
    public string? CommittedAt { get; init; }

    public bool IsCommittedSuccess =>
        string.Equals(Status, StatusCommitted, StringComparison.Ordinal)
        && string.Equals(FingerprintComparison, FingerprintMatched, StringComparison.Ordinal)
        && string.Equals(BundleIntegrity, SetupIntegrityMerger.Matched, StringComparison.Ordinal)
        && string.Equals(Readiness, ReadinessPassed, StringComparison.Ordinal)
        && string.Equals(RuntimeIdentityBinding, SetupRuntimeIdentityBindingResult.Matched, StringComparison.Ordinal)
        && !string.IsNullOrWhiteSpace(CommittedAt);
}
