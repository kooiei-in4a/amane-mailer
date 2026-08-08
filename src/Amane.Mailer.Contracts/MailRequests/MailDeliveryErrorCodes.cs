namespace Amane.Mailer.Contracts.MailRequests;

/// <summary>
/// Stable delivery-attempt <c>error_code</c> / <c>last_error_code</c> values
/// persisted on attempts and exposed on status API and delivery webhooks.
/// Distinct from HTTP acceptance <see cref="MailerErrorCodes"/>.
/// </summary>
public static class MailDeliveryErrorCodes
{
    public const string AcsNotConfigured = "ACS_NOT_CONFIGURED";
    public const string AcsSendFailed = "ACS_SEND_FAILED";
    public const string AcsRequestFailed = "ACS_REQUEST_FAILED";
    public const string LiveSendingDisabled = "LIVE_SENDING_DISABLED";
    public const string UnknownProvider = "UNKNOWN_PROVIDER";
    public const string SendTimeout = "SEND_TIMEOUT";
    public const string RecipientSuppressed = "RECIPIENT_SUPPRESSED";

    public const string ProviderTimeout = "PROVIDER_TIMEOUT";
    public const string ProviderNetwork = "PROVIDER_NETWORK";
    public const string ProviderProtocol = "PROVIDER_PROTOCOL";
    public const string ProviderAuth = "PROVIDER_AUTH";
    public const string ProviderUnknown = "PROVIDER_UNKNOWN";

    /// <summary>
    /// Attachment metadata exists in SQLite but the durable spool file is missing at dispatch
    /// time (ADR 0022 D-08 recovery). Terminal Failed; the provider is never invoked.
    /// </summary>
    public const string AttachmentStorageMissing = "ATTACHMENT_STORAGE_MISSING";

    /// <summary>
    /// Fixed category for the <c>DeliveryUnknown</c> terminal status (ADR 0022 D-08/D-12):
    /// provider acceptance could not be proven or disproven after a submission marker committed.
    /// Distinct from an ordinary failure; never treated as a synonym for Failed.
    /// </summary>
    public const string DeliveryUnknown = "DELIVERY_UNKNOWN";
}
