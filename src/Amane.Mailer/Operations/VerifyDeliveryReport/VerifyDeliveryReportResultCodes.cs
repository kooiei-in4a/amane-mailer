namespace Amane.Mailer.Operations.VerifyDeliveryReport;

/// <summary>
/// Canonical result codes for <c>setup verify-delivery-report</c> (#428).
/// Rejection codes shared with <see cref="AdminProviderTestAcsSendResultCodes"/> keep operator evidence consistent.
/// </summary>
public static class VerifyDeliveryReportResultCodes
{
    public const string Success = "SUCCESS";

    public const string RejectedInputRedirected = AdminProviderTestAcsSendResultCodes.RejectedInputRedirected;
    public const string RejectedEnvironmentMismatch = AdminProviderTestAcsSendResultCodes.RejectedEnvironmentMismatch;
    public const string RejectedIntentMismatch = AdminProviderTestAcsSendResultCodes.RejectedIntentMismatch;
    public const string RejectedSecretMismatch = AdminProviderTestAcsSendResultCodes.RejectedSecretMismatch;
    public const string RejectedInvalidConnectionString = AdminProviderTestAcsSendResultCodes.RejectedInvalidConnectionString;
    public const string RejectedInvalidSenderEmail = AdminProviderTestAcsSendResultCodes.RejectedInvalidSenderEmail;
    public const string RejectedInvalidRecipientEmail = AdminProviderTestAcsSendResultCodes.RejectedInvalidRecipientEmail;
    public const string RejectedCancelled = AdminProviderTestAcsSendResultCodes.RejectedCancelled;

    public const string RejectedInvalidQueueConnectionString = "REJECTED_INVALID_QUEUE_CONNECTION_STRING";
    public const string RejectedInvalidQueueName = "REJECTED_INVALID_QUEUE_NAME";
    public const string RejectedInvalidTimeout = "REJECTED_INVALID_TIMEOUT";
    public const string RejectedInvalidPollInterval = "REJECTED_INVALID_POLL_INTERVAL";

    public const string FailedAcsAuthentication = AdminProviderTestAcsSendResultCodes.FailedAcsAuthentication;
    public const string FailedAcsNetwork = AdminProviderTestAcsSendResultCodes.FailedAcsNetwork;
    public const string FailedAcsSenderRejected = AdminProviderTestAcsSendResultCodes.FailedAcsSenderRejected;
    public const string FailedAcsSendRequest = AdminProviderTestAcsSendResultCodes.FailedAcsSendRequest;
    public const string FailedAcsOperation = AdminProviderTestAcsSendResultCodes.FailedAcsOperation;
    public const string FailedAcsTimeout = AdminProviderTestAcsSendResultCodes.FailedAcsTimeout;
    public const string FailedAcsMessageIdInvalid = AdminProviderTestAcsSendResultCodes.FailedAcsMessageIdInvalid;

    public const string FailedQueueAuthentication = "FAILED_QUEUE_AUTHENTICATION";
    public const string FailedQueueNetwork = "FAILED_QUEUE_NETWORK";
    public const string FailedQueueNotFound = "FAILED_QUEUE_NOT_FOUND";
    public const string FailedDeliveryReportTimeout = "FAILED_DELIVERY_REPORT_TIMEOUT";
    public const string FailedDeliveryReportBacklog = "FAILED_DELIVERY_REPORT_BACKLOG";
    public const string FailedUnexpected = "FAILED_UNEXPECTED";
}
