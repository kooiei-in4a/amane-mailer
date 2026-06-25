using System.Globalization;
using System.Net;
using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.Extensions.FileProviders;

namespace Amane.Mailer.Admin;

public static class MailerAdminExtensions
{
    public static IServiceCollection AddMailerAdmin(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddSingleton(provider =>
        {
            var options = MailerAdminOptions.Load(provider.GetRequiredService<IConfiguration>());
            options.Validate();
            return options;
        });
        services.AddSingleton<AdminLoginThrottle>();
        services.AddSingleton<AdminDeadLetterCountCache>();

        // AMANE_ADMIN_ALLOW_HTTP=true drops HTTPS-only constraints for local dev behind no TLS proxy.
        // Production always keeps SecurePolicy.Always and __Host- cookie prefixes.
        var allowHttp = string.Equals(
            configuration["AMANE_ADMIN_ALLOW_HTTP"] ?? configuration["MAILER_ADMIN_ALLOW_HTTP"],
            "true",
            StringComparison.OrdinalIgnoreCase);
        var securePolicy = allowHttp ? CookieSecurePolicy.SameAsRequest : CookieSecurePolicy.Always;
        var authCookieName = allowHttp ? "amane-admin-auth" : "__Host-amane-admin-auth";
        var csrfCookieName = allowHttp ? "amane-admin-csrf" : "__Host-amane-admin-csrf";

        services
            .AddAuthentication(AdminAuthenticationConstants.Scheme)
            .AddCookie(AdminAuthenticationConstants.Scheme, cookie =>
            {
                cookie.Cookie.Name = authCookieName;
                cookie.Cookie.HttpOnly = true;
                cookie.Cookie.SecurePolicy = securePolicy;
                cookie.Cookie.SameSite = SameSiteMode.Strict;
                cookie.LoginPath = "/admin/login";
                cookie.AccessDeniedPath = "/admin/login";
                cookie.ExpireTimeSpan = TimeSpan.FromMinutes(30);
                cookie.SlidingExpiration = true;
                cookie.Events.OnRedirectToLogin = context => HandleApiRedirectAsync(context, StatusCodes.Status401Unauthorized);
                cookie.Events.OnRedirectToAccessDenied = context => HandleApiRedirectAsync(context, StatusCodes.Status403Forbidden);
                cookie.Events.OnValidatePrincipal = ValidateAdminCookieAsync;
            });
        services.AddAuthorization();
        services.AddAntiforgery(antiforgery =>
        {
            antiforgery.Cookie.Name = csrfCookieName;
            antiforgery.Cookie.HttpOnly = true;
            antiforgery.Cookie.SecurePolicy = securePolicy;
            antiforgery.Cookie.SameSite = SameSiteMode.Strict;
            antiforgery.FormFieldName = "__RequestVerificationToken";
            antiforgery.HeaderName = "X-CSRF-TOKEN";
        });

        return services;
    }

    public static WebApplication MapAdminIfEnabled(this WebApplication app)
    {
        var options = app.Services.GetRequiredService<MailerAdminOptions>();
        if (!options.Enabled)
            return app;

        app.UseAuthentication();
        app.UseAuthorization();
        app.UseWhen(IsAdminRequest, branch =>
        {
            branch.Use(async (context, next) =>
            {
                if (!IsAllowedBindAddress(context, options))
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
        app.MapGet("/admin/mail-requests/{id}", AdminMailRequestDetailPage.RenderAsync).RequireAuthorization();
        app.MapGet("/admin/mail-requests/{id}/body", AdminMailRequestBodyPage.RenderAsync).RequireAuthorization();
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
        TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
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

        if (throttle.IsLocked(username, remoteAddress, options, out var retryAfter))
            return TooManyRequests(context, retryAfter);

        if (!string.Equals(username, options.Username, StringComparison.Ordinal)
            || !AdminPasswordHasher.Verify(password, options.PasswordHash))
        {
            if (throttle.RecordFailure(username, remoteAddress, options, out retryAfter))
                return TooManyRequests(context, retryAfter);

            return Results.Text("Invalid username or password.", statusCode: StatusCodes.Status401Unauthorized);
        }

        throttle.Reset(username, remoteAddress);

        var now = timeProvider.GetUtcNow();
        var absoluteExpiresAt = now + options.SessionAbsoluteLifetime;
        var claims = new[]
        {
            new Claim(ClaimTypes.NameIdentifier, options.Username),
            new Claim(ClaimTypes.Name, options.Username),
        };
        var identity = new ClaimsIdentity(claims, AdminAuthenticationConstants.Scheme);
        var properties = new AuthenticationProperties
        {
            AllowRefresh = true,
            ExpiresUtc = now + options.SessionIdleTimeout,
            IssuedUtc = now,
            IsPersistent = false,
        };
        properties.Items[AdminAuthenticationConstants.AbsoluteExpiresUtcProperty] =
            absoluteExpiresAt.ToString("O", CultureInfo.InvariantCulture);

        await context.SignInAsync(
            AdminAuthenticationConstants.Scheme,
            new ClaimsPrincipal(identity),
            properties);

        return Results.Redirect("/admin");
    }

    private static async Task<IResult> LogoutAsync(
        HttpContext context,
        IAntiforgery antiforgery)
    {
        if (!await ValidateAntiforgeryAsync(context, antiforgery))
            return Results.Text("Invalid CSRF token.", statusCode: StatusCodes.Status400BadRequest);

        await context.SignOutAsync(AdminAuthenticationConstants.Scheme);
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

    private static bool IsAllowedBindAddress(HttpContext context, MailerAdminOptions options)
    {
        var localAddress = NormalizeIpAddress(context.Connection.LocalIpAddress);
        if (localAddress is null)
            return true;

        if (!IPAddress.TryParse(options.BindAddress, out var configuredAddress))
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
        var options = context.HttpContext.RequestServices.GetRequiredService<MailerAdminOptions>();
        var timeProvider = context.HttpContext.RequestServices.GetRequiredService<TimeProvider>();
        var now = timeProvider.GetUtcNow();

        if (!context.Properties.Items.TryGetValue(
                AdminAuthenticationConstants.AbsoluteExpiresUtcProperty,
                out var absoluteExpiresUtc)
            || !DateTimeOffset.TryParse(
                absoluteExpiresUtc,
                CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind,
                out var absoluteExpiresAt)
            || absoluteExpiresAt <= now)
        {
            context.RejectPrincipal();
            await context.HttpContext.SignOutAsync(AdminAuthenticationConstants.Scheme);
            return;
        }

        var idleExpiresAt = now + options.SessionIdleTimeout;
        context.Properties.ExpiresUtc = idleExpiresAt <= absoluteExpiresAt
            ? idleExpiresAt
            : absoluteExpiresAt;
    }
}
