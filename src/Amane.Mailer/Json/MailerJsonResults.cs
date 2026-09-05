using Amane.Mailer.Contracts.MailRequests;

namespace Amane.Mailer.Json;

public static class MailerJsonResults
{
    public static IResult Error(string code, int statusCode) =>
        Results.Json(
            new MailerErrorResponse(code),
            MailerJsonContext.Default.MailerErrorResponse,
            statusCode: statusCode);

    public static IResult ValidationError(string code, string message, int statusCode) =>
        Results.Json(
            new MailerValidationErrorResponse(code, message),
            MailerJsonContext.Default.MailerValidationErrorResponse,
            statusCode: statusCode);

    public static IResult ServiceUnavailable() =>
        ServiceUnavailable(MailerErrorCodes.MailerTemporarilyUnavailable, retryable: true);

    public static IResult StorageFull() =>
        ServiceUnavailable(MailerErrorCodes.StorageFull, retryable: false);

    public static IResult ServiceUnavailable(string code, bool retryable) =>
        Results.Json(
            new MailerServiceUnavailableResponse(code, Retryable: retryable),
            MailerJsonContext.Default.MailerServiceUnavailableResponse,
            statusCode: StatusCodes.Status503ServiceUnavailable);

    public static IResult Accepted(MailRequestCreateResponse response) =>
        Results.Json(
            response,
            MailerJsonContext.Default.MailRequestCreateResponse,
            statusCode: StatusCodes.Status202Accepted);

    public static IResult Ok(MailRequestStatusResponse response) =>
        Results.Json(
            response,
            MailerJsonContext.Default.MailRequestStatusResponse,
            statusCode: StatusCodes.Status200OK);

    public static IResult Health(bool healthy) =>
        Results.Json(
            new HealthStatusResponse(healthy),
            MailerJsonContext.Default.HealthStatusResponse);

    public static IResult Ready(
        bool ready,
        int? statusCode = null,
        string? reason = null) =>
        Results.Json(
            new ReadyStatusResponse(ready, reason),
            MailerJsonContext.Default.ReadyStatusResponse,
            statusCode: statusCode);
}
