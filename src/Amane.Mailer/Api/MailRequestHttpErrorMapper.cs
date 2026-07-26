using Amane.Mailer.Contracts.MailRequests;
using Amane.Mailer.Data.Sqlite;
using Amane.Mailer.Data.Sqlite.Models;
using Amane.Mailer.Json;

namespace Amane.Mailer.Api;

/// <summary>
/// Maps storage / body-read failures and status DTOs to the existing HTTP error contract.
/// </summary>
public static class MailRequestHttpErrorMapper
{
    public static IResult Error(int statusCode, string code) =>
        MailerJsonResults.Error(code, statusCode);

    public static IResult ServiceUnavailable() =>
        MailerJsonResults.ServiceUnavailable();

    public static IResult StorageFull() =>
        MailerJsonResults.StorageFull();

    public static IResult FromBodyReadFailure(MailRequestBodyReadFailure failure) =>
        failure switch
        {
            MailRequestBodyReadFailure.TooLarge => MailerJsonResults.Error(
                MailerErrorCodes.RequestTooLarge,
                StatusCodes.Status413PayloadTooLarge),
            MailRequestBodyReadFailure.InvalidUtf8 => MailerJsonResults.ValidationError(
                MailerErrorCodes.InvalidRequest,
                "Request body is not valid UTF-8.",
                StatusCodes.Status400BadRequest),
            _ => throw new ArgumentOutOfRangeException(nameof(failure), failure, null),
        };

    public static IResult FromJsonReadFailure(MailRequestJsonReadFailure failure) =>
        failure switch
        {
            MailRequestJsonReadFailure.InvalidJson => MailerJsonResults.ValidationError(
                MailerErrorCodes.InvalidRequest,
                "Request body is not valid JSON.",
                StatusCodes.Status400BadRequest),
            MailRequestJsonReadFailure.BodyRequired => MailerJsonResults.ValidationError(
                MailerErrorCodes.InvalidRequest,
                "Request body is required.",
                StatusCodes.Status400BadRequest),
            MailRequestJsonReadFailure.DuplicateProperty => MailerJsonResults.ValidationError(
                MailerErrorCodes.InvalidRequest,
                "Request body contains a duplicate JSON property.",
                StatusCodes.Status400BadRequest),
            _ => throw new ArgumentOutOfRangeException(nameof(failure), failure, null),
        };

    public static IResult StatusOk(MailRequestStatusRow statusRow) =>
        MailerJsonResults.Ok(new MailRequestStatusResponse
        {
            MailRequestId = statusRow.MailRequestId,
            Status = ToDeliveryStatus(statusRow.Status),
            AttemptCount = statusRow.AttemptCount,
            MaxAttempts = statusRow.MaxAttempts,
            NextAttemptAt = statusRow.NextAttemptAt,
            ScheduledAt = statusRow.ScheduledAt,
            AcceptedAt = statusRow.AcceptedAt,
            DeliveredAt = statusRow.DeliveredAt,
            LastErrorCode = statusRow.LastErrorCode,
        });

    public static bool IsStorageFullDatabaseException(Exception exception) =>
        SqliteDatabaseExceptionClassifier.IsStorageFull(exception);

    public static bool IsTransientDatabaseException(Exception exception) =>
        SqliteDatabaseExceptionClassifier.IsTransient(exception);

    public static string ToDeliveryStatus(MailRequestState status) =>
        status switch
        {
            MailRequestState.Queued => MailRequestStatus.Queued,
            MailRequestState.Processing => MailRequestStatus.Processing,
            MailRequestState.Delivered => MailRequestStatus.Delivered,
            MailRequestState.Failed => MailRequestStatus.Failed,
            MailRequestState.DeadLettered => MailRequestStatus.DeadLettered,
            MailRequestState.Cancelled => MailRequestStatus.Cancelled,
            _ => throw new ArgumentOutOfRangeException(nameof(status), status, null),
        };
}
