using Microsoft.AspNetCore.Http;

namespace Amane.Mailer.Setup.Assistant;

/// <summary>
/// Transport-level guards for the loopback assistant: Host allowlisting, Origin checking,
/// response hardening, and the per-session CSRF token. No custom authentication scheme and no
/// custom cryptography is introduced; the session cookie is an ordinary opaque identifier and
/// the CSRF token is a synchronizer token compared in constant time.
/// </summary>
internal static class SetupAssistantSecurity
{
    internal const string SessionCookieName = "amane_setup_assistant_session";
    internal const string CsrfFieldName = "__setup_assistant_csrf";

    /// <summary>
    /// Denies every external resource class outright. Styles come from a same-origin route, so
    /// no inline style, script, font, image, or connect source is permitted.
    /// </summary>
    internal const string ContentSecurityPolicy =
        "default-src 'none'; style-src 'self'; form-action 'self'; base-uri 'none'; "
        + "frame-ancestors 'none'; sandbox allow-forms allow-same-origin";

    internal static void ApplySecurityHeaders(HttpResponse response)
    {
        var headers = response.Headers;
        headers["Content-Security-Policy"] = ContentSecurityPolicy;
        headers["X-Frame-Options"] = "DENY";
        headers["X-Content-Type-Options"] = "nosniff";
        headers["Referrer-Policy"] = "no-referrer";
        headers["Cross-Origin-Opener-Policy"] = "same-origin";
        headers["Cross-Origin-Resource-Policy"] = "same-origin";
        headers["Permissions-Policy"] = "geolocation=(), camera=(), microphone=()";
        headers.CacheControl = "no-store";
        headers.Pragma = "no-cache";
    }

    internal static bool IsAllowedHost(HttpRequest request, IReadOnlyList<string> allowedHosts)
    {
        var host = request.Headers.Host.ToString();
        if (string.IsNullOrEmpty(host))
        {
            return false;
        }

        foreach (var allowed in allowedHosts)
        {
            if (string.Equals(host, allowed, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// State-changing requests must carry an Origin that is the very authority the request was
    /// addressed to, not merely some entry of the loopback allowlist. A missing Origin, a
    /// credentialed one, or one carrying a path, query, or fragment is rejected rather than
    /// tolerated, so a cross-origin form post cannot drive the workflow.
    /// </summary>
    internal static bool IsAllowedOrigin(HttpRequest request, IReadOnlyList<string> allowedHosts)
    {
        var origin = request.Headers.Origin.ToString();
        if (string.IsNullOrEmpty(origin)
            || !Uri.TryCreate(origin, UriKind.Absolute, out var parsed)
            || parsed.Scheme != Uri.UriSchemeHttp
            || !string.IsNullOrEmpty(parsed.UserInfo)
            || !string.IsNullOrEmpty(parsed.Query)
            || !string.IsNullOrEmpty(parsed.Fragment)
            || parsed.AbsolutePath != "/")
        {
            return false;
        }

        var authority = CanonicalAuthority(
            parsed.IsDefaultPort ? parsed.Host : $"{parsed.Host}:{parsed.Port}");
        return IsAllowedHost(request, allowedHosts)
            && string.Equals(
                authority,
                CanonicalAuthority(request.Headers.Host.ToString()),
                StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Treats <c>host</c> and <c>host:80</c> as the same http authority.</summary>
    private static string CanonicalAuthority(string value) =>
        value.EndsWith(":80", StringComparison.Ordinal) ? value[..^3] : value;

    internal static bool IsStateChangingMethod(string method) =>
        !HttpMethods.IsGet(method) && !HttpMethods.IsHead(method);

    internal static bool ValidateCsrf(SetupAssistantSession session, IFormCollection form)
    {
        var presented = form[CsrfFieldName].ToString();
        return !string.IsNullOrEmpty(presented)
            && presented.Length == session.CsrfToken.Length
            && SetupAssistantSessionManager.FixedTimeTextEquals(session.CsrfToken, presented);
    }

    internal static void WriteSessionCookie(HttpResponse response, SetupAssistantSession session) =>
        response.Cookies.Append(
            SessionCookieName,
            session.SessionId,
            new CookieOptions
            {
                HttpOnly = true,
                SameSite = SameSiteMode.Strict,
                Path = "/",
                IsEssential = true,

                // The listener is loopback-only plaintext HTTP, so Secure would make the cookie
                // unusable. Reachability is constrained by the loopback bind, the Host allowlist,
                // and the Origin check instead.
                Secure = false,
            });

    internal static void ClearSessionCookie(HttpResponse response) =>
        response.Cookies.Delete(SessionCookieName, new CookieOptions { Path = "/" });

    internal static string? ReadSessionCookie(HttpRequest request) =>
        request.Cookies.TryGetValue(SessionCookieName, out var value) ? value : null;
}
