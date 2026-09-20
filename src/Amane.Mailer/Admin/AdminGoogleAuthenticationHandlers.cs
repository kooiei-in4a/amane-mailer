using System.Security.Claims;
using System.Text.Encodings.Web;
using Amane.Mailer.Data.Sqlite;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication;

namespace Amane.Mailer.Admin;

public static class AdminGoogleAuthenticationHandlers
{
    public static async Task<IResult> ChallengeAsync(
        HttpContext context,
        IAntiforgery antiforgery,
        AdminGoogleOptions googleOptions)
    {
        if (!googleOptions.Enabled)
            return Results.NotFound();

        if (!await ValidateAntiforgeryAsync(context, antiforgery))
            return Results.Text("Invalid CSRF token.", statusCode: StatusCodes.Status400BadRequest);

        var properties = new AuthenticationProperties
        {
            RedirectUri = AdminGoogleAuthenticationConstants.CompletionPath,
        };
        return Results.Challenge(
            properties,
            [AdminGoogleAuthenticationConstants.AuthenticationScheme]);
    }

    public static async Task<IResult> CompleteAsync(
        HttpContext context,
        AdminGoogleOptions googleOptions,
        MailerAdminOptions options,
        AdminGoogleIdentityRepository googleIdentityRepository,
        AdminUserRepository userRepository,
        AdminSessionRepository sessionRepository,
        AdminAuditRepository auditRepository,
        ILoggerFactory loggerFactory,
        TimeProvider timeProvider,
        IAntiforgery antiforgery,
        CancellationToken cancellationToken)
    {
        if (!googleOptions.Enabled)
            return Results.Redirect("/admin/login");

        var external = await context.AuthenticateAsync(AdminGoogleAuthenticationConstants.ExternalScheme);
        if (!external.Succeeded || external.Principal is null)
        {
            await WriteAuditAsync(
                context,
                options,
                auditRepository,
                loggerFactory,
                timeProvider,
                AdminAuditLog.EventTypes.GoogleLoginFailed,
                AdminAuditLog.Results.Failure,
                "unknown",
                AdminAuditLog.ErrorCodes.InvalidState,
                cancellationToken);
            return Results.Redirect("/admin/login");
        }

        var subject = external.Principal.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrWhiteSpace(subject))
        {
            await context.SignOutAsync(AdminGoogleAuthenticationConstants.ExternalScheme);
            await WriteAuditAsync(
                context,
                options,
                auditRepository,
                loggerFactory,
                timeProvider,
                AdminAuditLog.EventTypes.GoogleLoginFailed,
                AdminAuditLog.Results.Failure,
                "unknown",
                AdminAuditLog.ErrorCodes.MissingSubject,
                cancellationToken);
            return Results.Text("Google login was rejected.", statusCode: StatusCodes.Status401Unauthorized);
        }

        var mapping = await googleIdentityRepository.FindByIssuerSubjectAsync(
            AdminGoogleAuthenticationConstants.Issuer,
            subject,
            cancellationToken);
        if (mapping is null)
        {
            await WriteAuditAsync(
                context,
                options,
                auditRepository,
                loggerFactory,
                timeProvider,
                AdminAuditLog.EventTypes.GoogleLoginFailed,
                AdminAuditLog.Results.Failure,
                "unknown",
                AdminAuditLog.ErrorCodes.UnmappedIdentity,
                cancellationToken);
            return RenderLinkPage(context, antiforgery, errorMessage: null);
        }

        var user = await userRepository.GetUserByIdAsync(mapping.AdminUserId, cancellationToken);
        if (user is null || user.Disabled)
        {
            await context.SignOutAsync(AdminGoogleAuthenticationConstants.ExternalScheme);
            await WriteAuditAsync(
                context,
                options,
                auditRepository,
                loggerFactory,
                timeProvider,
                AdminAuditLog.EventTypes.GoogleLoginFailed,
                AdminAuditLog.Results.Failure,
                user?.Username ?? "unknown",
                AdminAuditLog.ErrorCodes.Disabled,
                cancellationToken);
            return Results.Text("Google login was rejected.", statusCode: StatusCodes.Status401Unauthorized);
        }

        if (user.IsBreakGlass)
        {
            await context.SignOutAsync(AdminGoogleAuthenticationConstants.ExternalScheme);
            await WriteAuditAsync(
                context,
                options,
                auditRepository,
                loggerFactory,
                timeProvider,
                AdminAuditLog.EventTypes.GoogleLoginFailed,
                AdminAuditLog.Results.Failure,
                user.Username,
                AdminAuditLog.ErrorCodes.BreakGlass,
                cancellationToken);
            return Results.Text("Google login was rejected.", statusCode: StatusCodes.Status401Unauthorized);
        }

        var sessionId = await AdminSessionIssuer.SignInAsync(
            context,
            user,
            options,
            sessionRepository,
            timeProvider,
            cancellationToken);
        await context.SignOutAsync(AdminGoogleAuthenticationConstants.ExternalScheme);
        await WriteAuditAsync(
            context,
            options,
            auditRepository,
            loggerFactory,
            timeProvider,
            AdminAuditLog.EventTypes.GoogleLoginSucceeded,
            AdminAuditLog.Results.Success,
            user.Username,
            errorCode: null,
            cancellationToken,
            sessionId);

        context.Response.Headers.CacheControl = "no-store";
        return RenderContinuationPage();
    }

    public static async Task<IResult> LinkAsync(
        HttpContext context,
        IAntiforgery antiforgery,
        AdminGoogleOptions googleOptions,
        MailerAdminOptions options,
        AdminGoogleIdentityRepository googleIdentityRepository,
        AdminUserRepository userRepository,
        AdminSessionRepository sessionRepository,
        AdminLoginThrottle throttle,
        AdminAuditRepository auditRepository,
        ILoggerFactory loggerFactory,
        TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        if (!googleOptions.Enabled)
            return Results.NotFound();

        if (!await ValidateAntiforgeryAsync(context, antiforgery))
            return Results.Text("Invalid CSRF token.", statusCode: StatusCodes.Status400BadRequest);

        var external = await context.AuthenticateAsync(AdminGoogleAuthenticationConstants.ExternalScheme);
        if (!external.Succeeded || external.Principal is null)
        {
            await WriteAuditAsync(
                context,
                options,
                auditRepository,
                loggerFactory,
                timeProvider,
                AdminAuditLog.EventTypes.GoogleIdentityLinkFailed,
                AdminAuditLog.Results.Failure,
                "unknown",
                AdminAuditLog.ErrorCodes.InvalidState,
                cancellationToken);
            return Results.Redirect("/admin/login");
        }

        var subject = external.Principal.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrWhiteSpace(subject))
        {
            await context.SignOutAsync(AdminGoogleAuthenticationConstants.ExternalScheme);
            await WriteAuditAsync(
                context,
                options,
                auditRepository,
                loggerFactory,
                timeProvider,
                AdminAuditLog.EventTypes.GoogleIdentityLinkFailed,
                AdminAuditLog.Results.Failure,
                "unknown",
                AdminAuditLog.ErrorCodes.MissingSubject,
                cancellationToken);
            return Results.Text("Google login was rejected.", statusCode: StatusCodes.Status401Unauthorized);
        }

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
            await WriteAuditAsync(
                context,
                options,
                auditRepository,
                loggerFactory,
                timeProvider,
                AdminAuditLog.EventTypes.LoginRateLimited,
                AdminAuditLog.Results.Failure,
                normalizedActor,
                errorCode: null,
                cancellationToken);
            return TooManyRequests(context, retryAfter);
        }

        var user = await userRepository.GetActiveUserByUsernameAsync(username, cancellationToken);
        var passwordHash = user?.PasswordHash ?? AdminAuthenticationHandlers.DummyAdminPasswordHash;
        var passwordVerified = AdminPasswordHasher.Verify(password, passwordHash);
        if (user is null || !passwordVerified)
        {
            await WriteAuditAsync(
                context,
                options,
                auditRepository,
                loggerFactory,
                timeProvider,
                AdminAuditLog.EventTypes.GoogleIdentityLinkFailed,
                AdminAuditLog.Results.Failure,
                normalizedActor,
                AdminAuditLog.ErrorCodes.InvalidCredentials,
                cancellationToken);

            var (locked, failureRetryAfter, lockCreated) = await throttle.RecordFailureAsync(
                username,
                remoteAddress,
                cancellationToken);
            if (lockCreated)
            {
                await WriteAuditAsync(
                    context,
                    options,
                    auditRepository,
                    loggerFactory,
                    timeProvider,
                    AdminAuditLog.EventTypes.AccountTemporarilyLocked,
                    AdminAuditLog.Results.Failure,
                    normalizedActor,
                    errorCode: null,
                    cancellationToken);
            }

            if (locked)
                return TooManyRequests(context, failureRetryAfter);

            return RenderLinkPage(context, antiforgery, "Invalid username or password.");
        }

        if (user.IsBreakGlass)
        {
            await WriteAuditAsync(
                context,
                options,
                auditRepository,
                loggerFactory,
                timeProvider,
                AdminAuditLog.EventTypes.GoogleIdentityLinkFailed,
                AdminAuditLog.Results.Failure,
                user.Username,
                AdminAuditLog.ErrorCodes.BreakGlass,
                cancellationToken);
            return RenderLinkPage(context, antiforgery, "This administrator cannot be linked to Google.");
        }

        var linkResult = await googleIdentityRepository.TryLinkAsync(
            user.Id,
            AdminGoogleAuthenticationConstants.Issuer,
            subject,
            timeProvider.GetUtcNow(),
            cancellationToken);
        if (linkResult != AdminGoogleLinkResult.Linked)
        {
            await WriteAuditAsync(
                context,
                options,
                auditRepository,
                loggerFactory,
                timeProvider,
                AdminAuditLog.EventTypes.GoogleIdentityLinkFailed,
                AdminAuditLog.Results.Failure,
                user.Username,
                AdminAuditLog.ErrorCodes.Conflict,
                cancellationToken);
            return RenderLinkPage(context, antiforgery, "This Google account cannot be linked.");
        }

        await throttle.ResetAsync(username, remoteAddress, cancellationToken);
        var sessionId = await AdminSessionIssuer.SignInAsync(
            context,
            user,
            options,
            sessionRepository,
            timeProvider,
            cancellationToken);
        await context.SignOutAsync(AdminGoogleAuthenticationConstants.ExternalScheme);
        await WriteAuditAsync(
            context,
            options,
            auditRepository,
            loggerFactory,
            timeProvider,
            AdminAuditLog.EventTypes.GoogleIdentityLinked,
            AdminAuditLog.Results.Success,
            user.Username,
            errorCode: null,
            cancellationToken,
            sessionId);
        await WriteAuditAsync(
            context,
            options,
            auditRepository,
            loggerFactory,
            timeProvider,
            AdminAuditLog.EventTypes.GoogleLoginSucceeded,
            AdminAuditLog.Results.Success,
            user.Username,
            errorCode: null,
            cancellationToken,
            sessionId);

        context.Response.Headers.CacheControl = "no-store";
        return RenderContinuationPage();
    }

    internal static IResult RenderLinkPage(
        HttpContext context,
        IAntiforgery antiforgery,
        string? errorMessage)
    {
        context.Response.Headers.CacheControl = "no-store";
        var tokens = antiforgery.GetAndStoreTokens(context);
        var requestToken = HtmlEncoder.Default.Encode(tokens.RequestToken ?? string.Empty);
        var errorHtml = string.IsNullOrWhiteSpace(errorMessage)
            ? string.Empty
            : $"<p class=\"login-error\">{HtmlEncoder.Default.Encode(errorMessage)}</p>";
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
                .login-lead { margin: 0; font-size: 0.95rem; line-height: 1.5; }
                .login-error { margin: 0; color: #8a1f1f; font-size: 0.9rem; }
              </style>
            </head>
            <body class="admin-login-page">
              <main class="login-shell">
                <form method="post" action="{{AdminGoogleAuthenticationConstants.LinkPath}}" class="login-form">
                  <input type="hidden" name="__RequestVerificationToken" value="{{requestToken}}">
                  <p class="login-lead">Googleアカウントはまだ管理者に紐付いていません。</p>
                  {{errorHtml}}
                  <label>
                    <span>Username</span>
                    <input name="username" autocomplete="username" required>
                  </label>
                  <label>
                    <span>Password</span>
                    <input name="password" type="password" autocomplete="current-password" required>
                  </label>
                  <button type="submit">このGoogleアカウントを紐付ける</button>
                </form>
              </main>
            </body>
            </html>
            """;

        return Results.Content(html, "text/html; charset=utf-8");
    }

    internal static IResult RenderContinuationPage()
    {
        const string html = """
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
                .login-lead { margin: 0; font-size: 0.95rem; line-height: 1.5; }
                .login-form a { display: grid; place-items: center; min-height: 42px; border-radius: 6px; background: #2458a6; color: #ffffff; font: inherit; font-weight: 700; text-decoration: none; }
              </style>
            </head>
            <body class="admin-login-page">
              <main class="login-shell">
                <div class="login-form">
                  <p class="login-lead">Googleログインが完了しました。</p>
                  <a href="/admin">管理画面へ進む</a>
                </div>
              </main>
            </body>
            </html>
            """;

        return Results.Content(html, "text/html; charset=utf-8");
    }

    internal static async Task HandleRemoteFailureAsync(RemoteFailureContext context)
    {
        context.HandleResponse();
        var services = context.HttpContext.RequestServices;
        var options = services.GetRequiredService<MailerAdminOptions>();
        var auditRepository = services.GetRequiredService<AdminAuditRepository>();
        var loggerFactory = services.GetRequiredService<ILoggerFactory>();
        var timeProvider = services.GetRequiredService<TimeProvider>();
        await WriteAuditAsync(
            context.HttpContext,
            options,
            auditRepository,
            loggerFactory,
            timeProvider,
            AdminAuditLog.EventTypes.GoogleLoginFailed,
            AdminAuditLog.Results.Failure,
            "unknown",
            AdminAuditLog.ErrorCodes.InvalidState,
            context.HttpContext.RequestAborted);
        context.HttpContext.Response.Redirect("/admin/login");
    }

    private static async Task WriteAuditAsync(
        HttpContext context,
        MailerAdminOptions options,
        AdminAuditRepository auditRepository,
        ILoggerFactory loggerFactory,
        TimeProvider timeProvider,
        string eventType,
        string result,
        string actor,
        string? errorCode,
        CancellationToken cancellationToken,
        string? sessionId = null)
    {
        var auditLogger = loggerFactory.CreateLogger(AdminAuditLog.LoggerCategory);
        var auditEvent = AdminAuthenticationHandlers.BuildAuthAuditEvent(
            context,
            options,
            timeProvider,
            eventType,
            result,
            actor,
            sessionId);
        if (!string.IsNullOrWhiteSpace(errorCode))
        {
            auditEvent = new()
            {
                EventType = auditEvent.EventType,
                Actor = auditEvent.Actor,
                OccurredAt = auditEvent.OccurredAt,
                SourceIp = auditEvent.SourceIp,
                UserAgentSummary = auditEvent.UserAgentSummary,
                TargetType = auditEvent.TargetType,
                TargetId = auditEvent.TargetId,
                TenantId = auditEvent.TenantId,
                FieldName = auditEvent.FieldName,
                Result = auditEvent.Result,
                ErrorCode = errorCode,
            };
        }

        await AdminAuditLog.WriteBestEffortAsync(
            auditRepository,
            auditLogger,
            auditEvent,
            cancellationToken);
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

    private static IResult TooManyRequests(HttpContext context, TimeSpan retryAfter)
    {
        var seconds = Math.Max(1, (int)Math.Ceiling(retryAfter.TotalSeconds));
        context.Response.Headers.RetryAfter = seconds.ToString(System.Globalization.CultureInfo.InvariantCulture);
        return Results.Text("Too many failed login attempts.", statusCode: StatusCodes.Status429TooManyRequests);
    }
}
