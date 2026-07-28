namespace Amane.Mailer.Setup;

/// <summary>
/// Fixed operation results for Setup Core. Secret values and secret-derived integrity material
/// must never appear alongside these codes in public output.
/// </summary>
public static class SetupResultCode
{
    public const string Succeeded = "setup.succeeded";
    public const string DryRunPlan = "setup.dry_run_plan";

    public const string RejectedValidation = "setup.rejected.validation";
    public const string RejectedModeUnsupported = "setup.rejected.mode_unsupported";
    public const string RejectedPathUnsafe = "setup.rejected.path_unsafe";
    public const string RejectedConflictManual = "setup.rejected.conflict_manual";
    public const string RejectedBundleExists = "setup.rejected.bundle_exists";
    public const string RejectedPartialWrite = "setup.rejected.partial_write";
    public const string RejectedCleanupFailed = "setup.rejected.cleanup_failed";
    public const string RejectedRollbackFailed = "setup.rejected.rollback_failed";
    public const string FailedUnexpected = "setup.failed.unexpected";
}
