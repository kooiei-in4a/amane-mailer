namespace Amane.Mailer.Contracts.MailRequests;

public static class MailerErrorCodes
{
    public const string InvalidRequest = "INVALID_REQUEST";
    public const string RequestTooLarge = "REQUEST_TOO_LARGE";
    public const string SourceServiceNotAllowed = "SOURCE_SERVICE_NOT_ALLOWED";
    public const string TooManyRecipients = "TOO_MANY_RECIPIENTS";
    public const string IdempotencyConflict = "IDEMPOTENCY_CONFLICT";
    public const string InvalidPayloadHash = "INVALID_PAYLOAD_HASH";
    public const string InvalidMetadata = "INVALID_METADATA";
    public const string UnauthorizedTenant = "UNAUTHORIZED_TENANT";
    public const string MailerTemporarilyUnavailable = "MAILER_TEMPORARILY_UNAVAILABLE";
    public const string StorageFull = "STORAGE_FULL";
    public const string NotFound = "NOT_FOUND";
    public const string ScheduledAtInPast = "SCHEDULED_AT_IN_PAST";
    public const string ScheduledAtTooFar = "SCHEDULED_AT_TOO_FAR";
    public const string InvalidState = "INVALID_STATE";

    // Attachment acceptance failure categories (ADR 0022 D-12). Fixed, sanitized categories only;
    // never carry provider raw messages, digests, or raw filenames (ADR 0022 D-13).
    public const string TooManyAttachments = "TOO_MANY_ATTACHMENTS";
    public const string AttachmentTooLarge = "ATTACHMENT_TOO_LARGE";
    public const string AttachmentTotalTooLarge = "ATTACHMENT_TOTAL_TOO_LARGE";
    public const string MailPayloadTooLarge = "MAIL_PAYLOAD_TOO_LARGE";
    public const string AttachmentInvalidBase64 = "ATTACHMENT_INVALID_BASE64";
    public const string AttachmentDigestMismatch = "ATTACHMENT_DIGEST_MISMATCH";
    public const string AttachmentLengthMismatch = "ATTACHMENT_LENGTH_MISMATCH";
    public const string AttachmentFilenameInvalid = "ATTACHMENT_FILENAME_INVALID";
    public const string AttachmentDuplicateFilename = "ATTACHMENT_DUPLICATE_FILENAME";
    public const string AttachmentTypeNotAllowed = "ATTACHMENT_TYPE_NOT_ALLOWED";
    public const string AttachmentContentMismatch = "ATTACHMENT_CONTENT_MISMATCH";
    public const string AttachmentEncrypted = "ATTACHMENT_ENCRYPTED";
    public const string AttachmentStorageUnavailable = "ATTACHMENT_STORAGE_UNAVAILABLE";
}
