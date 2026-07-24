using Microsoft.Extensions.Hosting;

namespace Amane.Mailer.Admin;

/// <summary>
/// Resolves Admin auth/CSRF cookie transport settings and rejects HTTP relaxation
/// outside Development. Production and Staging always keep Secure + __Host- cookies.
/// </summary>
internal static class AdminCookieTransportPolicy
{
    internal const string SecureAuthCookieName = "__Host-amane-admin-auth";
    internal const string SecureCsrfCookieName = "__Host-amane-admin-csrf";
    internal const string HttpAuthCookieName = "amane-admin-auth";
    internal const string HttpCsrfCookieName = "amane-admin-csrf";

    internal static bool IsAllowHttpRequested(IConfiguration configuration) =>
        string.Equals(
            configuration["AMANE_ADMIN_ALLOW_HTTP"] ?? configuration["MAILER_ADMIN_ALLOW_HTTP"],
            "true",
            StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// HTTP cookie relaxation is Development-only. Testing/Production/Staging and any
    /// other environment keep SecurePolicy.Always and __Host- cookie names.
    /// </summary>
    internal static bool AllowsHttpTransport(string? environmentName) =>
        string.Equals(environmentName, Environments.Development, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// When Admin is enabled, fail closed if ALLOW_HTTP is requested outside Development.
    /// When Admin is disabled, ignore ALLOW_HTTP (including typos) so mail delivery still starts.
    /// </summary>
    internal static void Validate(bool allowHttpRequested, string environmentName, bool adminEnabled)
    {
        if (!adminEnabled || !allowHttpRequested)
            return;

        if (AllowsHttpTransport(environmentName))
            return;

        throw new InvalidOperationException(
            "AMANE_ADMIN_ALLOW_HTTP (or MAILER_ADMIN_ALLOW_HTTP) may only be true when "
            + "ASPNETCORE_ENVIRONMENT is Development. "
            + "Production and Staging require HTTPS with Secure cookies (__Host- prefix). "
            + "Unset the flag or set it to false on deploy hosts.");
    }

    internal static ResolvedCookieTransport Resolve(bool allowHttpRequested, string environmentName)
    {
        // Defense in depth: non-Development never gets SameAsRequest even if validation
        // were skipped (for example Admin disabled with a mis-set ALLOW_HTTP flag).
        if (allowHttpRequested && AllowsHttpTransport(environmentName))
        {
            return new ResolvedCookieTransport(
                CookieSecurePolicy.SameAsRequest,
                HttpAuthCookieName,
                HttpCsrfCookieName);
        }

        return new ResolvedCookieTransport(
            CookieSecurePolicy.Always,
            SecureAuthCookieName,
            SecureCsrfCookieName);
    }

    internal readonly record struct ResolvedCookieTransport(
        CookieSecurePolicy SecurePolicy,
        string AuthCookieName,
        string CsrfCookieName);
}
