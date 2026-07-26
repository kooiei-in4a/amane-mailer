using System.Net;
using Microsoft.Extensions.FileProviders;

namespace Amane.Mailer.Admin;

/// <summary>
/// Thin Admin composition root: DI registration, credential readiness, and pipeline mapping.
/// Security-critical policy lives in focused types (<see cref="AdminCookieTransportPolicy"/>,
/// <see cref="AdminLocalAddressPolicy"/>, <see cref="AdminCookieValidator"/>,
/// <see cref="AdminAuthenticationHandlers"/>, <see cref="AdminServiceRegistration"/>).
/// </summary>
public static class MailerAdminExtensions
{
    public static IServiceCollection AddMailerAdmin(
        this IServiceCollection services,
        IConfiguration configuration) =>
        AdminServiceRegistration.AddMailerAdmin(services, configuration);

    /// <summary>
    /// Synchronizes Admin credentials and tenant-scope readiness before Admin routes are mapped.
    /// No-ops when Admin is disabled. Must be awaited before <see cref="MapAdminIfEnabled"/>.
    /// </summary>
    public static async Task EnsureAdminReadyAsync(
        this WebApplication app,
        CancellationToken cancellationToken = default)
    {
        var options = app.Services.GetRequiredService<MailerAdminOptions>();
        if (!options.Enabled)
            return;

        var credentialSync = app.Services.GetRequiredService<AdminCredentialSync>();
        await credentialSync.EnsureSyncedAsync(cancellationToken);
    }

    public static WebApplication MapAdminIfEnabled(this WebApplication app)
    {
        var options = app.Services.GetRequiredService<MailerAdminOptions>();
        if (!options.Enabled)
            return app;

        // Credential sync / tenant-scope readiness run in EnsureAdminReadyAsync before this mapping (#350).

        app.UseAuthentication();
        app.UseAuthorization();
        app.UseWhen(IsAdminRequest, branch =>
        {
            branch.Use(async (context, next) =>
            {
                if (!AdminLocalAddressPolicy.IsAllowed(context, options))
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
                if (context.User.Identity?.IsAuthenticated != true)
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

        AdminEndpointMapping.MapEndpoints(app);
        return app;
    }

    /// <summary>
    /// Test and diagnostic entry point for local-address allowlist policy.
    /// </summary>
    internal static bool IsAllowedLocalAddress(
        IPAddress? requestLocalAddress,
        string configuredAllowedLocalAddress) =>
        AdminLocalAddressPolicy.IsAllowed(requestLocalAddress, configuredAllowedLocalAddress);

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
}
