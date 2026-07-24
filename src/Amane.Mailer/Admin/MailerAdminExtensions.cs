using System.Globalization;
using System.Net;
using System.Security.Claims;
using System.Text.Encodings.Web;
using Amane.Mailer.Data.Sqlite;
using Amane.Mailer.Data.Sqlite.Models;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace Amane.Mailer.Admin;

public static class MailerAdminExtensions
{
    // Keep unknown-user failures on the same PBKDF2 verification path as bad passwords.
    private const string DummyAdminPasswordHash =
        "pbkdf2:sha256:600000:YW1hbmUtZHVtbXktMTI0IQ==:qMTLpvljgavl6UScZshWUdoApY4JFTGZWhLPJ62+Ui0=";

    public static IServiceCollection AddMailerAdmin(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddSingleton(provider =>
        {
            var resolvedConfiguration = provider.GetRequiredService<IConfiguration>();
            var options = MailerAdminOptions.Load(resolvedConfiguration);
            options.Validate();
            var environment = provider.GetRequiredService<IHostEnvironment>();
            AdminCookieTransportPolicy.Validate(
                AdminCookieTransportPolicy.IsAllowHttpRequested(resolvedConfiguration),
                environment.EnvironmentName,
                options.Enabled);
            return options;
        });
        services.AddSingleton<AdminLoginThrottle>();
        services.AddSingleton<AdminSessionExpiredDedupe>();
        services.AddSingleton<AdminCredentialSync>();
        services.AddSingleton<AdminSessionRepository>();
        services.AddSingleton<AdminUserRepository>();
        services.AddSingleton<AdminLoginThrottleRepository>();
        services.AddSingleton<AdminDeadLetterCountCache>();
        services.AddSingleton(provider =>
        {
            var configuration = provider.GetRequiredService<IConfiguration>();
            var connections = provider.GetRequiredService<SqliteConnectionFactory>();
            var adminOptions = provider.GetRequiredService<MailerAdminOptions>();
            var options = MailerAdminDbOpsOptions.Load(
                configuration,
                connections.ConnectionString,
                adminOptions.Enabled);
            options.Validate(adminOptions.Enabled, connections);
            return options;
        });
        services.AddSingleton<AdminDbOpsService>();

        // Cookie transport is resolved from IHostEnvironment at options configure time so
        // WebApplicationFactory UseEnvironment and Production/Staging fail-closed agree.
        // Non-Development always keeps SecurePolicy.Always and __Host- cookie prefixes.
        services
            .AddAuthentication(AdminAuthenticationConstants.Scheme)
            .AddCookie(AdminAuthenticationConstants.Scheme, cookie =>
            {
                cookie.Cookie.HttpOnly = true;
                cookie.Cookie.SameSite = SameSiteMode.Strict;
                cookie.LoginPath = "/admin/login";
                cookie.AccessDeniedPath = "/admin/login";
                cookie.ExpireTimeSpan = TimeSpan.FromMinutes(30);
                cookie.SlidingExpiration = true;
                cookie.Events.OnRedirectToLogin = context => HandleApiRedirectAsync(context, StatusCodes.Status401Unauthorized);
                cookie.Events.OnRedirectToAccessDenied = context => HandleApiRedirectAsync(context, StatusCodes.Status403Forbidden);
                cookie.Events.OnValidatePrincipal = ValidateAdminCookieAsync;
            });
        services.AddOptions<CookieAuthenticationOptions>(AdminAuthenticationConstants.Scheme)
            .Configure<IHostEnvironment, IConfiguration>((cookie, environment, resolvedConfiguration) =>
            {
                var transport = AdminCookieTransportPolicy.Resolve(
                    AdminCookieTransportPolicy.IsAllowHttpRequested(resolvedConfiguration),
                    environment.EnvironmentName);
                cookie.Cookie.Name = transport.AuthCookieName;
                cookie.Cookie.SecurePolicy = transport.SecurePolicy;
            });
        services.AddAuthorization();
        services.AddAntiforgery(antiforgery =>
        {
            antiforgery.Cookie.HttpOnly = true;
            antiforgery.Cookie.SameSite = SameSiteMode.Strict;
            antiforgery.FormFieldName = "__RequestVerificationToken";
            antiforgery.HeaderName = "X-CSRF-TOKEN";
        });
        services.AddOptions<AntiforgeryOptions>()
            .Configure<IHostEnvironment, IConfiguration>((antiforgery, environment, resolvedConfiguration) =>
            {
                var transport = AdminCookieTransportPolicy.Resolve(
                    AdminCookieTransportPolicy.IsAllowHttpRequested(resolvedConfiguration),
                    environment.EnvironmentName);
                antiforgery.Cookie.Name = transport.CsrfCookieName;
                antiforgery.Cookie.SecurePolicy = transport.SecurePolicy;
            });

        return services;
    }

    public static WebApplication MapAdminIfEnabled(this WebApplication app)
    {
        var options = app.Services.GetRequiredService<MailerAdminOptions>();
        if (!options.Enabled)
            return app;

        var credentialSync = app.Services.GetRequiredService<AdminCredentialSync>();
        credentialSync.EnsureSyncedAsync(CancellationToken.None).GetAwaiter().GetResult();

        app.UseAuthentication();
        app.UseAuthorization();
        app.UseWhen(IsAdminRequest, branch =>
        {
            branch.Use(async (context, next) =>
            {
                if (!IsAllowedLocalAddress(context, options))
                {
                    context.Response.StatusCode = StatusCodes.Status404NotFound;
                    return;
                }

                await next(context);
            });
        });
        app.UseWhen(IsAdminStaticFileRequest, branch =>
        {
            branch.Use(async (context, next) =>
            {
                if (!IsAuthenticated(context))
                {
                    context.Response.Redirect("/admin/login");
                    return;
                }

                await next(context);
            });

            var webRoot = app.Environment.WebRootPath
                ?? Path.Combine(app.Environment.ContentRootPath, "wwwroot");
            branch.UseStaticFiles(new StaticFileOptions
            {
                FileProvider = new PhysicalFileProvider(Path.Combine(webRoot, "admin")),
                RequestPath = "/admin",
            });
        });

        app.MapGet("/admin", RedirectAdminHome).AllowAnonymous();
        app.MapGet("/admin/login", RenderLoginPage).AllowAnonymous();
        app.MapGet("/admin/mail-requests", AdminMailRequestsPage.RenderAsync).RequireAuthorization();
        app.MapGet("/admin/dead-letters", AdminDeadLettersPage.RenderAsync).RequireAuthorization();
        app.MapGet("/admin/webhook-dead-letters", AdminWebhookDeadLettersPage.RenderAsync).RequireAuthorization();
        app.MapGet("/admin/audit-log", AdminAuditLogPage.RenderAsync).RequireAuthorization();
        app.MapGet("/admin/audit-log/{id:long}", AdminAuditLogDetailPage.RenderAsync).RequireAuthorization();
        app.MapGet("/admin/ops", AdminOpsPage.RenderAsync).RequireAuthorization();
        app.MapPost("/admin/ops/checkpoint", AdminDbOpsHandlers.CheckpointAsync).RequireAuthorization();
        app.MapPost("/admin/ops/backup", AdminDbOpsHandlers.BackupAsync).RequireAuthorization();
        app.MapGet("/admin/mail-requests/{id}", AdminMailRequestDetailPage.RenderAsync).RequireAuthorization();
        app.MapGet("/admin/mail-requests/{id}/body", AdminMailRequestBodyPage.RenderAsync).RequireAuthorization();
        app.MapPost("/admin/mail-requests/{id}/retry", AdminMailRequestMutationHandlers.RetryAsync).RequireAuthorization();
        app.MapPost("/admin/mail-requests/{id}/cancel", AdminMailRequestMutationHandlers.CancelAsync).RequireAuthorization();
        app.MapPost("/admin/api/login", LoginAsync).AllowAnonymous();
        app.MapPost("/admin/api/logout", LogoutAsync).RequireAuthorization();

        return app;
    }

    private static IResult RedirectAdminHome(HttpContext context)
    {
        return IsAuthenticated(context)
            ? Results.Redirect("/admin/mail-requests")
            : Results.Redirect("/admin/login");
    }

    private static IResult RenderLoginPage(HttpContext context, IAntiforgery antiforgery)
    {
        if (IsAuthenticated(context))
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

    private static async Task<IResult> LoginAsync(
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
        var remoteAddress = GetRemoteAddress(context);
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
            AllowRefresh = true,
            ExpiresUtc = idleExpiresAt <= absoluteExpiresAt ? idleExpiresAt : absoluteExpiresAt,
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

    private static AdminAuditEvent BuildAuthAuditEvent(
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

    private static async Task<IResult> LogoutAsync(
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

    private static bool IsAuthenticated(HttpContext context) =>
        context.User.Identity?.IsAuthenticated == true;

    private static string GetRemoteAddress(HttpContext context) =>
        context.Connection.RemoteIpAddress?.ToString() ?? "unknown";

    private static bool IsAdminRequest(HttpContext context) =>
        context.Request.Path.StartsWithSegments("/admin");

    private static bool IsAdminStaticFileRequest(HttpContext context)
    {
        if (!context.Request.Path.StartsWithSegments("/admin", out var remaining))
            return false;

        return remaining.HasValue
            && remaining.Value is not null
            && Path.GetExtension(remaining.Value).Length > 0;
    }

    private static bool IsAllowedLocalAddress(HttpContext context, MailerAdminOptions options) =>
        IsAllowedLocalAddress(context.Connection.LocalIpAddress, options.AllowedLocalAddress);

    internal static bool IsAllowedLocalAddress(IPAddress? requestLocalAddress, string configuredAllowedLocalAddress)
    {
        var localAddress = NormalizeIpAddress(requestLocalAddress);
        if (localAddress is null)
            return false;

        if (!IPAddress.TryParse(configuredAllowedLocalAddress, out var configuredAddress))
            return false;

        configuredAddress = NormalizeRequiredIpAddress(configuredAddress);
        if (configuredAddress.Equals(IPAddress.Any) || configuredAddress.Equals(IPAddress.IPv6Any))
            return true;

        if (IPAddress.IsLoopback(configuredAddress))
            return IPAddress.IsLoopback(localAddress);

        return configuredAddress.Equals(localAddress);
    }

    private static IPAddress NormalizeRequiredIpAddress(IPAddress address)
    {
        return address.IsIPv4MappedToIPv6
            ? address.MapToIPv4()
            : address;
    }

    private static IPAddress? NormalizeIpAddress(IPAddress? address)
    {
        return address?.IsIPv4MappedToIPv6 == true
            ? address.MapToIPv4()
            : address;
    }

    private static Task HandleApiRedirectAsync(RedirectContext<CookieAuthenticationOptions> context, int statusCode)
    {
        if (context.Request.Path.StartsWithSegments("/admin/api"))
        {
            context.Response.StatusCode = statusCode;
            return Task.CompletedTask;
        }

        context.Response.Redirect(context.RedirectUri);
        return Task.CompletedTask;
    }

    private static async Task ValidateAdminCookieAsync(CookieValidatePrincipalContext context)
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

        var idleExpiresAt = now + options.SessionIdleTimeout;
        var cookieExpiresAt = idleExpiresAt <= session.AbsoluteExpiresAt
            ? idleExpiresAt
            : session.AbsoluteExpiresAt;
        context.Properties.ExpiresUtc = cookieExpiresAt;

        await sessionRepository.UpdateLastSeenAsync(
            sessionId,
            now,
            idleExpiresAt,
            context.HttpContext.RequestAborted);
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
                    BuildAuthAuditEvent(
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
