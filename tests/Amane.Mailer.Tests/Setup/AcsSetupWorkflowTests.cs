using Amane.Mailer.Operations;
using Amane.Mailer.Operations.AcsSetup;
using Amane.Mailer.Operations.AcsTestSend;
using Amane.Mailer.Setup;

namespace Amane.Mailer.Tests.Setup;

public sealed class AcsSetupWorkflowTests
{
    [Theory]
    [InlineData("Staging", true)]
    [InlineData("Production", true)]
    [InlineData("staging", false)]
    [InlineData("PRODUCTION", false)]
    [InlineData(" Staging", false)]
    [InlineData("Production ", false)]
    public void Environment_confirmation_is_exact(string value, bool expected)
    {
        Assert.Equal(expected, AcsEnvironmentConfirmation.TryMap(value, out _));
    }

    [Fact]
    public void Public_SetupCore_rejects_live_sending_true()
    {
        var root = SetupTestFixtures.CreateManagedRoot();
        try
        {
            var result = new SetupCore().GenerateBundle(
                SetupTestFixtures.ProductionAcsRequest(root, dryRun: true, liveSending: true));

            Assert.False(result.IsSuccess);
            Assert.Contains("live_sending", result.Message!, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Applied_proof_has_no_public_constructor()
    {
        Assert.Empty(typeof(AcsConfigurationAppliedProof).GetConstructors());
    }

    [Fact]
    public async Task Apply_requires_shared_typed_registration_validation()
    {
        var root = SetupTestFixtures.CreateManagedRoot();
        try
        {
            var workflow = new AcsSetupWorkflow();
            var request = SetupTestFixtures.ProductionAcsRequest(root, dryRun: true);
            var apply = new FakeApplyEngine(SuccessResult());

            var rejected = await workflow.ApplyConfigurationAsync(
                request,
                "production",
                AcsRegisterOperation.IntentPhrase,
                request.AcsConnectionString!,
                null!,
                apply,
                CancellationToken.None);
            Assert.Equal(
                AdminProviderRegisterAcsResultCodes.RejectedEnvironmentMismatch,
                rejected.Code);

            var accepted = await workflow.ApplyConfigurationAsync(
                request,
                AcsEnvironmentConfirmation.Production,
                AcsRegisterOperation.IntentPhrase,
                request.AcsConnectionString!,
                null!,
                apply,
                CancellationToken.None);
            Assert.Equal(AcsSetupResultCode.ConfigurationApplied, accepted.Code);
            Assert.NotNull(accepted.ConfigurationAppliedProof);
            Assert.False(accepted.DeploymentSendReady);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Production_promotion_uses_prior_authority_under_apply_lock()
    {
        var root = SetupTestFixtures.CreateManagedRoot();
        try
        {
            var ids = new Queue<string>(["bundle-disabled", "bundle-enabled"]);
            var workflow = new AcsSetupWorkflow(
                new SetupCore(bundleIdFactory: ids.Dequeue));
            var engine = new FakeApplyEngine(SuccessResult());
            var request = SetupTestFixtures.ProductionAcsRequest(root, dryRun: true);

            var first = await ApplyAsync(workflow, request, engine);
            var promoted = await workflow.EnableLiveSendingAsync(
                first.ConfigurationAppliedProof!,
                AcsEnvironmentConfirmation.Production,
                AcsLiveSendingApproval.EnablePhrase,
                null!,
                engine,
                CancellationToken.None);

            Assert.Equal(AcsSetupResultCode.DeploymentSendReady, promoted.Code);
            Assert.True(promoted.DeploymentSendReady);
            Assert.Equal("bundle-disabled", engine.ExpectedActive!.BundleId);
            Assert.Equal(first.ConfigurationFingerprint, engine.ExpectedActive.ConfigurationFingerprint);
            Assert.Equal(first.ActivationGeneration, engine.ExpectedActive.ActivationGeneration);
            Assert.Equal("bundle-enabled", engine.LastBundleId);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Production_promotion_rejects_phrase_and_stale_authority()
    {
        var root = SetupTestFixtures.CreateManagedRoot();
        try
        {
            var workflow = new AcsSetupWorkflow();
            var firstEngine = new FakeApplyEngine(SuccessResult());
            var first = await ApplyAsync(
                workflow,
                SetupTestFixtures.ProductionAcsRequest(root, dryRun: true),
                firstEngine);

            var phraseRejected = await workflow.EnableLiveSendingAsync(
                first.ConfigurationAppliedProof!,
                "Staging",
                AcsLiveSendingApproval.EnablePhrase,
                null!,
                firstEngine,
                CancellationToken.None);
            Assert.Equal(AcsSetupResultCode.ProductionConfirmationRejected, phraseRejected.Code);

            var staleEngine = new FakeApplyEngine(SetupApplyResult.Create(
                SetupApplyResultCode.IneligibleExistingActive,
                SetupManagedDeploymentState.Active,
                reasonCode: "expected_active_authority_mismatch"));
            var stale = await workflow.EnableLiveSendingAsync(
                first.ConfigurationAppliedProof!,
                AcsEnvironmentConfirmation.Production,
                AcsLiveSendingApproval.EnablePhrase,
                null!,
                staleEngine,
                CancellationToken.None);
            Assert.Equal(AcsSetupResultCode.LiveSendingEnableApplyFailed, stale.Code);
            Assert.False(stale.DeploymentSendReady);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Send_ready_requires_typed_doctor_pass()
    {
        var apply = SuccessResult();
        var pending = AcsSendReadyEvaluator.Evaluate(
            SetupMode.ProductionAcs,
            apply,
            effectiveLiveSendingEnabled: true,
            AcsSetupDoctorResult.Fail("doctor_checks_pending"));
        Assert.False(pending.SendReadyAsserted);
        Assert.Equal("doctor_checks_pending", pending.ReasonCode);

        var passed = AcsSendReadyEvaluator.Evaluate(
            SetupMode.ProductionAcs,
            apply,
            effectiveLiveSendingEnabled: true,
            AcsSetupDoctorResult.Pass());
        Assert.True(passed.SendReadyAsserted);
        Assert.Null(passed.ReasonCode);
    }

    [Fact]
    public void Doctor_evaluates_post_apply_effective_observations()
    {
        var doctor = new AcsSetupDoctorOperation();

        Assert.Equal(
            "doctor_effective_provider_not_acs",
            doctor.EvaluateProduction(
                WithEffective(SuccessResult(), "mailpit", liveSending: true)).ReasonCode);
        Assert.Equal(
            "doctor_effective_live_sending_disabled",
            doctor.EvaluateProduction(
                WithEffective(SuccessResult(), "acs", liveSending: false)).ReasonCode);
        Assert.True(doctor.EvaluateProduction(SuccessResult()).Passed);

        static SetupApplyResult WithEffective(
            SetupApplyResult source,
            string provider,
            bool liveSending) =>
            SetupApplyResult.Create(
                source.Code,
                source.DeploymentState,
                source.Message,
                source.ActionCode,
                source.ReasonCode,
                source.BundleId,
                source.ActivationGeneration,
                source.ConfigurationApplied,
                source.VerificationCommitted,
                source.ConfigRollbackStatus,
                source.PersistentSideEffectMayRemain,
                source.PersistentSideEffectKind,
                provider,
                liveSending);
    }

    [Fact]
    public async Task Staging_sender_is_derived_from_selected_applied_tenant()
    {
        var root = SetupTestFixtures.CreateManagedRoot();
        try
        {
            var capture = new FakeAcsClient();
            var workflow = new AcsSetupWorkflow(
                stagingVerification: new AcsStagingVerificationOperation(capture));
            var request = SetupTestFixtures.StagingAcsRequest(root, dryRun: true);
            var applied = await ApplyAsync(workflow, request, new FakeApplyEngine(SuccessResult()));

            var result = await workflow.VerifyStagingAsync(
                StagingRequest(request.Tenants.Tenants.Single().TenantId),
                applied.ConfigurationAppliedProof!,
                CancellationToken.None);

            Assert.Equal(AcsSetupResultCode.StagingVerificationSucceeded, result.Code);
            Assert.Equal(
                request.Tenants.Tenants.Single().DefaultFrom.Email,
                capture.LastRequest!.SenderEmail);
            Assert.Equal(AcsStagingVerificationOperation.SyntheticSubject, capture.LastRequest.Subject);
            Assert.Equal(AcsStagingVerificationOperation.SyntheticPlainTextBody, capture.LastRequest.PlainTextBody);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Staging_rejects_unknown_tenant_without_send()
    {
        var root = SetupTestFixtures.CreateManagedRoot();
        try
        {
            var capture = new FakeAcsClient();
            var workflow = new AcsSetupWorkflow(
                stagingVerification: new AcsStagingVerificationOperation(capture));
            var request = SetupTestFixtures.StagingAcsRequest(root, dryRun: true);
            var applied = await ApplyAsync(workflow, request, new FakeApplyEngine(SuccessResult()));

            var result = await workflow.VerifyStagingAsync(
                StagingRequest(Guid.NewGuid()),
                applied.ConfigurationAppliedProof!,
                CancellationToken.None);

            Assert.Equal(AcsSetupResultCode.StagingVerificationFailed, result.Code);
            Assert.Equal(
                AcsStagingVerificationOperation.RejectedTenantNotFound,
                result.StagingVerificationCode);
            Assert.Null(capture.LastRequest);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Session_limit_is_shared_across_operation_instances()
    {
        var root = SetupTestFixtures.CreateManagedRoot();
        try
        {
            var request = SetupTestFixtures.StagingAcsRequest(root, dryRun: true);
            var applied = await ApplyAsync(
                new AcsSetupWorkflow(),
                request,
                new FakeApplyEngine(SuccessResult()));
            var sessionId = "test-" + Guid.NewGuid().ToString("N");
            var first = new AcsStagingVerificationOperation(new FakeAcsClient());
            var second = new AcsStagingVerificationOperation(new FakeAcsClient());

            for (var index = 0; index < AcsSessionTestSendLimiter.DefaultMaxAttemptsPerSession; index++)
            {
                var operation = index % 2 == 0 ? first : second;
                var result = await operation.ExecuteAsync(
                    StagingRequest(request.Tenants.Tenants.Single().TenantId, sessionId),
                    applied.ConfigurationAppliedProof!,
                    CancellationToken.None);
                Assert.True(result.IsSuccess);
            }

            var limited = await second.ExecuteAsync(
                StagingRequest(request.Tenants.Tenants.Single().TenantId, sessionId),
                applied.ConfigurationAppliedProof!,
                CancellationToken.None);
            Assert.Equal(
                AcsStagingVerificationOperation.RejectedSessionLimitExceeded,
                limited.Code);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Provider_failure_is_canonical_and_sanitized()
    {
        var root = SetupTestFixtures.CreateManagedRoot();
        try
        {
            var request = SetupTestFixtures.StagingAcsRequest(root, dryRun: true);
            var applied = await ApplyAsync(
                new AcsSetupWorkflow(),
                request,
                new FakeApplyEngine(SuccessResult()));
            var operation = new AcsStagingVerificationOperation(
                new FakeAcsClient(AcsTestSendOutcome.Failed(
                    AdminProviderTestAcsSendResultCodes.FailedAcsAuthentication)));

            var result = await operation.ExecuteAsync(
                StagingRequest(request.Tenants.Tenants.Single().TenantId),
                applied.ConfigurationAppliedProof!,
                CancellationToken.None);

            Assert.Equal(AdminProviderTestAcsSendResultCodes.FailedAcsAuthentication, result.Code);
            Assert.Null(result.ProviderMessageIdForHandoff);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static Task<AcsSetupWorkflowResult> ApplyAsync(
        AcsSetupWorkflow workflow,
        SetupRequest request,
        ISetupApplyEngine engine) =>
        workflow.ApplyConfigurationAsync(
            request,
            request.Mode == SetupMode.ProductionAcs
                ? AcsEnvironmentConfirmation.Production
                : AcsEnvironmentConfirmation.Staging,
            AcsRegisterOperation.IntentPhrase,
            request.AcsConnectionString!,
            null!,
            engine,
            CancellationToken.None);

    private static AcsStagingVerificationRequest StagingRequest(
        Guid tenantId,
        string? sessionId = null) =>
        new()
        {
            EnvironmentConfirmation = AcsEnvironmentConfirmation.Staging,
            IntentConfirmation = AcsStagingVerificationOperation.IntentPhrase,
            TenantId = tenantId,
            RecipientEmail = "recipient@example.com",
            AssistantSessionId = sessionId,
        };

    private static SetupApplyResult SuccessResult() =>
        SetupApplyResult.Create(
            SetupApplyResultCode.ApplySucceeded,
            SetupManagedDeploymentState.Active,
            bundleId: "ignored",
            activationGeneration: 7,
            configurationApplied: true,
            verificationCommitted: true,
            effectiveProviderSummary: "acs",
            effectiveLiveSendingEnabled: true);

    private sealed class FakeAcsClient(AcsTestSendOutcome? outcome = null) : IAcsTestSendClient
    {
        public AcsTestSendRequest? LastRequest { get; private set; }

        public Task<AcsTestSendOutcome> SendAsync(
            AcsTestSendRequest request,
            CancellationToken cancellationToken)
        {
            LastRequest = request;
            return Task.FromResult(
                outcome ?? AcsTestSendOutcome.Succeeded(request.OperationId.ToString("D")));
        }
    }

    private sealed class FakeApplyEngine(SetupApplyResult result) : ISetupApplyEngine
    {
        public string? LastBundleId { get; private set; }
        public SetupExpectedActiveAuthority? ExpectedActive { get; private set; }

        public Task<SetupApplyResult> ApplyAsync(
            TrustedSetupHostLayout layout,
            string candidateBundleId,
            CancellationToken cancellationToken)
        {
            LastBundleId = candidateBundleId;
            return Task.FromResult(WithCandidate(result, candidateBundleId));
        }

        public Task<SetupApplyResult> ApplyAfterVerifiedAsync(
            TrustedSetupHostLayout layout,
            string candidateBundleId,
            SetupExpectedActiveAuthority expectedActive,
            CancellationToken cancellationToken)
        {
            ExpectedActive = expectedActive;
            return ApplyAsync(layout, candidateBundleId, cancellationToken);
        }

        public Task<SetupApplyResult> RecoverAsync(
            TrustedSetupHostLayout layout,
            CancellationToken cancellationToken) =>
            Task.FromResult(result);

        private static SetupApplyResult WithCandidate(
            SetupApplyResult source,
            string candidateBundleId) =>
            SetupApplyResult.Create(
                source.Code,
                source.DeploymentState,
                source.Message,
                source.ActionCode,
                source.ReasonCode,
                candidateBundleId,
                source.ActivationGeneration,
                source.ConfigurationApplied,
                source.VerificationCommitted,
                source.ConfigRollbackStatus,
                source.PersistentSideEffectMayRemain,
                source.PersistentSideEffectKind,
                source.EffectiveProviderSummary,
                source.EffectiveLiveSendingEnabled);
    }
}
