using System.Globalization;
using System.Security.Claims;
using System.Text.Encodings.Web;
using Amane.Mailer.Data.Sqlite;
using Amane.Mailer.Data.Sqlite.Models;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication;

namespace Amane.Mailer.Admin;

public static class AdminAuthenticationHandlers
{
    // Keep unknown-user failures on the same PBKDF2 verification path as bad passwords.
    internal const string DummyAdminPasswordHash =
        "pbkdf2:sha256:600000:YW1hbmUtZHVtbXktMTI0IQ==:qMTLpvljgavl6UScZshWUdoApY4JFTGZWhLPJ62+Ui0=";

    public static IResult RenderLoginPage(HttpContext context, IAntiforgery antiforgery)
    {
        if (context.User.Identity?.IsAuthenticated == true)
            return Results.Redirect("/admin/mail-requests");

        context.Response.Headers.CacheControl = "no-store";
        var tokens = antiforgery.GetAndStoreTokens(context);
        var requestToken = HtmlEncoder.Default.Encode(tokens.RequestToken ?? string.Empty);
        var html = $$"""
            <!doctype html>
            <html lang="ja">
            <head>
              <meta charset="utf-8">
              <meta name="viewport" content="width=device-width, initial-scale=1">
              <title>Amane Admin</title>
              <style>
                html { color-scheme: light; font-family: system-ui, -apple-system, BlinkMacSystemFont, "Segoe UI", sans-serif; }
                body { margin: 0; min-height: 100vh; background: #f6f7f9; color: #1b1f27; }
                .admin-login-page { display: grid; place-items: center; }
                .login-shell { width: min(100% - 32px, 360px); }
                .login-form { display: grid; gap: 16px; padding: 24px; border: 1px solid #d7dbe3; border-radius: 8px; background: #ffffff; box-shadow: 0 12px 32px rgb(27 31 39 / 8%); }
                .login-form label { display: grid; gap: 6px; font-size: 0.9rem; font-weight: 600; }
                .login-form input { min-height: 40px; padding: 0 12px; border: 1px solid #c9ced8; border-radius: 6px; font: inherit; }
                .login-form button { min-height: 42px; border: 0; border-radius: 6px; background: #2458a6; color: #ffffff; font: inherit; font-weight: 700; }
              </style>
            </head>
            <body class="admin-login-page">
              <main class="login-shell">
                <form method="post" action="/admin/api/login" class="login-form">
                  <input type="hidden" name="__RequestVerificationToken" value="{{requestToken}}">
                  <label>
                    <span>Username</span>
                    <input name="username" autocomplete="username" required>
                  </label>
                  <label>
                    <span>Password</span>
                    <input name="password" type="password" autocomplete="current-password" required>
                  </label>
                  <button type="submit">Sign in</button>
                </form>
              </main>
            </body>
            </html>
            """;

        return Results.Content(html, "text/html; charset=utf-8");
    }

    public static async Task<IResult> LoginAsync(
        HttpContext context,
        IAntiforgery antiforgery,
        MailerAdminOptions options,
        AdminLoginThrottle throttle,
        AdminAuditRepository auditRepository,
        AdminSessionRepository sessionRepository,
        AdminUserRepository userRepository,
        ILoggerFactory loggerFactory,
        TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        var auditLogger = loggerFactory.CreateLogger(AdminAuditLog.LoggerCategory);
        if (!await ValidateAntiforgeryAsync(context, antiforgery))
            return Results.Text("Invalid CSRF token.", statusCode: StatusCodes.Status400BadRequest);

        IFormCollection form;
        try
        {
            form = await context.Request.ReadFormAsync(cancellationToken);
        }
        catch (InvalidDataException)
        {
            return Results.Text("Invalid form body.", statusCode: StatusCodes.Status400BadRequest);
        }

        var username = form["username"].ToString();
        var password = form["password"].ToString();
        var remoteAddress = context.Connection.RemoteIpAddress?.ToString() ?? "unknown";
        var normalizedActor = AdminAuditLog.NormalizeActor(username);

        var (isLocked, retryAfter) = await throttle.IsLockedWithRetryAfterAsync(
            username,
            remoteAddress,
            cancellationToken);
        if (isLocked)
        {
            await AdminAuditLog.WriteBestEffortAsync(
                auditRepository,
                auditLogger,
                BuildAuthAuditEvent(
                    context,
                    options,
                    timeProvider,
                    AdminAuditLog.EventTypes.LoginRateLimited,
                    AdminAuditLog.Results.Failure,
                    normalizedActor),
                cancellationToken);

            return TooManyRequests(context, retryAfter);
        }

        var user = await userRepository.GetActiveUserByUsernameAsync(username, cancellationToken);
        var passwordHash = user?.PasswordHash ?? DummyAdminPasswordHash;
        var passwordVerified = AdminPasswordHasher.Verify(password, passwordHash);
        if (user is null || !passwordVerified)
        {
            await AdminAuditLog.WriteBestEffortAsync(
                auditRepository,
                auditLogger,
                BuildAuthAuditEvent(
                    context,
                    options,
                    timeProvider,
                    AdminAuditLog.EventTypes.LoginFailed,
                    AdminAuditLog.Results.Failure,
                    normalizedActor),
                cancellationToken);

            var (locked, failureRetryAfter, lockCreated) = await throttle.RecordFailureAsync(
                username,
                remoteAddress,
                cancellationToken);

            if (lockCreated)
            {
                await AdminAuditLog.WriteBestEffortAsync(
                    auditRepository,
                    auditLogger,
                    BuildAuthAuditEvent(
                        context,
                        options,
                        timeProvider,
                        AdminAuditLog.EventTypes.AccountTemporarilyLocked,
                        AdminAuditLog.Results.Failure,
                        normalizedActor),
                    cancellationToken);
            }

            if (locked)
                return TooManyRequests(context, failureRetryAfter);

            return Results.Text("Invalid username or password.", statusCode: StatusCodes.Status401Unauthorized);
        }

        await throttle.ResetAsync(username, remoteAddress, cancellationToken);

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
            IssuedUtc = now,
            IsPersistent = false,
        };
        properties.Items[AdminAuthenticationConstants.AbsoluteExpiresUtcProperty] =
            absoluteExpiresAt.ToString("O", CultureInfo.InvariantCulture);
        properties.Items[AdminAuthenticationConstants.SessionIdProperty] = sessionId;

        await context.SignInAsync(
            AdminAuthenticationConstants.Scheme,
            new ClaimsPrincipal(identity),
            properties);

        await AdminAuditLog.WriteBestEffortAsync(
            auditRepository,
            auditLogger,
            BuildAuthAuditEvent(
                context,
                options,
                timeProvider,
                AdminAuditLog.EventTypes.LoginSucceeded,
                AdminAuditLog.Results.Success,
                user.Username,
                sessionId),
            cancellationToken);

        if (user.IsBreakGlass)
        {
            await AdminAuditLog.WriteBestEffortAsync(
                auditRepository,
                auditLogger,
                BuildAuthAuditEvent(
                    context,
                    options,
                    timeProvider,
                    AdminAuditLog.EventTypes.BreakGlassLoginSucceeded,
                    AdminAuditLog.Results.Success,
                    user.Username,
                    sessionId),
                cancellationToken);
        }

        return Results.Redirect("/admin");
    }

    public static async Task<IResult> LogoutAsync(
        HttpContext context,
        IAntiforgery antiforgery,
        MailerAdminOptions options,
        AdminSessionRepository sessionRepository,
        AdminAuditRepository auditRepository,
        ILoggerFactory loggerFactory,
        TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        if (!await ValidateAntiforgeryAsync(context, antiforgery))
            return Results.Text("Invalid CSRF token.", statusCode: StatusCodes.Status400BadRequest);

        var authResult = await context.AuthenticateAsync(AdminAuthenticationConstants.Scheme);
        var sessionId = authResult.Properties?.Items.TryGetValue(
            AdminAuthenticationConstants.SessionIdProperty,
            out var storedSessionId) == true
            ? storedSessionId
            : null;
        var actor = AdminAuditLog.ResolveActor(context);
        var now = timeProvider.GetUtcNow();

        if (!string.IsNullOrWhiteSpace(sessionId))
        {
            await sessionRepository.RevokeSessionAsync(
                sessionId,
                AdminSessionRevokeReasons.Logout,
                now,
                cancellationToken);
        }

        await context.SignOutAsync(AdminAuthenticationConstants.Scheme);

        var auditLogger = loggerFactory.CreateLogger(AdminAuditLog.LoggerCategory);
        await AdminAuditLog.WriteBestEffortAsync(
            auditRepository,
            auditLogger,
            BuildAuthAuditEvent(
                context,
                options,
                timeProvider,
                AdminAuditLog.EventTypes.Logout,
                AdminAuditLog.Results.Success,
                actor,
                sessionId),
            cancellationToken);

        return Results.Redirect("/admin/login");
    }

    internal static AdminAuditEvent BuildAuthAuditEvent(
        HttpContext context,
        MailerAdminOptions options,
        TimeProvider timeProvider,
        string eventType,
        string result,
        string actor,
        string? sessionId = null) =>
        new()
        {
            EventType = eventType,
            Actor = actor,
            OccurredAt = timeProvider.GetUtcNow(),
            SourceIp = options.ResolveAuditSourceIp(AdminAuditLog.ResolveSourceIp(context)),
            UserAgentSummary = AdminAuditLog.SummarizeUserAgent(context),
            TargetType = AdminAuditLog.TargetTypes.AdminSession,
            TargetId = sessionId,
            Result = result,
        };

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

    private static IResult TooManyRequests(HttpContext context, TimeSpan retryAfter)
    {
        var seconds = Math.Max(1, (int)Math.Ceiling(retryAfter.TotalSeconds));
        context.Response.Headers.RetryAfter = seconds.ToString(CultureInfo.InvariantCulture);
        return Results.Text("Too many failed login attempts.", statusCode: StatusCodes.Status429TooManyRequests);
    }
}
