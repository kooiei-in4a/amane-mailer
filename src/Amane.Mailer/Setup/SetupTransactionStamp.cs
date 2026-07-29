namespace Amane.Mailer.Setup;

/// <summary>
/// Durable apply/rollback transaction stamp written under state/TX.stamp.
/// Declared as a record so phase advances are non-destructive copies of one planned transaction.
/// </summary>
public sealed record SetupTransactionStamp
{
    public const int CurrentSchemaVersion = 1;

    public required int SchemaVersion { get; init; }
    public required string Kind { get; init; }
    public required string Phase { get; init; }
    public required bool Terminal { get; init; }
    public string? ReasonCode { get; init; }
    public required string CandidateBundleId { get; init; }
    public required long TargetActivationGeneration { get; init; }
    public string? PreviousBundleId { get; init; }
    public long? PreviousActivationGeneration { get; init; }
    public required bool PersistentSideEffectMayRemain { get; init; }
    public required string PersistentSideEffectKind { get; init; }
    public required string StartedAt { get; init; }
}

public static class SetupTransactionKind
{
    public const string Apply = "Apply";
    public const string Rollback = "Rollback";
}

public static class SetupTransactionPhase
{
    public const string Prepared = "Prepared";
    public const string ActiveSwitchPending = "ActiveSwitchPending";
    public const string CandidateComposeValidating = "CandidateComposeValidating";
    public const string MigrationPending = "MigrationPending";
    public const string Migrating = "Migrating";
    public const string Recreating = "Recreating";
    public const string Inspecting = "Inspecting";
    public const string ReadinessChecking = "ReadinessChecking";
    public const string BindingPending = "BindingPending";
    public const string VerificationPending = "VerificationPending";
    public const string VerificationCommitted = "VerificationCommitted";
    public const string RollbackPending = "RollbackPending";
}

public static class SetupPersistentSideEffectKind
{
    public const string None = "none";
    public const string DatabaseMigration = "database-migration";
}
