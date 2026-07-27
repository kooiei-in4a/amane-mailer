namespace Amane.Mailer.Operations;

/// <summary>
/// Canonical result codes for <c>admin provider test-acs-send</c>. Shared rejection strings
/// match <see cref="AdminProviderRegisterAcsResultCodes"/> where the same operator/input failure
/// applies, so runbooks and evidence stay consistent.
/// </summary>
public static class AdminProviderTestAcsSendResultCodes
{
    public const string Success = "SUCCESS";

    public const string RejectedInputRedirected = "REJECTED_INPUT_REDIRECTED";
    public const string RejectedEnvironmentMismatch = "REJECTED_ENVIRONMENT_MISMATCH";
    public const string RejectedIntentMismatch = "REJECTED_INTENT_MISMATCH";
    public const string RejectedSecretMismatch = "REJECTED_SECRET_MISMATCH";
    public const string RejectedInvalidConnectionString = "REJECTED_INVALID_CONNECTION_STRING";
    public const string RejectedInvalidSenderEmail = "REJECTED_INVALID_SENDER_EMAIL";
    public const string RejectedInvalidRecipientEmail = "REJECTED_INVALID_RECIPIENT_EMAIL";
    public const string RejectedMessageIdHandoffPathInvalid = "REJECTED_MESSAGE_ID_HANDOFF_PATH_INVALID";
    public const string RejectedMessageIdHandoffWriteFailed = "REJECTED_MESSAGE_ID_HANDOFF_WRITE_FAILED";
    public const string RejectedCancelled = "REJECTED_CANCELLED";

    public const string FailedAcsAuthentication = "FAILED_ACS_AUTHENTICATION";
    public const string FailedAcsNetwork = "FAILED_ACS_NETWORK";
    public const string FailedAcsSenderRejected = "FAILED_ACS_SENDER_REJECTED";
    public const string FailedAcsSendRequest = "FAILED_ACS_SEND_REQUEST";
    public const string FailedAcsOperation = "FAILED_ACS_OPERATION";
    public const string FailedAcsTimeout = "FAILED_ACS_TIMEOUT";
    public const string FailedAcsMessageIdInvalid = "FAILED_ACS_MESSAGE_ID_INVALID";
    public const string FailedUnexpected = "FAILED_UNEXPECTED";
}
