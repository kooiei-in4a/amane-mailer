using System.Security.Claims;
using Amane.Mailer.Data.Sqlite;
using Amane.Mailer.Data.Sqlite.Models;
using Microsoft.AspNetCore.Authentication;

namespace Amane.Mailer.Admin;

/// <summary>
/// Defers Admin auth cookie renewal until response start, writes repository-exact
/// expiry via SignInAsync (not CookieAuthenticationHandler.RequestRefresh), and
/// skips Set-Cookie when a later touch has already superseded this request (#391).
/// </summary>
internal static class AdminSessionCookieRenewal
{
    /// <summary>
    /// Optional test seam: when set and the request carries
    /// <see cref="AdminSessionTouchTestHooks.HoldAfterTouchHeaderName"/>, awaited
    /// after a successful touch so cross-interval races can be reproduced.
    /// </summary>
    internal static Func<AdminSessionTouchResult, Task>? HoldAfterTouchAsync { get; set; }

    internal static bool IsStillAuthoritative(
        AdminSessionTouchResult touch,
        AdminSessionRow? current) =>
        current is not null
        && current.RevokedAt is null
        && current.LastSeenAt == touch.LastSeenAt
        && current.IdleExpiresAt == touch.IdleExpiresAt;

    internal static AuthenticationProperties CreateRenewalProperties(
        AuthenticationProperties source,
        AdminSessionTouchResult touch)
    {
        var renew = new AuthenticationProperties
        {
            AllowRefresh = source.AllowRefresh,
            IsPersistent = source.IsPersistent,
            RedirectUri = source.RedirectUri,
        };

        foreach (var item in source.Items)
            renew.Items[item.Key] = item.Value;

        // Set after copying Items — IssuedUtc/ExpiresUtc are stored in Items and would
        // otherwise be overwritten by the source ticket's prior expiry values.
        renew.IssuedUtc = touch.LastSeenAt;
        renew.ExpiresUtc = touch.IdleExpiresAt;

        return renew;
    }

    internal static void Schedule(
        HttpContext httpContext,
        ClaimsPrincipal principal,
        AuthenticationProperties properties,
        string sessionId,
        AdminSessionTouchResult touch)
    {
        httpContext.Response.OnStarting(async () =>
        {
            var sessions = httpContext.RequestServices.GetRequiredService<AdminSessionRepository>();
            var current = await sessions.GetSessionAsync(sessionId, CancellationToken.None);
            if (!IsStillAuthoritative(touch, current))
                return;

            var renewProperties = CreateRenewalProperties(properties, touch);
            await httpContext.SignInAsync(
                AdminAuthenticationConstants.Scheme,
                principal,
                renewProperties);
        });
    }

    internal static async Task MaybeHoldAfterTouchForTestsAsync(
        HttpContext httpContext,
        AdminSessionTouchResult touch)
    {
        if (HoldAfterTouchAsync is null)
            return;

        if (!httpContext.Request.Headers.ContainsKey(AdminSessionTouchTestHooks.HoldAfterTouchHeaderName))
            return;

        await HoldAfterTouchAsync(touch);
    }
}

/// <summary>
/// Test-only constants for Admin session touch race reproduction (#391).
/// </summary>
internal static class AdminSessionTouchTestHooks
{
    internal const string HoldAfterTouchHeaderName = "X-Amane-Test-Hold-After-Touch";
}
