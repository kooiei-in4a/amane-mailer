using Amane.Mailer.Configuration;
using Amane.Mailer.Contracts.MailRequests;
using Amane.Mailer.Data.Sqlite;
using Amane.Mailer.Data.Sqlite.Models;
using Amane.Mailer.Json;
using Amane.Mailer.Identity;
using Amane.Mailer.Operations;
using Amane.Mailer.Queue;
using Amane.Mailer.Webhooks;

namespace Amane.Mailer.Api;

/// <summary>
/// HTTP route mapping and conversion for mail-request endpoints.
/// Use-case logic lives in dedicated handlers (#348).
/// </summary>
public static class MailRequestEndpoints
{
    public static IEndpointRouteBuilder MapMailRequestEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapPost("/api/mail-requests", CreateMailRequestAsync);
        endpoints.MapGet("/api/mail-requests/{mailRequestId}", GetMailRequestStatusAsync);
        endpoints.MapPost("/api/mail-requests/{mailRequestId}/cancel", CancelMailRequestAsync);
        endpoints.MapPost("/api/mail-requests/{mailRequestId}/reschedule", RescheduleMailRequestAsync);
        return endpoints;
    }

    private static async Task<IResult> GetMailRequestStatusAsync(
        string mailRequestId,
        HttpContext context,
        MailRequestRepository repository,
        SenderRepository senders,
        ApiAuthenticationRateLimiter rateLimiter,
        CancellationToken cancellationToken)
    {
        var authorization = await ApiKeyRequestAuthorizer.AuthorizeAsync(
            context, senders, rateLimiter, cancellationToken);
        if (authorization.Error is not null)
        {
            return authorization.Error;
        }

        if (!TryParseMailRequestId(mailRequestId, out var parsedMailRequestId, out var parseError))
        {
            return parseError!;
        }

        MailRequestStatusRow? statusRow;
        try
        {
            statusRow = await repository.GetStatusByIdempotencyKeyAsync(
                V2PersistenceCompatibility.ToPhysicalTenantId(authorization.Identity!.Sender.SenderId),
                V2PersistenceCompatibility.SourceService,
                parsedMailRequestId,
                cancellationToken);
        }
        catch (Exception ex) when (MailRequestHttpErrorMapper.IsStorageFullDatabaseException(ex))
        {
            return MailRequestHttpErrorMapper.StorageFull();
        }
        catch (Exception ex) when (MailRequestHttpErrorMapper.IsTransientDatabaseException(ex))
        {
            return MailRequestHttpErrorMapper.ServiceUnavailable();
        }

        if (statusRow is null)
        {
            return MailRequestHttpErrorMapper.Error(
                StatusCodes.Status404NotFound,
                MailerErrorCodes.NotFound);
        }

        return MailRequestHttpErrorMapper.StatusOk(statusRow);
    }

    private static async Task<IResult> CancelMailRequestAsync(
        string mailRequestId,
        HttpContext context,
        MailRequestRepository repository,
        DeliveryEventEnqueuer deliveryEventEnqueuer,
        SenderRepository senders,
        ApiAuthenticationRateLimiter rateLimiter,
        TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        var authorization = await ApiKeyRequestAuthorizer.AuthorizeAsync(
            context, senders, rateLimiter, cancellationToken);
        if (authorization.Error is not null)
        {
            return authorization.Error;
        }

        if (!TryParseMailRequestId(mailRequestId, out var parsedMailRequestId, out var parseError))
        {
            return parseError!;
        }

        return await MailRequestMutationHandler.CancelAsync(
            V2PersistenceCompatibility.ToPhysicalTenantId(authorization.Identity!.Sender.SenderId),
            V2PersistenceCompatibility.SourceService,
            parsedMailRequestId,
            repository,
            deliveryEventEnqueuer,
            timeProvider,
            cancellationToken);
    }

    private static async Task<IResult> RescheduleMailRequestAsync(
        string mailRequestId,
        HttpContext context,
        MailRequestRepository repository,
        IMailRequestQueue queue,
        SenderRepository senders,
        ApiAuthenticationRateLimiter rateLimiter,
        TimeProvider timeProvider,
        ILoggerFactory loggerFactory,
        CancellationToken cancellationToken)
    {
        var authorization = await ApiKeyRequestAuthorizer.AuthorizeAsync(
            context, senders, rateLimiter, cancellationToken);
        if (authorization.Error is not null)
        {
            return authorization.Error;
        }

        if (!TryParseMailRequestId(mailRequestId, out var parsedMailRequestId, out var parseError))
        {
            return parseError!;
        }

        var bodyRead = await MailRequestRequestReader.ReadAsync(context.Request, cancellationToken);
        if (!bodyRead.Succeeded)
        {
            return MailRequestHttpErrorMapper.FromBodyReadFailure(bodyRead.Failure!.Value);
        }

        var jsonRead = MailRequestRequestReader.DeserializeStrict(
            bodyRead.Body!,
            MailerJsonContext.Default.MailRequestRescheduleRequest);
        if (!jsonRead.Succeeded)
        {
            return MailRequestHttpErrorMapper.FromJsonReadFailure(jsonRead.Failure!.Value);
        }

        return await MailRequestMutationHandler.RescheduleAsync(
            V2PersistenceCompatibility.ToPhysicalTenantId(authorization.Identity!.Sender.SenderId),
            V2PersistenceCompatibility.SourceService,
            parsedMailRequestId,
            jsonRead.Value!,
            repository,
            queue,
            timeProvider,
            loggerFactory,
            cancellationToken);
    }

    private static async Task<IResult> CreateMailRequestAsync(
        HttpContext context,
        MailRequestRepository repository,
        IMailRequestQueue queue,
        SenderRepository senders,
        SenderDeliveryConfigurationAdapter senderConfiguration,
        ApiAuthenticationRateLimiter rateLimiter,
        Amane.Mailer.Attachments.Spool.AttachmentSpool attachmentSpool,
        MailerRuntimeMetrics runtimeMetrics,
        TimeProvider timeProvider,
        ILoggerFactory loggerFactory,
        CancellationToken cancellationToken)
    {
        var authorization = await ApiKeyRequestAuthorizer.AuthorizeAsync(
            context, senders, rateLimiter, cancellationToken);
        if (authorization.Error is not null)
        {
            return authorization.Error;
        }

        var httpRequest = context.Request;
        var logger = loggerFactory.CreateLogger("MailRequestEndpoints");
        if (MailRequestRequestReader.IsContentLengthTooLarge(
                httpRequest,
                MailRequestRequestReader.MaxAttachmentCapableRequestBodyBytes))
        {
            return MailRequestHttpErrorMapper.FromBodyReadFailure(MailRequestBodyReadFailure.TooLarge);
        }

        var bodyRead = await MailRequestRequestReader.ReadAsync(
            httpRequest,
            cancellationToken,
            MailRequestRequestReader.MaxAttachmentCapableRequestBodyBytes);
        if (!bodyRead.Succeeded)
        {
            return MailRequestHttpErrorMapper.FromBodyReadFailure(bodyRead.Failure!.Value);
        }

        var jsonRead = MailRequestRequestReader.DeserializeStrict(
            bodyRead.Body!,
            MailerJsonContext.Default.MailRequestCreateRequest);
        if (!jsonRead.Succeeded)
        {
            return MailRequestHttpErrorMapper.FromJsonReadFailure(jsonRead.Failure!.Value);
        }

        return await MailRequestCreateHandler.HandleAsync(
            jsonRead.Value!,
            bodyRead.Body!,
            authorization.Identity!,
            senderConfiguration.Resolve(authorization.Identity!.Sender),
            repository,
            queue,
            attachmentSpool,
            timeProvider,
            logger,
            cancellationToken,
            runtimeMetrics);
    }

    private static bool TryParseMailRequestId(
        string value,
        out Guid mailRequestId,
        out IResult? error)
    {
        if (Guid.TryParse(value, out mailRequestId))
        {
            error = null;
            return true;
        }

        error = MailerJsonResults.ValidationError(
            MailerErrorCodes.InvalidRequest,
            "mail_request_id must be a UUID.",
            StatusCodes.Status400BadRequest);
        return false;
    }

    // Test seam retained for scheduled dispatch characterization tests.
    internal static bool IsDispatchableQueued(MailRequestIdempotencyRow row, DateTimeOffset now) =>
        MailRequestCreateHandler.IsDispatchableQueued(row, now);
}
