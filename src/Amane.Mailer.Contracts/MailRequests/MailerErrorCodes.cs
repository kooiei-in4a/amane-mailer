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
}
