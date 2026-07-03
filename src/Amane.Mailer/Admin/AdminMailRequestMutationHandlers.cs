using Amane.Mailer.Data.Sqlite;
using Amane.Mailer.Data.Sqlite.Models;
using Amane.Mailer.Queue;
using Microsoft.AspNetCore.Antiforgery;

namespace Amane.Mailer.Admin;

public static class AdminMailRequestMutationHandlers
{
    public static async Task<IResult> RetryAsync(
        string id,
        HttpContext context,
        MailRequestRepository repository,
        AdminUserRepository userRepository,
        AdminAuditRepository auditRepository,
        IMailRequestQueue queue,
        MailerAdminOptions options,
        TimeProvider timeProvider,
        IAntiforgery antiforgery,
        ILoggerFactory loggerFactory,
        CancellationToken cancellationToken)
    {
        if (!Guid.TryParse(id, out var requestId))
            return Results.NotFound();

        var accessResult = await ResolveMutationAccessAsync(context, antiforgery, userRepository, cancellationToken);
        if (accessResult.Error is not null)
            return accessResult.Error;

        var now = timeProvider.GetUtcNow();
        var auditTemplate = BuildAuditTemplate(
            context,
            options,
            timeProvider,
            AdminAuditLog.EventTypes.ManualRetryRequested,
            requestId);

        var result = await repository.TryManualRetryAsync(
            requestId,
            accessResult.Access!.AllowedTenantIdsForQuery,
            now,
            auditRepository,
            auditTemplate,
            cancellationToken);

        return await CompleteMutationAsync(
            context,
            queue,
            loggerFactory,
            requestId,
            result,
            signalWorker: result.Status == ManualMailRequestMutationStatus.Succeeded,
            cancellationToken);
    }

    public static async Task<IResult> CancelAsync(
        string id,
        HttpContext context,
        MailRequestRepository repository,
        AdminUserRepository userRepository,
        AdminAuditRepository auditRepository,
        MailerAdminOptions options,
        TimeProvider timeProvider,
        IAntiforgery antiforgery,
        ILoggerFactory loggerFactory,
        CancellationToken cancellationToken)
    {
        if (!Guid.TryParse(id, out var requestId))
            return Results.NotFound();

        var accessResult = await ResolveMutationAccessAsync(context, antiforgery, userRepository, cancellationToken);
        if (accessResult.Error is not null)
            return accessResult.Error;

        var now = timeProvider.GetUtcNow();
        var auditTemplate = BuildAuditTemplate(
            context,
            options,
            timeProvider,
            AdminAuditLog.EventTypes.ManualCancelRequested,
            requestId);

        var result = await repository.TryManualCancelAsync(
            requestId,
            accessResult.Access!.AllowedTenantIdsForQuery,
            now,
            auditRepository,
            auditTemplate,
            cancellationToken);

        return await CompleteMutationAsync(
            context,
            queue: null,
            loggerFactory,
            requestId,
            result,
            signalWorker: false,
            cancellationToken);
    }

    private static async Task<(AdminTenantAccess? Access, IResult? Error)> ResolveMutationAccessAsync(
        HttpContext context,
        IAntiforgery antiforgery,
        AdminUserRepository userRepository,
        CancellationToken cancellationToken)
    {
        if (!await ValidateAntiforgeryAsync(context, antiforgery))
            return (null, Results.BadRequest("Invalid CSRF token."));

        var access = await userRepository.GetTenantAccessAsync(
            AdminAuditLog.ResolveActor(context),
            cancellationToken);
        if (access is null)
            return (null, Results.StatusCode(StatusCodes.Status403Forbidden));

        return (access, null);
    }

    private static async Task<IResult> CompleteMutationAsync(
        HttpContext context,
        IMailRequestQueue? queue,
        ILoggerFactory loggerFactory,
        Guid requestId,
        ManualMailRequestMutationResult result,
        bool signalWorker,
        CancellationToken cancellationToken)
    {
        _ = cancellationToken;

        if (result.Status == ManualMailRequestMutationStatus.Succeeded)
        {
            if (signalWorker && queue is not null && !queue.TrySignalWorkAvailable())
            {
                var logger = loggerFactory.CreateLogger(typeof(AdminMailRequestMutationHandlers));
                logger.LogWarning(
                    "WorkAvailable channel is full after manual retry for mail request {MailRequestId}; sweep will catch up.",
                    requestId);
            }

            return Results.Redirect($"/admin/mail-requests/{requestId:D}");
        }

        return result.Status switch
        {
            ManualMailRequestMutationStatus.NotFound => Results.NotFound(),
            ManualMailRequestMutationStatus.LockHeld or ManualMailRequestMutationStatus.InvalidState =>
                Results.Conflict(),
            _ => Results.Conflict(),
        };
    }

    private static AdminAuditEvent BuildAuditTemplate(
        HttpContext context,
        MailerAdminOptions options,
        TimeProvider timeProvider,
        string eventType,
        Guid requestId) =>
        AdminAuditLog.SanitizeForOutput(new AdminAuditEvent
        {
            EventType = eventType,
            Actor = AdminAuditLog.ResolveActor(context),
            OccurredAt = timeProvider.GetUtcNow(),
            SourceIp = options.ResolveAuditSourceIp(AdminAuditLog.ResolveSourceIp(context)),
            UserAgentSummary = AdminAuditLog.SummarizeUserAgent(context),
            TargetType = AdminAuditLog.TargetTypes.MailRequest,
            TargetId = requestId.ToString("D"),
            Result = AdminAuditLog.Results.Failure,
            ErrorCode = null,
        });

    private static async Task<bool> ValidateAntiforgeryAsync(HttpContext context, IAntiforgery antiforgery)
    {
        try
        {
            await antiforgery.ValidateRequestAsync(context);
            return true;
        }
        catch (AntiforgeryValidationException)
        {
            return false;
        }
    }
}
