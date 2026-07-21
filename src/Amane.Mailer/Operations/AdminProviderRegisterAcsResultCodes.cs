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
    public const string RejectedCancelled = "REJECTED_CANCELLED";
    public const string FailedUnexpected = "FAILED_UNEXPECTED";
}
