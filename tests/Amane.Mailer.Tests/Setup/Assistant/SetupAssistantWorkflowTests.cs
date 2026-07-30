using System.Net;
using Amane.Mailer.Operations.AcsSetup;
using Amane.Mailer.Operations.AdminBootstrap;
using Amane.Mailer.Setup;
using Amane.Mailer.Setup.Assistant;

namespace Amane.Mailer.Tests.Setup.Assistant;

/// <summary>
/// Screen-to-screen behaviour of the two transactions: mode 1-4 main setup, the mode 5 manual
/// boundary, and the optional Admin bootstrap that can only start after main setup succeeded.
/// </summary>
public sealed class SetupAssistantWorkflowTests
{
    private const string ServiceToken = "assistant-test-service-token";
    private const string AcsConnectionString = "endpoint=https://example.test/;accesskey=not-a-real-key";

    [Fact]
    public async Task Mode_1_walks_from_welcome_to_main_setup_complete()
    {
        await using var harness = await SetupAssistantHarness.StartAsync();
        await StartMainSetupAsync(harness, SetupMode.LocalMailpit);

        var page = await harness.ReadCurrentPageAsync();

        Assert.Contains("Main setup は完了しました", page, StringComparison.Ordinal);
        Assert.Equal(1, harness.Operations.DockerPreflightCalls);
        Assert.Equal(SetupMode.LocalMailpit, harness.Operations.LastMainSetupInput?.Mode);
    }

    [Fact]
    public async Task Mode_1_skips_the_acs_screen_and_never_sends_an_acs_connection_string()
    {
        await using var harness = await SetupAssistantHarness.StartAsync();
        await RedeemAndReachModeAsync(harness);

        await harness.PostStepAsync("/mode", ("mode", "local-mailpit"));
        await PostTenantAsync(harness);
        await PostProviderAsync(harness);

        var page = await harness.ReadCurrentPageAsync();
        Assert.Contains("適用前確認", page, StringComparison.Ordinal);

        await harness.PostStepAsync("/confirm");
        Assert.Null(harness.Operations.LastMainSetupInput?.AcsConnectionString);
    }

    [Fact]
    public async Task Mode_3_collects_acs_settings_and_runs_staging_verification()
    {
        await using var harness = await SetupAssistantHarness.StartAsync();
        await StartMainSetupAsync(harness, SetupMode.StagingVerification);

        var page = await harness.ReadCurrentPageAsync();

        Assert.Contains("送信要求受理", page, StringComparison.Ordinal);
        Assert.Equal("q***@example.test", harness.Operations.Staging.MaskedRecipientEmail);
        Assert.Equal(
            AcsEnvironmentConfirmation.Staging,
            harness.Operations.LastStagingInput?.EnvironmentConfirmation);
    }

    [Fact]
    public async Task Mode_4_completes_at_send_ready_and_never_claims_operational_verification()
    {
        await using var harness = await SetupAssistantHarness.StartAsync();
        await StartMainSetupAsync(harness, SetupMode.ProductionAcs);

        var page = await harness.ReadCurrentPageAsync();

        Assert.Contains("Deployment send-ready", page, StringComparison.Ordinal);
        Assert.Contains("到達", page, StringComparison.Ordinal);
        Assert.Contains("記録していません", page, StringComparison.Ordinal);
        Assert.DoesNotContain("operationally verified", page, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("実送信確認済み", page, StringComparison.Ordinal);
        Assert.DoesNotContain("release qualification", page, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Mode_5_only_shows_manual_guidance_and_applies_nothing()
    {
        await using var harness = await SetupAssistantHarness.StartAsync();
        await RedeemAndReachModeAsync(harness);

        await harness.PostStepAsync("/mode", ("mode", SetupAssistantInputs.ManualModeValue));

        var page = await harness.ReadCurrentPageAsync();
        Assert.Contains("Easy Setup の自動化対象外", page, StringComparison.Ordinal);
        Assert.Contains("runbook", page, StringComparison.OrdinalIgnoreCase);
        Assert.Null(harness.Operations.LastMainSetupInput);
    }

    [Fact]
    public async Task An_exact_confirmation_phrase_is_required_before_an_acs_apply()
    {
        await using var harness = await SetupAssistantHarness.StartAsync();
        await RedeemAndReachModeAsync(harness);
        await harness.PostStepAsync("/mode", ("mode", "staging-no-send"));
        await PostTenantAsync(harness);
        await PostProviderAsync(harness);
        await PostAcsAsync(harness);

        await harness.PostStepAsync(
            "/confirm",
            ("environment_confirmation", "staging"),
            ("intent_confirmation", AcsRegisterOperation.IntentPhrase));

        var page = await harness.ReadCurrentPageAsync();
        Assert.Contains("確認フレーズが完全に一致していません", page, StringComparison.Ordinal);
        Assert.Null(harness.Operations.LastMainSetupInput);
    }

    [Fact]
    public async Task A_failed_main_setup_is_shown_as_a_failure_and_offers_a_retry()
    {
        await using var harness = await SetupAssistantHarness.StartAsync();
        harness.Operations.MainSetup = new SetupAssistantMainSetupOutcome
        {
            Code = SetupApplyResultCode.ApplyFailedRollbackSucceeded,
            Kind = SetupAssistantOutcomeKind.Failed,
            ConfigurationApplied = false,
            ConfigRollbackStatus = "rolled-back",
        };

        await RedeemAndReachModeAsync(harness);
        await harness.PostStepAsync("/mode", ("mode", "local-mailpit"));
        await PostTenantAsync(harness);
        await PostProviderAsync(harness);
        await harness.PostStepAsync("/confirm");

        var page = await harness.ReadCurrentPageAsync();
        Assert.Contains("[FAIL]", page, StringComparison.Ordinal);
        Assert.Contains("適用をやり直す", page, StringComparison.Ordinal);
        Assert.DoesNotContain("Main setup は完了しました", page, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_manual_intervention_result_is_not_shown_as_success()
    {
        await using var harness = await SetupAssistantHarness.StartAsync();
        harness.Operations.MainSetup = new SetupAssistantMainSetupOutcome
        {
            Code = SetupApplyResultCode.RecoveryRequired,
            Kind = SetupAssistantOutcomeKind.ManualInterventionRequired,
            ConfigurationApplied = false,
            PersistentSideEffectMayRemain = true,
            PersistentSideEffectKind = "managed-state",
        };

        await RedeemAndReachModeAsync(harness);
        await harness.PostStepAsync("/mode", ("mode", "local-mailpit"));
        await PostTenantAsync(harness);
        await PostProviderAsync(harness);
        await harness.PostStepAsync("/confirm");

        var page = await harness.ReadCurrentPageAsync();
        Assert.Contains("[手動対応が必要]", page, StringComparison.Ordinal);
        Assert.Contains("復旧処理が必要", page, StringComparison.Ordinal);
        Assert.DoesNotContain("[成功]", page, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_action_required_result_is_distinguished_from_a_failure()
    {
        await using var harness = await SetupAssistantHarness.StartAsync();
        harness.Operations.MainSetup = new SetupAssistantMainSetupOutcome
        {
            Code = SetupApplyResultCode.ApplySucceeded,
            Kind = SetupAssistantOutcomeKind.ActionRequired,
            ConfigurationApplied = true,
            ActionCode = SetupApplyActionCode.ReviewDatabaseSchema,
        };

        await RedeemAndReachModeAsync(harness);
        await harness.PostStepAsync("/mode", ("mode", "local-mailpit"));
        await PostTenantAsync(harness);
        await PostProviderAsync(harness);
        await harness.PostStepAsync("/confirm");

        var page = await harness.ReadCurrentPageAsync();
        Assert.Contains("[ACTION]", page, StringComparison.Ordinal);
        Assert.Contains("データベース schema の確認が必要", page, StringComparison.Ordinal);
        Assert.DoesNotContain("[FAIL]", page, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Admin_bootstrap_cannot_start_before_main_setup_succeeds()
    {
        await using var harness = await SetupAssistantHarness.StartAsync();
        await RedeemAndReachModeAsync(harness);

        using var afterChoice = await harness.PostStepAsync("/admin-choice", ("action", "open"));
        using var afterBootstrap = await harness.PostStepAsync("/admin-bootstrap", ("action", "open"));

        Assert.Equal(HttpStatusCode.Conflict, afterChoice.StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, afterBootstrap.StatusCode);
        Assert.Contains(
            "画面の内容が古くなっています",
            await afterChoice.Content.ReadAsStringAsync(TestContext.Current.CancellationToken),
            StringComparison.Ordinal);
        Assert.Contains("Setup mode 選択", await harness.ReadCurrentPageAsync(), StringComparison.Ordinal);
        Assert.Null(harness.Operations.LastAdminBootstrapInput);
    }

    [Fact]
    public async Task Admin_can_be_skipped_after_a_successful_main_setup()
    {
        await using var harness = await SetupAssistantHarness.StartAsync();
        await StartMainSetupAsync(harness, SetupMode.LocalMailpit);

        await harness.PostStepAsync("/finish", ("action", "skip"));

        var page = await harness.ReadCurrentPageAsync();
        Assert.Contains("実行していません（skip）", page, StringComparison.Ordinal);
        Assert.Null(harness.Operations.LastAdminBootstrapInput);
    }

    [Fact]
    public async Task Admin_bootstrap_succeeds_as_a_separate_transaction()
    {
        await using var harness = await SetupAssistantHarness.StartAsync();
        await StartMainSetupAsync(harness, SetupMode.LocalMailpit);
        await RunAdminBootstrapAsync(harness);

        var page = await harness.ReadCurrentPageAsync();

        Assert.Contains("Admin bootstrap transaction", page, StringComparison.Ordinal);
        Assert.Contains("login と状態表示の確認に成功", page, StringComparison.Ordinal);
        Assert.Equal("setup-admin", harness.Operations.LastAdminBootstrapInput?.Username);
    }

    [Fact]
    public async Task A_failed_admin_bootstrap_keeps_the_main_setup_success()
    {
        await using var harness = await SetupAssistantHarness.StartAsync();
        harness.Operations.AdminBootstrap =
            FakeSetupAssistantOperations.FailedAdmin(AdminBootstrapResultCode.ApplyFailed);

        await StartMainSetupAsync(harness, SetupMode.LocalMailpit);
        await RunAdminBootstrapAsync(harness);

        var outcomePage = await harness.ReadCurrentPageAsync();
        Assert.Contains("Admin 有効化の適用に失敗", outcomePage, StringComparison.Ordinal);
        Assert.Contains("Main setup の成功は維持されます", outcomePage, StringComparison.Ordinal);

        await harness.PostStepAsync("/finish", ("action", "continue"));
        var finalPage = await harness.ReadCurrentPageAsync();
        Assert.Contains("<dd>失敗</dd>", finalPage, StringComparison.Ordinal);
        Assert.Contains("無効（disabled）", finalPage, StringComparison.Ordinal);
        Assert.Contains("<dd>成功</dd>", finalPage, StringComparison.Ordinal);
        Assert.DoesNotContain("Admin は無効のまま", finalPage, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_unsatisfied_access_profile_keeps_admin_disabled()
    {
        await using var harness = await SetupAssistantHarness.StartAsync();
        harness.Operations.AdminPreflight = new SetupAssistantAdminPreflightOutcome
        {
            Satisfied = false,
            ReasonCode = "access_endpoint_rejected",
            Profile = SetupAssistantAdminProfile.ProductionHttps,
        };

        await StartMainSetupAsync(harness, SetupMode.LocalMailpit);
        await harness.PostStepAsync("/admin-choice", ("action", "open"));
        await harness.PostStepAsync("/admin-preflight", ("action", "open"));
        await PostAdminPreflightAsync(harness, "production-https", "https://admin.example.test/");

        var page = await harness.ReadCurrentPageAsync();
        Assert.Contains("Admin は無効のまま維持します", page, StringComparison.Ordinal);
        Assert.DoesNotContain("Admin bootstrap へ進む", page, StringComparison.Ordinal);

        await harness.PostStepAsync("/admin-bootstrap", ("action", "open"));
        Assert.Null(harness.Operations.LastAdminBootstrapInput);
    }

    [Fact]
    public async Task Completing_the_assistant_stops_the_local_server()
    {
        await using var harness = await SetupAssistantHarness.StartAsync();
        await StartMainSetupAsync(harness, SetupMode.LocalMailpit);
        await harness.PostStepAsync("/finish", ("action", "skip"));

        using var response = await harness.PostStepAsync("/finish", ("action", "stop"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains(
            "ローカルサーバーは停止済み",
            await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken),
            StringComparison.Ordinal);
        Assert.Equal(SetupAssistantShutdownReason.Completed, harness.Sessions.ShutdownReason);
        Assert.True(harness.Sessions.ShutdownToken.IsCancellationRequested);
    }

    [Fact]
    public async Task Every_in_progress_screen_offers_a_cancel_action()
    {
        await using var harness = await SetupAssistantHarness.StartAsync();
        await harness.RedeemTokenAsync();

        var page = await harness.ReadCurrentPageAsync();

        Assert.Contains("Assistant を中止する", page, StringComparison.Ordinal);
        Assert.Contains("action=\"/cancel\"", page, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Every_screen_states_the_current_stage_and_transaction()
    {
        await using var harness = await SetupAssistantHarness.StartAsync();
        await harness.RedeemTokenAsync();

        var welcome = await harness.ReadCurrentPageAsync();
        Assert.Contains("Main setup transaction", welcome, StringComparison.Ordinal);
        Assert.Contains("ステップ 1 /", welcome, StringComparison.Ordinal);

        await StartMainSetupAsync(harness, SetupMode.LocalMailpit, alreadyRedeemed: true);
        await RunAdminBootstrapAsync(harness);

        var adminPage = await harness.ReadCurrentPageAsync();
        Assert.Contains("Admin bootstrap transaction", adminPage, StringComparison.Ordinal);
    }

    // ------------------------------------------------------------------- helpers

    private static async Task RedeemAndReachModeAsync(SetupAssistantHarness harness)
    {
        await harness.RedeemTokenAsync();
        await harness.PostStepAsync("/welcome");
        await harness.PostStepAsync("/preflight");
        await harness.PostStepAsync("/preflight", ("action", "continue"));
    }

    private static async Task StartMainSetupAsync(
        SetupAssistantHarness harness,
        SetupMode mode,
        bool alreadyRedeemed = false)
    {
        if (!alreadyRedeemed)
        {
            await harness.RedeemTokenAsync();
        }

        await harness.PostStepAsync("/welcome");
        await harness.PostStepAsync("/preflight");
        await harness.PostStepAsync("/preflight", ("action", "continue"));
        await harness.PostStepAsync("/mode", ("mode", SetupModeParser.ToWireValue(mode)));
        await PostTenantAsync(harness);
        await PostProviderAsync(harness);

        if (mode != SetupMode.LocalMailpit)
        {
            await PostAcsAsync(harness);
            await harness.PostStepAsync(
                "/confirm",
                ("environment_confirmation", SetupAssistantInputs.EnvironmentConfirmationFor(mode)),
                ("intent_confirmation", AcsRegisterOperation.IntentPhrase));
        }
        else
        {
            await harness.PostStepAsync("/confirm");
        }

        await harness.PostStepAsync("/verify", ("action", "continue"));

        switch (mode)
        {
            case SetupMode.StagingVerification:
                await harness.PostStepAsync(
                    "/verify",
                    ("action", "staging"),
                    ("recipient_email", "qa@example.test"),
                    ("environment_confirmation", AcsEnvironmentConfirmation.Staging),
                    ("intent_confirmation", AcsStagingVerificationOperation.IntentPhrase));
                await harness.PostStepAsync("/verify", ("action", "finish"));
                break;

            case SetupMode.ProductionAcs:
                await harness.PostStepAsync(
                    "/verify",
                    ("action", "production"),
                    ("environment_confirmation", AcsEnvironmentConfirmation.Production),
                    ("live_sending_approval", AcsLiveSendingApproval.EnablePhrase));
                break;

            default:
                await harness.PostStepAsync("/verify", ("action", "finish"));
                break;
        }
    }

    private static Task PostTenantAsync(SetupAssistantHarness harness) =>
        harness.PostStepAsync(
            "/tenant",
            ("tenant_name", "example-tenant"),
            ("source_service", "example-service"),
            ("sender_email", "no-reply@example.test"),
            ("sender_display_name", "Example Notifications"));

    private static Task PostProviderAsync(SetupAssistantHarness harness) =>
        harness.PostStepAsync(
            "/provider",
            ("service_token", ServiceToken),
            ("service_token_confirm", ServiceToken));

    private static Task PostAcsAsync(SetupAssistantHarness harness) =>
        harness.PostStepAsync(
            "/acs",
            ("acs_connection_string", AcsConnectionString),
            ("acs_connection_string_confirm", AcsConnectionString),
            ("platform_sender_display_name", "Example Platform"));

    private static async Task RunAdminBootstrapAsync(SetupAssistantHarness harness)
    {
        await harness.PostStepAsync("/admin-choice", ("action", "open"));
        await harness.PostStepAsync("/admin-preflight", ("action", "open"));
        await PostAdminPreflightAsync(harness, "local-development", "http://127.0.0.1:5280/");
        await harness.PostStepAsync("/admin-bootstrap", ("action", "open"));
        await harness.PostStepAsync(
            "/admin-bootstrap",
            ("admin_username", "setup-admin"),
            ("admin_password", "assistant-test-admin-password"),
            ("admin_password_confirm", "assistant-test-admin-password"));
    }

    private static Task PostAdminPreflightAsync(
        SetupAssistantHarness harness,
        string profile,
        string origin) =>
        harness.PostStepAsync(
            "/admin-preflight",
            ("profile", profile),
            ("origin", origin),
            ("environment_name", "Development"),
            ("allowed_local_address", "127.0.0.1"),
            ("loopback_only_published", "true"),
            ("server_local_address_confirmed", "true"));
}
