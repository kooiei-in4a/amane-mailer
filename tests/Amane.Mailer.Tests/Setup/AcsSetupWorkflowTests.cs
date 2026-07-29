using Amane.Mailer.Operations;
using Amane.Mailer.Operations.AcsSetup;
using Amane.Mailer.Operations.AcsTestSend;
using Amane.Mailer.Setup;
using Amane.Mailer.Tests.TestSupport;

namespace Amane.Mailer.Tests.Setup;

public sealed class AcsSetupWorkflowTests
{
    private const string ValidConnectionString =
        "endpoint=https://synthetic.example.communication.azure.com/;accesskey=SYNTHETICACCESSKEY000000000000000000000000000000=";

    [Fact]
    public void AcsRegisterOperation_runs_without_console()
    {
        using var scratch = new RegisterScratch();
        var result = new AcsRegisterOperation().Execute(new AcsRegisterRequest
        {
            EnvironmentConfirmation = AcsEnvironmentConfirmation.Staging,
            IntentConfirmation = AcsRegisterOperation.IntentPhrase,
            ConnectionString = ValidConnectionString,
            ConnectionStringConfirmation = ValidConnectionString,
            SenderEmail = "sender@example.com",
            SenderDisplayName = "Sender",
            AcsSecretDirectory = scratch.AcsDir,
            PlatformSenderDirectory = scratch.SenderDir,
        });

        Assert.True(result.IsSuccess);
        Assert.Equal("staging", result.InternalEnvironment);
        Assert.DoesNotContain("sender@example.com", result.MaskedSenderEmail!, StringComparison.Ordinal);
        Assert.Equal(ValidConnectionString, File.ReadAllText(scratch.AcsFilePath));
    }

    [Theory]
    [InlineData("Staging", true)]
    [InlineData("Production", true)]
    [InlineData("staging", false)]
    [InlineData("PRODUCTION", false)]
    [InlineData(" Staging", false)]
    [InlineData("Staging ", false)]
    public void AcsEnvironmentConfirmation_is_exact_ordinal(string value, bool expected)
    {
        Assert.Equal(expected, AcsEnvironmentConfirmation.TryMap(value, out _));
    }

    [Fact]
    public async Task Staging_verification_rejects_Production_confirmation()
    {
        var op = new AcsStagingVerificationOperation(new FakeAcsClient());
        var result = await op.ExecuteAsync(
            BaseStagingRequest() with { EnvironmentConfirmation = AcsEnvironmentConfirmation.Production },
            CancellationToken.None);

        Assert.Equal(AcsStagingVerificationOperation.RejectedProductionEnvironment, result.Code);
    }

    [Fact]
    public async Task Staging_verification_rejects_case_and_whitespace_variants()
    {
        var op = new AcsStagingVerificationOperation(new FakeAcsClient());
        foreach (var bad in new[] { "staging", "STAGING", " Staging", "Staging " })
        {
            var result = await op.ExecuteAsync(
                BaseStagingRequest() with { EnvironmentConfirmation = bad },
                CancellationToken.None);
            Assert.Equal(AdminProviderTestAcsSendResultCodes.RejectedEnvironmentMismatch, result.Code);
        }
    }

    [Fact]
    public async Task Staging_verification_requires_tenant_sender_match()
    {
        var op = new AcsStagingVerificationOperation(new FakeAcsClient());
        var result = await op.ExecuteAsync(
            BaseStagingRequest() with
            {
                SenderEmail = "other@example.com",
                ExpectedTenantSenderEmail = "noreply@example.com",
            },
            CancellationToken.None);

        Assert.Equal(AcsStagingVerificationOperation.RejectedSenderMismatch, result.Code);
    }

    [Fact]
    public async Task Staging_verification_succeeds_with_matching_sender_and_separates_mailbox_ACTION()
    {
        var op = new AcsStagingVerificationOperation(new FakeAcsClient());
        var result = await op.ExecuteAsync(BaseStagingRequest(), CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.True(result.SendRequestAccepted);
        Assert.True(result.OperationCompleted);
        Assert.Equal(AcsStagingVerificationResult.MailboxCheckActionRequired, result.MailboxCheckStatus);
        Assert.DoesNotContain("noreply@example.com", result.MaskedSenderEmail!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Staging_verification_enforces_session_limit()
    {
        var limiter = new AcsSessionTestSendLimiter(maxAttemptsPerSession: 2);
        var op = new AcsStagingVerificationOperation(new FakeAcsClient(), limiter);
        var request = BaseStagingRequest() with { AssistantSessionId = "session-a" };

        Assert.True((await op.ExecuteAsync(request, CancellationToken.None)).IsSuccess);
        Assert.True((await op.ExecuteAsync(request, CancellationToken.None)).IsSuccess);
        var limited = await op.ExecuteAsync(request, CancellationToken.None);
        Assert.Equal(AcsStagingVerificationOperation.RejectedSessionLimitExceeded, limited.Code);
    }

    [Fact]
    public async Task Staging_verification_does_not_limit_cli_without_session_id()
    {
        var limiter = new AcsSessionTestSendLimiter(maxAttemptsPerSession: 1);
        var op = new AcsStagingVerificationOperation(new FakeAcsClient(), limiter);
        var request = BaseStagingRequest() with { AssistantSessionId = null };

        Assert.True((await op.ExecuteAsync(request, CancellationToken.None)).IsSuccess);
        Assert.True((await op.ExecuteAsync(request, CancellationToken.None)).IsSuccess);
    }

    [Fact]
    public async Task Staging_verification_maps_provider_failures_without_leaking_raw_error()
    {
        var op = new AcsStagingVerificationOperation(new FakeAcsClient(
            AcsTestSendOutcome.Failed(AdminProviderTestAcsSendResultCodes.FailedAcsAuthentication)));
        var result = await op.ExecuteAsync(BaseStagingRequest(), CancellationToken.None);

        Assert.Equal(AdminProviderTestAcsSendResultCodes.FailedAcsAuthentication, result.Code);
        Assert.Null(result.ProviderMessageIdForHandoff);
    }

    [Fact]
    public void SetupCore_rejects_live_sending_true_without_promotion_authorization()
    {
        var root = SetupTestFixtures.CreateManagedRoot();
        try
        {
            var request = SetupTestFixtures.ProductionAcsRequest(root, dryRun: true, liveSending: true) with
            {
                LiveSendingPromotion = null,
            };
            var result = new SetupCore().GenerateBundle(request);
            Assert.False(result.IsSuccess);
            Assert.Contains("live_sending", result.Message!, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void SetupCore_accepts_live_sending_true_with_promotion_authorization_as_separate_fingerprint()
    {
        var root = SetupTestFixtures.CreateManagedRoot();
        try
        {
            var disabled = SetupTestFixtures.ProductionAcsRequest(root, dryRun: true, liveSending: false);
            var enabled = SetupTestFixtures.ProductionAcsRequest(root, dryRun: true, liveSending: true);
            var core = new SetupCore(bundleIdFactory: () => "bundle-dry-run");
            var first = core.GenerateBundle(disabled);
            var second = core.GenerateBundle(enabled);

            Assert.True(first.IsSuccess);
            Assert.True(second.IsSuccess);
            Assert.NotEqual(first.ConfigurationFingerprint, second.ConfigurationFingerprint);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task EnableLiveSending_rejects_Staging_confirmation_and_missing_approval()
    {
        var workflow = new AcsSetupWorkflow();
        var root = SetupTestFixtures.CreateManagedRoot();
        try
        {
            var request = SetupTestFixtures.ProductionAcsRequest(root, dryRun: true);
            TrustedSetupHostLayout layout = null!;
            var apply = new FakeApplyEngine(SetupApplyResult.Create(
                SetupApplyResultCode.ApplySucceeded,
                SetupManagedDeploymentState.Active));

            var stagingConfirm = await workflow.EnableLiveSendingAsync(
                request,
                AcsEnvironmentConfirmation.Staging,
                AcsLiveSendingApproval.EnablePhrase,
                layout,
                apply,
                CancellationToken.None);
            Assert.Equal(AcsSetupResultCode.ProductionConfirmationRejected, stagingConfirm.Code);

            var missingApproval = await workflow.EnableLiveSendingAsync(
                request,
                AcsEnvironmentConfirmation.Production,
                "WRONG",
                layout,
                apply,
                CancellationToken.None);
            Assert.Equal(AcsSetupResultCode.RejectedLiveSendingWithoutConfirmation, missingApproval.Code);
            Assert.Null(apply.LastBundleId);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void SendReady_evaluator_separates_operational_verification()
    {
        var apply = SetupApplyResult.Create(
            SetupApplyResultCode.ApplySucceeded,
            SetupManagedDeploymentState.Active,
            configurationApplied: true,
            verificationCommitted: true);

        var ready = AcsSendReadyEvaluator.Evaluate(SetupMode.ProductionAcs, apply, effectiveLiveSendingEnabled: true);
        Assert.True(ready.SendReadyAsserted);
        Assert.Equal(AcsSendReadyEvaluator.SendReadyReady, ready.SendReadyEvaluation);

        var disabled = AcsSendReadyEvaluator.Evaluate(SetupMode.ProductionAcs, apply, effectiveLiveSendingEnabled: false);
        Assert.False(disabled.SendReadyAsserted);

        var result = new AcsSetupWorkflowResult
        {
            Code = AcsSetupResultCode.DeploymentSendReady,
            State = AcsSetupWorkflowState.DeploymentSendReady,
            DeploymentSendReady = true,
            ConfigurationApplied = true,
        };
        Assert.False(result.OperationalVerificationRecorded);
    }

    [Fact]
    public async Task Workflow_maps_apply_failure_and_external_side_effect()
    {
        var root = SetupTestFixtures.CreateManagedRoot();
        try
        {
            var request = SetupTestFixtures.ProductionAcsRequest(root);
            var workflow = new AcsSetupWorkflow();
            var applyEngine = new FakeApplyEngine(SetupApplyResult.Create(
                SetupApplyResultCode.ApplyFailedRollbackSucceeded,
                SetupManagedDeploymentState.Active,
                configRollbackStatus: SetupConfigRollbackStatus.Succeeded,
                persistentSideEffectMayRemain: true,
                persistentSideEffectKind: SetupPersistentSideEffectKind.None));

            var result = await workflow.EnableLiveSendingAsync(
                request,
                AcsEnvironmentConfirmation.Production,
                AcsLiveSendingApproval.EnablePhrase,
                null!,
                applyEngine,
                CancellationToken.None);

            Assert.Equal(AcsSetupResultCode.ExternalSideEffectMayRemain, result.Code);
            Assert.True(result.PersistentSideEffectMayRemain);
            Assert.False(result.DeploymentSendReady);
            Assert.False(result.OperationalVerificationRecorded);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Workflow_configuration_apply_success_is_not_send_ready()
    {
        var root = SetupTestFixtures.CreateManagedRoot();
        try
        {
            var request = SetupTestFixtures.StagingAcsRequest(root);
            var workflow = new AcsSetupWorkflow();
            var applyEngine = new FakeApplyEngine(SetupApplyResult.Create(
                SetupApplyResultCode.ApplySucceeded,
                SetupManagedDeploymentState.Active,
                configurationApplied: true,
                verificationCommitted: true,
                bundleId: "applied-bundle"));

            var result = await workflow.ApplyConfigurationAsync(
                request,
                null!,
                applyEngine,
                CancellationToken.None);

            Assert.Equal(AcsSetupResultCode.ConfigurationApplied, result.Code);
            Assert.True(result.ConfigurationApplied);
            Assert.False(result.DeploymentSendReady);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Workflow_live_sending_success_asserts_send_ready_only()
    {
        var root = SetupTestFixtures.CreateManagedRoot();
        try
        {
            var request = SetupTestFixtures.ProductionAcsRequest(root);
            var workflow = new AcsSetupWorkflow();
            var applyEngine = new FakeApplyEngine(SetupApplyResult.Create(
                SetupApplyResultCode.ApplySucceeded,
                SetupManagedDeploymentState.Active,
                configurationApplied: true,
                verificationCommitted: true,
                bundleId: "live-bundle"));

            var result = await workflow.EnableLiveSendingAsync(
                request,
                AcsEnvironmentConfirmation.Production,
                AcsLiveSendingApproval.EnablePhrase,
                null!,
                applyEngine,
                CancellationToken.None);

            Assert.Equal(AcsSetupResultCode.DeploymentSendReady, result.Code);
            Assert.True(result.DeploymentSendReady);
            Assert.False(result.OperationalVerificationRecorded);
            Assert.NotNull(applyEngine.LastBundleId);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Live_sending_promotion_creates_distinct_bundle_ids_and_fingerprints()
    {
        var root = SetupTestFixtures.CreateManagedRoot();
        try
        {
            var ids = new Queue<string>(["bundle-a", "bundle-b"]);
            var core = new SetupCore(bundleIdFactory: () => ids.Dequeue());
            var first = core.GenerateBundle(SetupTestFixtures.ProductionAcsRequest(root));
            var second = core.GenerateBundle(SetupTestFixtures.ProductionAcsRequest(root, liveSending: true));

            Assert.True(first.IsSuccess);
            Assert.True(second.IsSuccess);
            Assert.Equal("bundle-a", first.BundleId);
            Assert.Equal("bundle-b", second.BundleId);
            Assert.NotEqual(first.ConfigurationFingerprint, second.ConfigurationFingerprint);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static AcsStagingVerificationRequest BaseStagingRequest() => new()
    {
        EnvironmentConfirmation = AcsEnvironmentConfirmation.Staging,
        IntentConfirmation = AcsStagingVerificationOperation.IntentPhrase,
        ConnectionString = ValidConnectionString,
        SenderEmail = "noreply@example.com",
        RecipientEmail = "recipient@example.com",
        ExpectedTenantSenderEmail = "noreply@example.com",
    };

    private sealed class FakeAcsClient(AcsTestSendOutcome? outcome = null) : IAcsTestSendClient
    {
        public Task<AcsTestSendOutcome> SendAsync(AcsTestSendRequest request, CancellationToken cancellationToken)
        {
            Assert.Equal(AcsStagingVerificationOperation.SyntheticSubject, request.Subject);
            Assert.Equal(AcsStagingVerificationOperation.SyntheticPlainTextBody, request.PlainTextBody);
            return Task.FromResult(outcome ?? AcsTestSendOutcome.Succeeded(request.OperationId.ToString("D")));
        }
    }

    private sealed class FakeApplyEngine(SetupApplyResult result) : ISetupApplyEngine
    {
        public string? LastBundleId { get; private set; }

        public Task<SetupApplyResult> ApplyAsync(
            TrustedSetupHostLayout layout,
            string candidateBundleId,
            CancellationToken cancellationToken)
        {
            LastBundleId = candidateBundleId;
            return Task.FromResult(SetupApplyResult.Create(
                result.Code,
                result.DeploymentState,
                result.Message,
                result.ActionCode,
                result.ReasonCode,
                candidateBundleId,
                result.ActivationGeneration,
                result.ConfigurationApplied,
                result.VerificationCommitted,
                result.ConfigRollbackStatus,
                result.PersistentSideEffectMayRemain,
                result.PersistentSideEffectKind));
        }

        public Task<SetupApplyResult> RecoverAsync(
            TrustedSetupHostLayout layout,
            CancellationToken cancellationToken) =>
            Task.FromResult(result);
    }

    private sealed class RegisterScratch : IDisposable
    {
        public string Root { get; } = Path.Combine(
            Path.GetTempPath(), "amane-acs-register-" + Guid.NewGuid().ToString("N"));

        public string AcsDir { get; }
        public string SenderDir { get; }
        public string AcsFilePath { get; }

        public RegisterScratch()
        {
            AcsDir = Path.Combine(Root, "secrets", "acs");
            SenderDir = Path.Combine(Root, "config", "platform-sender");
            TestSecretDirectory.CreateSecure(AcsDir);
            TestSecretDirectory.CreateSecure(SenderDir);
            AcsFilePath = Path.Combine(AcsDir, AcsSecretFileNames.CanonicalFileName);
        }

        public void Dispose()
        {
            if (Directory.Exists(Root))
            {
                Directory.Delete(Root, recursive: true);
            }
        }
    }
}
