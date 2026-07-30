using System.Net;
using System.Net.Http;

namespace Amane.Mailer.Setup.Assistant;

/// <summary>
/// AOT startup smoke for the assistant. It starts the real loopback host with stub operations and
/// asserts the bind address, the security headers, and the Host/Origin/CSRF rejections over real
/// HTTP. No browser, no Docker, no ACS call, and no Admin database write is involved.
/// </summary>
public static class SetupAssistantSelfCheckCommand
{
    public const int SuccessExitCode = 0;
    public const int FailureExitCode = 1;

    public static bool IsSelfCheckCommand(IReadOnlyList<string> args) =>
        args.Count == 2
        && string.Equals(args[0], "setup", StringComparison.Ordinal)
        && string.Equals(args[1], "assistant-self-check", StringComparison.Ordinal);

    public static async Task<int> ExecuteAsync(TextWriter output, TextWriter error)
    {
        try
        {
            var options = new SetupAssistantOptions();
            using var sessions = new SetupAssistantSessionManager(options);
            await using var host = await SetupAssistantHost.StartAsync(
                options,
                sessions,
                new SelfCheckOperations(),
                CancellationToken.None);

            foreach (var address in host.BoundAddresses)
            {
                if (!Uri.TryCreate(address, UriKind.Absolute, out var uri)
                    || !IPAddress.TryParse(uri.Host, out var ip)
                    || !IPAddress.IsLoopback(ip))
                {
                    error.WriteLine("setup assistant-self-check failed: non-loopback address bound.");
                    return FailureExitCode;
                }
            }

            using var client = new HttpClient { BaseAddress = new Uri(host.BaseAddress) };

            var landing = await client.GetAsync("/");
            if (landing.StatusCode != HttpStatusCode.OK)
            {
                error.WriteLine("setup assistant-self-check failed: landing page unavailable.");
                return FailureExitCode;
            }

            if (!HeaderEquals(landing, "Content-Security-Policy", SetupAssistantSecurity.ContentSecurityPolicy)
                || !HeaderEquals(landing, "X-Frame-Options", "DENY")
                || !HeaderEquals(landing, "X-Content-Type-Options", "nosniff")
                || landing.Headers.Contains("Access-Control-Allow-Origin")
                || landing.Headers.CacheControl?.NoStore != true)
            {
                error.WriteLine("setup assistant-self-check failed: response hardening missing.");
                return FailureExitCode;
            }

            var body = await landing.Content.ReadAsStringAsync();
            if (body.Contains("//", StringComparison.Ordinal)
                && (body.Contains("http://", StringComparison.OrdinalIgnoreCase)
                    || body.Contains("https://", StringComparison.OrdinalIgnoreCase)))
            {
                error.WriteLine("setup assistant-self-check failed: external resource reference found.");
                return FailureExitCode;
            }

            using var foreignHost = new HttpRequestMessage(HttpMethod.Get, "/");
            foreignHost.Headers.Host = "assistant.invalid:1";
            var hostRejected = await client.SendAsync(foreignHost);
            if (hostRejected.StatusCode != HttpStatusCode.BadRequest)
            {
                error.WriteLine("setup assistant-self-check failed: foreign Host accepted.");
                return FailureExitCode;
            }

            using var foreignOrigin = new HttpRequestMessage(HttpMethod.Post, "/token")
            {
                Content = new FormUrlEncodedContent(
                    new[] { new KeyValuePair<string, string>("one_time_token", "x") }),
            };
            foreignOrigin.Headers.Add("Origin", "http://assistant.invalid");
            var originRejected = await client.SendAsync(foreignOrigin);
            if (originRejected.StatusCode != HttpStatusCode.Forbidden)
            {
                error.WriteLine("setup assistant-self-check failed: foreign Origin accepted.");
                return FailureExitCode;
            }

            var stateChangingGet = await client.GetAsync("/welcome");
            if (stateChangingGet.StatusCode != HttpStatusCode.MethodNotAllowed)
            {
                error.WriteLine("setup assistant-self-check failed: state change reachable by GET.");
                return FailureExitCode;
            }

            sessions.Stop(SetupAssistantShutdownReason.Completed);
            output.WriteLine("setup assistant-self-check: ok");
            return SuccessExitCode;
        }
        catch (Exception)
        {
            error.WriteLine("setup assistant-self-check failed: unexpected error.");
            return FailureExitCode;
        }
    }

    private static bool HeaderEquals(HttpResponseMessage response, string name, string expected) =>
        response.Headers.TryGetValues(name, out var values)
        && values.Any(value => string.Equals(value, expected, StringComparison.Ordinal));

    /// <summary>Rejects every operation so the smoke never touches Docker, ACS, or Admin.</summary>
    private sealed class SelfCheckOperations : ISetupAssistantOperations
    {
        public Task<SetupAssistantDockerPreflightOutcome> CheckDockerAsync(
            CancellationToken cancellationToken) =>
            Task.FromResult(new SetupAssistantDockerPreflightOutcome
            {
                Passed = false,
                Code = SetupDockerResultCode.UnsupportedDockerEnvironment,
            });

        public Task<SetupAssistantMainSetupOutcome> ApplyMainSetupAsync(
            SetupAssistantMainSetupInput input,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<SetupAssistantStagingOutcome> VerifyStagingAsync(
            SetupAssistantStagingInput input,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<SetupAssistantMainSetupOutcome> EnableLiveSendingAsync(
            SetupAssistantProductionInput input,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<SetupAssistantAdminPreflightOutcome> CheckAdminAccessProfileAsync(
            SetupAssistantAdminAccessInput input,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<SetupAssistantAdminBootstrapOutcome> BootstrapAdminAsync(
            SetupAssistantAdminBootstrapInput input,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }
}
