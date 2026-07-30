using Amane.Mailer.Admin;
using Amane.Mailer.Data.Sqlite;
using Amane.Mailer.Operations.AcsSetup;
using Amane.Mailer.Operations.AdminBootstrap;
using Microsoft.Extensions.Configuration;

namespace Amane.Mailer.Setup.Assistant;

/// <summary>
/// Binds the assistant screens to the existing typed operations. Every branch here is glue:
/// resolving the trusted host layout, building the Setup Core request from collected input, and
/// projecting canonical results. No configuration, Docker, ACS, or Admin decision is made here.
/// </summary>
internal sealed class SetupAssistantOperations : ISetupAssistantOperations
{
    private readonly ISetupFileSystem _fileSystem;
    private readonly SetupHostDockerAdapter _dockerAdapter;
    private readonly SetupApplyEngine _applyEngine;
    private readonly SetupCore _setupCore;
    private readonly AcsSetupWorkflow _acsWorkflow;

    private TrustedSetupHostLayout? _layout;

    internal SetupAssistantOperations()
    {
        _fileSystem = new HostSetupFileSystem();
        _dockerAdapter = new SetupHostDockerAdapter(_fileSystem);
        _applyEngine = new SetupApplyEngine(_fileSystem, _dockerAdapter);
        _setupCore = new SetupCore(_fileSystem);
        _acsWorkflow = new AcsSetupWorkflow(_setupCore);
    }

    public async Task<SetupAssistantDockerPreflightOutcome> CheckDockerAsync(
        CancellationToken cancellationToken)
    {
        var (result, binding) = await _dockerAdapter.CheckDockerAsync(cancellationToken);
        return new SetupAssistantDockerPreflightOutcome
        {
            Passed = result.IsSuccess && binding is not null,
            Code = result.Code,
            EngineKind = result.EngineKind?.ToString(),
        };
    }

    public async Task<SetupAssistantMainSetupOutcome> ApplyMainSetupAsync(
        SetupAssistantMainSetupInput input,
        CancellationToken cancellationToken)
    {
        if (!TryResolveLayout(input.Mode, out var layout, out var layoutFailureCode))
        {
            return Failure(layoutFailureCode);
        }

        var request = BuildSetupRequest(input, layout);

        if (input.Mode == SetupMode.LocalMailpit)
        {
            var generated = _setupCore.GenerateBundle(request);
            if (!generated.IsSuccess || string.IsNullOrEmpty(generated.BundleId))
            {
                return Failure(generated.Code);
            }

            var applied = await _applyEngine.ApplyAsync(
                layout,
                generated.BundleId,
                cancellationToken);
            return FromApply(applied, generated.ConfigurationFingerprint);
        }

        var workflow = await _acsWorkflow.ApplyConfigurationAsync(
            request,
            input.EnvironmentConfirmation,
            input.IntentConfirmation,
            input.AcsConnectionStringConfirmation ?? string.Empty,
            layout,
            _applyEngine,
            cancellationToken);
        return FromAcsWorkflow(workflow);
    }

    public async Task<SetupAssistantStagingOutcome> VerifyStagingAsync(
        SetupAssistantStagingInput input,
        CancellationToken cancellationToken)
    {
        if (input.AppliedProof is not AcsConfigurationAppliedProof proof)
        {
            return new SetupAssistantStagingOutcome
            {
                Code = AcsSetupResultCode.RejectedInvalidMode,
                Kind = SetupAssistantOutcomeKind.Rejected,
            };
        }

        var result = await _acsWorkflow.VerifyStagingAsync(
            new AcsStagingVerificationRequest
            {
                EnvironmentConfirmation = input.EnvironmentConfirmation,
                IntentConfirmation = input.IntentConfirmation,
                TenantId = input.TenantId,
                RecipientEmail = input.RecipientEmail,
                AssistantSessionId = input.AssistantSessionId,
            },
            proof,
            cancellationToken);

        return new SetupAssistantStagingOutcome
        {
            Code = result.Code,
            Kind = result.Code == AcsSetupResultCode.StagingVerificationSucceeded
                ? SetupAssistantOutcomeKind.Succeeded
                : SetupAssistantResultPresenter.ClassifyApply(
                    result.Code,
                    result.ActionCode,
                    result.PersistentSideEffectMayRemain),
            SendRequestAccepted = result.StagingSendRequestAccepted,
            OperationCompleted = result.StagingOperationCompleted,
            MailboxCheckStatus = result.StagingMailboxCheckStatus,
            MaskedSenderEmail = result.MaskedSenderEmail,
            MaskedRecipientEmail = result.MaskedRecipientEmail,
        };
    }

    public async Task<SetupAssistantMainSetupOutcome> EnableLiveSendingAsync(
        SetupAssistantProductionInput input,
        CancellationToken cancellationToken)
    {
        if (input.AppliedProof is not AcsConfigurationAppliedProof proof || _layout is null)
        {
            return Failure(AcsSetupResultCode.RejectedInvalidMode);
        }

        var result = await _acsWorkflow.EnableLiveSendingAsync(
            proof,
            input.EnvironmentConfirmation,
            input.LiveSendingEnableApproval,
            _layout,
            _applyEngine,
            cancellationToken);
        return FromAcsWorkflow(result);
    }

    public Task<SetupAssistantAdminPreflightOutcome> CheckAdminAccessProfileAsync(
        SetupAssistantAdminAccessInput input,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        // Both the endpoint shape and the access-profile policy are decided by #459; the assistant
        // only surfaces the canonical verdict. The preconditions that depend on host and database
        // state are still evaluated by the bootstrap run itself.
        if (!TryCreateAccessEndpoint(input, out var endpoint) || endpoint is null)
        {
            return Task.FromResult(new SetupAssistantAdminPreflightOutcome
            {
                Satisfied = false,
                ReasonCode = "access_endpoint_rejected",
                Profile = input.Profile,
            });
        }

        var satisfied = AdminBootstrapWorkflow.TryValidateAccessProfile(
            endpoint.Profile,
            input.EnvironmentName,
            input.AllowedLocalAddress,
            input.AllowHttp,
            input.LoopbackOnlyPublished,
            input.ApprovedReverseProxy,
            input.ServerLocalAddressConfirmed,
            out var reasonCode);

        return Task.FromResult(new SetupAssistantAdminPreflightOutcome
        {
            Satisfied = satisfied,
            ReasonCode = reasonCode,
            Profile = input.Profile,
        });
    }

    public async Task<SetupAssistantAdminBootstrapOutcome> BootstrapAdminAsync(
        SetupAssistantAdminBootstrapInput input,
        CancellationToken cancellationToken)
    {
        if (_layout is not { } layout)
        {
            return AdminFailure(AdminBootstrapResultCode.PreflightRejected, "main_setup_not_applied");
        }

        if (!TryCreateAccessEndpoint(input.Access, out var endpoint) || endpoint is null)
        {
            return AdminFailure(AdminBootstrapResultCode.PreflightRejected, "access_endpoint_rejected");
        }

        var database = await ResolveAdminDatabaseAsync(layout, cancellationToken);
        if (database.Connections is not { } connections)
        {
            return AdminFailure(AdminBootstrapResultCode.PreflightRejected, database.ReasonCode);
        }

        var ownership = new AdminBootstrapOwnershipStore(_fileSystem);
        var workflow = new AdminBootstrapWorkflow(
            _setupCore,
            _fileSystem,
            new AdminBootstrapDatabase(connections, TimeProvider.System),
            new AdminBootstrapSourceClassifier(_fileSystem, ownership),
            ownership,
            _applyEngine,
            new AdminAccessVerifier(),
            new AdminSessionRepository(connections));

        using var credential = new AdminBootstrapCredentialLease(input.Password.AsSpan());
        var result = await workflow.ExecuteAsync(
            new AdminBootstrapRequest
            {
                Layout = layout,
                RuntimeFileOwnership = ResolveRuntimeFileOwnership(),
                AccessEndpoint = endpoint,
                EnvironmentName = input.Access.EnvironmentName,
                Username = input.Username,
                Credential = credential,
                AllowedLocalAddress = input.Access.AllowedLocalAddress,
                AllowHttp = input.Access.AllowHttp,
                Interactive = true,
                LoopbackOnlyPublished = input.Access.LoopbackOnlyPublished,
                ApprovedReverseProxy = input.Access.ApprovedReverseProxy,
                ServerLocalAddressConfirmed = input.Access.ServerLocalAddressConfirmed,
                TenantIds = input.TenantIds,
            },
            cancellationToken);

        return new SetupAssistantAdminBootstrapOutcome
        {
            Code = result.Code,
            Kind = ClassifyAdmin(result),
            AccessProfile = result.AccessProfile,
            ConfigRollback = result.ConfigRollback,
            AdminDatabaseState = result.AdminDatabaseState,
            AdminExposure = result.AdminExposure,
            LoginVerification = result.LoginVerification,
            SetupStatusVerification = result.SetupStatusVerification,
            VerificationSessionCleanup = result.VerificationSessionCleanup,
            ManualActionRequired = result.ManualActionRequired,
            ReasonCode = result.ReasonCode,
        };
    }

    private bool TryResolveLayout(
        SetupMode mode,
        out TrustedSetupHostLayout layout,
        out string failureCode)
    {
        var resolved = TrustedSetupHostLayoutResolver.TryResolveInstalled(
            _fileSystem,
            mode,
            SetupModeParser.ToWireValue(mode),
            out var candidate);
        if (!resolved.IsSuccess || candidate is null)
        {
            layout = null!;
            failureCode = resolved.Code;
            return false;
        }

        _layout = candidate;
        layout = candidate;
        failureCode = string.Empty;
        return true;
    }

    /// <summary>
    /// Locates the host-visible SQLite file through the pinned external input layer owned by
    /// #449, so no host path is guessed and none is ever surfaced to the browser.
    /// </summary>
    private async Task<(SqliteConnectionFactory? Connections, string ReasonCode)>
        ResolveAdminDatabaseAsync(
            TrustedSetupHostLayout layout,
            CancellationToken cancellationToken)
    {
        const string Unavailable = "admin_database_unavailable";

        var (probe, binding) = await _dockerAdapter.CheckDockerAsync(cancellationToken);
        if (!probe.IsSuccess || binding is null)
        {
            return (null, Unavailable);
        }

        var (sessionResult, session) = await _dockerAdapter.AcquireSessionAsync(
            layout,
            binding,
            cancellationToken);
        if (!sessionResult.IsSuccess || session is null)
        {
            return (null, Unavailable);
        }

        string databasePath;
        await using (session)
        {
            var pin = await _dockerAdapter.PinExternalInputsAsync(session, cancellationToken);
            if (!pin.IsSuccess
                || session.ExternalInputs is not { } external
                || !SetupDatabaseFileProbe.TryResolveHostDatabasePath(
                    external.NormalizedDataPath,
                    external.NormalizedConnectionString,
                    out databasePath,
                    out _))
            {
                return (null, Unavailable);
            }
        }

        var connections = new SqliteConnectionFactory(
            new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["ConnectionStrings:Mailer"] = $"Data Source={databasePath}",
                })
                .Build());
        return (connections, string.Empty);
    }

    private static bool TryCreateAccessEndpoint(
        SetupAssistantAdminAccessInput input,
        out TrustedAdminAccessEndpoint? endpoint)
    {
        endpoint = null;
        if (!Uri.TryCreate(input.OriginText, UriKind.Absolute, out var origin))
        {
            return false;
        }

        var profile = input.Profile == SetupAssistantAdminProfile.ProductionHttps
            ? AdminAccessProfile.ProductionHttps
            : AdminAccessProfile.LocalDevelopment;
        return TrustedAdminAccessEndpoint.TryCreate(profile, origin, out endpoint);
    }

    private SetupRuntimeFileOwnership? ResolveRuntimeFileOwnership()
    {
        if (_fileSystem.GetEffectiveUnixUserId() is not { } userId
            || _fileSystem.GetEffectiveUnixGroupId() is not { } groupId)
        {
            return null;
        }

        return new SetupRuntimeFileOwnership { UnixUserId = userId, UnixGroupId = groupId };
    }

    private SetupRequest BuildSetupRequest(
        SetupAssistantMainSetupInput input,
        TrustedSetupHostLayout layout) =>
        new()
        {
            Mode = input.Mode,
            ManagedRootPath = layout.ManagedRoot,
            Tenants = input.Tenants,
            TokenSecrets = input.TokenSecrets,
            AcsConnectionString = input.AcsConnectionString,
            PlatformSender = input.PlatformSender,
            RuntimeFileOwnership = ResolveRuntimeFileOwnership(),

            // Image identity comes from the trusted release inventory. The UI cannot supply a
            // repository, tag, or digest (ADR 0021 D-06).
            ImageRepository = layout.ReleaseInventory.AllowedImageRepository,
            ImageTag = layout.ReleaseInventory.AllowedDisplayTag,
        };

    private static SetupAssistantMainSetupOutcome FromAcsWorkflow(AcsSetupWorkflowResult result) =>
        new()
        {
            Code = result.Code,
            Kind = SetupAssistantResultPresenter.ClassifyApply(
                result.Code,
                result.ActionCode,
                result.PersistentSideEffectMayRemain),
            ConfigurationApplied = result.ConfigurationApplied,
            DeploymentSendReady = result.DeploymentSendReady,
            BundleId = result.BundleId,
            ConfigurationFingerprint = result.ConfigurationFingerprint,
            ApplyResultCode = result.ApplyResultCode,
            ConfigRollbackStatus = result.ConfigRollbackStatus,
            ActionCode = result.ActionCode,
            PersistentSideEffectMayRemain = result.PersistentSideEffectMayRemain,
            PersistentSideEffectKind = result.PersistentSideEffectKind,
            AppliedProof = result.ConfigurationAppliedProof,
        };

    private static SetupAssistantMainSetupOutcome FromApply(
        SetupApplyResult apply,
        string? fingerprint) =>
        new()
        {
            Code = apply.Code,
            Kind = SetupAssistantResultPresenter.ClassifyApply(
                apply.Code,
                apply.ActionCode,
                apply.PersistentSideEffectMayRemain),
            ConfigurationApplied = apply.ConfigurationApplied,

            // Mailpit setup never asserts send-ready from the apply engine alone (ADR 0021 D-07).
            DeploymentSendReady = false,
            BundleId = apply.BundleId,
            ConfigurationFingerprint = fingerprint,
            ApplyResultCode = apply.Code,
            ConfigRollbackStatus = apply.ConfigRollbackStatus,
            ActionCode = apply.ActionCode,
            PersistentSideEffectMayRemain = apply.PersistentSideEffectMayRemain,
            PersistentSideEffectKind = apply.PersistentSideEffectKind,
        };

    private static SetupAssistantOutcomeKind ClassifyAdmin(AdminBootstrapWorkflowResult result)
    {
        if (result.IsSuccess)
        {
            return SetupAssistantOutcomeKind.Succeeded;
        }

        if (result.ManualActionRequired
            || result.Code == AdminBootstrapResultCode.ManualActionRequired
            || result.Code == AdminBootstrapResultCode.ConfigRollbackFailed)
        {
            return SetupAssistantOutcomeKind.ManualInterventionRequired;
        }

        return result.Code == AdminBootstrapResultCode.PreflightRejected
            ? SetupAssistantOutcomeKind.Rejected
            : SetupAssistantOutcomeKind.Failed;
    }

    private static SetupAssistantMainSetupOutcome Failure(string code) =>
        new()
        {
            Code = code,
            Kind = SetupAssistantResultPresenter.ClassifyApply(code, null, false),
        };

    private static SetupAssistantAdminBootstrapOutcome AdminFailure(string code, string reasonCode) =>
        new()
        {
            Code = code,
            Kind = SetupAssistantOutcomeKind.Rejected,
            AccessProfile = "unknown",
            ConfigRollback = "not-applicable",
            AdminDatabaseState = "unknown",
            AdminExposure = "disabled",
            LoginVerification = "not-attempted",
            SetupStatusVerification = "not-attempted",
            VerificationSessionCleanup = "not-attempted",
            ReasonCode = reasonCode,
        };
}
