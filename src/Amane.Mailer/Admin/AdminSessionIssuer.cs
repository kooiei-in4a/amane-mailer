using System.Globalization;
using System.Security.Claims;
using Amane.Mailer.Data.Sqlite;
using Amane.Mailer.Data.Sqlite.Models;
using Microsoft.AspNetCore.Authentication;

namespace Amane.Mailer.Admin;

/// <summary>
/// Shared AmaneAdmin session cookie issuance for password and Google login.
/// Workflow setup-verification sessions stay on the password login path.
/// </summary>
internal static class AdminSessionIssuer
{
    public static async Task<string> SignInAsync(
        HttpContext context,
        AdminUserRow user,
        MailerAdminOptions options,
        AdminSessionRepository sessionRepository,
        TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        var absoluteExpiresAt = now + options.SessionAbsoluteLifetime;
        var idleExpiresAt = now + options.SessionIdleTimeout;
        var sessionId = AdminSessionIds.CreateNew();
        var session = new AdminSessionRow(
            sessionId,
            user.Username,
            now,
            now,
            absoluteExpiresAt,
            idleExpiresAt,
            null,
            null,
            user.CredentialEpoch);

        await sessionRepository.CreateSessionAsync(
            session,
            options.MaxConcurrentSessions,
            cancellationToken);

        await SignInExistingAsync(context, user, sessionId, now, absoluteExpiresAt);
        return sessionId;
    }

    public static Task SignInExistingAsync(
        HttpContext context,
        AdminUserRow user,
        string sessionId,
        DateTimeOffset issuedAt,
        DateTimeOffset absoluteExpiresAt)
    {
        var claims = new[]
        {
            new Claim(ClaimTypes.NameIdentifier, user.Id.ToString(CultureInfo.InvariantCulture)),
            new Claim(ClaimTypes.Name, user.Username),
        };
        var identity = new ClaimsIdentity(claims, AdminAuthenticationConstants.Scheme);
        var properties = new AuthenticationProperties
        {
            AllowRefresh = false,
            // Absolute lifetime only: idle timeout is enforced from admin_sessions on each
            // request. Avoids touch-time Set-Cookie races that can regress browser expiry (#391).
            ExpiresUtc = absoluteExpiresAt,
            IssuedUtc = issuedAt,
            IsPersistent = false,
        };
        properties.Items[AdminAuthenticationConstants.AbsoluteExpiresUtcProperty] =
            absoluteExpiresAt.ToString("O", CultureInfo.InvariantCulture);
        properties.Items[AdminAuthenticationConstants.SessionIdProperty] = sessionId;

        return context.SignInAsync(
            AdminAuthenticationConstants.Scheme,
            new ClaimsPrincipal(identity),
            properties);
    }
}
