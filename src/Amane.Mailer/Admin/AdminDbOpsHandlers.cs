using Amane.Mailer.Configuration;
using Amane.Mailer.Data.Sqlite;
using Amane.Mailer.Data.Sqlite.Models;
using Microsoft.AspNetCore.Antiforgery;

namespace Amane.Mailer.Admin;

public static class AdminDbOpsHandlers
{
    public static async Task<IResult> CheckpointAsync(
        HttpContext context,
        AdminDbOpsService dbOpsService,
        AdminUserRepository userRepository,
        AdminAuditRepository auditRepository,
        MailerAdminOptions adminOptions,
        MailerTenantRegistry tenantRegistry,
        IAntiforgery antiforgery,
        ILoggerFactory loggerFactory,
        TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        var gate = await ResolveAccessAsync(
            context,
            dbOpsService,
            userRepository,
            tenantRegistry,
            antiforgery,
            requireServiceWideBackup: true,
            cancellationToken);
        if (gate.Error is not null)
            return gate.Error;

        var auditLogger = loggerFactory.CreateLogger(AdminAuditLog.LoggerCategory);
        var result = await dbOpsService.RunCheckpointAsync(cancellationToken);
        var requestedEvent = BuildAuditEvent(
            context,
            adminOptions,
            timeProvider,
            AdminAuditLog.EventTypes.DbCheckpointRequested,
            AdminAuditLog.Results.Failure);
        return await CompleteCheckpointAsync(
            context,
            auditRepository,
            adminOptions,
            auditLogger,
            timeProvider,
            requestedEvent,
            result,
            cancellationToken);
    }

    public static async Task<IResult> BackupAsync(
        HttpContext context,
        AdminDbOpsService dbOpsService,
        AdminUserRepository userRepository,
        AdminAuditRepository auditRepository,
        MailerAdminOptions adminOptions,
        MailerTenantRegistry tenantRegistry,
        IAntiforgery antiforgery,
        ILoggerFactory loggerFactory,
        TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        var gate = await ResolveAccessAsync(
            context,
            dbOpsService,
            userRepository,
            tenantRegistry,
            antiforgery,
            requireServiceWideBackup: true,
            cancellationToken);
        if (gate.Error is not null)
            return gate.Error;

        var auditLogger = loggerFactory.CreateLogger(AdminAuditLog.LoggerCategory);
        var requestedEvent = BuildAuditEvent(
            context,
            adminOptions,
            timeProvider,
            AdminAuditLog.EventTypes.DbBackupRequested,
            AdminAuditLog.Results.Success,
            fieldName: AdminAuditLog.FieldNames.ConfiguredBackupDirectory);
        await AdminAuditLog.WriteBestEffortAsync(
            auditRepository,
            auditLogger,
            requestedEvent,
            cancellationToken);

        var result = await dbOpsService.RunBackupAsync(cancellationToken);
        return await CompleteBackupAsync(
            context,
            auditRepository,
            adminOptions,
            auditLogger,
            timeProvider,
            result,
            cancellationToken);
    }

    private static async Task<(AdminTenantAccess? Access, IResult? Error)> ResolveAccessAsync(
        HttpContext context,
        AdminDbOpsService dbOpsService,
        AdminUserRepository userRepository,
        MailerTenantRegistry tenantRegistry,
        IAntiforgery antiforgery,
        bool requireServiceWideBackup,
        CancellationToken cancellationToken)
    {
        if (!dbOpsService.IsEnabled)
            return (null, Results.NotFound());

        if (!await ValidateAntiforgeryAsync(context, antiforgery))
            return (null, Results.BadRequest("Invalid CSRF token."));

        var access = await userRepository.GetTenantAccessAsync(
            AdminAuditLog.ResolveActor(context),
            cancellationToken);
        if (access is null)
            return (null, Results.StatusCode(StatusCodes.Status403Forbidden));

        if (requireServiceWideBackup)
        {
            var canBackup = await userRepository.CanRunServiceWideBackupAsync(
                access.Username,
                tenantRegistry.ListTenants().Select(tenant => tenant.TenantId),
                cancellationToken);
            if (!canBackup)
                return (null, Results.StatusCode(StatusCodes.Status403Forbidden));
        }

        return (access, null);
    }

    private static async Task<IResult> CompleteCheckpointAsync(
        HttpContext context,
        AdminAuditRepository auditRepository,
        MailerAdminOptions adminOptions,
        ILogger auditLogger,
        TimeProvider timeProvider,
        AdminAuditEvent requestedEvent,
        AdminDbOpsResult result,
        CancellationToken cancellationToken)
    {
        return result.Status switch
        {
            AdminDbOpsStatus.Succeeded => await CompleteSuccessAsync(
                context,
                auditRepository,
                adminOptions,
                auditLogger,
                timeProvider,
                requestedEvent with { Result = AdminAuditLog.Results.Success },
                "/admin/ops",
                cancellationToken),
            AdminDbOpsStatus.LockHeld => await CompleteFailureAsync(
                context,
                auditRepository,
                adminOptions,
                auditLogger,
                timeProvider,
                requestedEvent,
                AdminAuditLog.ErrorCodes.LockHeld,
                StatusCodes.Status409Conflict,
                cancellationToken),
            AdminDbOpsStatus.Disabled => Results.NotFound(),
            _ => await CompleteFailureAsync(
                context,
                auditRepository,
                adminOptions,
                auditLogger,
                timeProvider,
                requestedEvent,
                AdminAuditLog.ErrorCodes.OperationFailed,
                StatusCodes.Status500InternalServerError,
                cancellationToken),
        };
    }

    private static async Task<IResult> CompleteBackupAsync(
        HttpContext context,
        AdminAuditRepository auditRepository,
        MailerAdminOptions adminOptions,
        ILogger auditLogger,
        TimeProvider timeProvider,
        AdminDbOpsBackupResult result,
        CancellationToken cancellationToken)
    {
        return result.Status switch
        {
            AdminDbOpsStatus.Succeeded => await CompleteBackupSuccessAsync(
                context,
                auditRepository,
                adminOptions,
                auditLogger,
                timeProvider,
                result.BackupFileName!,
                cancellationToken),
            AdminDbOpsStatus.LockHeld => await CompleteBackupFailureAsync(
                context,
                auditRepository,
                adminOptions,
                auditLogger,
                timeProvider,
                AdminAuditLog.ErrorCodes.LockHeld,
                StatusCodes.Status409Conflict,
                cancellationToken),
            AdminDbOpsStatus.Disabled => Results.NotFound(),
            _ => await CompleteBackupFailureAsync(
                context,
                auditRepository,
                adminOptions,
                auditLogger,
                timeProvider,
                AdminAuditLog.ErrorCodes.OperationFailed,
                StatusCodes.Status500InternalServerError,
                cancellationToken),
        };
    }

    private static async Task<IResult> CompleteSuccessAsync(
        HttpContext context,
        AdminAuditRepository auditRepository,
        MailerAdminOptions adminOptions,
        ILogger auditLogger,
        TimeProvider timeProvider,
        AdminAuditEvent auditEvent,
        string redirectUrl,
        CancellationToken cancellationToken)
    {
        await AdminAuditLog.WriteBestEffortAsync(
            auditRepository,
            auditLogger,
            auditEvent,
            cancellationToken);

        return SeeOtherRedirect(redirectUrl);
    }

    private static async Task<IResult> CompleteFailureAsync(
        HttpContext context,
        AdminAuditRepository auditRepository,
        MailerAdminOptions adminOptions,
        ILogger auditLogger,
        TimeProvider timeProvider,
        AdminAuditEvent auditEvent,
        string errorCode,
        int statusCode,
        CancellationToken cancellationToken)
    {
        await AdminAuditLog.WriteBestEffortAsync(
            auditRepository,
            auditLogger,
            auditEvent with { ErrorCode = errorCode },
            cancellationToken);

        return Results.StatusCode(statusCode);
    }

    private static async Task<IResult> CompleteBackupSuccessAsync(
        HttpContext context,
        AdminAuditRepository auditRepository,
        MailerAdminOptions adminOptions,
        ILogger auditLogger,
        TimeProvider timeProvider,
        string backupFileName,
        CancellationToken cancellationToken)
    {
        await AdminAuditLog.WriteBestEffortAsync(
            auditRepository,
            auditLogger,
            BuildAuditEvent(
                context,
                adminOptions,
                timeProvider,
                AdminAuditLog.EventTypes.DbBackupCompleted,
                AdminAuditLog.Results.Success,
                targetId: backupFileName,
                fieldName: AdminAuditLog.FieldNames.ConfiguredBackupDirectory),
            cancellationToken);

        return SeeOtherRedirect("/admin/ops");
    }

    private static async Task<IResult> CompleteBackupFailureAsync(
        HttpContext context,
        AdminAuditRepository auditRepository,
        MailerAdminOptions adminOptions,
        ILogger auditLogger,
        TimeProvider timeProvider,
        string errorCode,
        int statusCode,
        CancellationToken cancellationToken)
    {
        await AdminAuditLog.WriteBestEffortAsync(
            auditRepository,
            auditLogger,
            BuildAuditEvent(
                context,
                adminOptions,
                timeProvider,
                AdminAuditLog.EventTypes.DbBackupFailed,
                AdminAuditLog.Results.Failure,
                fieldName: AdminAuditLog.FieldNames.ConfiguredBackupDirectory,
                errorCode: errorCode),
            cancellationToken);

        return Results.StatusCode(statusCode);
    }

    private static AdminAuditEvent BuildAuditEvent(
        HttpContext context,
        MailerAdminOptions options,
        TimeProvider timeProvider,
        string eventType,
        string result,
        string? targetId = null,
        string? fieldName = null,
        string? errorCode = null) =>
        AdminAuditLog.SanitizeForOutput(new AdminAuditEvent
        {
            EventType = eventType,
            Actor = AdminAuditLog.ResolveActor(context),
            OccurredAt = timeProvider.GetUtcNow(),
            SourceIp = options.ResolveAuditSourceIp(AdminAuditLog.ResolveSourceIp(context)),
            UserAgentSummary = AdminAuditLog.SummarizeUserAgent(context),
            TargetType = AdminAuditLog.TargetTypes.DbOps,
            TargetId = targetId,
            FieldName = fieldName,
            Result = result,
            ErrorCode = errorCode,
        });

    private static IResult SeeOtherRedirect(string url) => new SeeOtherRedirectResult(url);

    private sealed class SeeOtherRedirectResult(string url) : IResult
    {
        public Task ExecuteAsync(HttpContext httpContext)
        {
            httpContext.Response.StatusCode = StatusCodes.Status303SeeOther;
            httpContext.Response.Headers.Location = url;
            return Task.CompletedTask;
        }
    }

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
