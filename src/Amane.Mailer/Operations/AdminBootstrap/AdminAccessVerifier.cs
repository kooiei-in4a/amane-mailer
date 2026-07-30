using System.Net;
using System.Net.Http.Headers;
using System.Text.RegularExpressions;
using Amane.Mailer.Admin;
using Amane.Mailer.Setup;

namespace Amane.Mailer.Operations.AdminBootstrap;

internal enum AdminAccessProfile
{
    LocalDevelopment = 0,
    ProductionHttps = 1,
}

/// <summary>A normalized, policy-checked Admin origin. Raw URL strings never reach the verifier.</summary>
internal sealed class TrustedAdminAccessEndpoint
{
    private TrustedAdminAccessEndpoint(AdminAccessProfile profile, Uri origin)
    {
        Profile = profile;
        Origin = origin;
    }

    internal AdminAccessProfile Profile { get; }
    internal Uri Origin { get; }

    internal static bool TryCreate(
        AdminAccessProfile profile,
        Uri origin,
        out TrustedAdminAccessEndpoint? endpoint)
    {
        endpoint = null;
        if (!origin.IsAbsoluteUri
            || !string.IsNullOrEmpty(origin.UserInfo)
            || !string.IsNullOrEmpty(origin.Query)
            || !string.IsNullOrEmpty(origin.Fragment)
            || origin.AbsolutePath != "/")
        {
            return false;
        }

        if (profile == AdminAccessProfile.LocalDevelopment)
        {
            if (origin.Scheme != Uri.UriSchemeHttp
                || !(origin.IsLoopback
                    || IPAddress.TryParse(origin.Host, out var address) && IPAddress.IsLoopback(address)))
            {
                return false;
            }
        }
        else if (profile == AdminAccessProfile.ProductionHttps
                 && origin.Scheme != Uri.UriSchemeHttps)
        {
            return false;
        }

        endpoint = new TrustedAdminAccessEndpoint(profile, origin);
        return true;
    }

    internal Uri Resolve(string path) => new(Origin, path);
}

internal sealed class AdminAccessVerificationResult
{
    internal required bool LoginPageReached { get; init; }
    internal required bool LoginSucceeded { get; init; }
    internal required bool SetupStatusReached { get; init; }
    internal required bool SessionMayExist { get; init; }
    internal required string Code { get; init; }
}

internal enum AdminExposureProbeResult
{
    Unknown = 0,
    NotFound = 1,
    LoginPageReached = 2,
}

/// <summary>
/// Same-origin, no-redirect verifier for the selected Admin access profile. It owns its cookie
/// container and never returns response bodies, credentials, operation IDs, or raw exceptions.
/// </summary>
internal sealed partial class AdminAccessVerifier
{
    internal static readonly TimeSpan DefaultVerificationBudget = TimeSpan.FromSeconds(60);

    internal async Task<AdminExposureProbeResult> ProbeExposureAsync(
        TrustedAdminAccessEndpoint endpoint,
        TimeSpan budget,
        CancellationToken cancellationToken)
    {
        using var budgetCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        budgetCts.CancelAfter(budget);
        var token = budgetCts.Token;
        using var handler = new HttpClientHandler { AllowAutoRedirect = false };
        using var client = new HttpClient(handler)
        {
            Timeout = Timeout.InfiniteTimeSpan,
            BaseAddress = endpoint.Origin,
        };
        client.DefaultRequestHeaders.UserAgent.Add(
            new ProductInfoHeaderValue("amane-setup-verifier", "1"));

        try
        {
            using var loginPage = await client.GetAsync(endpoint.Resolve("/admin/login"), token);
            if (loginPage.StatusCode == HttpStatusCode.NotFound
                && IsSameOrigin(endpoint, loginPage.RequestMessage?.RequestUri))
            {
                return AdminExposureProbeResult.NotFound;
            }

            if (loginPage.StatusCode == HttpStatusCode.OK
                && IsSameOrigin(endpoint, loginPage.RequestMessage?.RequestUri)
                && IsHtml(loginPage.Content.Headers.ContentType))
            {
                var loginHtml = await loginPage.Content.ReadAsStringAsync(token);
                if (loginHtml.Contains("action=\"/admin/api/login\"", StringComparison.Ordinal))
                    return AdminExposureProbeResult.LoginPageReached;
            }

            return AdminExposureProbeResult.Unknown;
        }
        catch (OperationCanceledException)
        {
            return AdminExposureProbeResult.Unknown;
        }
        catch (HttpRequestException)
        {
            return AdminExposureProbeResult.Unknown;
        }
        catch (IOException)
        {
            return AdminExposureProbeResult.Unknown;
        }
    }

    internal async Task<AdminAccessVerificationResult> VerifyAsync(
        TrustedAdminAccessEndpoint endpoint,
        string username,
        string password,
        AdminBootstrapOperationId operationId,
        TimeSpan budget,
        CancellationToken cancellationToken)
    {
        using var budgetCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        budgetCts.CancelAfter(budget);
        var token = budgetCts.Token;
        var cookies = new CookieContainer();
        using var handler = new HttpClientHandler
        {
            AllowAutoRedirect = false,
            CookieContainer = cookies,
            UseCookies = true,
        };
        using var client = new HttpClient(handler)
        {
            Timeout = Timeout.InfiniteTimeSpan,
            BaseAddress = endpoint.Origin,
        };
        client.DefaultRequestHeaders.UserAgent.Add(
            new ProductInfoHeaderValue("amane-setup-verifier", "1"));

        var sessionMayExist = false;
        try
        {
            using var loginPage = await client.GetAsync(endpoint.Resolve("/admin/login"), token);
            if (loginPage.StatusCode != HttpStatusCode.OK
                || !IsSameOrigin(endpoint, loginPage.RequestMessage?.RequestUri)
                || !IsHtml(loginPage.Content.Headers.ContentType))
            {
                return Failed("login-page-unavailable", sessionMayExist);
            }

            var loginHtml = await loginPage.Content.ReadAsStringAsync(token);
            var tokenMatch = AntiforgeryTokenPattern().Match(loginHtml);
            if (!tokenMatch.Success
                || !loginHtml.Contains("action=\"/admin/api/login\"", StringComparison.Ordinal))
            {
                return Failed("login-page-marker-missing", sessionMayExist);
            }

            using var loginRequest = new HttpRequestMessage(
                HttpMethod.Post,
                endpoint.Resolve("/admin/api/login"));
            loginRequest.Headers.TryAddWithoutValidation(
                AdminAuthenticationHandlers.WorkflowOperationHeader,
                operationId.Value);
            loginRequest.Content = new FormUrlEncodedContent(
            [
                new("__RequestVerificationToken", WebUtility.HtmlDecode(tokenMatch.Groups[1].Value)),
                new("username", username),
                new("password", password),
            ]);
            using var login = await client.SendAsync(loginRequest, token);
            sessionMayExist = login.StatusCode is HttpStatusCode.Redirect
                or HttpStatusCode.SeeOther
                or HttpStatusCode.TemporaryRedirect;
            if (!sessionMayExist
                || !IsSameOrigin(endpoint, login.RequestMessage?.RequestUri)
                || !IsExpectedLocalRedirect(login, "/admin"))
            {
                return Failed("login-verification-failed", sessionMayExist, loginPageReached: true);
            }

            using var status = await client.GetAsync(endpoint.Resolve("/admin/setup-status"), token);
            if (status.StatusCode != HttpStatusCode.OK
                || !IsSameOrigin(endpoint, status.RequestMessage?.RequestUri)
                || !IsHtml(status.Content.Headers.ContentType))
            {
                return Failed(
                    "setup-status-unavailable",
                    sessionMayExist,
                    loginPageReached: true,
                    loginSucceeded: true);
            }

            var statusHtml = await status.Content.ReadAsStringAsync(token);
            if (!statusHtml.Contains(
                    "aria-label=\"Setup status\"",
                    StringComparison.Ordinal))
            {
                return Failed(
                    "setup-status-marker-missing",
                    sessionMayExist,
                    loginPageReached: true,
                    loginSucceeded: true);
            }

            return new AdminAccessVerificationResult
            {
                LoginPageReached = true,
                LoginSucceeded = true,
                SetupStatusReached = true,
                SessionMayExist = true,
                Code = "succeeded",
            };
        }
        catch (OperationCanceledException)
        {
            return Failed("verification-timeout-or-cancelled", sessionMayExist);
        }
        catch (HttpRequestException)
        {
            return Failed("network-verification-failed", sessionMayExist);
        }
        catch (IOException)
        {
            return Failed("network-verification-failed", sessionMayExist);
        }
    }

    private static AdminAccessVerificationResult Failed(
        string code,
        bool sessionMayExist,
        bool loginPageReached = false,
        bool loginSucceeded = false) =>
        new()
        {
            LoginPageReached = loginPageReached,
            LoginSucceeded = loginSucceeded,
            SetupStatusReached = false,
            SessionMayExist = sessionMayExist,
            Code = code,
        };

    private static bool IsSameOrigin(TrustedAdminAccessEndpoint endpoint, Uri? uri) =>
        uri is not null
        && string.Equals(endpoint.Origin.Scheme, uri.Scheme, StringComparison.OrdinalIgnoreCase)
        && string.Equals(endpoint.Origin.Host, uri.Host, StringComparison.OrdinalIgnoreCase)
        && endpoint.Origin.Port == uri.Port;

    private static bool IsHtml(MediaTypeHeaderValue? contentType) =>
        string.Equals(contentType?.MediaType, "text/html", StringComparison.OrdinalIgnoreCase);

    private static bool IsExpectedLocalRedirect(HttpResponseMessage response, string expectedPath)
    {
        var location = response.Headers.Location;
        if (location is null)
            return false;

        return location.IsAbsoluteUri
            ? string.Equals(location.AbsolutePath, expectedPath, StringComparison.Ordinal)
            : string.Equals(location.OriginalString, expectedPath, StringComparison.Ordinal);
    }

    [GeneratedRegex(
        "name=\"__RequestVerificationToken\"\\s+value=\"([^\"]+)\"",
        RegexOptions.CultureInvariant,
        matchTimeoutMilliseconds: 250)]
    private static partial Regex AntiforgeryTokenPattern();
}
