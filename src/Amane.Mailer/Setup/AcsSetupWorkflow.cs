using Amane.Mailer.Operations.AcsSetup;

namespace Amane.Mailer.Setup;

/// <summary>
/// Console-independent Easy Setup ACS workflow. #450 remains the sole apply, verification,
/// ACTIVE, Docker, and rollback authority.
/// </summary>
public sealed class AcsSetupWorkflow
{
    private readonly SetupCore _setupCore;
    private readonly AcsStagingVerificationOperation _stagingVerification;
    private readonly AcsSetupDoctorOperation _doctor;

    public AcsSetupWorkflow(
        SetupCore? setupCore = null,
        AcsStagingVerificationOperation? stagingVerification = null,
        AcsSetupDoctorOperation? doctor = null)
    {
        _setupCore = setupCore ?? new SetupCore();
        _stagingVerification = stagingVerification ?? new AcsStagingVerificationOperation();
        _doctor = doctor ?? new AcsSetupDoctorOperation();
    }

    public async Task<AcsSetupWorkflowResult> ApplyConfigurationAsync(
        SetupRequest request,
        string environmentConfirmation,
        string intentConfirmation,
        string connectionStringConfirmation,
        TrustedSetupHostLayout layout,
        ISetupApplyEngine applyEngine,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(applyEngine);

        try
        {
            if (request.Mode is not (
                SetupMode.StagingNoSend
                or SetupMode.StagingVerification
                or SetupMode.ProductionAcs))
            {
                return Fail(
                    AcsSetupResultCode.RejectedInvalidMode,
                    AcsSetupWorkflowState.NotStarted,
                    "ACS configuration apply requires an ACS Setup mode.");
            }

            var validationError = AcsConfigurationValidator.ValidateManagedRequest(
                request,
                environmentConfirmation,
                intentConfirmation,
                connectionStringConfirmation);
            if (validationError is not null)
            {
                return Fail(
                    validationError,
                    AcsSetupWorkflowState.NotStarted,
                    "ACS configuration input was rejected.");
            }

            if (request.Tenants.Tenants.Any(static tenant => tenant.LiveSending))
            {
                return Fail(
                    AcsSetupResultCode.RejectedLiveSendingWithoutConfirmation,
                    AcsSetupWorkflowState.NotStarted,
                    "Initial ACS apply must use live_sending=false.");
            }

            var generated = _setupCore.GenerateBundle(request);
            if (!generated.IsSuccess || string.IsNullOrEmpty(generated.BundleId))
            {
                return new AcsSetupWorkflowResult
                {
                    Code = AcsSetupResultCode.BundleGenerationFailed,
                    State = AcsSetupWorkflowState.BundleGenerationFailed,
                    Message = "Bundle generation failed.",
                    ConfigurationFingerprint = generated.ConfigurationFingerprint,
                };
            }

            var apply = await applyEngine.ApplyAsync(
                layout,
                generated.BundleId,
                cancellationToken);
            return MapConfigurationApply(apply, generated.ConfigurationFingerprint, request);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return Fail(
                AcsSetupResultCode.ManualActionRequired,
                AcsSetupWorkflowState.NeedsIntervention,
                "ACS configuration apply was cancelled.");
        }
        catch (Exception)
        {
            return Fail(
                AcsSetupResultCode.FailedUnexpected,
                AcsSetupWorkflowState.NeedsIntervention,
                "Unexpected ACS configuration apply failure.");
        }
    }

    public async Task<AcsSetupWorkflowResult> VerifyStagingAsync(
        AcsStagingVerificationRequest request,
        AcsConfigurationAppliedProof appliedProof,
        CancellationToken cancellationToken)
    {
        var result = await _stagingVerification.ExecuteAsync(
            request,
            appliedProof,
            cancellationToken);
        return AcsSetupWorkflowResult.FromStaging(result);
    }

    public async Task<AcsSetupWorkflowResult> EnableLiveSendingAsync(
        AcsConfigurationAppliedProof configurationAppliedProof,
        string productionEnvironmentConfirmation,
        string liveSendingEnableApproval,
        TrustedSetupHostLayout layout,
        ISetupApplyEngine applyEngine,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(configurationAppliedProof);
        ArgumentNullException.ThrowIfNull(applyEngine);

        try
        {
            var baseRequest = configurationAppliedProof.AppliedRequest;
            if (configurationAppliedProof.Mode != SetupMode.ProductionAcs
                || baseRequest.Tenants.Tenants.Any(static tenant => tenant.LiveSending))
            {
                return Fail(
                    AcsSetupResultCode.RejectedInvalidMode,
                    AcsSetupWorkflowState.ProductionConfirmationPending,
                    "Promotion requires a Production live_sending=false applied proof.");
            }

            if (!AcsEnvironmentConfirmation.IsExactProduction(productionEnvironmentConfirmation))
            {
                return Fail(
                    AcsSetupResultCode.ProductionConfirmationRejected,
                    AcsSetupWorkflowState.ProductionConfirmationRejected,
                    "Exact Production confirmation is required.");
            }

            if (!string.Equals(
                    liveSendingEnableApproval,
                    AcsLiveSendingApproval.EnablePhrase,
                    StringComparison.Ordinal))
            {
                return Fail(
                    AcsSetupResultCode.RejectedLiveSendingWithoutConfirmation,
                    AcsSetupWorkflowState.ProductionConfirmationRejected,
                    "Explicit live_sending enable approval is required.");
            }

            var promotedRequest = CloneWithLiveSendingEnabled(baseRequest);
            var generated = _setupCore.GenerateLiveSendingPromotionBundle(promotedRequest);
            if (!generated.IsSuccess || string.IsNullOrEmpty(generated.BundleId))
            {
                return new AcsSetupWorkflowResult
                {
                    Code = AcsSetupResultCode.BundleGenerationFailed,
                    State = AcsSetupWorkflowState.BundleGenerationFailed,
                    Message = "live_sending=true bundle generation failed.",
                    ConfigurationFingerprint = generated.ConfigurationFingerprint,
                };
            }

            var apply = await applyEngine.ApplyAfterVerifiedAsync(
                layout,
                generated.BundleId,
                configurationAppliedProof.ToExpectedAuthority(),
                cancellationToken);
            return MapPromotionApply(
                apply,
                generated.ConfigurationFingerprint,
                _doctor.EvaluateProduction(apply));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return Fail(
                AcsSetupResultCode.ManualActionRequired,
                AcsSetupWorkflowState.NeedsIntervention,
                "live_sending enable was cancelled.");
        }
        catch (Exception)
        {
            return Fail(
                AcsSetupResultCode.FailedUnexpected,
                AcsSetupWorkflowState.NeedsIntervention,
                "Unexpected live_sending enable failure.");
        }
    }

    private static SetupRequest CloneWithLiveSendingEnabled(SetupRequest source) =>
        new()
        {
            Mode = source.Mode,
            ManagedRootPath = source.ManagedRootPath,
            DryRun = source.DryRun,
            Tenants = source.Tenants with
            {
                Tenants = source.Tenants.Tenants
                    .Select(static tenant => tenant with { LiveSending = true })
                    .ToArray(),
            },
            TokenSecrets = source.TokenSecrets,
            WebhookSecrets = source.WebhookSecrets,
            MetricsBearerToken = source.MetricsBearerToken,
            AcsConnectionString = source.AcsConnectionString,
            PlatformSender = source.PlatformSender,
            PublicEnvOverrides = source.PublicEnvOverrides,
            Admin = source.Admin,
            RuntimeFileOwnership = source.RuntimeFileOwnership,
            ImageRepository = source.ImageRepository,
            ImageTag = source.ImageTag,
        };

    private static AcsSetupWorkflowResult MapConfigurationApply(
        SetupApplyResult apply,
        string? fingerprint,
        SetupRequest request)
    {
        if (apply.Code == SetupApplyResultCode.ApplySucceeded
            && apply.ConfigurationApplied
            && apply.VerificationCommitted
            && apply.BundleId is not null
            && apply.ActivationGeneration is { } generation
            && fingerprint is not null)
        {
            return new AcsSetupWorkflowResult
            {
                Code = AcsSetupResultCode.ConfigurationApplied,
                State = AcsSetupWorkflowState.ConfigurationApplied,
                ConfigurationApplied = true,
                BundleId = apply.BundleId,
                ConfigurationFingerprint = fingerprint,
                ActivationGeneration = generation,
                ApplyResultCode = apply.Code,
                ConfigRollbackStatus = apply.ConfigRollbackStatus,
                ActionCode = apply.ActionCode,
                Message = "Configuration applied. Deployment send-ready is not asserted.",
                ConfigurationAppliedProof = new AcsConfigurationAppliedProof(
                    apply.BundleId,
                    fingerprint,
                    generation,
                    SnapshotRequest(request)),
            };
        }

        return MapFailedApply(apply, fingerprint, liveSendingStep: false);
    }

    private static AcsSetupWorkflowResult MapPromotionApply(
        SetupApplyResult apply,
        string? fingerprint,
        AcsSetupDoctorResult doctor)
    {
        if (apply.Code == SetupApplyResultCode.ApplySucceeded && apply.ConfigurationApplied)
        {
            var sendReady = AcsSendReadyEvaluator.Evaluate(
                SetupMode.ProductionAcs,
                apply,
                effectiveLiveSendingEnabled: apply.EffectiveLiveSendingEnabled is true,
                doctor);
            return new AcsSetupWorkflowResult
            {
                Code = sendReady.SendReadyAsserted
                    ? AcsSetupResultCode.DeploymentSendReady
                    : AcsSetupResultCode.ConfigurationApplied,
                State = sendReady.SendReadyAsserted
                    ? AcsSetupWorkflowState.DeploymentSendReady
                    : AcsSetupWorkflowState.ConfigurationApplied,
                ConfigurationApplied = true,
                DeploymentSendReady = sendReady.SendReadyAsserted,
                BundleId = apply.BundleId,
                ConfigurationFingerprint = fingerprint,
                ActivationGeneration = apply.ActivationGeneration,
                ApplyResultCode = apply.Code,
                ConfigRollbackStatus = apply.ConfigRollbackStatus,
                PersistentSideEffectMayRemain = apply.PersistentSideEffectMayRemain,
                PersistentSideEffectKind = apply.PersistentSideEffectKind,
                ActionCode = sendReady.SendReadyAsserted
                    ? ClearCompletedSendReadyHandoff(apply.ActionCode)
                    : sendReady.ReasonCode ?? apply.ActionCode,
                Message = sendReady.SendReadyAsserted
                    ? "Deployment send-ready. Operational verification is not recorded."
                    : "Configuration applied; send-ready doctor gate did not pass.",
            };
        }

        return MapFailedApply(apply, fingerprint, liveSendingStep: true);
    }

    /// <summary>
    /// #450 hands the send-ready evaluation to #451 via an ACTION. Once the typed doctor gate
    /// asserts send-ready, that handoff is complete and must not stay in the canonical result.
    /// </summary>
    private static string? ClearCompletedSendReadyHandoff(string? applyActionCode) =>
        string.Equals(
            applyActionCode,
            SetupApplyActionCode.CompleteSendReadyEvaluation,
            StringComparison.Ordinal)
            ? null
            : applyActionCode;

    private static AcsSetupWorkflowResult MapFailedApply(
        SetupApplyResult apply,
        string? fingerprint,
        bool liveSendingStep)
    {
        var (code, state) = apply.Code switch
        {
            SetupApplyResultCode.ApplyFailedRollbackSucceeded or SetupApplyResultCode.RollbackSucceeded =>
                (AcsSetupResultCode.ConfigRollbackSucceeded, AcsSetupWorkflowState.RollbackSucceeded),
            SetupApplyResultCode.ApplyFailedRollbackFailed =>
                (AcsSetupResultCode.ConfigRollbackFailed, AcsSetupWorkflowState.RollbackFailed),
            SetupApplyResultCode.NeedsIntervention or SetupApplyResultCode.RecoveryRequired =>
                (AcsSetupResultCode.ManualActionRequired, AcsSetupWorkflowState.NeedsIntervention),
            _ => (
                liveSendingStep
                    ? AcsSetupResultCode.LiveSendingEnableApplyFailed
                    : AcsSetupResultCode.ConfigurationApplyFailed,
                AcsSetupWorkflowState.ApplyFailed),
        };

        if (apply.PersistentSideEffectMayRemain)
        {
            code = AcsSetupResultCode.ExternalSideEffectMayRemain;
            state = AcsSetupWorkflowState.ExternalSideEffectMayRemain;
        }

        return new AcsSetupWorkflowResult
        {
            Code = code,
            State = state,
            ConfigurationApplied = apply.ConfigurationApplied,
            BundleId = apply.BundleId,
            ConfigurationFingerprint = fingerprint,
            ActivationGeneration = apply.ActivationGeneration,
            ApplyResultCode = apply.Code,
            ConfigRollbackStatus = apply.ConfigRollbackStatus,
            PersistentSideEffectMayRemain = apply.PersistentSideEffectMayRemain,
            PersistentSideEffectKind = apply.PersistentSideEffectKind,
            ActionCode = apply.ActionCode,
            Message = liveSendingStep
                ? "live_sending enable apply failed."
                : "Configuration apply failed.",
        };
    }

    private static AcsSetupWorkflowResult Fail(
        string code,
        AcsSetupWorkflowState state,
        string message) =>
        new() { Code = code, State = state, Message = message };

    private static SetupRequest SnapshotRequest(SetupRequest source) =>
        new()
        {
            Mode = source.Mode,
            ManagedRootPath = source.ManagedRootPath,
            DryRun = source.DryRun,
            Tenants = source.Tenants with
            {
                Tenants = source.Tenants.Tenants.ToArray(),
            },
            TokenSecrets = new Dictionary<string, string>(
                source.TokenSecrets,
                StringComparer.Ordinal),
            WebhookSecrets = new Dictionary<string, string>(
                source.WebhookSecrets,
                StringComparer.Ordinal),
            MetricsBearerToken = source.MetricsBearerToken,
            AcsConnectionString = source.AcsConnectionString,
            PlatformSender = source.PlatformSender,
            PublicEnvOverrides = new Dictionary<string, string>(
                source.PublicEnvOverrides,
                StringComparer.Ordinal),
            Admin = source.Admin,
            RuntimeFileOwnership = source.RuntimeFileOwnership,
            ImageRepository = source.ImageRepository,
            ImageTag = source.ImageTag,
        };
}
