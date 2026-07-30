using System.Net;
using Amane.Mailer.Operations.AcsSetup;
using Amane.Mailer.Operations.AdminBootstrap;
using Amane.Mailer.Setup;
using Amane.Mailer.Setup.Assistant;

namespace Amane.Mailer.Tests.Setup.Assistant;

/// <summary>
/// Regression coverage for the Agent B findings on the Draft PR: transition enforcement,
/// mode-specific completion, Admin exposure honesty, cancel wording, and idle leases.
/// </summary>
public sealed class SetupAssistantStateMachineTests
{
    private const string ServiceToken = "assistant-test-service-token";

    [Fact]
    public async Task Mode_4_cannot_finish_before_send_ready()
    {
        await using var harness = await SetupAssistantHarness.StartAsync();
        await ReachApplyOutcomeAsync(harness, SetupMode.ProductionAcs);
        await harness.PostStepAsync("/verify", ("action", "continue"));

        using var finish = await harness.PostStepAsync("/verify", ("action", "finish"));
        Assert.Equal(HttpStatusCode.Conflict, finish.StatusCode);
        Assert.Contains("Staging verification / Production 有効化", await harness.ReadCurrentPageAsync(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_failed_staging_verification_cannot_finish_main_setup()
    {
        await using var harness = await SetupAssistantHarness.StartAsync();
        harness.Operations.Staging = new SetupAssistantStagingOutcome
        {
            Code = AcsSetupResultCode.StagingVerificationFailed,
            Kind = SetupAssistantOutcomeKind.Failed,
        };

        await ReachApplyOutcomeAsync(harness, SetupMode.StagingVerification);
        await harness.PostStepAsync("/verify", ("action", "continue"));
        await harness.PostStepAsync(
            "/verify",
            ("action", "staging"),
            ("recipient_email", "ops@example.test"),
            ("environment_confirmation", AcsEnvironmentConfirmation.Staging),
            ("intent_confirmation", AcsStagingVerificationOperation.IntentPhrase));

        var page = await harness.ReadCurrentPageAsync();
        Assert.DoesNotContain("Main setup 完了へ進む", page, StringComparison.Ordinal);
        Assert.Contains("テスト送信をやり直す", page, StringComparison.Ordinal);

        using var finish = await harness.PostStepAsync("/verify", ("action", "finish"));
        Assert.Equal(HttpStatusCode.Conflict, finish.StatusCode);
    }

    [Fact]
    public async Task A_side_effect_operation_cannot_be_posted_twice()
    {
        await using var harness = await SetupAssistantHarness.StartAsync();
        await ReachApplyConfirmationAsync(harness, SetupMode.LocalMailpit);

        await harness.PostStepAsync("/confirm");
        Assert.Equal(1, harness.Operations.ApplyCalls);

        using var replay = await harness.PostStepAsync("/confirm");
        Assert.Equal(HttpStatusCode.Conflict, replay.StatusCode);
        Assert.Equal(1, harness.Operations.ApplyCalls);
    }

    [Fact]
    public async Task Final_guidance_reports_enabled_admin_exposure_after_a_failed_bootstrap()
    {
        await using var harness = await SetupAssistantHarness.StartAsync();
        harness.Operations.AdminBootstrap = FakeSetupAssistantOperations.FailedAdmin(
            AdminBootstrapResultCode.ConfigRollbackSucceeded,
            adminExposure: "enabled");

        await StartMainSetupAsync(harness, SetupMode.LocalMailpit);
        await RunAdminBootstrapAsync(harness);
        await harness.PostStepAsync("/finish", ("action", "continue"));

        var page = await harness.ReadCurrentPageAsync();
        Assert.Contains("有効（enabled）", page, StringComparison.Ordinal);
        Assert.DoesNotContain("無効（disabled）", page, StringComparison.Ordinal);
        Assert.Contains("[ACTION]", page, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Final_guidance_reports_unknown_admin_exposure_and_manual_action()
    {
        await using var harness = await SetupAssistantHarness.StartAsync();
        harness.Operations.AdminBootstrap = FakeSetupAssistantOperations.FailedAdmin(
            AdminBootstrapResultCode.ManualActionRequired,
            adminExposure: "unknown",
            configRollback: "failed",
            manualActionRequired: true);

        await StartMainSetupAsync(harness, SetupMode.LocalMailpit);
        await RunAdminBootstrapAsync(harness);
        await harness.PostStepAsync("/finish", ("action", "continue"));

        var page = await harness.ReadCurrentPageAsync();
        Assert.Contains("不明（unknown）", page, StringComparison.Ordinal);
        Assert.Contains("<dd>必要</dd>", page, StringComparison.Ordinal);
        Assert.Contains("[手動対応が必要]", page, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Cancel_after_apply_does_not_claim_settings_were_unchanged()
    {
        await using var harness = await SetupAssistantHarness.StartAsync();
        await StartMainSetupAsync(harness, SetupMode.LocalMailpit);

        using var response = await harness.PostStepAsync("/cancel");
        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        Assert.Contains("すでに実行した適用", body, StringComparison.Ordinal);
        Assert.DoesNotContain("設定は変更していません", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_in_flight_apply_is_not_treated_as_idle()
    {
        var options = new SetupAssistantOptions { IdleTimeout = TimeSpan.FromMinutes(15) };
        var hold = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var harness = await SetupAssistantHarness.StartAsync(options: options);
        harness.Operations.ApplyHold = hold;

        await ReachApplyConfirmationAsync(harness, SetupMode.LocalMailpit);

        var apply = harness.PostStepAsync("/confirm");
        await WaitUntilAsync(() => harness.Operations.ApplyCalls == 1);

        harness.Time.Advance(TimeSpan.FromMinutes(16));
        harness.Sessions.EvaluateDeadlines();
        Assert.Equal(SetupAssistantShutdownReason.None, harness.Sessions.ShutdownReason);

        hold.SetResult();
        using var response = await apply;
        Assert.Equal(HttpStatusCode.SeeOther, response.StatusCode);
    }

    [Fact]
    public async Task Origin_must_match_the_request_host_authority()
    {
        await using var harness = await SetupAssistantHarness.StartAsync();
        await harness.RedeemTokenAsync();
        var token = SetupAssistantHarness.ExtractCsrfToken(await harness.ReadCurrentPageAsync());

        using var response = await harness.PostAsync(
            "/welcome",
            [],
            csrfToken: token,
            origin: $"http://localhost:{harness.Host.BoundPort}");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Contains("ようこそ", await harness.ReadCurrentPageAsync(), StringComparison.Ordinal);
    }

    [Fact]
    public void Access_profile_preflight_rejects_local_profile_without_development()
    {
        Assert.False(AdminBootstrapWorkflow.TryValidateAccessProfile(
            AdminAccessProfile.LocalDevelopment,
            environmentName: "Production",
            allowedLocalAddress: "127.0.0.1",
            allowHttp: true,
            loopbackOnlyPublished: true,
            approvedReverseProxy: false,
            serverLocalAddressConfirmed: false,
            out var reason));
        Assert.Equal("local_profile_precondition_failed", reason);
    }

    [Fact]
    public void Access_profile_preflight_accepts_a_valid_local_profile()
    {
        Assert.True(AdminBootstrapWorkflow.TryValidateAccessProfile(
            AdminAccessProfile.LocalDevelopment,
            environmentName: "Development",
            allowedLocalAddress: "127.0.0.1",
            allowHttp: true,
            loopbackOnlyPublished: true,
            approvedReverseProxy: false,
            serverLocalAddressConfirmed: false,
            out var reason));
        Assert.Equal(AdminBootstrapWorkflow.AccessProfileAccepted, reason);
    }

    private static async Task ReachApplyConfirmationAsync(SetupAssistantHarness harness, SetupMode mode)
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
            ("service_token", ServiceToken),
            ("service_token_confirm", ServiceToken));
        if (mode != SetupMode.LocalMailpit)
        {
            await harness.PostStepAsync(
                "/acs",
                ("acs_connection_string", "endpoint=https://example.communication.azure.com/;accesskey=abcdefghijklmnopqrstuvwxyz0123456789ABCD"),
                ("acs_connection_string_confirm", "endpoint=https://example.communication.azure.com/;accesskey=abcdefghijklmnopqrstuvwxyz0123456789ABCD"),
                ("platform_sender_display_name", "Example Notifications"));
        }
    }

    private static async Task ReachApplyOutcomeAsync(SetupAssistantHarness harness, SetupMode mode)
    {
        await ReachApplyConfirmationAsync(harness, mode);
        if (mode == SetupMode.LocalMailpit)
        {
            await harness.PostStepAsync("/confirm");
            return;
        }

        await harness.PostStepAsync(
            "/confirm",
            ("environment_confirmation", SetupAssistantInputs.EnvironmentConfirmationFor(mode)),
            ("intent_confirmation", AcsRegisterOperation.IntentPhrase));
    }

    private static async Task StartMainSetupAsync(SetupAssistantHarness harness, SetupMode mode)
    {
        await ReachApplyOutcomeAsync(harness, mode);
        await harness.PostStepAsync("/verify", ("action", "continue"));
        await harness.PostStepAsync("/verify", ("action", "finish"));
    }

    private static async Task RunAdminBootstrapAsync(SetupAssistantHarness harness)
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
        await harness.PostStepAsync(
            "/admin-bootstrap",
            ("admin_username", "setup-admin"),
            ("admin_password", "assistant-test-admin-password"),
            ("admin_password_confirm", "assistant-test-admin-password"));
    }

    private static async Task WaitUntilAsync(Func<bool> predicate)
    {
        for (var i = 0; i < 100; i++)
        {
            if (predicate())
            {
                return;
            }

            await Task.Delay(20, TestContext.Current.CancellationToken);
        }

        Assert.Fail("condition was not met");
    }
}
