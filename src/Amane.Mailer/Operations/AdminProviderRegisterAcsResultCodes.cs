namespace Amane.Mailer.Operations;

/// <summary>
/// Canonical, sanitized result codes for <c>admin provider register-acs</c> and
/// <c>admin provider check-acs-preflight</c>. Never construct these strings ad hoc; reference
/// these constants everywhere (code, tests, runbooks) to keep sanitized output consistent.
/// </summary>
public static class AdminProviderRegisterAcsResultCodes
{
    public const string Success = "SUCCESS";
    public const string RejectedInputRedirected = "REJECTED_INPUT_REDIRECTED";
    public const string RejectedEnvironmentMismatch = "REJECTED_ENVIRONMENT_MISMATCH";
    public const string RejectedIntentMismatch = "REJECTED_INTENT_MISMATCH";
    public const string RejectedSecretMismatch = "REJECTED_SECRET_MISMATCH";
    public const string RejectedInvalidConnectionString = "REJECTED_INVALID_CONNECTION_STRING";
    public const string RejectedInvalidSenderEmail = "REJECTED_INVALID_SENDER_EMAIL";
    public const string RejectedInvalidDisplayName = "REJECTED_INVALID_DISPLAY_NAME";
    public const string RejectedDirectoryUnsafe = "REJECTED_DIRECTORY_UNSAFE";
    public const string RejectedDirectoryNotWritable = "REJECTED_DIRECTORY_NOT_WRITABLE";
    public const string RejectedAlreadyRegistered = "REJECTED_ALREADY_REGISTERED";
    public const string RejectedPartialState = "REJECTED_PARTIAL_STATE";
    public const string RejectedConcurrentExecution = "REJECTED_CONCURRENT_EXECUTION";
    public const string RejectedPartialWriteRolledBack = "REJECTED_PARTIAL_WRITE_ROLLED_BACK";

    /// <summary>
    /// The second file failed to commit AND rolling back the first also failed. Unlike
    /// <see cref="RejectedPartialWriteRolledBack"/>, this does not claim the on-disk state is
    /// clean again — it may still hold the first file's value. Manual review is required; do not
    /// auto-retry.
    /// </summary>
    public const string RejectedRollbackFailed = "REJECTED_ROLLBACK_FAILED";

    /// <summary>
    /// A prepared (uncommitted) temp file failed to be deleted during cleanup after a sibling
    /// step failed. Distinct from <see cref="RejectedRollbackFailed"/>, which is specifically
    /// about undoing an already-committed value; this is about an uncommitted <c>.tmp-*</c> file
    /// (which may still contain the ACS secret content) that could not be removed. Manual review
    /// is required; do not auto-retry.
    /// </summary>
    public const string RejectedCleanupFailed = "REJECTED_CLEANUP_FAILED";

    public const string RejectedCancelled = "REJECTED_CANCELLED";
    public const string FailedUnexpected = "FAILED_UNEXPECTED";
}
