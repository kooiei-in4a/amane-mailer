using System.Text.Encodings.Web;
using Amane.Mailer.Data.Sqlite;
using Amane.Mailer.Data.Sqlite.Models;
using Amane.Mailer.Identity;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.Data.Sqlite;

namespace Amane.Mailer.Admin;

public static class AdminSenderMutationHandlers
{
    public static async Task<IResult> CreateAsync(
        HttpContext context,
        SenderRepository senderRepository,
        AdminUserRepository userRepository,
        AdminAuditRepository auditRepository,
        MailerAdminOptions options,
        IAntiforgery antiforgery,
        ILoggerFactory loggerFactory,
        TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        var gate = await ValidateMutationAsync(context, antiforgery, userRepository, cancellationToken);
        if (gate is not null)
            return gate;

        var form = await ReadFormAsync(context, cancellationToken);
        if (form is null || !HasConfirmation(form))
            return Results.BadRequest("Explicit confirmation is required.");

        var email = form["email"].ToString();
        var displayName = form["display_name"].ToString();
        if (displayName.Length > 200 || displayName.Any(char.IsControl))
            return Results.BadRequest("Display name is invalid.");

        try
        {
            if (await senderRepository.FindByEmailAsync(email, cancellationToken) is not null)
                return Results.Conflict();

            var sender = await senderRepository.CreateAsync(email, displayName, cancellationToken);
            await WriteAuditAsync(
                context,
                auditRepository,
                options,
                loggerFactory,
                timeProvider,
                AdminAuditLog.EventTypes.SenderCreated,
                AdminAuditLog.TargetTypes.Sender,
                sender.SenderId,
                sender.SenderId,
                cancellationToken);
            return SeeOther($"/admin/senders/{sender.SenderId:D}");
        }
        catch (ArgumentException)
        {
            return Results.BadRequest("Sender input is invalid.");
        }
        catch (SqliteException ex) when (ex.SqliteErrorCode == 19)
        {
            return Results.Conflict();
        }
    }

    public static async Task<IResult> EnableAsync(
        Guid senderId,
        HttpContext context,
        SenderRepository senderRepository,
        AdminUserRepository userRepository,
        AdminAuditRepository auditRepository,
        MailerAdminOptions options,
        IAntiforgery antiforgery,
        ILoggerFactory loggerFactory,
        TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        return await SetSenderEnabledAsync(
            senderId,
            enabled: true,
            context,
            senderRepository,
            userRepository,
            auditRepository,
            options,
            antiforgery,
            loggerFactory,
            timeProvider,
            cancellationToken);
    }

    public static async Task<IResult> DisableAsync(
        Guid senderId,
        HttpContext context,
        SenderRepository senderRepository,
        AdminUserRepository userRepository,
        AdminAuditRepository auditRepository,
        MailerAdminOptions options,
        IAntiforgery antiforgery,
        ILoggerFactory loggerFactory,
        TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        return await SetSenderEnabledAsync(
            senderId,
            enabled: false,
            context,
            senderRepository,
            userRepository,
            auditRepository,
            options,
            antiforgery,
            loggerFactory,
            timeProvider,
            cancellationToken);
    }

    public static async Task<IResult> CreateApiKeyAsync(
        Guid senderId,
        HttpContext context,
        SenderRepository senderRepository,
        AdminUserRepository userRepository,
        AdminAuditRepository auditRepository,
        AdminDeadLetterCountCache deadLetterCountCache,
        MailRequestRepository mailRequestRepository,
        MailerAdminOptions options,
        IAntiforgery antiforgery,
        ILoggerFactory loggerFactory,
        TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        var gate = await ValidateMutationAsync(context, antiforgery, userRepository, cancellationToken);
        if (gate is not null)
            return gate;

        var form = await ReadFormAsync(context, cancellationToken);
        if (form is null || !HasConfirmation(form))
            return Results.BadRequest("Explicit confirmation is required.");

        var sender = await senderRepository.FindAsync(senderId, cancellationToken);
        if (sender is null)
            return Results.NotFound();

        var name = form["name"].ToString().Trim();
        if (name.Length is 0 or > 200 || name.Any(char.IsControl))
            return Results.BadRequest("API key name is invalid.");

        CreatedApiKey created;
        try
        {
            created = await senderRepository.CreateApiKeyAsync(senderId, name, cancellationToken);
        }
        catch (ArgumentException)
        {
            return Results.BadRequest("API key name is invalid.");
        }
        catch (SqliteException ex) when (ex.SqliteErrorCode == 19)
        {
            return Results.Conflict();
        }

        await WriteAuditAsync(
            context,
            auditRepository,
            options,
            loggerFactory,
            timeProvider,
            AdminAuditLog.EventTypes.ApiKeyCreated,
            AdminAuditLog.TargetTypes.ApiKey,
            created.KeyId,
            senderId,
            cancellationToken);

        var keys = await senderRepository.ListApiKeysAsync(senderId, cancellationToken);
        var deadLetterCount = await deadLetterCountCache.GetCountAsync(
            mailRequestRepository,
            null,
            cancellationToken);
        var csrfToken = HtmlEncoder.Default.Encode(
            antiforgery.GetAndStoreTokens(context).RequestToken ?? string.Empty);
        context.Response.Headers.CacheControl = "no-store";
        context.Response.Headers.Pragma = "no-cache";
        return Results.Content(
            AdminSendersPage.RenderDetailHtml(sender, keys, deadLetterCount, csrfToken, created),
            "text/html; charset=utf-8");
    }

    public static async Task<IResult> RevokeApiKeyAsync(
        Guid senderId,
        Guid keyId,
        HttpContext context,
        SenderRepository senderRepository,
        AdminUserRepository userRepository,
        AdminAuditRepository auditRepository,
        MailerAdminOptions options,
        IAntiforgery antiforgery,
        ILoggerFactory loggerFactory,
        TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        var gate = await ValidateMutationAsync(context, antiforgery, userRepository, cancellationToken);
        if (gate is not null)
            return gate;

        var form = await ReadFormAsync(context, cancellationToken);
        if (form is null || !HasConfirmation(form))
            return Results.BadRequest("Explicit confirmation is required.");

        if (await senderRepository.FindAsync(senderId, cancellationToken) is null)
            return Results.NotFound();

        if (!await senderRepository.RevokeApiKeyAsync(senderId, keyId, cancellationToken))
            return Results.NotFound();

        await WriteAuditAsync(
            context,
            auditRepository,
            options,
            loggerFactory,
            timeProvider,
            AdminAuditLog.EventTypes.ApiKeyRevoked,
            AdminAuditLog.TargetTypes.ApiKey,
            keyId,
            senderId,
            cancellationToken);
        context.Response.Headers.CacheControl = "no-store";
        return SeeOther($"/admin/senders/{senderId:D}");
    }

    internal static bool HasConfirmation(IFormCollection form) =>
        string.Equals(form["confirmation"].ToString(), "confirm", StringComparison.Ordinal)
        || string.Equals(form["confirm"].ToString(), "on", StringComparison.Ordinal)
        || string.Equals(form["confirm"].ToString(), "true", StringComparison.OrdinalIgnoreCase);

    private static async Task<IResult?> ValidateMutationAsync(
        HttpContext context,
        IAntiforgery antiforgery,
        AdminUserRepository userRepository,
        CancellationToken cancellationToken)
    {
        if (!await ValidateAntiforgeryAsync(context, antiforgery))
            return Results.BadRequest("Invalid CSRF token.");

        var accessResult = await AdminManagedConfigurationAuthorization.RequireInstanceOwnerAsync(
            context,
            userRepository,
            cancellationToken);
        return accessResult.Error;
    }

    private static async Task<IFormCollection?> ReadFormAsync(
        HttpContext context,
        CancellationToken cancellationToken)
    {
        try
        {
            return await context.Request.ReadFormAsync(cancellationToken);
        }
        catch (InvalidDataException)
        {
            return null;
        }
    }

    private static async Task<IResult> SetSenderEnabledAsync(
        Guid senderId,
        bool enabled,
        HttpContext context,
        SenderRepository senderRepository,
        AdminUserRepository userRepository,
        AdminAuditRepository auditRepository,
        MailerAdminOptions options,
        IAntiforgery antiforgery,
        ILoggerFactory loggerFactory,
        TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        var gate = await ValidateMutationAsync(context, antiforgery, userRepository, cancellationToken);
        if (gate is not null)
            return gate;

        var form = await ReadFormAsync(context, cancellationToken);
        if (form is null || !HasConfirmation(form))
            return Results.BadRequest("Explicit confirmation is required.");

        var sender = await senderRepository.FindAsync(senderId, cancellationToken);
        if (sender is null)
            return Results.NotFound();

        if (enabled)
            await senderRepository.EnableAsync(senderId, cancellationToken);
        else
            await senderRepository.DisableAsync(senderId, cancellationToken);

        await WriteAuditAsync(
            context,
            auditRepository,
            options,
            loggerFactory,
            timeProvider,
            enabled ? AdminAuditLog.EventTypes.SenderEnabled : AdminAuditLog.EventTypes.SenderDisabled,
            AdminAuditLog.TargetTypes.Sender,
            senderId,
            senderId,
            cancellationToken);
        context.Response.Headers.CacheControl = "no-store";
        return SeeOther($"/admin/senders/{senderId:D}");
    }

    private static async Task WriteAuditAsync(
        HttpContext context,
        AdminAuditRepository auditRepository,
        MailerAdminOptions options,
        ILoggerFactory loggerFactory,
        TimeProvider timeProvider,
        string eventType,
        string targetType,
        Guid targetId,
        Guid? tenantId,
        CancellationToken cancellationToken)
    {
        await AdminAuditLog.WriteBestEffortAsync(
            auditRepository,
            loggerFactory.CreateLogger(AdminAuditLog.LoggerCategory),
            AdminAuditLog.SanitizeForOutput(new AdminAuditEvent
            {
                EventType = eventType,
                Actor = AdminAuditLog.ResolveActor(context),
                OccurredAt = timeProvider.GetUtcNow(),
                SourceIp = options.ResolveAuditSourceIp(AdminAuditLog.ResolveSourceIp(context)),
                UserAgentSummary = AdminAuditLog.SummarizeUserAgent(context),
                TargetType = targetType,
                TargetId = targetId.ToString("D"),
                TenantId = tenantId,
                Result = AdminAuditLog.Results.Success,
            }),
            cancellationToken);
    }

    private static async Task<bool> ValidateAntiforgeryAsync(
        HttpContext context,
        IAntiforgery antiforgery)
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

    private static IResult SeeOther(string url) => new SeeOtherRedirectResult(url);

    private sealed class SeeOtherRedirectResult(string url) : IResult
    {
        public Task ExecuteAsync(HttpContext httpContext)
        {
            httpContext.Response.StatusCode = StatusCodes.Status303SeeOther;
            httpContext.Response.Headers.Location = url;
            return Task.CompletedTask;
        }
    }
}
