using Amane.Mailer.Configuration;
using Amane.Mailer.Contracts.MailRequests;
using Amane.Mailer.Data.Sqlite;
using Amane.Mailer.Data.Sqlite.Models;
using Amane.Mailer.Json;
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
        endpoints.MapPost("/internal/mail-requests", CreateMailRequestAsync);
        endpoints.MapGet("/internal/mail-requests/{mailRequestId}", GetMailRequestStatusAsync);
        endpoints.MapPost("/internal/mail-requests/{mailRequestId}/cancel", CancelMailRequestAsync);
        endpoints.MapPost("/internal/mail-requests/{mailRequestId}/reschedule", RescheduleMailRequestAsync);
        return endpoints;
    }

    private static async Task<IResult> GetMailRequestStatusAsync(
        string mailRequestId,
        HttpRequest httpRequest,
        MailRequestRepository repository,
        MailerTenantRegistry tenantRegistry,
        CancellationToken cancellationToken)
    {
        if (!TenantRequestAuthorizer.TryAuthorizeScoped(
                httpRequest,
                tenantRegistry,
                mailRequestId,
                out var tenantId,
                out var sourceService,
                out var parsedMailRequestId,
                out var authError))
        {
            return authError!;
        }

        MailRequestStatusRow? statusRow;
        try
        {
            statusRow = await repository.GetStatusByIdempotencyKeyAsync(
                tenantId,
                sourceService,
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
        HttpRequest httpRequest,
        MailRequestRepository repository,
        DeliveryEventEnqueuer deliveryEventEnqueuer,
        MailerTenantRegistry tenantRegistry,
        TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        if (!TenantRequestAuthorizer.TryAuthorizeScoped(
                httpRequest,
                tenantRegistry,
                mailRequestId,
                out var tenantId,
                out var sourceService,
                out var parsedMailRequestId,
                out var authError))
        {
            return authError!;
        }

        return await MailRequestMutationHandler.CancelAsync(
            tenantId,
            sourceService,
            parsedMailRequestId,
            repository,
            deliveryEventEnqueuer,
            timeProvider,
            cancellationToken);
    }

    private static async Task<IResult> RescheduleMailRequestAsync(
        string mailRequestId,
        HttpRequest httpRequest,
        MailRequestRepository repository,
        IMailRequestQueue queue,
        MailerTenantRegistry tenantRegistry,
        TimeProvider timeProvider,
        ILoggerFactory loggerFactory,
        CancellationToken cancellationToken)
    {
        if (!TenantRequestAuthorizer.TryAuthorizeScoped(
                httpRequest,
                tenantRegistry,
                mailRequestId,
                out var tenantId,
                out var sourceService,
                out var parsedMailRequestId,
                out var authError))
        {
            return authError!;
        }

        var bodyRead = await MailRequestRequestReader.ReadAsync(httpRequest, cancellationToken);
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
            tenantId,
            sourceService,
            parsedMailRequestId,
            jsonRead.Value!,
            repository,
            queue,
            timeProvider,
            loggerFactory,
            cancellationToken);
    }

    private static async Task<IResult> CreateMailRequestAsync(
        HttpRequest httpRequest,
        MailRequestRepository repository,
        IMailRequestQueue queue,
        MailerTenantRegistry tenantRegistry,
        Amane.Mailer.Attachments.Spool.AttachmentSpool attachmentSpool,
        TimeProvider timeProvider,
        ILoggerFactory loggerFactory,
        CancellationToken cancellationToken)
    {
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
            httpRequest,
            jsonRead.Value!,
            bodyRead.Body!,
            repository,
            queue,
            tenantRegistry,
            attachmentSpool,
            timeProvider,
            logger,
            cancellationToken);
    }

    // Test seam retained for scheduled dispatch characterization tests.
    internal static bool IsDispatchableQueued(MailRequestIdempotencyRow row, DateTimeOffset now) =>
        MailRequestCreateHandler.IsDispatchableQueued(row, now);
}
