using Amane.Mailer.Configuration;
using Amane.Mailer.Operations.AcsSetup;
using Amane.Mailer.Setup;
using Amane.Mailer.Setup.Assistant;
using Amane.Mailer.Setup.NonInteractive;
using Amane.Mailer.Tests.Setup;
using Amane.Mailer.Tests.Setup.NonInteractive;

namespace Amane.Mailer.Tests.Setup.Assistant;

public sealed class SetupAssistantMainSetupOrchestratorTests
{
    [Fact]
    public async Task Full_staging_mode_runs_docker_apply_then_staging_verification()
    {
        var tracker = new OrchestratorTrackingOperations();
        var tenantId = Guid.Parse("00000000-0000-0000-0000-000000000201");
        var input = BuildStagingMainSetupInput(tenantId);
        var request = new SetupAssistantMainSetupRunRequest
        {
            Mode = SetupMode.StagingVerification,
            Phase = SetupAssistantMainSetupRunPhase.Full,
            MainSetupInput = input,
            TenantId = tenantId,
            StagingRecipientEmail = "qa-recipient@example.com",
            StagingEnvironmentConfirmation = AcsEnvironmentConfirmation.Staging,
            StagingIntentConfirmation = AcsStagingVerificationOperation.IntentPhrase,
            AssistantSessionId = "terminal-session-001",
        };

        var result = await SetupAssistantMainSetupOrchestrator.RunAsync(
            tracker,
            request,
            CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.Equal(
            [
                OrchestratorTrackingOperations.OpDocker,
                OrchestratorTrackingOperations.OpApply,
                OrchestratorTrackingOperations.OpStaging,
            ],
            tracker.Sequence);
        Assert.NotNull(tracker.LastStagingInput);
        Assert.Equal(tenantId, tracker.LastStagingInput!.TenantId);
    }

    [Fact]
    public async Task Full_production_mode_runs_docker_apply_then_live_sending()
    {
        var tracker = new OrchestratorTrackingOperations();
        var tenantId = Guid.Parse("00000000-0000-0000-0000-000000000301");
        var input = BuildProductionMainSetupInput(tenantId);
        var request = new SetupAssistantMainSetupRunRequest
        {
            Mode = SetupMode.ProductionAcs,
            Phase = SetupAssistantMainSetupRunPhase.Full,
            MainSetupInput = input,
            TenantId = tenantId,
            ProductionEnvironmentConfirmation = AcsEnvironmentConfirmation.Production,
            LiveSendingEnableApproval = AcsLiveSendingApproval.EnablePhrase,
        };

        var result = await SetupAssistantMainSetupOrchestrator.RunAsync(
            tracker,
            request,
            CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.True(result.DeploymentSendReady);
        Assert.Equal(
            [
                OrchestratorTrackingOperations.OpDocker,
                OrchestratorTrackingOperations.OpApply,
                OrchestratorTrackingOperations.OpLive,
            ],
            tracker.Sequence);
    }

    [Fact]
    public async Task Skip_docker_preflight_omits_docker_check()
    {
        var tracker = new OrchestratorTrackingOperations();
        var request = new SetupAssistantMainSetupRunRequest
        {
            Mode = SetupMode.LocalMailpit,
            Phase = SetupAssistantMainSetupRunPhase.Apply,
            SkipDockerPreflight = true,
            MainSetupInput = BuildLocalMainSetupInput(),
        };

        var result = await SetupAssistantMainSetupOrchestrator.RunAsync(
            tracker,
            request,
            CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.Equal([OrchestratorTrackingOperations.OpApply], tracker.Sequence);
    }

    [Fact]
    public async Task Non_interactive_adapter_request_is_accepted()
    {
        var tracker = new OrchestratorTrackingOperations();
        var parsed = SetupNonInteractiveTestSupport.BuildLocalMailpitInput();
        var request = SetupNonInteractiveOrchestratorAdapter.BuildRunRequest(parsed);

        var result = await SetupAssistantMainSetupOrchestrator.RunAsync(
            tracker,
            request,
            CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.Equal(
            [
                OrchestratorTrackingOperations.OpDocker,
                OrchestratorTrackingOperations.OpApply,
            ],
            tracker.Sequence);
        Assert.NotNull(tracker.LastMainSetupInput);
        Assert.Equal(SetupMode.LocalMailpit, tracker.LastMainSetupInput!.Mode);
    }

    [Fact]
    public async Task Terminal_style_main_setup_input_is_accepted()
    {
        var tracker = new OrchestratorTrackingOperations();
        var request = new SetupAssistantMainSetupRunRequest
        {
            Mode = SetupMode.LocalMailpit,
            Phase = SetupAssistantMainSetupRunPhase.Apply,
            SkipDockerPreflight = true,
            MainSetupInput = BuildLocalMainSetupInput(),
        };

        var result = await SetupAssistantMainSetupOrchestrator.RunAsync(
            tracker,
            request,
            CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.NotNull(tracker.LastMainSetupInput);
        Assert.Contains(
            SetupNonInteractiveTestSupport.SyntheticServiceToken,
            tracker.LastMainSetupInput!.TokenSecrets.Values);
    }

    private static SetupAssistantMainSetupInput BuildLocalMainSetupInput() =>
        new()
        {
            Mode = SetupMode.LocalMailpit,
            Tenants = SetupTestFixtures.LocalMailpitTenants(),
            TokenSecrets = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [SetupAssistantInputs.TokenEnvFor(SetupMode.LocalMailpit)] =
                    SetupNonInteractiveTestSupport.SyntheticServiceToken,
            },
            EnvironmentConfirmation = string.Empty,
            IntentConfirmation = string.Empty,
        };

    private static SetupAssistantMainSetupInput BuildStagingMainSetupInput(Guid tenantId)
    {
        var tenants = SetupTestFixtures.AcsStagingTenants();
        return new SetupAssistantMainSetupInput
        {
            Mode = SetupMode.StagingVerification,
            Tenants = tenants,
            TokenSecrets = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["MAIL_SERVICE_TOKEN_STAGING"] = "synthetic-staging-token-not-real",
            },
            AcsConnectionString = SetupNonInteractiveTestSupport.SyntheticAcsConnectionString,
            AcsConnectionStringConfirmation = SetupNonInteractiveTestSupport.SyntheticAcsConnectionString,
            PlatformSender = new SetupPlatformSenderInput
            {
                Environment = "staging",
                Email = "noreply@example.com",
                DisplayName = "Example Service",
            },
            EnvironmentConfirmation = AcsEnvironmentConfirmation.Staging,
            IntentConfirmation = AcsRegisterOperation.IntentPhrase,
        };
    }

    private static SetupAssistantMainSetupInput BuildProductionMainSetupInput(Guid tenantId)
    {
        var tenants = SetupTestFixtures.AcsProductionTenants(liveSending: false);
        return new SetupAssistantMainSetupInput
        {
            Mode = SetupMode.ProductionAcs,
            Tenants = tenants,
            TokenSecrets = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["MAIL_SERVICE_TOKEN_PRODUCTION"] = "synthetic-production-token-not-real",
            },
            AcsConnectionString = SetupNonInteractiveTestSupport.SyntheticAcsConnectionString,
            AcsConnectionStringConfirmation = SetupNonInteractiveTestSupport.SyntheticAcsConnectionString,
            PlatformSender = new SetupPlatformSenderInput
            {
                Environment = "production",
                Email = "noreply@example.com",
                DisplayName = "Example Service",
            },
            EnvironmentConfirmation = AcsEnvironmentConfirmation.Production,
            IntentConfirmation = AcsRegisterOperation.IntentPhrase,
        };
    }

    private sealed class OrchestratorTrackingOperations : ISetupAssistantOperations
    {
        internal const string OpDocker = "docker";
        internal const string OpApply = "apply";
        internal const string OpStaging = "staging";
        internal const string OpLive = "live";

        internal List<string> Sequence { get; } = [];

        internal SetupAssistantMainSetupInput? LastMainSetupInput { get; private set; }

        internal SetupAssistantStagingInput? LastStagingInput { get; private set; }

        public Task<SetupAssistantDockerPreflightOutcome> CheckDockerAsync(CancellationToken cancellationToken)
        {
            Sequence.Add(OpDocker);
            return Task.FromResult(new SetupAssistantDockerPreflightOutcome
            {
                Passed = true,
                Code = SetupDockerResultCode.Succeeded,
                EngineKind = "LocalUnixSocket",
            });
        }

        public Task<SetupAssistantMainSetupOutcome> ApplyMainSetupAsync(
            SetupAssistantMainSetupInput input,
            CancellationToken cancellationToken)
        {
            Sequence.Add(OpApply);
            LastMainSetupInput = input;
            return Task.FromResult(new SetupAssistantMainSetupOutcome
            {
                Code = SetupApplyResultCode.ApplySucceeded,
                Kind = SetupAssistantOutcomeKind.Succeeded,
                ConfigurationApplied = true,
                AppliedProof = FakeSetupAssistantOperations.Proof,
            });
        }

        public Task<SetupAssistantStagingOutcome> VerifyStagingAsync(
            SetupAssistantStagingInput input,
            CancellationToken cancellationToken)
        {
            Sequence.Add(OpStaging);
            LastStagingInput = input;
            return Task.FromResult(new SetupAssistantStagingOutcome
            {
                Code = AcsSetupResultCode.StagingVerificationSucceeded,
                Kind = SetupAssistantOutcomeKind.Succeeded,
                SendRequestAccepted = true,
            });
        }

        public Task<SetupAssistantMainSetupOutcome> EnableLiveSendingAsync(
            SetupAssistantProductionInput input,
            CancellationToken cancellationToken)
        {
            Sequence.Add(OpLive);
            return Task.FromResult(new SetupAssistantMainSetupOutcome
            {
                Code = AcsSetupResultCode.DeploymentSendReady,
                Kind = SetupAssistantOutcomeKind.Succeeded,
                ConfigurationApplied = true,
                DeploymentSendReady = true,
                AppliedProof = FakeSetupAssistantOperations.Proof,
            });
        }

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
