using Amane.Mailer.Data.Sqlite;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;

namespace Amane.Mailer.Admin;

/// <summary>
/// Server-side Admin session cookie validation and revocation
/// (absolute / idle expiry, credential epoch, missing session).
/// </summary>
internal static class AdminCookieValidator
{
    internal static async Task ValidateAsync(CookieValidatePrincipalContext context)
    {
        var services = context.HttpContext.RequestServices;
        var options = services.GetRequiredService<MailerAdminOptions>();
        var timeProvider = services.GetRequiredService<TimeProvider>();
        var sessionRepository = services.GetRequiredService<AdminSessionRepository>();
        var userRepository = services.GetRequiredService<AdminUserRepository>();
        var auditRepository = services.GetRequiredService<AdminAuditRepository>();
        var sessionExpiredDedupe = services.GetRequiredService<AdminSessionExpiredDedupe>();
        var loggerFactory = services.GetRequiredService<ILoggerFactory>();
        var now = timeProvider.GetUtcNow();

        if (!context.Properties.Items.TryGetValue(
                AdminAuthenticationConstants.SessionIdProperty,
                out var sessionId)
            || string.IsNullOrWhiteSpace(sessionId))
        {
            await RejectSessionAsync(
                context,
                sessionRepository,
                null,
                AdminSessionRevokeReasons.Invalid,
                options,
                auditRepository,
                sessionExpiredDedupe,
                loggerFactory,
                timeProvider,
                now,
                recordSessionExpired: false);
            return;
        }

        var session = await sessionRepository.GetSessionAsync(sessionId, context.HttpContext.RequestAborted);
        var user = session is null
            ? null
            : await userRepository.GetActiveUserByUsernameAsync(session.Actor, context.HttpContext.RequestAborted);
        if (session is null
            || session.RevokedAt is not null
            || user is null
            || session.CredentialEpoch != user.CredentialEpoch)
        {
            await RejectSessionAsync(
                context,
                sessionRepository,
                sessionId,
                AdminSessionRevokeReasons.Invalid,
                options,
                auditRepository,
                sessionExpiredDedupe,
                loggerFactory,
                timeProvider,
                now,
                recordSessionExpired: false);
            return;
        }

        string? revokeReason = null;
        if (session.AbsoluteExpiresAt <= now)
            revokeReason = AdminSessionRevokeReasons.AbsoluteExpired;
        else if (session.IdleExpiresAt <= now)
            revokeReason = AdminSessionRevokeReasons.IdleExpired;

        if (revokeReason is not null)
        {
            await RejectSessionAsync(
                context,
                sessionRepository,
                sessionId,
                revokeReason,
                options,
                auditRepository,
                sessionExpiredDedupe,
                loggerFactory,
                timeProvider,
                now,
                recordSessionExpired: true,
                actor: session.Actor);
            return;
        }

        var proposedIdleExpiresAt = now + options.SessionIdleTimeout;
        var touchInterval = AdminSessionTouch.ResolveInterval(options.SessionIdleTimeout);
        var touch = await sessionRepository.TryTouchAsync(
            sessionId,
            now,
            proposedIdleExpiresAt,
            touchInterval,
            context.HttpContext.RequestAborted);

        // Cookie renewal is tied to the atomic DB touch winner only (#391).
        // SlidingExpiration is disabled so the framework cannot Set-Cookie without a touch.
        // IssuedUtc must advance with ExpiresUtc so RequestRefresh computes
        // refreshExpires = now + (ExpiresUtc - IssuedUtc) == IdleExpiresAt.
        if (touch is not null)
        {
            context.Properties.IssuedUtc = now;
            context.Properties.ExpiresUtc = touch.IdleExpiresAt;
            context.ShouldRenew = true;
        }
    }

    private static async Task RejectSessionAsync(
        CookieValidatePrincipalContext context,
        AdminSessionRepository sessionRepository,
        string? sessionId,
        string revokeReason,
        MailerAdminOptions options,
        AdminAuditRepository auditRepository,
        AdminSessionExpiredDedupe sessionExpiredDedupe,
        ILoggerFactory loggerFactory,
        TimeProvider timeProvider,
        DateTimeOffset now,
        bool recordSessionExpired,
        string? actor = null)
    {
        if (!string.IsNullOrWhiteSpace(sessionId))
        {
            await sessionRepository.RevokeSessionAsync(
                sessionId,
                revokeReason,
                now,
                context.HttpContext.RequestAborted);

            if (recordSessionExpired && sessionExpiredDedupe.ShouldRecord(sessionId))
            {
                var auditLogger = loggerFactory.CreateLogger(AdminAuditLog.LoggerCategory);
                await AdminAuditLog.WriteBestEffortAsync(
                    auditRepository,
                    auditLogger,
                    AdminAuthenticationHandlers.BuildAuthAuditEvent(
                        context.HttpContext,
                        options,
                        timeProvider,
                        AdminAuditLog.EventTypes.SessionExpired,
                        AdminAuditLog.Results.Failure,
                        actor ?? AdminAuditLog.ResolveActor(context.HttpContext),
                        sessionId),
                    context.HttpContext.RequestAborted);
            }
        }

        context.RejectPrincipal();
        await context.HttpContext.SignOutAsync(AdminAuthenticationConstants.Scheme);
    }
}
