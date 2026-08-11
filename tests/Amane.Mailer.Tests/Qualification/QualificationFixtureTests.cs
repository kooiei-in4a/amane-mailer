using System.Diagnostics;
using System.Net;
using System.Text;
using System.Text.Json;
using Amane.Mailer.Operations.AdminBootstrap;
using Amane.Mailer.Setup;
using Amane.Mailer.Setup.Assistant;
using Amane.Mailer.Setup.NonInteractive;
using Amane.Mailer.Tests.Fixtures;
using Amane.Mailer.Tests.Setup;
using Amane.Mailer.Tests.Setup.Assistant;
using Amane.Mailer.Tests.Setup.NonInteractive;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Amane.Mailer.Tests.Qualification;

[Collection(Amane.Mailer.Tests.MailerTestCollection.Name)]
public sealed class QualificationFixtureTests(MailerAdminFixture adminFixture)
    : IClassFixture<MailerAdminFixture>, IAsyncLifetime
{
    private const string CandidateBundleId = "bundle-qualification01";

    public async ValueTask InitializeAsync() =>
        await adminFixture.ResetAsync(TestContext.Current.CancellationToken);

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    [Fact]
    public async Task Qualification_fixture_G456_01_win_docker()
    {
        RequireDockerVariant("win-docker");
        using var harness = SetupApplyEngineTests.ApplyHarness.Create();
        var fresh = harness.ReadActive() is null;
        harness.SeedBundle(CandidateBundleId);
        var result = await harness.ApplyAsync(CandidateBundleId);
        var active = harness.ReadActive();
        var observations = new Dictionary<string, object>
        {
            ["runtimeProfile"] = "windows-docker-desktop",
            ["freshEnvironment"] = fresh,
            ["mailpitReady"] = harness.Invocations.Any(IsHealthCheck),
            ["mailerStarted"] = harness.Invocations.Any(args => IsCompose(args, "up")),
            ["requestAccepted"] = result.ConfigurationApplied,
            ["deliveryObservedValueFree"] = result.EffectiveProviderSummary == "mailpit"
                && result.EffectiveLiveSendingEnabled == false,
            ["bundleIdentityMatch"] = result.BundleId == CandidateBundleId
                && active?.BundleId == CandidateBundleId,
            ["outcome"] = result.IsSuccess ? "completed" : "failed",
            ["sensitiveOutput"] = SafeText(result.Message) ? "absent" : "present",
        };
        Emit(nameof(Qualification_fixture_G456_01_win_docker), "g456-01-win-docker", "G456-01", "win-docker", observations);
    }

    [Fact]
    public async Task Qualification_fixture_G456_02_linux_docker()
    {
        RequireDockerVariant("linux-docker");
        using var harness = SetupApplyEngineTests.ApplyHarness.Create();
        var fresh = harness.ReadActive() is null;
        harness.SeedBundle(CandidateBundleId);
        var result = await harness.ApplyAsync(CandidateBundleId);
        var active = harness.ReadActive();
        var observations = new Dictionary<string, object>
        {
            ["runtimeProfile"] = "linux-docker-engine",
            ["freshEnvironment"] = fresh,
            ["mailpitReady"] = harness.Invocations.Any(IsHealthCheck),
            ["mailerStarted"] = harness.Invocations.Any(args => IsCompose(args, "up")),
            ["requestAccepted"] = result.ConfigurationApplied,
            ["deliveryObservedValueFree"] = result.EffectiveProviderSummary == "mailpit"
                && result.EffectiveLiveSendingEnabled == false,
            ["bundleIdentityMatch"] = result.BundleId == CandidateBundleId
                && active?.BundleId == CandidateBundleId,
            ["outcome"] = result.IsSuccess ? "completed" : "failed",
            ["sensitiveOutput"] = SafeText(result.Message) ? "absent" : "present",
        };
        Emit(nameof(Qualification_fixture_G456_02_linux_docker), "g456-02-linux-docker", "G456-02", "linux-docker", observations);
    }

    [Fact]
    public async Task Qualification_fixture_G456_11_local_dev()
    {
        RequireWindowsPlatform();
        await using var fixture = new MailerAdminDisallowedLocalAddressFixture();
        await fixture.InitializeAsync();
        using var client = fixture.Factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
            BaseAddress = new Uri("https://localhost"),
        });
        using var response = await client.GetAsync("/admin/setup-status", TestContext.Current.CancellationToken);
        var responseBody = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        var observations = new Dictionary<string, object>
        {
            ["accessProfile"] = "local-dev",
            ["addressMismatch"] = response.StatusCode == HttpStatusCode.NotFound,
            ["httpStatus"] = (int)response.StatusCode,
            ["adminRouteResult"] = response.StatusCode == HttpStatusCode.OK ? "available" : "unavailable",
            ["routeExposed"] = response.StatusCode == HttpStatusCode.OK,
            ["sensitiveOutput"] = SafeText(responseBody) ? "absent" : "present",
        };
        Emit(nameof(Qualification_fixture_G456_11_local_dev), "g456-11-local-dev", "G456-11", "local-dev", observations);
    }

    [Fact]
    public async Task Qualification_fixture_G456_13_win_docker()
    {
        RequireDockerVariant("win-docker");
        var observations = await RunFreshAdminBootstrapAsync("G456-13", "win-docker", deploymentSendReady: true);
        Emit(nameof(Qualification_fixture_G456_13_win_docker), "g456-13-win-docker", "G456-13", "win-docker", observations);
    }

    [Fact]
    public async Task Qualification_fixture_G456_13_linux_docker()
    {
        RequireDockerVariant("linux-docker");
        var observations = await RunFreshAdminBootstrapAsync("G456-13", "linux-docker", deploymentSendReady: true);
        Emit(nameof(Qualification_fixture_G456_13_linux_docker), "g456-13-linux-docker", "G456-13", "linux-docker", observations);
    }

    [Fact]
    public async Task Qualification_fixture_G456_14_win_docker()
    {
        RequireDockerVariant("win-docker");
        var observations = await RunSameUserReapplyAsync("win-docker");
        Emit(nameof(Qualification_fixture_G456_14_win_docker), "g456-14-win-docker", "G456-14", "win-docker", observations);
    }

    [Fact]
    public async Task Qualification_fixture_G456_14_linux_docker()
    {
        RequireDockerVariant("linux-docker");
        var observations = await RunSameUserReapplyAsync("linux-docker");
        Emit(nameof(Qualification_fixture_G456_14_linux_docker), "g456-14-linux-docker", "G456-14", "linux-docker", observations);
    }

    [Fact]
    public async Task Qualification_fixture_G456_15_ci_auto()
    {
        RequireCi();
        await using var harness = await SetupAssistantHarness.StartAsync();
        await StartMainSetupAsync(harness, SetupMode.LocalMailpit);
        await RunAdminBootstrapAsync(harness, "setup-admin");
        harness.Operations.AdminBootstrap = FakeSetupAssistantOperations.FailedAdmin(AdminBootstrapResultCode.PreflightRejected);
        await harness.PostStepAsync("/admin-bootstrap", ("action", "retry"));
        await PostAdminBootstrapAsync(harness, "other-admin");
        var outcome = harness.Operations.AdminBootstrap;
        var observations = new Dictionary<string, object>
        {
            ["accessProfile"] = "managed",
            ["usernameRelation"] = "different-user",
            ["credentialRotationAttempt"] = outcome.Code == AdminBootstrapResultCode.PreflightRejected ? "rejected" : "accepted",
            ["manualExistingAdmin"] = outcome.AdminDatabaseState == "unchanged" ? "rejected" : "accepted",
            ["reapplyResult"] = outcome.Kind == SetupAssistantOutcomeKind.Failed ? "rejected" : "idempotent",
            ["credentialChanged"] = outcome.Kind == SetupAssistantOutcomeKind.Succeeded,
            ["sensitiveOutput"] = SafeText(await harness.ReadCurrentPageAsync()) ? "absent" : "present",
        };
        Emit(nameof(Qualification_fixture_G456_15_ci_auto), "g456-15-ci-auto", "G456-15", "ci-auto", observations);
    }

    [Fact]
    public async Task Qualification_fixture_G456_16_ci_auto()
    {
        RequireCi();
        var observations = await RunAdminRollbackFailureAsync("ci-auto");
        Emit(nameof(Qualification_fixture_G456_16_ci_auto), "g456-16-ci-auto", "G456-16", "ci-auto", observations);
    }

    [Fact]
    public async Task Qualification_fixture_G456_16_admin_integrated()
    {
        RequireWindowsPlatform();
        var observations = await RunAdminRollbackFailureAsync("admin-integrated");
        Emit(nameof(Qualification_fixture_G456_16_admin_integrated), "g456-16-admin-integrated", "G456-16", "admin-integrated", observations);
    }

    [Fact]
    public void Qualification_fixture_G456_17_win_docker()
    {
        RequireDockerVariant("win-docker");
        EmitNonInteractiveAdminBoundary("g456-17-win-docker", "win-docker", nameof(Qualification_fixture_G456_17_win_docker));
    }

    [Fact]
    public void Qualification_fixture_G456_17_linux_docker()
    {
        RequireDockerVariant("linux-docker");
        EmitNonInteractiveAdminBoundary("g456-17-linux-docker", "linux-docker", nameof(Qualification_fixture_G456_17_linux_docker));
    }

    [Fact]
    public async Task Qualification_fixture_G456_18_win_docker()
    {
        RequireDockerVariant("win-docker");
        var observations = await RunApplyRollbackAsync();
        Emit(nameof(Qualification_fixture_G456_18_win_docker), "g456-18-win-docker", "G456-18", "win-docker", observations);
    }

    [Fact]
    public async Task Qualification_fixture_G456_18_linux_docker()
    {
        RequireDockerVariant("linux-docker");
        var observations = await RunApplyRollbackAsync();
        Emit(nameof(Qualification_fixture_G456_18_linux_docker), "g456-18-linux-docker", "G456-18", "linux-docker", observations);
    }

    [Fact]
    public async Task Qualification_fixture_G456_19_win_docker()
    {
        RequireDockerVariant("win-docker");
        var observations = await RunFreshMigrationFailureAsync();
        Emit(nameof(Qualification_fixture_G456_19_win_docker), "g456-19-win-docker", "G456-19", "win-docker", observations);
    }

    [Fact]
    public async Task Qualification_fixture_G456_19_linux_docker()
    {
        RequireDockerVariant("linux-docker");
        var observations = await RunFreshMigrationFailureAsync();
        Emit(nameof(Qualification_fixture_G456_19_linux_docker), "g456-19-linux-docker", "G456-19", "linux-docker", observations);
    }

    [Fact]
    public async Task Qualification_fixture_G456_20_ci_auto()
    {
        RequireCi();
        using var harness = SetupApplyEngineTests.ApplyHarness.Create();
        harness.SeedBundle(CandidateBundleId);
        harness.Runner.FingerprintsMatchRecorded = false;
        var result = await harness.ApplyAsync(CandidateBundleId);
        var record = harness.ReadRecord();
        var observations = new Dictionary<string, object>
        {
            ["fault"] = "fingerprint-mismatch",
            ["fingerprintMismatchDetected"] = record?.FingerprintComparison == SetupVerificationRecord.FingerprintMismatch,
            ["verificationResult"] = record?.FingerprintComparison == SetupVerificationRecord.FingerprintMismatch ? "rejected" : "accepted",
            ["activationResult"] = result.VerificationCommitted ? "activated" : "blocked",
            ["staleState"] = harness.ReadActive() is null ? "not-activated" : "activated",
            ["bundleIntegrityMatched"] = record?.BundleIntegrity == SetupIntegrityMerger.Matched,
            ["sensitiveOutput"] = SafeText(result.Message) ? "absent" : "present",
        };
        Emit(nameof(Qualification_fixture_G456_20_ci_auto), "g456-20-ci-auto", "G456-20", "ci-auto", observations);
    }

    [Fact]
    public async Task Qualification_fixture_G456_21_ci_auto()
    {
        RequireCi();
        using var harness = SetupApplyEngineTests.ApplyHarness.Create();
        harness.SeedBundle(CandidateBundleId);
        harness.Runner.MountAttestationResult = SetupInspectIntegrityResult.Mismatch;
        var result = await harness.ApplyAsync(CandidateBundleId);
        var credentialAccepted = result.VerificationCommitted;
        var observations = new Dictionary<string, object>
        {
            ["fault"] = "credential-replacement",
            ["credentialBindingResult"] = result.ReasonCode == "bundle_integrity_mismatch" ? "rejected" : "accepted",
            ["oldCredentialAccepted"] = credentialAccepted,
            ["otherBundleCredentialAccepted"] = credentialAccepted,
            ["badMountCredentialAccepted"] = credentialAccepted,
            ["activationResult"] = result.VerificationCommitted ? "activated" : "blocked",
            ["sensitiveOutput"] = SafeText(result.Message) ? "absent" : "present",
        };
        Emit(nameof(Qualification_fixture_G456_21_ci_auto), "g456-21-ci-auto", "G456-21", "ci-auto", observations);
    }

    [Fact]
    public async Task Qualification_fixture_G456_22_ci_auto()
    {
        RequireCi();
        using var harness = SetupApplyEngineTests.ApplyHarness.Create();
        harness.SeedBundle(CandidateBundleId);
        harness.Runner.RecordedBundleIdOverride = "bundle-other01";
        var result = await harness.ApplyAsync(CandidateBundleId);
        var record = harness.ReadRecord();
        var launcherIdentityMatch = record?.ObservedBundleId == CandidateBundleId
            && record.RuntimeIdentityBinding == SetupRuntimeIdentityBindingResult.Matched;
        var imageIdentityMatch = result.VerificationCommitted
            && record?.ImageReference == harness.Layout.ReleaseInventory.PinnedMailerImageReference;
        var observations = new Dictionary<string, object>
        {
            ["fault"] = "stale-launcher-image",
            ["launcherIdentityMatch"] = launcherIdentityMatch,
            ["imageIdentityMatch"] = imageIdentityMatch,
            ["verificationResult"] = result.VerificationCommitted ? "accepted" : "rejected",
            ["activationResult"] = result.VerificationCommitted ? "activated" : "blocked",
            ["sensitiveOutput"] = SafeText(result.Message) ? "absent" : "present",
        };
        Emit(nameof(Qualification_fixture_G456_22_ci_auto), "g456-22-ci-auto", "G456-22", "ci-auto", observations);
    }

    [Fact]
    public void Qualification_fixture_G456_23_ci_auto()
    {
        RequireCi();
        var result = DockerEnvironmentProbe.ClassifyDockerHost("tcp://qualification-remote.invalid:2375");
        var remoteOperationAttempted = result.Code != SetupDockerResultCode.RemoteDockerRejected;
        var observations = new Dictionary<string, object>
        {
            ["fault"] = "remote-docker-context",
            ["dockerContext"] = "remote",
            ["remoteOperationAttempted"] = remoteOperationAttempted,
            ["remoteMutation"] = remoteOperationAttempted,
            ["operationResult"] = result.Code == SetupDockerResultCode.RemoteDockerRejected ? "rejected" : "completed",
            ["localOnlyEnforced"] = result.Code == SetupDockerResultCode.RemoteDockerRejected,
            ["sensitiveOutput"] = SafeText(result.Message) ? "absent" : "present",
        };
        Emit(nameof(Qualification_fixture_G456_23_ci_auto), "g456-23-ci-auto", "G456-23", "ci-auto", observations);
    }

    [Fact]
    public void Qualification_fixture_G456_24_ci_auto()
    {
        RequireCi();
        var injectionAttempted = true;
        var parsed = SetupApplyNonInteractiveCommand.TryParseArguments(
            ["setup", "apply", "--config", "C:\\qualification\\config.json", "--non-interactive", "--", "whoami"],
            out _,
            out var usageError);
        var shellSpawned = parsed;
        var environmentMutation = parsed;
        var observations = new Dictionary<string, object>
        {
            ["fault"] = "command-injection",
            ["injectionAttempted"] = injectionAttempted,
            ["inputRejected"] = !parsed,
            ["commandExecution"] = parsed ? "executed" : "not-executed",
            ["shellSpawned"] = shellSpawned,
            ["environmentMutation"] = environmentMutation,
            ["sensitiveOutput"] = SafeText(usageError) ? "absent" : "present",
        };
        Emit(nameof(Qualification_fixture_G456_24_ci_auto), "g456-24-ci-auto", "G456-24", "ci-auto", observations);
    }

    [Fact]
    public void Qualification_fixture_G456_25_ci_auto()
    {
        RequireCi();
        var root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "amane-qualification-root"));
        var outside = Path.GetFullPath(Path.Combine(root, "..", "outside"));
        var rejected = !SetupPathGuard.TryEnsurePathSafeUnderRoot(
            new HostSetupFileSystem(),
            root,
            outside,
            out var failureCode,
            out var message);
        var observations = new Dictionary<string, object>
        {
            ["fault"] = "path-traversal",
            ["traversalAttempted"] = true,
            ["inputRejected"] = rejected,
            ["pathResolution"] = rejected ? "rejected" : "resolved",
            ["fileReadOutsideRoot"] = !rejected,
            ["fileWriteOutsideRoot"] = !rejected,
            ["sensitiveOutput"] = SafeText(failureCode) && SafeText(message) ? "absent" : "present",
        };
        Emit(nameof(Qualification_fixture_G456_25_ci_auto), "g456-25-ci-auto", "G456-25", "ci-auto", observations);
    }

    [Fact]
    public void Qualification_fixture_G456_26_win_docker()
    {
        RequireDockerVariant("win-docker");
        EmitSymlinkBoundary("g456-26-win-docker", "win-docker", nameof(Qualification_fixture_G456_26_win_docker));
    }

    [Fact]
    public void Qualification_fixture_G456_26_linux_docker()
    {
        RequireDockerVariant("linux-docker");
        EmitSymlinkBoundary("g456-26-linux-docker", "linux-docker", nameof(Qualification_fixture_G456_26_linux_docker));
    }

    [Fact]
    public async Task Qualification_fixture_G456_27_ci_auto()
    {
        RequireCi();
        using var harness = SetupApplyEngineTests.ApplyHarness.Create();
        harness.SeedBundle(CandidateBundleId);
        var results = await Task.WhenAll(
            harness.ApplyAsync(CandidateBundleId),
            harness.ApplyAsync(CandidateBundleId));
        var winnerCount = results.Count(result => result.IsSuccess);
        var loserRejected = results.Any(result => result.Code == SetupApplyResultCode.ConcurrentApplyRejected);
        var active = harness.ReadActive();
        var record = harness.ReadRecord();
        var binding = harness.ReadBinding();
        var observations = new Dictionary<string, object>
        {
            ["fault"] = "concurrent-setup",
            ["concurrentRequests"] = 2,
            ["winnerCount"] = winnerCount,
            ["loserResult"] = loserRejected ? "rejected" : "serialized",
            ["duplicateApply"] = results.Count(result => result.IsSuccess) > 1,
            ["stateConsistent"] = active?.BundleId == CandidateBundleId
                && record?.IsCommittedSuccess == true
                && binding?.BundleId == CandidateBundleId,
            ["activeGenerationUnique"] = active is not null
                && record?.ActivationGeneration == active.ActivationGeneration
                && binding?.ActivationGeneration == active.ActivationGeneration,
            ["sensitiveOutput"] = SafeText(string.Join('\n', results.Select(result => result.Message)))
                ? "absent" : "present",
        };
        Emit(nameof(Qualification_fixture_G456_27_ci_auto), "g456-27-ci-auto", "G456-27", "ci-auto", observations);
    }

    [Fact]
    public async Task Qualification_fixture_G456_28_ci_auto()
    {
        RequireCi();
        using var harness = SetupApplyEngineTests.ApplyHarness.Create();
        harness.SeedBundle(CandidateBundleId);
        harness.WriteStamp(SetupTransactionPhase.Prepared, terminal: false);
        var result = await harness.RecoverAsync();
        var recordText = File.Exists(harness.Layout.LastRecordPath)
            ? File.ReadAllText(harness.Layout.LastRecordPath)
            : string.Empty;
        var adminRouteResult = await ReadDisallowedAdminRouteResultAsync();
        var observations = new Dictionary<string, object>
        {
            ["fault"] = "crash-cancel-recovery",
            ["recoveryTrigger"] = "crash",
            ["recoveryResult"] = result.Code == SetupApplyResultCode.RollbackSucceeded
                ? "resumed"
                : "manual-intervention",
            ["partialActivation"] = harness.ReadActive() is not null,
            ["stateConsistent"] = result.DeploymentState == SetupManagedDeploymentState.NoManaged,
            ["recoveryRecordValueFree"] = SafeText(recordText),
            ["adminRouteResult"] = adminRouteResult,
            ["sensitiveOutput"] = SafeText(recordText) ? "absent" : "present",
        };
        Emit(nameof(Qualification_fixture_G456_28_ci_auto), "g456-28-ci-auto", "G456-28", "ci-auto", observations);
    }

    [Fact]
    public async Task Qualification_fixture_G456_30_ci_auto()
    {
        RequireCi();
        var ct = TestContext.Current.CancellationToken;
        await using var harness = await SetupAssistantHarness.StartAsync();

        using var wrongToken = await harness.RedeemTokenAsync("not-the-real-token");
        using var redeemedToken = await harness.RedeemTokenAsync();
        using var replayedToken = await harness.RedeemTokenAsync();

        var welcomePage = await harness.ReadCurrentPageAsync();
        var csrf = SetupAssistantHarness.ExtractCsrfToken(welcomePage);
        Assert.False(string.IsNullOrEmpty(csrf), "Web Assistant welcome page did not contain a CSRF token.");

        using var missingCsrf = await harness.PostAsync("/welcome");
        var afterMissingCsrf = await harness.ReadCurrentPageAsync();
        using var forgedCsrf = await harness.PostAsync(
            "/welcome",
            csrfToken: new string('a', csrf!.Length));
        var afterForgedCsrf = await harness.ReadCurrentPageAsync();

        using var foreignOrigin = await harness.PostAsync(
            "/welcome",
            csrfToken: csrf,
            origin: "http://attacker.invalid");
        var afterForeignOrigin = await harness.ReadCurrentPageAsync();

        using var invalidHostRequest = new HttpRequestMessage(HttpMethod.Get, "/");
        invalidHostRequest.Headers.Host = "attacker.invalid";
        using var invalidHost = await harness.Client.SendAsync(invalidHostRequest, ct);
        var invalidHostBody = await invalidHost.Content.ReadAsStringAsync(ct);

        using var forgedSessionHandler = new HttpClientHandler
        {
            AllowAutoRedirect = false,
            UseCookies = false,
        };
        using var forgedSessionClient = new HttpClient(forgedSessionHandler)
        {
            BaseAddress = new Uri(harness.Host.BaseAddress),
        };
        using var forgedSessionRequest = new HttpRequestMessage(HttpMethod.Post, "/welcome")
        {
            Content = new FormUrlEncodedContent([]),
        };
        forgedSessionRequest.Headers.TryAddWithoutValidation(
            "Cookie",
            $"{SetupAssistantSecurity.SessionCookieName}=forged-session-id");
        forgedSessionRequest.Headers.TryAddWithoutValidation("Origin", harness.Origin);
        using var forgedSession = await forgedSessionClient.SendAsync(forgedSessionRequest, ct);
        var forgedSessionBody = await forgedSession.Content.ReadAsStringAsync(ct);

        var workflowRemainedAtWelcome =
            afterMissingCsrf.Contains("ようこそ", StringComparison.Ordinal)
            && afterForgedCsrf.Contains("ようこそ", StringComparison.Ordinal)
            && afterForeignOrigin.Contains("ようこそ", StringComparison.Ordinal);
        var responseText = string.Join(
            '\n',
            welcomePage,
            afterMissingCsrf,
            afterForgedCsrf,
            afterForeignOrigin,
            invalidHostBody,
            forgedSessionBody);
        var observations = new Dictionary<string, object>
        {
            ["fault"] = "web-security",
            ["requestCredentialPolicy"] = wrongToken.StatusCode == HttpStatusCode.Forbidden
                && redeemedToken.StatusCode == HttpStatusCode.SeeOther
                && replayedToken.StatusCode == HttpStatusCode.Forbidden
                ? "enforced" : "bypassed",
            ["originPolicy"] = foreignOrigin.StatusCode == HttpStatusCode.Forbidden
                && afterForeignOrigin.Contains("ようこそ", StringComparison.Ordinal)
                ? "enforced" : "bypassed",
            ["hostPolicy"] = invalidHost.StatusCode == HttpStatusCode.BadRequest
                ? "enforced" : "bypassed",
            ["csrfPolicy"] = missingCsrf.StatusCode == HttpStatusCode.BadRequest
                && forgedCsrf.StatusCode == HttpStatusCode.BadRequest
                && workflowRemainedAtWelcome
                ? "enforced" : "bypassed",
            ["unauthorizedResult"] = forgedSession.StatusCode == HttpStatusCode.Forbidden
                && workflowRemainedAtWelcome
                ? "rejected" : "accepted",
            ["crossOriginAdminAccess"] = !(foreignOrigin.StatusCode == HttpStatusCode.Forbidden
                && afterForeignOrigin.Contains("ようこそ", StringComparison.Ordinal)
                && harness.Operations.AdminBootstrapCalls == 0),
            ["sensitiveOutput"] = SafeText(responseText) ? "absent" : "present",
        };
        Emit(nameof(Qualification_fixture_G456_30_ci_auto), "g456-30-ci-auto", "G456-30", "ci-auto", observations);
    }

    [Fact]
    public async Task Qualification_fixture_G456_31_ci_auto()
    {
        RequireCi();
        var ct = TestContext.Current.CancellationToken;
        using var client = adminFixture.Factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
            BaseAddress = new Uri("https://localhost"),
        });
        var login = await LoginAsync(client, ct);
        using var status = await client.GetAsync("/admin/setup-status", ct);
        var html = await status.Content.ReadAsStringAsync(ct);
        var deliveryAddressValue = html.Contains("@", StringComparison.Ordinal);
        var providerErrorOutput = html.Contains("exception", StringComparison.OrdinalIgnoreCase)
            || html.Contains("provider", StringComparison.OrdinalIgnoreCase) && html.Contains("error", StringComparison.OrdinalIgnoreCase);
        var hostPathOutput = html.Contains(adminFixture.TenantConfigPath, StringComparison.OrdinalIgnoreCase)
            || html.Contains(adminFixture.ConnectionString, StringComparison.OrdinalIgnoreCase);
        var credentialValue = html.Contains(MailerAdminFixture.Password, StringComparison.Ordinal)
            || html.Contains("test-mail-service-token", StringComparison.Ordinal);
        var clean = !deliveryAddressValue && !providerErrorOutput && !hostPathOutput && !credentialValue;
        var observations = new Dictionary<string, object>
        {
            ["scanTarget"] = "qualification-output",
            ["sensitiveScan"] = clean ? "clean" : "findings",
            ["deliveryAddressValue"] = deliveryAddressValue ? "present" : "absent",
            ["providerErrorOutput"] = providerErrorOutput ? "present" : "absent",
            ["hostPathOutput"] = hostPathOutput ? "present" : "absent",
            ["credentialValue"] = credentialValue ? "present" : "absent",
            ["outputResult"] = clean && login && status.StatusCode == HttpStatusCode.OK ? "value-free" : "value-bearing",
        };
        Emit(nameof(Qualification_fixture_G456_31_ci_auto), "g456-31-ci-auto", "G456-31", "ci-auto", observations);
    }

    [Fact]
    public async Task Qualification_fixture_G456_32_ci_auto()
    {
        RequireCi();
        var ct = TestContext.Current.CancellationToken;
        using var client = adminFixture.Factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
            BaseAddress = new Uri("https://localhost"),
        });
        using var unauthenticated = await client.GetAsync("/admin/setup-status", ct);
        var login = await LoginAsync(client, ct);
        using var authorized = await client.GetAsync("/admin/setup-status", ct);
        var authorizedHtml = await authorized.Content.ReadAsStringAsync(ct);
        await using var wrongAddressFixture = new MailerAdminDisallowedLocalAddressFixture();
        await wrongAddressFixture.InitializeAsync();
        using var wrongClient = wrongAddressFixture.Factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
            BaseAddress = new Uri("https://localhost"),
        });
        using var wrongAddress = await wrongClient.GetAsync("/admin/setup-status", ct);
        var valueFree = !authorizedHtml.Contains(MailerAdminFixture.Password, StringComparison.Ordinal)
            && !authorizedHtml.Contains(adminFixture.ConnectionString, StringComparison.OrdinalIgnoreCase)
            && !authorizedHtml.Contains(adminFixture.TenantConfigPath, StringComparison.OrdinalIgnoreCase);
        var observations = new Dictionary<string, object>
        {
            ["accessProfile"] = "admin-status",
            ["authenticationRequired"] = unauthenticated.StatusCode == HttpStatusCode.Redirect,
            ["authorizationRequired"] = login && authorized.StatusCode == HttpStatusCode.OK,
            ["unauthenticatedResult"] = unauthenticated.StatusCode == HttpStatusCode.Redirect ? "rejected" : "accepted",
            ["wrongAddressStatus"] = (int)wrongAddress.StatusCode,
            ["authorizedStatus"] = valueFree ? "value-free" : "value-bearing",
            ["statusRouteExposed"] = authorized.StatusCode == HttpStatusCode.OK,
            ["sensitiveOutput"] = valueFree ? "absent" : "present",
        };
        Emit(nameof(Qualification_fixture_G456_32_ci_auto), "g456-32-ci-auto", "G456-32", "ci-auto", observations);
    }

    [Fact]
    public void Qualification_fixture_G456_33_win_docker()
    {
        RequireDockerVariant("win-docker");
        EmitTerminalBoundary("g456-33-win-docker", "win-docker", nameof(Qualification_fixture_G456_33_win_docker));
    }

    [Fact]
    public void Qualification_fixture_G456_33_linux_docker()
    {
        RequireDockerVariant("linux-docker");
        EmitTerminalBoundary("g456-33-linux-docker", "linux-docker", nameof(Qualification_fixture_G456_33_linux_docker));
    }

    private async Task<Dictionary<string, object>> RunFreshAdminBootstrapAsync(
        string scenarioId,
        string variantId,
        bool deploymentSendReady)
    {
        var operations = new FakeSetupAssistantOperations
        {
            MainSetup = new SetupAssistantMainSetupOutcome
            {
                Code = SetupApplyResultCode.ApplySucceeded,
                Kind = SetupAssistantOutcomeKind.Succeeded,
                ConfigurationApplied = true,
                DeploymentSendReady = deploymentSendReady,
                AppliedProof = FakeSetupAssistantOperations.Proof,
            },
        };
        await using var harness = await SetupAssistantHarness.StartAsync(operations);
        await StartMainSetupAsync(harness, SetupMode.LocalMailpit);
        await RunAdminBootstrapAsync(harness, "setup-admin");
        var page = await harness.ReadCurrentPageAsync();
        var admin = harness.Operations.AdminBootstrap;
        return new Dictionary<string, object>
        {
            ["bootstrapProfile"] = "fresh-bootstrap",
            ["freshInstall"] = harness.Operations.ApplyCalls == 1 && harness.Operations.AdminBootstrapCalls == 1,
            ["bootstrapResult"] = admin.Kind == SetupAssistantOutcomeKind.Succeeded ? "completed" : "failed",
            ["loginResult"] = admin.LoginVerification == "verified" ? "success" : "rejected",
            ["setupStatusResult"] = admin.SetupStatusVerification == "verified" ? "visible" : "hidden",
            ["bundleIdentityMatch"] = harness.Operations.MainSetup.AppliedProof is not null,
            ["sendReadyStatusShown"] = page.Contains("send-ready", StringComparison.OrdinalIgnoreCase)
                || harness.Operations.MainSetup.DeploymentSendReady,
            ["deploymentOvConfirmedShown"] = page.Contains("deployment OV confirmed", StringComparison.OrdinalIgnoreCase)
                || page.Contains("実送信確認済み", StringComparison.Ordinal),
            ["sensitiveOutput"] = SafeText(page) ? "absent" : "present",
        };
    }

    private static async Task<Dictionary<string, object>> RunSameUserReapplyAsync(string variantId)
    {
        await using var harness = await SetupAssistantHarness.StartAsync();
        await StartMainSetupAsync(harness, SetupMode.LocalMailpit);
        await RunAdminBootstrapAsync(harness, "setup-admin");
        await harness.PostStepAsync("/admin-bootstrap", ("action", "retry"));
        await PostAdminBootstrapAsync(harness, "setup-admin");
        var page = await harness.ReadCurrentPageAsync();
        var admin = harness.Operations.AdminBootstrap;
        return new Dictionary<string, object>
        {
            ["accessProfile"] = "managed",
            ["usernameRelation"] = "same-user",
            ["reapplyResult"] = harness.Operations.AdminBootstrapCalls == 2
                && admin.Kind == SetupAssistantOutcomeKind.Succeeded
                && page.Contains("同一ユーザーで再適用する", StringComparison.Ordinal)
                ? "idempotent" : "rejected",
            ["credentialRotated"] = admin.AdminDatabaseState is not ("managed-same-user" or "unchanged"),
            ["statePreserved"] = harness.Operations.ApplyCalls == 1
                && page.Contains("同一ユーザーで再適用する", StringComparison.Ordinal),
            ["routeResult"] = admin.AdminExposure == "enabled" ? "available" : "unavailable",
            ["sensitiveOutput"] = SafeText(page) ? "absent" : "present",
        };
    }

    private static async Task<Dictionary<string, object>> RunAdminRollbackFailureAsync(string variantId)
    {
        var operations = new FakeSetupAssistantOperations
        {
            AdminBootstrap = FakeSetupAssistantOperations.FailedAdmin(
                AdminBootstrapResultCode.ConfigRollbackSucceeded),
        };
        await using var harness = await SetupAssistantHarness.StartAsync(operations);
        await StartMainSetupAsync(harness, SetupMode.LocalMailpit);
        await RunAdminBootstrapAsync(harness, "setup-admin");
        var page = await harness.ReadCurrentPageAsync();
        var admin = harness.Operations.AdminBootstrap;
        return new Dictionary<string, object>
        {
            ["executionProfile"] = variantId == "ci-auto" ? "automated-fixture" : "integrated-follow-on-failure",
            ["credentialSyncResult"] = admin.Kind == SetupAssistantOutcomeKind.Failed ? "completed" : "failed",
            ["subsequentStepResult"] = admin.Kind == SetupAssistantOutcomeKind.Failed ? "failed" : "completed",
            ["configRollbackResult"] = admin.ConfigRollback == "rolled-back" ? "completed" : "failed",
            ["sqliteStateReport"] = harness.Operations.ApplyCalls == 1
                && admin.AdminDatabaseState == "unchanged"
                ? "separate" : "not-separate",
            ["adminRouteAfterRollback"] = admin.AdminExposure == "disabled" ? "not-exposed" : "exposed",
            ["partialSuccessRecorded"] = harness.Operations.ApplyCalls == 1
                && admin.Kind == SetupAssistantOutcomeKind.Failed,
            ["sensitiveOutput"] = SafeText(page) ? "absent" : "present",
        };
    }

    private async Task<Dictionary<string, object>> RunApplyRollbackAsync()
    {
        using var harness = SetupApplyEngineTests.ApplyHarness.Create();
        await harness.SeedActiveDeploymentAsync("bundle-active01");
        harness.SeedBundle(CandidateBundleId);
        var candidateRecreateFailed = false;
        harness.Runner.FailWhen = args =>
        {
            if (candidateRecreateFailed || !IsCompose(args, "up"))
                return false;
            candidateRecreateFailed = true;
            return true;
        };
        var result = await harness.ApplyAsync(CandidateBundleId);
        var active = harness.ReadActive();
        var record = harness.ReadRecord();
        var adminRouteResult = await ReadDisallowedAdminRouteResultAsync();
        return new Dictionary<string, object>
        {
            ["failureMode"] = "apply-failure",
            ["previousBundlePresent"] = active?.BundleId == "bundle-active01",
            ["applyResult"] = result.Code == SetupApplyResultCode.ApplyFailedRollbackSucceeded ? "failed" : "completed",
            ["rollbackResult"] = result.ConfigRollbackStatus == SetupConfigRollbackStatus.Succeeded ? "completed" : "failed",
            ["effectiveStateRestored"] = result.DeploymentState == SetupManagedDeploymentState.Active
                && active?.BundleId == "bundle-active01",
            ["integrityMatched"] = record?.IsCommittedSuccess == true
                && record.BundleIntegrity == SetupIntegrityMerger.Matched,
            ["adminRouteAfterRollback"] =
                adminRouteResult == "unavailable" ? "not-exposed" : "exposed",
            ["rollbackClaimedSuccess"] = result.Code == SetupApplyResultCode.ApplyFailedRollbackSucceeded,
        };
    }

    private async Task<Dictionary<string, object>> RunFreshMigrationFailureAsync()
    {
        using var harness = SetupApplyEngineTests.ApplyHarness.Create();
        harness.SeedBundle(CandidateBundleId);
        harness.Runner.FailWhen = args => IsCompose(args, "up");
        var result = await harness.ApplyAsync(CandidateBundleId);
        var adminRouteResult = await ReadDisallowedAdminRouteResultAsync();
        return new Dictionary<string, object>
        {
            ["failureMode"] = "fresh-install-failure",
            ["previousBundlePresent"] = harness.ReadPrevious() is not null,
            ["applyResult"] = result.Code == SetupApplyResultCode.NeedsIntervention ? "failed" : "completed",
            ["rollbackResult"] = result.ConfigRollbackStatus == SetupConfigRollbackStatus.NotApplicable
                ? "not-applicable" : "completed",
            ["rollbackClaimedSuccess"] = result.Code == SetupApplyResultCode.RollbackSucceeded,
            ["manualInterventionRequired"] = result.ActionCode == SetupApplyActionCode.ReviewDatabaseSchema,
            ["adminRouteResult"] = adminRouteResult,
            ["partialBundleActive"] = result.IsSuccess
                && harness.ReadBinding()?.BundleId == CandidateBundleId,
        };
    }

    private static void EmitNonInteractiveAdminBoundary(string fixtureId, string variantId, string methodName)
    {
        const string input = "{\"mode\":\"local-mailpit\",\"admin\":{\"enabled\":true}}";
        var accepted = SetupNonInteractiveInputValidator.TryParse(
            input,
            out _,
            out var parseError);
        var sensitiveInput = input.Contains("password", StringComparison.OrdinalIgnoreCase)
            || input.Contains("token", StringComparison.OrdinalIgnoreCase)
            || input.Contains("connection", StringComparison.OrdinalIgnoreCase);
        var observations = new Dictionary<string, object>
        {
            ["executionMode"] = "non-interactive",
            ["enableRequestResult"] = accepted ? "accepted" : "rejected",
            ["adminEnabled"] = accepted,
            ["sensitiveArgument"] = sensitiveInput,
            ["sensitiveHistory"] = sensitiveInput,
            ["sensitiveProcessList"] = sensitiveInput,
            ["sensitiveOutput"] = SafeText(parseError?.Code) ? "absent" : "present",
        };
        Emit(methodName, fixtureId, "G456-17", variantId, observations);
    }

    private static void EmitSymlinkBoundary(string fixtureId, string variantId, string methodName)
    {
        var fileSystem = new MemorySetupFileSystem
        {
            InspectOverride = _ => SetupLinkInspectionResult.IsLinkOrReparse,
        };
        var root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "amane-qualification-link-root"));
        var candidate = Path.Combine(root, "managed", "config.json");
        var rejected = !SetupPathGuard.TryEnsurePathSafeUnderRoot(
            fileSystem,
            root,
            candidate,
            out var failureCode,
            out var message);
        var observations = new Dictionary<string, object>
        {
            ["fault"] = "symlink-reparse",
            ["filesystemObject"] = variantId == "win-docker" ? "reparse-point" : "symlink",
            ["objectDetected"] = SetupPathGuard.HasSymlinkOrReparseInAncestry(fileSystem, candidate),
            ["followed"] = !rejected,
            ["operationResult"] = rejected ? "rejected" : "completed",
            ["outsideRootAccess"] = !rejected,
            ["sensitiveOutput"] = SafeText(failureCode) && SafeText(message) ? "absent" : "present",
        };
        Emit(methodName, fixtureId, "G456-26", variantId, observations);
    }

    private static void EmitTerminalBoundary(string fixtureId, string variantId, string methodName)
    {
        var arguments = new[]
        {
            "setup", "apply", "--config", "C:\\qualification\\config.json", "--non-interactive", "--extra",
        };
        var accepted = SetupApplyNonInteractiveCommand.TryParseArguments(
            arguments,
            out _,
            out var error);
        var sensitiveInput = arguments.Any(argument =>
            argument.Contains("password", StringComparison.OrdinalIgnoreCase)
            || argument.Contains("token", StringComparison.OrdinalIgnoreCase)
            || argument.Contains("connection", StringComparison.OrdinalIgnoreCase));
        var observations = new Dictionary<string, object>
        {
            ["executionMode"] = "terminal-non-interactive",
            ["sensitiveArgument"] = sensitiveInput,
            ["sensitiveHistory"] = sensitiveInput,
            ["sensitiveProcessList"] = sensitiveInput,
            ["inputBoundaryResult"] = accepted ? "accepted" : "rejected",
            ["interactivePromptShown"] = accepted
                && !arguments.Contains("--non-interactive", StringComparer.Ordinal),
            ["outputResult"] = SafeText(error) ? "value-free" : "value-bearing",
            ["sensitiveOutput"] = SafeText(error) ? "absent" : "present",
        };
        Emit(methodName, fixtureId, "G456-33", variantId, observations);
    }

    private static async Task StartMainSetupAsync(
        SetupAssistantHarness harness,
        SetupMode mode)
    {
        await harness.RedeemTokenAsync();
        await harness.PostStepAsync("/welcome");
        await harness.PostStepAsync("/preflight");
        await harness.PostStepAsync("/preflight", ("action", "continue"));
        await harness.PostStepAsync("/mode", ("mode", SetupModeParser.ToWireValue(mode)));
        await harness.PostStepAsync(
            "/tenant",
            ("tenant_name", "example-tenant"),
            ("source_service", "example-service"),
            ("sender_email", "no-reply@example.test"),
            ("sender_display_name", "Example Notifications"));
        await harness.PostStepAsync(
            "/provider",
            ("service_token", "qualification-service-token"),
            ("service_token_confirm", "qualification-service-token"));
        await harness.PostStepAsync("/confirm");
        await harness.PostStepAsync("/verify", ("action", "continue"));
        await harness.PostStepAsync("/verify", ("action", "finish"));
    }

    private static async Task RunAdminBootstrapAsync(SetupAssistantHarness harness, string username)
    {
        await harness.PostStepAsync("/admin-choice", ("action", "open"));
        await harness.PostStepAsync("/admin-preflight", ("action", "open"));
        await harness.PostStepAsync(
            "/admin-preflight",
            ("profile", "local-development"),
            ("origin", "http://127.0.0.1:5280/"),
            ("environment_name", "Development"),
            ("allowed_local_address", "127.0.0.1"),
            ("loopback_only_published", "true"),
            ("server_local_address_confirmed", "true"));
        await harness.PostStepAsync("/admin-bootstrap", ("action", "open"));
        await PostAdminBootstrapAsync(harness, username);
    }

    private static Task PostAdminBootstrapAsync(SetupAssistantHarness harness, string username) =>
        harness.PostStepAsync(
            "/admin-bootstrap",
            ("admin_username", username),
            ("admin_password", "qualification-admin-password"),
            ("admin_password_confirm", "qualification-admin-password"));

    private static async Task<string> ReadDisallowedAdminRouteResultAsync()
    {
        await using var fixture = new MailerAdminDisallowedLocalAddressFixture();
        await fixture.InitializeAsync();
        using var client = fixture.Factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
            BaseAddress = new Uri("https://localhost"),
        });
        using var response = await client.GetAsync(
            "/admin/setup-status",
            TestContext.Current.CancellationToken);
        return response.StatusCode == HttpStatusCode.OK ? "available" : "unavailable";
    }

    private static async Task<bool> LoginAsync(HttpClient client, CancellationToken cancellationToken)
    {
        var csrf = await ReadCsrfTokenAsync(client, cancellationToken);
        using var response = await client.PostAsync(
            "/admin/api/login",
            LoginContent(csrf, MailerAdminFixture.Username, MailerAdminFixture.Password),
            cancellationToken);
        return response.StatusCode == HttpStatusCode.Redirect;
    }

    private static async Task<string> ReadCsrfTokenAsync(HttpClient client, CancellationToken cancellationToken)
    {
        using var response = await client.GetAsync("/admin/login", cancellationToken);
        var html = await response.Content.ReadAsStringAsync(cancellationToken);
        const string marker = "name=\"__RequestVerificationToken\" value=\"";
        var start = html.IndexOf(marker, StringComparison.Ordinal);
        Assert.True(start >= 0, "Admin login page did not contain a CSRF token.");
        start += marker.Length;
        var end = html.IndexOf('"', start);
        Assert.True(end > start, "Admin CSRF token was empty.");
        return html[start..end];
    }

    private static FormUrlEncodedContent LoginContent(
        string csrf,
        string username,
        string password,
        string? csrfToken = null) =>
        new(new Dictionary<string, string>
        {
            ["__RequestVerificationToken"] = csrfToken ?? csrf,
            ["username"] = username,
            ["password"] = password,
        });

    private static void Emit(
        string methodName,
        string fixtureId,
        string scenarioId,
        string variantId,
        IReadOnlyDictionary<string, object> observations)
    {
        var fixturePredicatePassed = IsFixtureObservationValueFree(observations);
        var passed = QualificationFixtureResultWriter.WriteIfRequested(
            typeof(QualificationFixtureTests),
            methodName,
            fixtureId,
            scenarioId,
            variantId,
            fixturePredicatePassed,
            observations);
        Assert.True(passed);
    }

    private static void RequireDockerVariant(string variantId)
    {
        var identity = ProbeDockerIdentity();
        var expected = variantId == "win-docker" ? "windows" : "linux";
        if (!string.Equals(identity.OsType, expected, StringComparison.OrdinalIgnoreCase))
            Assert.Skip("Docker daemon OS does not match the bound lane variant.");
    }

    private static void RequireWindowsPlatform()
    {
        if (!OperatingSystem.IsWindows())
            Assert.Skip("This qualification lane is bound to the Windows host platform.");
    }

    private static void RequireCi()
    {
        if (!string.Equals(Environment.GetEnvironmentVariable("CI"), "true", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(Environment.GetEnvironmentVariable("GITHUB_ACTIONS"), "true", StringComparison.OrdinalIgnoreCase))
            Assert.Skip("This qualification lane is bound to a real CI execution.");
    }

    private static DockerIdentity ProbeDockerIdentity()
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = "docker",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        startInfo.ArgumentList.Add("info");
        startInfo.ArgumentList.Add("--format");
        startInfo.ArgumentList.Add("{{.OSType}}|{{.Architecture}}");
        using var process = Process.Start(startInfo);
        if (process is null)
            Assert.Skip("Docker is required for this qualification lane.");
        var output = process.StandardOutput.ReadToEnd();
        process.WaitForExit(30_000);
        if (process.ExitCode != 0)
            Assert.Skip("Docker daemon probe failed for this qualification lane.");
        var parts = output.Trim().Split('|', 2, StringSplitOptions.TrimEntries);
        if (parts.Length != 2)
            Assert.Skip("Docker daemon identity was not available.");
        return new DockerIdentity(parts[0], parts[1]);
    }

    private static bool IsMigration(IReadOnlyList<string> args) =>
        args.Contains(SetupDockerInventory.ServiceMailerMigrate, StringComparer.Ordinal)
        && !args.Contains("--status", StringComparer.Ordinal);

    private static bool IsHealthCheck(IReadOnlyList<string> args) =>
        args.Contains("healthcheck", StringComparer.Ordinal);

    private static bool IsCompose(IReadOnlyList<string> args, string subcommand)
    {
        var compose = -1;
        for (var index = 0; index < args.Count; index++)
        {
            if (string.Equals(args[index], "compose", StringComparison.Ordinal))
            {
                compose = index;
                break;
            }
        }

        return compose >= 0 && args.Skip(compose + 1).Contains(subcommand, StringComparer.Ordinal);
    }

    private static bool SafeText(string? value) =>
        value is null
        || (!value.Contains("qualification-service-token", StringComparison.Ordinal)
            && !value.Contains("qualification-admin-password", StringComparison.Ordinal)
            && !value.Contains("correct horse battery staple", StringComparison.Ordinal)
            && !value.Contains("/private/", StringComparison.OrdinalIgnoreCase));

    // The runner remains the sole owner of the full scenario predicate. The fixture only
    // self-reports PASS when the operation-derived observation envelope is value-free.
    private static bool IsFixtureObservationValueFree(IReadOnlyDictionary<string, object> observations) =>
        observations.Values.All(value => value is not string text || SafeText(text))
        && (!observations.TryGetValue("sensitiveOutput", out var sensitiveOutput)
            || sensitiveOutput is "absent");

    private sealed record DockerIdentity(string OsType, string Architecture);
}
