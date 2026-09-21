namespace Amane.Mailer.Admin;

/// <summary>
/// Read-only projection of backup evidence for the Admin operations status page.
/// Artifact presence and upload receipts are evidence of those individual steps;
/// neither is proof that an archive can be restored.
/// </summary>
public sealed record AdminBackupStatusReadModel(
    DateTimeOffset AsOfUtc,
    AdminOnlineDatabaseBackupStatus AdminOnlineDatabaseBackup,
    AdminBackupScriptStatus DatabaseOnly,
    AdminBackupScriptStatus FullInstance,
    bool RestoreVerificationRecorded);

public sealed record AdminOnlineDatabaseBackupStatus(
    AdminBackupEvidenceState EvidenceState,
    AdminBackupOutcome LatestOutcome,
    DateTimeOffset? LatestAtUtc);

public sealed record AdminBackupScriptStatus(
    AdminBackupEvidenceState AttemptEvidenceState,
    AdminBackupEvidenceState SuccessEvidenceState,
    AdminBackupOutcome LatestOutcome,
    DateTimeOffset? LatestAttemptAtUtc,
    string? LatestAttemptStage,
    DateTimeOffset? LastSuccessAtUtc,
    bool? LastSuccessArtifactPresent,
    AdminBackupOffsiteState LatestAttemptOffsiteState,
    DateTimeOffset? LastOffsiteUploadAtUtc,
    bool FreshnessPolicyConfigured,
    AdminBackupFreshness Freshness);

public enum AdminBackupEvidenceState
{
    NotRecorded,
    Available,
    Invalid,
}

public enum AdminBackupOutcome
{
    Unknown,
    Running,
    Succeeded,
    Failed,
}

public enum AdminBackupOffsiteState
{
    Unknown,
    NotAttempted,
    Pending,
    Succeeded,
    Failed,
    Skipped,
}

public enum AdminBackupFreshness
{
    Unknown,
    Fresh,
    Stale,
}
