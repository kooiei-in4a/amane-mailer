using Amane.Mailer.Configuration;
using Amane.Mailer.Operations.AcsSetup;

namespace Amane.Mailer.Setup;

/// <summary>
/// Easy Setup ACS workflow Application Service (#451).
/// Orchestrates bundle generation (#448), apply/verify/rollback (#450), Staging verification,
/// and Production live_sending two-step promotion. Console-independent.
/// </summary>
public sealed class AcsSetupWorkflow
{
    private readonly SetupCore _setupCore;
    private readonly AcsStagingVerificationOperation _stagingVerification;

    public AcsSetupWorkflow(
        SetupCore? setupCore = null,
        AcsStagingVerificationOperation? stagingVerification = null)
    {
        _setupCore = setupCore ?? new SetupCore();
        _stagingVerification = stagingVerification ?? new AcsStagingVerificationOperation();
    }

    /// <summary>
    /// Generates a <c>live_sending=false</c> ACS bundle and applies it via #450.
    /// Reaches Configuration applied (not send-ready).
    /// </summary>
    public async Task<AcsSetupWorkflowResult> ApplyConfigurationAsync(
        SetupRequest request,
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

            if (request.Tenants.Tenants.Any(t => t.LiveSending))
            {
                return Fail(
                    AcsSetupResultCode.RejectedLiveSendingWithoutConfirmation,
                    AcsSetupWorkflowState.NotStarted,
                    "Initial ACS apply must use live_sending=false; enable live sending in a separate step.");
            }

            var generate = _setupCore.GenerateBundle(request);
            if (!generate.IsSuccess || string.IsNullOrEmpty(generate.BundleId))
            {
                return new AcsSetupWorkflowResult
                {
                    Code = AcsSetupResultCode.BundleGenerationFailed,
                    State = AcsSetupWorkflowState.BundleGenerationFailed,
                    Message = "Bundle generation failed.",
                    ConfigurationFingerprint = generate.ConfigurationFingerprint,
                };
            }

            var apply = await applyEngine.ApplyAsync(layout, generate.BundleId, cancellationToken);
            return MapApplyToConfigurationResult(apply, generate.ConfigurationFingerprint);
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

    /// <summary>
    /// Staging-only verification against the generated tenant sender. Session limits apply when
    /// <see cref="AcsStagingVerificationRequest.AssistantSessionId"/> is set.
    /// </summary>
    public Task<AcsSetupWorkflowResult> VerifyStagingAsync(
        AcsStagingVerificationRequest request,
        CancellationToken cancellationToken) =>
        VerifyStagingCoreAsync(request, cancellationToken);

    private async Task<AcsSetupWorkflowResult> VerifyStagingCoreAsync(
        AcsStagingVerificationRequest request,
        CancellationToken cancellationToken)
    {
        var staging = await _stagingVerification.ExecuteAsync(request, cancellationToken);
        return AcsSetupWorkflowResult.FromStaging(staging);
    }

    /// <summary>
    /// Production step 2: exact Production confirmation + explicit approval → new live_sending=true
    /// bundle → #450 re-apply → Deployment send-ready evaluation.
    /// </summary>
    public async Task<AcsSetupWorkflowResult> EnableLiveSendingAsync(
        SetupRequest baseRequest,
        string productionEnvironmentConfirmation,
        string liveSendingEnableApproval,
        TrustedSetupHostLayout layout,
        ISetupApplyEngine applyEngine,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(applyEngine);

        try
        {
            if (baseRequest.Mode != SetupMode.ProductionAcs)
            {
                return Fail(
                    AcsSetupResultCode.RejectedInvalidMode,
                    AcsSetupWorkflowState.ProductionConfirmationPending,
                    "live_sending enable requires production-acs mode.");
            }

            if (!AcsEnvironmentConfirmation.IsExactProduction(productionEnvironmentConfirmation))
            {
                return Fail(
                    AcsSetupResultCode.ProductionConfirmationRejected,
                    AcsSetupWorkflowState.ProductionConfirmationRejected,
                    "Exact Production confirmation is required to enable live sending.");
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

            var promotedTenants = WithLiveSendingEnabled(baseRequest.Tenants);
            var promotionRequest = CloneRequest(
                baseRequest,
                promotedTenants,
                new SetupLiveSendingPromotionAuthorization
                {
                    ProductionEnvironmentConfirmed = true,
                    LiveSendingEnableApproved = true,
                });

            var generate = _setupCore.GenerateBundle(promotionRequest);
            if (!generate.IsSuccess || string.IsNullOrEmpty(generate.BundleId))
            {
                return new AcsSetupWorkflowResult
                {
                    Code = AcsSetupResultCode.BundleGenerationFailed,
                    State = AcsSetupWorkflowState.BundleGenerationFailed,
                    Message = "live_sending=true bundle generation failed.",
                    ConfigurationFingerprint = generate.ConfigurationFingerprint,
                };
            }

            var apply = await applyEngine.ApplyAsync(layout, generate.BundleId, cancellationToken);
            return MapApplyToLiveSendingResult(apply, generate.ConfigurationFingerprint);
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

    /// <summary>
    /// Builds a Production promotion request from an existing configuration-applied request,
    /// forcing tenant live_sending=true under the promotion authorization gate.
    /// </summary>
    public static SetupRequest CreateLiveSendingPromotionRequest(
        SetupRequest baseRequest,
        SetupLiveSendingPromotionAuthorization authorization)
    {
        if (!authorization.IsAuthorized)
        {
            throw new InvalidOperationException("Live sending promotion authorization is incomplete.");
        }

        return CloneRequest(baseRequest, WithLiveSendingEnabled(baseRequest.Tenants), authorization);
    }

    public static MailerTenantsFile WithLiveSendingEnabled(MailerTenantsFile source) =>
        source with
        {
            Tenants = source.Tenants.Select(static t => t with { LiveSending = true }).ToArray(),
        };

    private static SetupRequest CloneRequest(
        SetupRequest source,
        MailerTenantsFile tenants,
        SetupLiveSendingPromotionAuthorization? authorization) =>
        new()
        {
            Mode = source.Mode,
            ManagedRootPath = source.ManagedRootPath,
            DryRun = source.DryRun,
            Tenants = tenants,
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
            LiveSendingPromotion = authorization,
        };

    private static AcsSetupWorkflowResult MapApplyToConfigurationResult(
        SetupApplyResult apply,
        string? fingerprint)
    {
        if (apply.Code == SetupApplyResultCode.ApplySucceeded && apply.ConfigurationApplied)
        {
            return new AcsSetupWorkflowResult
            {
                Code = AcsSetupResultCode.ConfigurationApplied,
                State = AcsSetupWorkflowState.ConfigurationApplied,
                ConfigurationApplied = true,
                DeploymentSendReady = false,
                BundleId = apply.BundleId,
                ConfigurationFingerprint = fingerprint,
                ApplyResultCode = apply.Code,
                ConfigRollbackStatus = apply.ConfigRollbackStatus,
                PersistentSideEffectMayRemain = apply.PersistentSideEffectMayRemain,
                PersistentSideEffectKind = apply.PersistentSideEffectKind,
                ActionCode = apply.ActionCode,
                Message = "Configuration applied. Deployment send-ready is not asserted.",
            };
        }

        return MapFailedApply(apply, fingerprint, liveSendingStep: false);
    }

    private static AcsSetupWorkflowResult MapApplyToLiveSendingResult(
        SetupApplyResult apply,
        string? fingerprint)
    {
        if (apply.Code == SetupApplyResultCode.ApplySucceeded && apply.ConfigurationApplied)
        {
            var sendReady = AcsSendReadyEvaluator.Evaluate(
                SetupMode.ProductionAcs,
                apply,
                effectiveLiveSendingEnabled: true);

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
                ApplyResultCode = apply.Code,
                ConfigRollbackStatus = apply.ConfigRollbackStatus,
                PersistentSideEffectMayRemain = apply.PersistentSideEffectMayRemain,
                PersistentSideEffectKind = apply.PersistentSideEffectKind,
                ActionCode = apply.ActionCode ?? sendReady.ReasonCode,
                Message = sendReady.SendReadyAsserted
                    ? "Deployment send-ready. Operational verification is not recorded."
                    : "live_sending bundle applied but send-ready gates were not met.",
            };
        }

        return MapFailedApply(apply, fingerprint, liveSendingStep: true);
    }

    private static AcsSetupWorkflowResult MapFailedApply(
        SetupApplyResult apply,
        string? fingerprint,
        bool liveSendingStep)
    {
        var (code, state) = apply.Code switch
        {
            SetupApplyResultCode.ApplyFailedRollbackSucceeded => (
                AcsSetupResultCode.ConfigRollbackSucceeded,
                AcsSetupWorkflowState.RollbackSucceeded),
            SetupApplyResultCode.RollbackSucceeded => (
                AcsSetupResultCode.ConfigRollbackSucceeded,
                AcsSetupWorkflowState.RollbackSucceeded),
            SetupApplyResultCode.ApplyFailedRollbackFailed => (
                AcsSetupResultCode.ConfigRollbackFailed,
                AcsSetupWorkflowState.RollbackFailed),
            SetupApplyResultCode.NeedsIntervention => (
                AcsSetupResultCode.ManualActionRequired,
                AcsSetupWorkflowState.NeedsIntervention),
            SetupApplyResultCode.RecoveryRequired => (
                AcsSetupResultCode.ManualActionRequired,
                AcsSetupWorkflowState.NeedsIntervention),
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
            DeploymentSendReady = false,
            BundleId = apply.BundleId,
            ConfigurationFingerprint = fingerprint,
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
        new()
        {
            Code = code,
            State = state,
            Message = message,
            DeploymentSendReady = false,
        };
}
