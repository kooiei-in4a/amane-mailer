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
    public async Task RunToCompletion_staging_mode_runs_docker_apply_then_staging_verification()
    {
        var tracker = new OrchestratorTrackingOperations();
        var tenantId = Guid.Parse("00000000-0000-0000-0000-000000000201");
        var state = SetupAssistantMainSetupOrchestrator.CreateInitial(SetupMode.StagingVerification);
        var input = new SetupAssistantMainCollectedInput
        {
            MainSetupInput = BuildStagingMainSetupInput(tenantId),
            TenantId = tenantId,
            StagingRecipientEmail = "qa-recipient@example.com",
            StagingEnvironmentConfirmation = AcsEnvironmentConfirmation.Staging,
            StagingIntentConfirmation = AcsStagingVerificationOperation.IntentPhrase,
            AssistantSessionId = "terminal-session-001",
        };

        var result = await SetupAssistantMainSetupOrchestrator.RunToCompletionAsync(
            tracker,
            state,
            input,
            CancellationToken.None);

        Assert.True(result.State.IsComplete);
        Assert.Equal(
            [
                OrchestratorTrackingOperations.OpDocker,
                OrchestratorTrackingOperations.OpApply,
                OrchestratorTrackingOperations.OpStaging,
            ],
            tracker.Sequence);
        Assert.NotNull(tracker.LastStagingInput);
        Assert.Equal(tenantId, tracker.LastStagingInput!.TenantId);
        Assert.NotNull(result.State.AppliedProof);
    }

    [Fact]
    public async Task RunToCompletion_production_mode_runs_docker_apply_then_live_sending()
    {
        var tracker = new OrchestratorTrackingOperations();
        var tenantId = Guid.Parse("00000000-0000-0000-0000-000000000301");
        var result = await SetupAssistantMainSetupOrchestrator.RunToCompletionAsync(
            tracker,
            SetupAssistantMainSetupOrchestrator.CreateInitial(SetupMode.ProductionAcs),
            new SetupAssistantMainCollectedInput
            {
                MainSetupInput = BuildProductionMainSetupInput(tenantId),
                TenantId = tenantId,
                ProductionEnvironmentConfirmation = AcsEnvironmentConfirmation.Production,
                LiveSendingEnableApproval = AcsLiveSendingApproval.EnablePhrase,
            },
            CancellationToken.None);

        Assert.True(result.State.IsComplete);
        Assert.True(result.State.DeploymentSendReady);
        Assert.Equal(
            [
                OrchestratorTrackingOperations.OpDocker,
                OrchestratorTrackingOperations.OpApply,
                OrchestratorTrackingOperations.OpLive,
            ],
            tracker.Sequence);
    }

    [Fact]
    public async Task Advance_skips_docker_only_after_acknowledged_passed_preflight()
    {
        var tracker = new OrchestratorTrackingOperations();
        var initial = SetupAssistantMainSetupOrchestrator.CreateInitial(SetupMode.LocalMailpit);
        var acknowledged = SetupAssistantMainSetupOrchestrator.AcknowledgeDockerPreflight(
            initial,
            new SetupAssistantDockerPreflightOutcome
            {
                Passed = true,
                Code = "docker.ok",
                EngineKind = "docker",
            });

        var result = await SetupAssistantMainSetupOrchestrator.AdvanceAsync(
            tracker,
            acknowledged,
            new SetupAssistantMainCollectedInput
            {
                MainSetupInput = BuildLocalMainSetupInput(),
            },
            CancellationToken.None);

        Assert.True(result.State.IsComplete);
        Assert.Equal([OrchestratorTrackingOperations.OpApply], tracker.Sequence);
    }

    [Fact]
    public async Task AcknowledgeDockerPreflight_ignores_failed_outcome()
    {
        var tracker = new OrchestratorTrackingOperations();
        var initial = SetupAssistantMainSetupOrchestrator.CreateInitial(SetupMode.LocalMailpit);
        var stillRequiresDocker = SetupAssistantMainSetupOrchestrator.AcknowledgeDockerPreflight(
            initial,
            new SetupAssistantDockerPreflightOutcome
            {
                Passed = false,
                Code = "docker.missing",
                EngineKind = "none",
            });

        Assert.False(stillRequiresDocker.SkipDockerPreflight);

        tracker.DockerFails = true;
        var result = await SetupAssistantMainSetupOrchestrator.AdvanceAsync(
            tracker,
            stillRequiresDocker,
            new SetupAssistantMainCollectedInput
            {
                MainSetupInput = BuildLocalMainSetupInput(),
            },
            CancellationToken.None);

        Assert.False(result.State.ConfigurationStageSucceeded);
        Assert.Equal([OrchestratorTrackingOperations.OpDocker], tracker.Sequence);
    }

    [Fact]
    public async Task Advance_rejects_non_service_issued_state()
    {
        var tracker = new OrchestratorTrackingOperations();
        var result = await SetupAssistantMainSetupOrchestrator.AdvanceAsync(
            tracker,
            new ForgedWorkflowState(),
            new SetupAssistantMainCollectedInput
            {
                MainSetupInput = BuildLocalMainSetupInput(),
            },
            CancellationToken.None);

        Assert.True(result.Rejected);
        Assert.Empty(tracker.Sequence);
    }

    [Fact]
    public async Task Advance_rejects_mode_mismatch_between_state_and_main_input()
    {
        var tracker = new OrchestratorTrackingOperations();
        var result = await SetupAssistantMainSetupOrchestrator.AdvanceAsync(
            tracker,
            SetupAssistantMainSetupOrchestrator.CreateInitial(SetupMode.LocalMailpit),
            new SetupAssistantMainCollectedInput
            {
                MainSetupInput = BuildStagingMainSetupInput(
                    Guid.Parse("00000000-0000-0000-0000-000000000501")),
            },
            CancellationToken.None);

        Assert.True(result.Rejected);
        Assert.Empty(tracker.Sequence);
    }

    [Fact]
    public async Task Advance_rejects_staging_without_service_issued_proof()
    {
        var tracker = new OrchestratorTrackingOperations();
        var result = await SetupAssistantMainSetupOrchestrator.AdvanceAsync(
            tracker,
            SetupAssistantMainSetupOrchestrator.CreateInitial(SetupMode.StagingVerification),
            new SetupAssistantMainCollectedInput
            {
                TenantId = Guid.NewGuid(),
                StagingRecipientEmail = "qa@example.com",
                StagingEnvironmentConfirmation = AcsEnvironmentConfirmation.Staging,
                StagingIntentConfirmation = AcsStagingVerificationOperation.IntentPhrase,
                AssistantSessionId = "x",
            },
            CancellationToken.None);

        Assert.True(result.Rejected);
        Assert.Empty(tracker.Sequence);
    }

    [Fact]
    public async Task PrepareStagingRetry_preserves_applied_proof()
    {
        var tracker = new OrchestratorTrackingOperations
        {
            StagingFailsOnce = true,
        };
        var tenantId = Guid.Parse("00000000-0000-0000-0000-000000000601");
        var afterApply = await SetupAssistantMainSetupOrchestrator.AdvanceAsync(
            tracker,
            SetupAssistantMainSetupOrchestrator.AcknowledgeDockerPreflight(
                SetupAssistantMainSetupOrchestrator.CreateInitial(SetupMode.StagingVerification),
                new SetupAssistantDockerPreflightOutcome
                {
                    Passed = true,
                    Code = "docker.ok",
                    EngineKind = "docker",
                }),
            new SetupAssistantMainCollectedInput
            {
                MainSetupInput = BuildStagingMainSetupInput(tenantId),
                TenantId = tenantId,
            },
            CancellationToken.None);

        var afterFailedStaging = await SetupAssistantMainSetupOrchestrator.AdvanceAsync(
            tracker,
            afterApply.State,
            new SetupAssistantMainCollectedInput
            {
                TenantId = tenantId,
                StagingRecipientEmail = "qa-recipient@example.com",
                StagingEnvironmentConfirmation = AcsEnvironmentConfirmation.Staging,
                StagingIntentConfirmation = AcsStagingVerificationOperation.IntentPhrase,
                AssistantSessionId = "web-session",
            },
            CancellationToken.None);

        Assert.True(afterFailedStaging.State.CanRetryStaging);
        var retried = SetupAssistantMainSetupOrchestrator.PrepareStagingRetry(afterFailedStaging.State);
        Assert.Null(retried.Staging);
        Assert.Same(afterApply.State.AppliedProof, retried.AppliedProof);
        Assert.Equal(SetupAssistantMainWorkflowStage.AwaitingStagingVerification, retried.Stage);
    }

    [Fact]
    public async Task Non_interactive_run_to_completion_is_accepted()
    {
        var tracker = new OrchestratorTrackingOperations();
        var parsed = SetupNonInteractiveTestSupport.BuildLocalMailpitInput();
        var result = await SetupAssistantMainSetupOrchestrator.RunToCompletionAsync(
            tracker,
            SetupNonInteractiveOrchestratorAdapter.BuildInitialState(parsed),
            SetupNonInteractiveOrchestratorAdapter.BuildCollectedInput(parsed),
            CancellationToken.None);

        Assert.True(result.State.IsComplete);
        Assert.Equal(
            [
                OrchestratorTrackingOperations.OpDocker,
                OrchestratorTrackingOperations.OpApply,
            ],
            tracker.Sequence);
    }

    [Fact]
    public async Task Web_style_advance_apply_then_staging_preserves_service_proof()
    {
        var tracker = new OrchestratorTrackingOperations();
        var tenantId = Guid.Parse("00000000-0000-0000-0000-000000000401");
        var afterApply = await SetupAssistantMainSetupOrchestrator.AdvanceAsync(
            tracker,
            SetupAssistantMainSetupOrchestrator.AcknowledgeDockerPreflight(
                SetupAssistantMainSetupOrchestrator.CreateInitial(SetupMode.StagingVerification),
                new SetupAssistantDockerPreflightOutcome
                {
                    Passed = true,
                    Code = "docker.ok",
                    EngineKind = "docker",
                }),
            new SetupAssistantMainCollectedInput
            {
                MainSetupInput = BuildStagingMainSetupInput(tenantId),
                TenantId = tenantId,
            },
            CancellationToken.None);

        Assert.True(afterApply.State.ConfigurationStageSucceeded);
        Assert.False(afterApply.State.IsComplete);
        Assert.Equal(SetupAssistantMainWorkflowStage.AwaitingStagingVerification, afterApply.State.Stage);

        var afterStaging = await SetupAssistantMainSetupOrchestrator.AdvanceAsync(
            tracker,
            afterApply.State,
            new SetupAssistantMainCollectedInput
            {
                TenantId = tenantId,
                StagingRecipientEmail = "qa-recipient@example.com",
                StagingEnvironmentConfirmation = AcsEnvironmentConfirmation.Staging,
                StagingIntentConfirmation = AcsStagingVerificationOperation.IntentPhrase,
                AssistantSessionId = "web-session",
            },
            CancellationToken.None);

        Assert.True(afterStaging.State.IsComplete);
        Assert.Same(afterApply.State.AppliedProof, tracker.LastStagingInput!.AppliedProof);
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

    private sealed class ForgedWorkflowState : ISetupAssistantMainWorkflowState
    {
        public SetupMode Mode => SetupMode.LocalMailpit;
        public SetupAssistantMainWorkflowStage Stage => SetupAssistantMainWorkflowStage.AwaitingApply;
        public bool SkipDockerPreflight => true;
        public SetupAssistantMainSetupOutcome? MainSetup => null;
        public SetupAssistantStagingOutcome? Staging => null;
        public SetupAssistantMainSetupOutcome? LiveSending => null;
        public object? AppliedProof => new object();
        public bool ConfigurationStageSucceeded => false;
        public bool DeploymentSendReady => false;
        public bool IsComplete => false;
        public bool CanRetryApply => false;
        public bool CanRetryStaging => false;
        public bool CanRunLiveSending => false;
        public bool CanFinish => false;
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
        internal bool DockerFails { get; set; }
        internal bool StagingFailsOnce { get; set; }
        private bool _stagingFailed;

        public Task<SetupAssistantDockerPreflightOutcome> CheckDockerAsync(CancellationToken cancellationToken)
        {
            Sequence.Add(OpDocker);
            return Task.FromResult(new SetupAssistantDockerPreflightOutcome
            {
                Passed = !DockerFails,
                Code = DockerFails ? "docker.missing" : "docker.ok",
                EngineKind = "docker",
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
                BundleId = "bundle-test",
                AppliedProof = new object(),
                ActionCode = input.Mode is SetupMode.StagingVerification or SetupMode.ProductionAcs
                    ? SetupApplyActionCode.CompleteSendReadyEvaluation
                    : null,
            });
        }

        public Task<SetupAssistantStagingOutcome> VerifyStagingAsync(
            SetupAssistantStagingInput input,
            CancellationToken cancellationToken)
        {
            Sequence.Add(OpStaging);
            LastStagingInput = input;
            if (StagingFailsOnce && !_stagingFailed)
            {
                _stagingFailed = true;
                return Task.FromResult(new SetupAssistantStagingOutcome
                {
                    Code = AcsSetupResultCode.StagingVerificationFailed,
                    Kind = SetupAssistantOutcomeKind.Failed,
                    SendRequestAccepted = false,
                });
            }

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
                AppliedProof = input.AppliedProof,
            });
        }

        public Task<SetupAssistantAdminPreflightOutcome> CheckAdminAccessProfileAsync(
            SetupAssistantAdminAccessInput input,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<SetupAssistantAdminBootstrapOutcome> BootstrapAdminAsync(
            SetupAssistantAdminBootstrapInput input,
            CancellationToken cancellationToken) => throw new NotSupportedException();
    }
}
