using System.Net;
using System.Text.RegularExpressions;
using Amane.Mailer.Operations.AcsSetup;
using Amane.Mailer.Operations.AdminBootstrap;
using Amane.Mailer.Setup;
using Amane.Mailer.Setup.Assistant;

namespace Amane.Mailer.Tests.Setup.Assistant;

/// <summary>
/// Drives the real assistant host over real HTTP on an ephemeral loopback port. Typed operations
/// are replaced by <see cref="FakeSetupAssistantOperations"/>, so no test touches Docker, ACS, or
/// an Admin database.
/// </summary>
internal sealed class SetupAssistantHarness : IAsyncDisposable
{
    private readonly HttpClientHandler _handler;

    private SetupAssistantHarness(
        SetupAssistantHost host,
        SetupAssistantSessionManager sessions,
        FakeSetupAssistantOperations operations,
        TestTimeProvider time,
        HttpClientHandler handler,
        HttpClient client)
    {
        Host = host;
        Sessions = sessions;
        Operations = operations;
        Time = time;
        _handler = handler;
        Client = client;
    }

    internal SetupAssistantHost Host { get; }

    internal SetupAssistantSessionManager Sessions { get; }

    internal FakeSetupAssistantOperations Operations { get; }

    internal TestTimeProvider Time { get; }

    internal HttpClient Client { get; }

    internal string Origin => $"http://127.0.0.1:{Host.BoundPort}";

    internal static async Task<SetupAssistantHarness> StartAsync(
        FakeSetupAssistantOperations? operations = null,
        SetupAssistantOptions? options = null,
        TestTimeProvider? time = null)
    {
        var resolvedOptions = options ?? new SetupAssistantOptions();
        var resolvedTime = time ?? new TestTimeProvider(DateTimeOffset.Parse("2026-01-01T00:00:00Z"));
        var resolvedOperations = operations ?? new FakeSetupAssistantOperations();
        var sessions = new SetupAssistantSessionManager(resolvedOptions, resolvedTime);
        var host = await SetupAssistantHost.StartAsync(
            resolvedOptions,
            sessions,
            resolvedOperations,
            CancellationToken.None);

        var handler = new HttpClientHandler
        {
            AllowAutoRedirect = false,
            UseCookies = true,
            CookieContainer = new CookieContainer(),
        };
        var client = new HttpClient(handler) { BaseAddress = new Uri(host.BaseAddress) };
        return new SetupAssistantHarness(host, sessions, resolvedOperations, resolvedTime, handler, client);
    }

    internal Task<HttpResponseMessage> GetAsync(string path) => Client.GetAsync(path);

    /// <summary>Reads the current screen. Every POST redirects here, so this is the render target.</summary>
    internal async Task<string> ReadCurrentPageAsync()
    {
        using var response = await Client.GetAsync("/");
        var html = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        AssertNoExternalResource(html);
        return html;
    }

    /// <summary>
    /// Guards the offline invariant on every screen any test walks through: no script, no browser
    /// storage, and no absolute URL that leaves the loopback interface.
    /// </summary>
    internal static void AssertNoExternalResource(string document)
    {
        foreach (var forbidden in new[]
                 {
                     "<script", "serviceWorker", "localStorage", "sessionStorage", "indexedDB", "@import",
                 })
        {
            Assert.DoesNotContain(forbidden, document, StringComparison.OrdinalIgnoreCase);
        }

        foreach (Match url in Regex.Matches(document, "https?://[^\\s\"'<>]*"))
        {
            var host = new Uri(url.Value).Host;
            Assert.True(
                host is "127.0.0.1" or "localhost" or "::1",
                $"screen references a non-loopback host: {host}");
        }
    }

    internal async Task<HttpResponseMessage> PostAsync(
        string path,
        IEnumerable<KeyValuePair<string, string>>? fields = null,
        string? csrfToken = null,
        string? origin = null,
        bool includeOrigin = true)
    {
        var values = new List<KeyValuePair<string, string>>(fields ?? []);
        if (csrfToken is not null)
        {
            values.Add(new KeyValuePair<string, string>(
                SetupAssistantSecurity.CsrfFieldName,
                csrfToken));
        }

        var request = new HttpRequestMessage(HttpMethod.Post, path)
        {
            Content = new FormUrlEncodedContent(values),
        };
        if (includeOrigin)
        {
            request.Headers.Add("Origin", origin ?? Origin);
        }

        return await Client.SendAsync(request, TestContext.Current.CancellationToken);
    }

    /// <summary>Posts a step using the CSRF token currently rendered on the assistant screen.</summary>
    internal async Task<HttpResponseMessage> PostStepAsync(
        string path,
        params (string Name, string Value)[] fields)
    {
        var token = ExtractCsrfToken(await ReadCurrentPageAsync());
        return await PostAsync(
            path,
            fields.Select(field => new KeyValuePair<string, string>(field.Name, field.Value)),
            token);
    }

    internal async Task<HttpResponseMessage> RedeemTokenAsync(string? token = null) =>
        await PostAsync(
            "/token",
            [new KeyValuePair<string, string>("one_time_token", token ?? Sessions.OneTimeTokenText)]);

    internal static string? ExtractCsrfToken(string html)
    {
        var match = Regex.Match(
            html,
            $"name=\"{Regex.Escape(SetupAssistantSecurity.CsrfFieldName)}\" value=\"([^\"]+)\"");
        return match.Success ? match.Groups[1].Value : null;
    }

    public async ValueTask DisposeAsync()
    {
        Client.Dispose();
        _handler.Dispose();
        await Host.DisposeAsync();
        Sessions.Dispose();
    }
}

internal sealed class TestTimeProvider(DateTimeOffset start) : TimeProvider
{
    private DateTimeOffset _now = start;

    public override DateTimeOffset GetUtcNow() => _now;

    internal void Advance(TimeSpan delta) => _now += delta;
}

/// <summary>
/// Canonical-result stand-in for the #448-#451/#459 typed operations. It records the inputs the
/// assistant passed so tests can assert the assistant never invents Docker, ACS, or Admin values.
/// </summary>
internal sealed class FakeSetupAssistantOperations : ISetupAssistantOperations
{
    internal static readonly object Proof = new();

    internal SetupAssistantDockerPreflightOutcome DockerPreflight { get; set; } = new()
    {
        Passed = true,
        Code = SetupDockerResultCode.Succeeded,
        EngineKind = "LocalUnixSocket",
    };

    internal SetupAssistantMainSetupOutcome MainSetup { get; set; } = new()
    {
        Code = SetupApplyResultCode.ApplySucceeded,
        Kind = SetupAssistantOutcomeKind.Succeeded,
        ConfigurationApplied = true,
        AppliedProof = Proof,
    };

    internal SetupAssistantStagingOutcome Staging { get; set; } = new()
    {
        Code = AcsSetupResultCode.StagingVerificationSucceeded,
        Kind = SetupAssistantOutcomeKind.Succeeded,
        SendRequestAccepted = true,
        MaskedSenderEmail = "n***@example.test",
        MaskedRecipientEmail = "q***@example.test",
    };

    internal SetupAssistantMainSetupOutcome Production { get; set; } = new()
    {
        Code = AcsSetupResultCode.DeploymentSendReady,
        Kind = SetupAssistantOutcomeKind.Succeeded,
        ConfigurationApplied = true,
        DeploymentSendReady = true,
        AppliedProof = Proof,
    };

    internal SetupAssistantAdminPreflightOutcome AdminPreflight { get; set; } = new()
    {
        Satisfied = true,
        ReasonCode = "access_endpoint_accepted",
        Profile = SetupAssistantAdminProfile.LocalDevelopment,
    };

    internal SetupAssistantAdminBootstrapOutcome AdminBootstrap { get; set; } = SucceededAdmin();

    internal SetupAssistantMainSetupInput? LastMainSetupInput { get; private set; }

    internal SetupAssistantStagingInput? LastStagingInput { get; private set; }

    internal SetupAssistantAdminBootstrapInput? LastAdminBootstrapInput { get; private set; }

    internal int AdminBootstrapCalls { get; private set; }

    internal int DockerPreflightCalls { get; private set; }

    /// <summary>When set, BootstrapAdminAsync throws this exception after recording the call.</summary>
    internal Exception? BootstrapThrows { get; set; }

    /// <summary>When true, BootstrapAdminAsync throws OperationCanceledException.</summary>
    internal bool BootstrapCancels { get; set; }

    /// <summary>When set, CheckAdminAccessProfileAsync throws this exception.</summary>
    internal Exception? AdminPreflightThrows { get; set; }

    /// <summary>When true, CheckAdminAccessProfileAsync throws OperationCanceledException.</summary>
    internal bool AdminPreflightCancels { get; set; }

    public Task<SetupAssistantDockerPreflightOutcome> CheckDockerAsync(
        CancellationToken cancellationToken)
    {
        DockerPreflightCalls++;
        return Task.FromResult(DockerPreflight);
    }

    /// <summary>
    /// When set, <see cref="ApplyMainSetupAsync"/> waits for the task before returning. Used to
    /// keep a typed operation in flight across idle-deadline evaluations.
    /// </summary>
    internal TaskCompletionSource? ApplyHold { get; set; }

    internal int ApplyCalls { get; private set; }

    public async Task<SetupAssistantMainSetupOutcome> ApplyMainSetupAsync(
        SetupAssistantMainSetupInput input,
        CancellationToken cancellationToken)
    {
        ApplyCalls++;
        LastMainSetupInput = input;
        if (ApplyHold is { } hold)
        {
            await hold.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        }

        return MainSetup;
    }

    public Task<SetupAssistantStagingOutcome> VerifyStagingAsync(
        SetupAssistantStagingInput input,
        CancellationToken cancellationToken)
    {
        LastStagingInput = input;
        return Task.FromResult(Staging);
    }

    public Task<SetupAssistantMainSetupOutcome> EnableLiveSendingAsync(
        SetupAssistantProductionInput input,
        CancellationToken cancellationToken) =>
        Task.FromResult(Production);

    public Task<SetupAssistantAdminPreflightOutcome> CheckAdminAccessProfileAsync(
        SetupAssistantAdminAccessInput input,
        CancellationToken cancellationToken)
    {
        if (AdminPreflightCancels)
        {
            throw new OperationCanceledException(cancellationToken);
        }

        if (AdminPreflightThrows is { } ex)
        {
            throw ex;
        }

        return Task.FromResult(AdminPreflight);
    }

    public Task<SetupAssistantAdminBootstrapOutcome> BootstrapAdminAsync(
        SetupAssistantAdminBootstrapInput input,
        CancellationToken cancellationToken)
    {
        AdminBootstrapCalls++;
        LastAdminBootstrapInput = input;
        if (BootstrapCancels)
        {
            throw new OperationCanceledException(cancellationToken);
        }

        if (BootstrapThrows is { } ex)
        {
            throw ex;
        }

        return Task.FromResult(AdminBootstrap);
    }

    internal static SetupAssistantAdminBootstrapOutcome SucceededAdmin() => new()
    {
        Code = AdminBootstrapResultCode.Succeeded,
        Kind = SetupAssistantOutcomeKind.Succeeded,
        AccessProfile = "local-development",
        ConfigRollback = "not-applicable",
        AdminDatabaseState = "managed-same-user",
        AdminExposure = "enabled",
        LoginVerification = "verified",
        SetupStatusVerification = "verified",
        VerificationSessionCleanup = "revoked",
    };

    internal static SetupAssistantAdminBootstrapOutcome FailedAdmin(
        string code,
        string adminExposure = "disabled",
        string configRollback = "rolled-back",
        bool manualActionRequired = false) => new()
        {
            Code = code,
            Kind = manualActionRequired
            ? SetupAssistantOutcomeKind.ManualInterventionRequired
            : SetupAssistantOutcomeKind.Failed,
            AccessProfile = "local-development",
            ConfigRollback = configRollback,
            AdminDatabaseState = "unchanged",
            AdminExposure = adminExposure,
            LoginVerification = "not-attempted",
            SetupStatusVerification = "not-attempted",
            VerificationSessionCleanup = "not-attempted",
            ManualActionRequired = manualActionRequired,
        };
}
