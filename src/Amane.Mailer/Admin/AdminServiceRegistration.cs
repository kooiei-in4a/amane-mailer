using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.Extensions.Hosting;
using Amane.Mailer.Configuration;
using Amane.Mailer.Data.Sqlite;

namespace Amane.Mailer.Admin;

/// <summary>
/// Registers Admin DI, cookie authentication, authorization, and antiforgery.
/// Cookie transport (Secure / names) is resolved via <see cref="AdminCookieTransportPolicy"/>.
/// </summary>
internal static class AdminServiceRegistration
{
    internal static IServiceCollection AddMailerAdmin(
        IServiceCollection services,
        IConfiguration configuration,
        InstanceRuntimeState? instanceState = null)
    {
        services.AddStartupValidatedSingleton(provider =>
        {
            var resolvedConfiguration = provider.GetRequiredService<IConfiguration>();
            var databaseOwnedCredentials = instanceState?.IsInitialized == true
                && instanceState.HasInstanceOwner;
            var options = MailerAdminOptions.Load(resolvedConfiguration, databaseOwnedCredentials);
            options.Validate(databaseOwnedCredentials);
            var environment = provider.GetRequiredService<IHostEnvironment>();
            AdminCookieTransportPolicy.Validate(
                AdminCookieTransportPolicy.IsAllowHttpRequested(resolvedConfiguration, options.Enabled),
                environment.EnvironmentName,
                options.Enabled);
            return options;
        });
        services.AddSingleton<AdminLoginThrottle>();
        services.AddSingleton<AdminSessionExpiredDedupe>();
        services.AddSingleton<AdminBootstrapDatabase>();
        services.AddSingleton<AdminSessionRepository>();
        services.AddSingleton<AdminUserRepository>();
        services.AddSingleton<AdminLoginThrottleRepository>();
        services.AddSingleton<AdminDeadLetterCountCache>();
        services.AddStartupValidatedSingleton(provider =>
        {
            var resolvedConfiguration = provider.GetRequiredService<IConfiguration>();
            var connections = provider.GetRequiredService<SqliteConnectionFactory>();
            var adminOptions = provider.GetRequiredService<MailerAdminOptions>();
            var options = MailerAdminDbOpsOptions.Load(
                resolvedConfiguration,
                connections.ConnectionString,
                adminOptions.Enabled);
            options.Validate(adminOptions.Enabled, connections);
            return options;
        });
        // Keep this factory after any .Load( registration body so the startup-inventory
        // AddSingleton+Load heuristic does not false-positive across adjacent registrations.
        services.AddSingleton(provider => new AdminCredentialSync(
            provider.GetRequiredService<AdminSessionRepository>(),
            provider.GetRequiredService<AdminUserRepository>(),
            provider.GetRequiredService<MailerTenantRegistry>(),
            provider.GetRequiredService<MailerAdminOptions>(),
            provider.GetRequiredService<IConfiguration>(),
            provider.GetRequiredService<AdminBootstrapDatabase>()));
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
                // Session lifetime is owned by admin_sessions + AdminCookieValidator touch.
                // Framework sliding refresh must not Set-Cookie without a successful DB touch (#391).
                cookie.SlidingExpiration = false;
                cookie.Events.OnRedirectToLogin = context =>
                    HandleApiRedirectAsync(context, StatusCodes.Status401Unauthorized);
                cookie.Events.OnRedirectToAccessDenied = context =>
                    HandleApiRedirectAsync(context, StatusCodes.Status403Forbidden);
                cookie.Events.OnValidatePrincipal = AdminCookieValidator.ValidateAsync;
            });
        services.AddOptions<CookieAuthenticationOptions>(AdminAuthenticationConstants.Scheme)
            .Configure<IHostEnvironment, IConfiguration, MailerAdminOptions>((cookie, environment, resolvedConfiguration, adminOptions) =>
            {
                var transport = AdminCookieTransportPolicy.Resolve(
                    AdminCookieTransportPolicy.IsAllowHttpRequested(resolvedConfiguration, adminOptions.Enabled),
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
            .Configure<IHostEnvironment, IConfiguration, MailerAdminOptions>((antiforgery, environment, resolvedConfiguration, adminOptions) =>
            {
                var transport = AdminCookieTransportPolicy.Resolve(
                    AdminCookieTransportPolicy.IsAllowHttpRequested(resolvedConfiguration, adminOptions.Enabled),
                    environment.EnvironmentName);
                antiforgery.Cookie.Name = transport.CsrfCookieName;
                antiforgery.Cookie.SecurePolicy = transport.SecurePolicy;
            });

        return services;
    }

    private static Task HandleApiRedirectAsync(
        RedirectContext<CookieAuthenticationOptions> context,
        int statusCode)
    {
        if (context.Request.Path.StartsWithSegments("/admin/api"))
        {
            context.Response.StatusCode = statusCode;
            return Task.CompletedTask;
        }

        context.Response.Redirect(context.RedirectUri);
        return Task.CompletedTask;
    }
}
