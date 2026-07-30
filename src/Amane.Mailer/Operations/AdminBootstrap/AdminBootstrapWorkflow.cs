using System.Net;
using Amane.Mailer.Admin;
using Amane.Mailer.Data.Sqlite;
using Amane.Mailer.Setup;

namespace Amane.Mailer.Operations.AdminBootstrap;

internal sealed class AdminBootstrapRequest
{
    internal required TrustedSetupHostLayout Layout { get; init; }
    internal SetupRuntimeFileOwnership? RuntimeFileOwnership { get; init; }
    internal required TrustedAdminAccessEndpoint AccessEndpoint { get; init; }
    internal required string EnvironmentName { get; init; }
    internal required string Username { get; init; }
    internal required AdminBootstrapCredentialLease Credential { get; init; }
    internal required string AllowedLocalAddress { get; init; }
    internal required bool AllowHttp { get; init; }
    internal required bool Interactive { get; init; }
    internal required bool LoopbackOnlyPublished { get; init; }
    internal required bool ApprovedReverseProxy { get; init; }
    internal required bool ServerLocalAddressConfirmed { get; init; }
    internal required IReadOnlyCollection<Guid> TenantIds { get; init; }
    internal TimeSpan VerificationBudget { get; init; } =
        AdminAccessVerifier.DefaultVerificationBudget;
}

internal static class AdminBootstrapResultCode
{
    internal const string Succeeded = "admin.bootstrap.succeeded";
    internal const string PreflightRejected = "admin.bootstrap.preflight_rejected";
    internal const string BundleGenerationFailed = "admin.bootstrap.bundle_generation_failed";
    internal const string ApplyFailed = "admin.bootstrap.apply_failed";
    internal const string AccessVerificationFailed = "admin.bootstrap.access_verification_failed";
    internal const string ConfigRollbackSucceeded = "admin.bootstrap.config_rollback_succeeded";
    internal const string ConfigRollbackFailed = "admin.bootstrap.config_rollback_failed";
    internal const string ManualActionRequired = "admin.bootstrap.manual_action_required";
    internal const string FailedUnexpected = "admin.bootstrap.failed_unexpected";
}

internal sealed class AdminBootstrapWorkflowResult
{
    internal required string Code { get; init; }
    internal required string AccessProfile { get; init; }
    internal required string ConfigRollback { get; init; }
    internal required string AdminDatabaseState { get; init; }
    internal required string AdminExposure { get; init; }
    internal required string LoginVerification { get; init; }
    internal required string SetupStatusVerification { get; init; }
    internal required string VerificationSessionCleanup { get; init; }
    internal required bool ManualActionRequired { get; init; }
    internal string? ReasonCode { get; init; }
    internal bool IsSuccess => Code == AdminBootstrapResultCode.Succeeded;
}

/// <summary>
/// Console-independent interactive Admin bootstrap. It keeps config rollback, SQLite state,
/// access verification, and workflow-session cleanup as independent result dimensions.
/// </summary>
internal sealed class AdminBootstrapWorkflow
{
    internal static readonly TimeSpan CleanupBudget = TimeSpan.FromSeconds(30);

    private readonly SetupCore _setupCore;
    private readonly ISetupFileSystem _fileSystem;
    private readonly AdminBootstrapDatabase _database;
    private readonly AdminBootstrapSourceClassifier _sourceClassifier;
    private readonly AdminBootstrapOwnershipStore _ownership;
    private readonly ISetupVerifiedWorkflowApplyEngine _applyEngine;
    private readonly AdminAccessVerifier _accessVerifier;
    private readonly AdminSessionRepository _sessions;
    private readonly TimeProvider _timeProvider;

    internal AdminBootstrapWorkflow(
        SetupCore setupCore,
        ISetupFileSystem fileSystem,
        AdminBootstrapDatabase database,
        AdminBootstrapSourceClassifier sourceClassifier,
        AdminBootstrapOwnershipStore ownership,
        ISetupVerifiedWorkflowApplyEngine applyEngine,
        AdminAccessVerifier accessVerifier,
        AdminSessionRepository sessions,
        TimeProvider? timeProvider = null)
    {
        _setupCore = setupCore;
        _fileSystem = fileSystem;
        _database = database;
        _sourceClassifier = sourceClassifier;
        _ownership = ownership;
        _applyEngine = applyEngine;
        _accessVerifier = accessVerifier;
        _sessions = sessions;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    internal async Task<AdminBootstrapWorkflowResult> RecoverAsync(
        TrustedSetupHostLayout layout,
        AdminAccessProfile profile,
        CancellationToken cancellationToken)
    {
        var pendingRead = _ownership.ReadPending(layout.ManagedRoot);
        if (pendingRead.Kind == AdminBootstrapOwnershipReadKind.Missing)
            return RecoveryResult(profile, AdminBootstrapResultCode.PreflightRejected, "no_pending_operation");
        if (pendingRead.Kind != AdminBootstrapOwnershipReadKind.Valid
            || pendingRead.Document is not { } pending)
        {
            return RecoveryResult(
                profile,
                AdminBootstrapResultCode.ManualActionRequired,
                "pending_ownership_unreadable",
                manualActionRequired: true);
        }

        var current = _ownership.ReadCurrent(layout.ManagedRoot);
        if (current.Kind == AdminBootstrapOwnershipReadKind.Valid
            && current.Document is { } currentDocument
            && string.Equals(
                currentDocument.OperationId,
                pending.OperationId,
                StringComparison.Ordinal)
            && string.Equals(
                currentDocument.State,
                AdminBootstrapOwnershipState.Succeeded,
                StringComparison.Ordinal))
        {
            var delete = _ownership.DeletePending(layout.ManagedRoot);
            return RecoveryResult(
                profile,
                delete.IsSuccess
                    ? AdminBootstrapResultCode.Succeeded
                    : AdminBootstrapResultCode.ManualActionRequired,
                delete.IsSuccess ? "pending_cleanup_completed" : "pending_cleanup_failed",
                adminExposure: "enabled",
                sessionCleanup: "succeeded",
                manualActionRequired: !delete.IsSuccess);
        }

        if (string.Equals(pending.State, AdminBootstrapOwnershipState.Prepared, StringComparison.Ordinal))
        {
            if (!TryReadActive(layout, out var active))
            {
                return RecoveryResult(
                    profile,
                    AdminBootstrapResultCode.ManualActionRequired,
                    "active_authority_unreadable",
                    manualActionRequired: true);
            }

            if (string.Equals(active!.BundleId, pending.Source.BundleId, StringComparison.Ordinal))
            {
                var delete = _ownership.DeletePending(layout.ManagedRoot);
                return RecoveryResult(
                    profile,
                    delete.IsSuccess
                        ? AdminBootstrapResultCode.PreflightRejected
                        : AdminBootstrapResultCode.ManualActionRequired,
                    delete.IsSuccess
                        ? "aborted_before_activation"
                        : "pending_cleanup_failed",
                    manualActionRequired: !delete.IsSuccess);
            }

            return RecoveryResult(
                profile,
                AdminBootstrapResultCode.ManualActionRequired,
                "prepared_candidate_authority_unknown",
                manualActionRequired: true);
        }

        var operationIdValid = AdminBootstrapOperationId.TryParse(
            pending.OperationId,
            out var operationId);
        if (!operationIdValid)
        {
            return RecoveryResult(
                profile,
                AdminBootstrapResultCode.ManualActionRequired,
                "pending_operation_invalid",
                manualActionRequired: true);
        }

        if (string.Equals(
                pending.State,
                AdminBootstrapOwnershipState.AccessVerified,
                StringComparison.Ordinal))
        {
            var cleanup = await CleanupSessionAsync(operationId);
            if (cleanup)
            {
                pending = Transition(
                    pending,
                    AdminBootstrapOwnershipState.SessionCleaned,
                    pending.ObservedDatabaseClassification ?? "unknown");
                if (!_ownership.WritePending(layout.ManagedRoot, pending).IsSuccess)
                {
                    return RecoveryResult(
                        profile,
                        AdminBootstrapResultCode.ManualActionRequired,
                        "session_cleaned_write_failed",
                        sessionCleanup: "succeeded",
                        manualActionRequired: true);
                }
            }
            else
            {
                var rollbackAfterCleanupFailure =
                    await _applyEngine.RecoverAdminBootstrapRollbackAsync(
                        layout,
                        pending,
                        cancellationToken);
                _ = await ConvergeRecoveredRollbackAsync(
                    layout,
                    pending,
                    rollbackAfterCleanupFailure);
                return RecoveryResult(
                    profile,
                    rollbackAfterCleanupFailure.ConfigRollbackStatus
                        == SetupConfigRollbackStatus.Succeeded
                        ? AdminBootstrapResultCode.ConfigRollbackSucceeded
                        : AdminBootstrapResultCode.ConfigRollbackFailed,
                    "admin_session_cleanup_failed",
                    configRollback: rollbackAfterCleanupFailure.ConfigRollbackStatus,
                    sessionCleanup: "failed",
                    manualActionRequired: true);
            }
        }

        if (string.Equals(
                pending.State,
                AdminBootstrapOwnershipState.SessionCleaned,
                StringComparison.Ordinal))
        {
            var authority = await _applyEngine.VerifyPendingCandidateAsync(
                layout,
                pending,
                cancellationToken);
            if (!authority.IsCurrent)
            {
                return RecoveryResult(
                    profile,
                    AdminBootstrapResultCode.ManualActionRequired,
                    authority.ReasonCode ?? "candidate_authority_changed",
                    sessionCleanup: "succeeded",
                    manualActionRequired: true);
            }

            var promote = _ownership.PromotePendingToCurrent(
                layout.ManagedRoot,
                pending with
                {
                    State = AdminBootstrapOwnershipState.Succeeded,
                    LastTransitionAt = Timestamp(),
                });
            return RecoveryResult(
                profile,
                promote.CurrentCommitted
                    ? AdminBootstrapResultCode.Succeeded
                    : AdminBootstrapResultCode.ManualActionRequired,
                promote.IsFullySucceeded
                    ? "pending_promotion_completed"
                    : promote.CurrentCommitted
                        ? "pending_cleanup_required"
                        : "ownership_promotion_failed",
                adminExposure: promote.CurrentCommitted ? "enabled" : "unknown",
                loginVerification: "succeeded",
                setupStatusVerification: "succeeded",
                sessionCleanup: "succeeded",
                manualActionRequired: !promote.IsFullySucceeded);
        }

        if (pending.State is AdminBootstrapOwnershipState.Armed
            or AdminBootstrapOwnershipState.DatabaseObserved)
        {
            var cleanup = await CleanupSessionAsync(operationId);
            var rollback = await _applyEngine.RecoverAdminBootstrapRollbackAsync(
                layout,
                pending,
                cancellationToken);
            var ownershipConverged = await ConvergeRecoveredRollbackAsync(layout, pending, rollback);
            return RecoveryResult(
                profile,
                rollback.ConfigRollbackStatus == SetupConfigRollbackStatus.Succeeded
                    ? AdminBootstrapResultCode.ConfigRollbackSucceeded
                    : AdminBootstrapResultCode.ConfigRollbackFailed,
                "crash_recovery_rollback",
                configRollback: rollback.ConfigRollbackStatus,
                sessionCleanup: cleanup ? "succeeded" : "failed",
                manualActionRequired:
                    rollback.ConfigRollbackStatus != SetupConfigRollbackStatus.Succeeded
                    || !cleanup
                    || !ownershipConverged);
        }

        return RecoveryResult(
            profile,
            AdminBootstrapResultCode.ManualActionRequired,
            "pending_state_requires_intervention",
            manualActionRequired: true);
    }

    internal async Task<AdminBootstrapWorkflowResult> ExecuteAsync(
        AdminBootstrapRequest request,
        CancellationToken cancellationToken)
    {
        try
        {
            if (!TryValidateRequest(request, out var preflightReason))
                return PreflightFailed(request, preflightReason);

            var existingPending = _ownership.ReadPending(request.Layout.ManagedRoot);
            if (existingPending.Kind == AdminBootstrapOwnershipReadKind.NeedsIntervention)
                return PreflightFailed(request, "pending_ownership_unreadable");
            if (existingPending.Kind != AdminBootstrapOwnershipReadKind.Missing)
                return PreflightFailed(request, "pending_operation_requires_recovery");

            AdminBootstrapDatabaseSnapshot databaseBefore;
            try
            {
                databaseBefore = await _database.InspectReadOnlyAsync(cancellationToken);
            }
            catch
            {
                return PreflightFailed(request, "database_preflight_failed");
            }

            var disposition = _sourceClassifier.Classify(request.Layout, databaseBefore);
            if (disposition == SourceAdminDisposition.Unknown)
                return PreflightFailed(request, "source_admin_disposition_unknown");
            if (!_sourceClassifier.TryReadActiveAuthority(
                    request.Layout,
                    out var active,
                    out var recorded)
                || active is null
                || recorded is null)
            {
                return PreflightFailed(request, "source_authority_unreadable");
            }

            if (request.AccessEndpoint.Profile == AdminAccessProfile.LocalDevelopment
                && !string.Equals(
                    recorded.Mode,
                    SetupModeParser.ToWireValue(SetupMode.LocalMailpit),
                    StringComparison.Ordinal))
            {
                return PreflightFailed(request, "local_profile_requires_mailpit_mode");
            }

            string password;
            string passwordHash;
            try
            {
                password = request.Credential.Materialize();
                var reuseExistingHash = disposition == SourceAdminDisposition.EnabledManagedSameUser
                    || (disposition == SourceAdminDisposition.DisabledMain
                        && databaseBefore.Classification
                            == AdminBootstrapDatabaseClassification.ManagedSameUser);
                if (reuseExistingHash)
                {
                    if (databaseBefore.UserPasswordHash is null
                        || databaseBefore.Username is null
                        || !string.Equals(
                            request.Username,
                            databaseBefore.Username,
                            StringComparison.Ordinal)
                        || !AdminPasswordHasher.Verify(password, databaseBefore.UserPasswordHash)
                        || databaseBefore.AppliedPasswordHash is null
                        || !string.Equals(
                            databaseBefore.AppliedPasswordHash,
                            databaseBefore.UserPasswordHash,
                            StringComparison.Ordinal))
                    {
                        return PreflightFailed(request, "managed_password_preverification_failed");
                    }

                    passwordHash = databaseBefore.UserPasswordHash;
                }
                else
                {
                    passwordHash = AdminPasswordHasher.Hash(password);
                }
            }
            catch
            {
                return PreflightFailed(request, "credential_validation_failed");
            }

            var operationId = AdminBootstrapOperationId.Create();
            var expectation = BuildExpectation(
                operationId,
                disposition,
                databaseBefore,
                request.TenantIds);

            var generated = _setupCore.GenerateAdminDerivedBundle(
                request.Layout,
                active.BundleId,
                disposition,
                request.RuntimeFileOwnership,
                new SetupAdminBundleDelta
                {
                    Username = request.Username,
                    PasswordHash = passwordHash,
                    AllowedLocalAddress = request.AllowedLocalAddress,
                    AllowHttp = request.AllowHttp,
                    Expectation = expectation,
                });
            if (!generated.IsSuccess || generated.BundleId is null)
            {
                return Failed(
                    request,
                    AdminBootstrapResultCode.BundleGenerationFailed,
                    "bundle_generation_failed",
                    databaseBefore.Classification);
            }

            var pending = new AdminBootstrapOwnershipDocument
            {
                OperationId = operationId.Value,
                State = AdminBootstrapOwnershipState.Prepared,
                Source = new AdminBootstrapSourceAuthority
                {
                    BundleId = active.BundleId,
                    ActivationGeneration = active.ActivationGeneration,
                    ConfigurationFingerprint = recorded.ConfigurationFingerprint,
                    RecordedSchemaVersion = recorded.SchemaVersion,
                    ImageIdentity = (recorded.ImageRepository ?? string.Empty)
                        + ":"
                        + (recorded.ImageTag ?? string.Empty),
                    ComposeIdentity = string.Empty,
                    RuntimeIdentityBindingDigest = string.Empty,
                    AdminDisposition = disposition,
                    CapturedAt = Timestamp(),
                },
                Candidate = new AdminBootstrapCandidateAuthority
                {
                    BundleId = generated.BundleId,
                    ExpectedActivationGeneration = active.ActivationGeneration + 1,
                },
                ExpectedDatabase = expectation,
                ObservedDatabaseClassification = databaseBefore.Classification,
                LastTransitionAt = Timestamp(),
            };
            var pendingWrite = _ownership.WritePendingPrepared(request.Layout.ManagedRoot, pending);
            if (!pendingWrite.IsSuccess)
            {
                return Failed(
                    request,
                    AdminBootstrapResultCode.ManualActionRequired,
                    pendingWrite.Code == SetupDockerResultCode.InvalidBundleInventory
                        ? "pending_operation_requires_recovery"
                        : "ownership_prepared_write_failed",
                    databaseBefore.Classification,
                    manualActionRequired: true);
            }

            var leaseResult = await _applyEngine.AcquireVerifiedWorkflowLeaseAsync(
                request.Layout,
                disposition,
                cancellationToken);
            if (!leaseResult.IsSuccess || leaseResult.Lease is null)
            {
                var delete = _ownership.DeletePending(request.Layout.ManagedRoot);
                return Failed(
                    request,
                    delete.IsSuccess
                        ? AdminBootstrapResultCode.PreflightRejected
                        : AdminBootstrapResultCode.ManualActionRequired,
                    leaseResult.Result.ReasonCode ?? "workflow_lease_failed",
                    databaseBefore.Classification,
                    manualActionRequired: !delete.IsSuccess);
            }

            await using var lease = leaseResult.Lease;
            if (_sourceClassifier.Classify(request.Layout, databaseBefore) != disposition
                || !string.Equals(
                    lease.Source.Active.BundleId,
                    pending.Source.BundleId,
                    StringComparison.Ordinal)
                || lease.Source.Active.ActivationGeneration != pending.Source.ActivationGeneration
                || !string.Equals(
                    lease.Source.Recorded.ConfigurationFingerprint,
                    pending.Source.ConfigurationFingerprint,
                    StringComparison.Ordinal))
            {
                var delete = _ownership.DeletePending(request.Layout.ManagedRoot);
                return Failed(
                    request,
                    delete.IsSuccess
                        ? AdminBootstrapResultCode.PreflightRejected
                        : AdminBootstrapResultCode.ManualActionRequired,
                    "source_admin_authority_changed",
                    databaseBefore.Classification,
                    manualActionRequired: !delete.IsSuccess);
            }

            var apply = await lease.ApplyCandidateAsync(
                generated.BundleId,
                pending,
                cancellationToken);
            if (apply.Code != SetupApplyResultCode.ApplySucceeded)
            {
                return await ConvergeFailedApplyAsync(
                    request,
                    pending,
                    disposition,
                    databaseBefore,
                    apply);
            }

            var databaseAfter = await TryInspectDatabaseAsync();
            if (databaseAfter is null || !Matches(databaseAfter, expectation.After))
            {
                return await FailAfterActivationAsync(
                    request,
                    lease,
                    operationId,
                    pending,
                    databaseAfter,
                    "database_postflight_failed",
                    loginVerification: "not-run",
                    setupStatusVerification: "not-run");
            }

            pending = Transition(
                pending,
                AdminBootstrapOwnershipState.DatabaseObserved,
                databaseAfter.Classification);
            if (!_ownership.WritePending(request.Layout.ManagedRoot, pending).IsSuccess)
            {
                return await FailAfterActivationAsync(
                    request,
                    lease,
                    operationId,
                    pending,
                    databaseAfter,
                    "database_observed_write_failed",
                    "not-run",
                    "not-run");
            }

            var authorityBeforeLogin = await lease.VerifyCandidateStillCurrentAsync(cancellationToken);
            if (!authorityBeforeLogin.IsCurrent)
            {
                return await FailAfterActivationAsync(
                    request,
                    lease,
                    operationId,
                    pending,
                    databaseAfter,
                    authorityBeforeLogin.ReasonCode ?? "candidate_authority_changed",
                    "not-run",
                    "not-run");
            }

            var access = await _accessVerifier.VerifyAsync(
                request.AccessEndpoint,
                request.Username,
                password,
                operationId,
                request.VerificationBudget,
                cancellationToken);
            if (!access.LoginSucceeded || !access.SetupStatusReached)
            {
                return await FailAfterActivationAsync(
                    request,
                    lease,
                    operationId,
                    pending,
                    databaseAfter,
                    access.Code,
                    access.LoginPageReached
                        ? access.LoginSucceeded ? "succeeded" : "failed"
                        : "failed",
                    access.SetupStatusReached ? "succeeded" : "failed",
                    cleanupAlreadyAttempted: false);
            }

            pending = Transition(
                pending,
                AdminBootstrapOwnershipState.AccessVerified,
                databaseAfter.Classification);
            if (!_ownership.WritePending(request.Layout.ManagedRoot, pending).IsSuccess)
            {
                return await FailAfterActivationAsync(
                    request,
                    lease,
                    operationId,
                    pending,
                    databaseAfter,
                    "access_verified_write_failed",
                    "succeeded",
                    "succeeded");
            }

            var cleanup = await CleanupSessionAsync(operationId);
            if (!cleanup)
            {
                return await FailAfterActivationAsync(
                    request,
                    lease,
                    operationId,
                    pending,
                    databaseAfter,
                    "admin_session_cleanup_failed",
                    "succeeded",
                    "succeeded",
                    cleanupAlreadyAttempted: true);
            }

            pending = Transition(
                pending,
                AdminBootstrapOwnershipState.SessionCleaned,
                databaseAfter.Classification);
            if (!_ownership.WritePending(request.Layout.ManagedRoot, pending).IsSuccess)
            {
                return await FailAfterActivationAsync(
                    request,
                    lease,
                    operationId,
                    pending,
                    databaseAfter,
                    "session_cleaned_write_failed",
                    "succeeded",
                    "succeeded",
                    cleanupAlreadyAttempted: true);
            }

            var authorityBeforePromote = await lease.VerifyCandidateStillCurrentAsync(cancellationToken);
            if (!authorityBeforePromote.IsCurrent)
            {
                return await FailAfterActivationAsync(
                    request,
                    lease,
                    operationId,
                    pending,
                    databaseAfter,
                    authorityBeforePromote.ReasonCode ?? "candidate_authority_changed",
                    "succeeded",
                    "succeeded",
                    cleanupAlreadyAttempted: true);
            }

            var promote = _ownership.PromotePendingToCurrent(
                request.Layout.ManagedRoot,
                pending with
                {
                    State = AdminBootstrapOwnershipState.Succeeded,
                    LastTransitionAt = Timestamp(),
                });
            if (!promote.CurrentCommitted)
            {
                return await FailAfterActivationAsync(
                    request,
                    lease,
                    operationId,
                    pending,
                    databaseAfter,
                    "ownership_promotion_failed",
                    "succeeded",
                    "succeeded",
                    cleanupAlreadyAttempted: true);
            }

            return new AdminBootstrapWorkflowResult
            {
                Code = AdminBootstrapResultCode.Succeeded,
                AccessProfile = Profile(request),
                ConfigRollback = SetupConfigRollbackStatus.NotApplicable,
                AdminDatabaseState = databaseAfter.Classification,
                AdminExposure = "enabled",
                LoginVerification = "succeeded",
                SetupStatusVerification = "succeeded",
                VerificationSessionCleanup = "succeeded",
                ManualActionRequired = !promote.IsFullySucceeded,
                ReasonCode = promote.IsFullySucceeded ? null : "pending_cleanup_required",
            };
        }
        finally
        {
            request.Credential.Dispose();
        }
    }

    private async Task<AdminBootstrapWorkflowResult> ConvergeFailedApplyAsync(
        AdminBootstrapRequest request,
        AdminBootstrapOwnershipDocument pending,
        SourceAdminDisposition disposition,
        AdminBootstrapDatabaseSnapshot databaseBefore,
        SetupApplyResult apply)
    {
        var pendingNow = _ownership.ReadPending(request.Layout.ManagedRoot);
        if (pendingNow.Kind == AdminBootstrapOwnershipReadKind.Valid
            && pendingNow.Document is { } livePending)
        {
            pending = livePending;
        }

        var databaseAfter = await TryInspectDatabaseAsync() ?? databaseBefore;
        var rollbackSucceeded = apply.ConfigRollbackStatus == SetupConfigRollbackStatus.Succeeded
            || string.Equals(apply.ReasonCode, "source_already_active", StringComparison.Ordinal);
        var ownershipConverged = ConvergeOwnershipAfterConfigRollback(
            request.Layout,
            pending,
            disposition,
            databaseAfter,
            apply.ActivationGeneration,
            rollbackSucceeded,
            apply.ReasonCode);

        return Failed(
            request,
            rollbackSucceeded
                ? AdminBootstrapResultCode.ConfigRollbackSucceeded
                : AdminBootstrapResultCode.ApplyFailed,
            apply.ReasonCode ?? "candidate_apply_failed",
            databaseAfter.Classification,
            configRollback: apply.ConfigRollbackStatus,
            adminExposure: rollbackSucceeded
                && disposition == SourceAdminDisposition.DisabledMain
                    ? "disabled"
                    : rollbackSucceeded
                        && disposition == SourceAdminDisposition.EnabledManagedSameUser
                        ? "enabled"
                        : "unknown",
            manualActionRequired:
                !rollbackSucceeded
                || !ownershipConverged
                || apply.DeploymentState == SetupManagedDeploymentState.NeedsIntervention
                || apply.PersistentSideEffectMayRemain);
    }

    private async Task<AdminBootstrapWorkflowResult> FailAfterActivationAsync(
        AdminBootstrapRequest request,
        ISetupVerifiedWorkflowLease lease,
        AdminBootstrapOperationId operationId,
        AdminBootstrapOwnershipDocument pending,
        AdminBootstrapDatabaseSnapshot? database,
        string reasonCode,
        string loginVerification,
        string setupStatusVerification,
        bool cleanupAlreadyAttempted = false)
    {
        var cleanup = cleanupAlreadyAttempted || await CleanupSessionAsync(operationId);
        // Cleanup failure never suppresses config rollback.
        var rollback = await lease.RollbackToSourceAsync(MapRollbackReason(reasonCode));
        var rollbackSucceeded = rollback.ConfigRollbackStatus == SetupConfigRollbackStatus.Succeeded;

        var adminExposure = "unknown";
        if (rollbackSucceeded)
        {
            var exposure = await _accessVerifier.ProbeExposureAsync(
                request.AccessEndpoint,
                CleanupBudget,
                CancellationToken.None);
            adminExposure = lease.Source.AdminDisposition switch
            {
                SourceAdminDisposition.DisabledMain =>
                    exposure == AdminExposureProbeResult.NotFound ? "disabled" : "unknown",
                SourceAdminDisposition.EnabledManagedSameUser =>
                    exposure == AdminExposureProbeResult.LoginPageReached ? "enabled" : "unknown",
                _ => "unknown",
            };
        }

        var databaseAfter = database ?? await TryInspectDatabaseAsync();
        var ownershipConverged = ConvergeOwnershipAfterConfigRollback(
            request.Layout,
            pending,
            lease.Source.AdminDisposition,
            databaseAfter,
            rollback.ActivationGeneration,
            rollbackSucceeded,
            rollback.ReasonCode);

        return Failed(
            request,
            rollbackSucceeded
                ? AdminBootstrapResultCode.ConfigRollbackSucceeded
                : AdminBootstrapResultCode.ConfigRollbackFailed,
            reasonCode,
            databaseAfter?.Classification ?? "unknown",
            configRollback: rollback.ConfigRollbackStatus,
            adminExposure: adminExposure,
            loginVerification: loginVerification,
            setupStatusVerification: setupStatusVerification,
            sessionCleanup: cleanup ? "succeeded" : "failed",
            manualActionRequired:
                !rollbackSucceeded
                || !cleanup
                || !ownershipConverged
                || adminExposure == "unknown");
    }

    private bool ConvergeOwnershipAfterConfigRollback(
        TrustedSetupHostLayout layout,
        AdminBootstrapOwnershipDocument pending,
        SourceAdminDisposition disposition,
        AdminBootstrapDatabaseSnapshot? database,
        long? activationGeneration,
        bool rollbackSucceeded,
        string? reasonCode)
    {
        if (!rollbackSucceeded)
        {
            return _ownership.WritePending(
                layout.ManagedRoot,
                pending with
                {
                    State = AdminBootstrapOwnershipState.NeedsIntervention,
                    ObservedDatabaseClassification = database?.Classification,
                    LastTransitionAt = Timestamp(),
                }).IsSuccess;
        }

        if (disposition == SourceAdminDisposition.EnabledManagedSameUser)
        {
            var deleted = _ownership.DeletePending(layout.ManagedRoot).IsSuccess;
            if (!deleted)
                return false;

            if (activationGeneration is null)
                return true;

            var current = _ownership.ReadCurrent(layout.ManagedRoot);
            if (current.Kind != AdminBootstrapOwnershipReadKind.Valid
                || current.Document is not { } succeeded
                || !string.Equals(
                    succeeded.State,
                    AdminBootstrapOwnershipState.Succeeded,
                    StringComparison.Ordinal))
            {
                return false;
            }

            return _ownership.TryUpdateSucceededCurrentGeneration(
                layout.ManagedRoot,
                succeeded.OperationId,
                pending.Source.BundleId,
                activationGeneration.Value).IsSuccess;
        }

        var residualRequired = database is not null
            && string.Equals(
                database.Classification,
                AdminBootstrapDatabaseClassification.ManagedSameUser,
                StringComparison.Ordinal);
        if (residualRequired || disposition == SourceAdminDisposition.DisabledMain)
        {
            if (!residualRequired
                && string.Equals(reasonCode, "source_already_active", StringComparison.Ordinal)
                && (database is null
                    || string.Equals(
                        database.Classification,
                        AdminBootstrapDatabaseClassification.Fresh,
                        StringComparison.Ordinal)))
            {
                return _ownership.DeletePending(layout.ManagedRoot).IsSuccess;
            }

            if (!residualRequired)
                return _ownership.DeletePending(layout.ManagedRoot).IsSuccess;

            var residual = pending with
            {
                State = AdminBootstrapOwnershipState.ResidualAfterConfigRollback,
                Source = pending.Source with
                {
                    ActivationGeneration = activationGeneration
                        ?? pending.Source.ActivationGeneration,
                },
                ObservedDatabaseClassification = database?.Classification,
                LastTransitionAt = Timestamp(),
            };
            var promote = _ownership.PromotePendingToCurrent(layout.ManagedRoot, residual);
            return promote.CurrentCommitted;
        }

        return _ownership.DeletePending(layout.ManagedRoot).IsSuccess;
    }

    private async Task<bool> CleanupSessionAsync(AdminBootstrapOperationId operationId)
    {
        using var cleanupCts = new CancellationTokenSource(CleanupBudget);
        try
        {
            await _sessions.RevokeWorkflowSessionAsync(
                AdminWorkflowSessionId.FromOperationId(operationId),
                _timeProvider.GetUtcNow(),
                cleanupCts.Token);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private async Task<AdminBootstrapDatabaseSnapshot?> TryInspectDatabaseAsync()
    {
        try
        {
            return await _database.InspectReadOnlyAsync(CancellationToken.None);
        }
        catch
        {
            return null;
        }
    }

    private static SetupAdminBootstrapExpectation BuildExpectation(
        AdminBootstrapOperationId operationId,
        SourceAdminDisposition disposition,
        AdminBootstrapDatabaseSnapshot before,
        IReadOnlyCollection<Guid> tenantIds)
    {
        var beforeState = before.ToExpectationState(
            includeFreshSessionGuard: disposition == SourceAdminDisposition.DisabledMain
                && before.Classification == AdminBootstrapDatabaseClassification.Fresh);
        SetupAdminDatabaseExpectationState afterState;
        if (disposition == SourceAdminDisposition.EnabledManagedSameUser
            || before.Classification == AdminBootstrapDatabaseClassification.ManagedSameUser)
        {
            afterState = before.ToExpectationState(includeFreshSessionGuard: false);
        }
        else
        {
            afterState = new SetupAdminDatabaseExpectationState
            {
                Classification = AdminBootstrapDatabaseClassification.ManagedSameUser,
                AdminConfigCount = 1,
                AdminUserCount = 1,
                AdminConfigCredentialEpoch = 0,
                AdminUserCredentialEpoch = 0,
                ScopeFingerprint = AdminBootstrapScopeFingerprint.Compute(tenantIds),
                FreshHasAnyAdminSessionRows = null,
            };
        }

        return new SetupAdminBootstrapExpectation
        {
            OperationId = operationId.Value,
            Before = beforeState,
            After = afterState,
        };
    }

    private static bool Matches(
        AdminBootstrapDatabaseSnapshot actual,
        SetupAdminDatabaseExpectationState expected) =>
        string.Equals(actual.Classification, expected.Classification, StringComparison.Ordinal)
        && actual.AdminConfigCount == expected.AdminConfigCount
        && actual.AdminUserCount == expected.AdminUserCount
        && actual.AdminConfigCredentialEpoch == expected.AdminConfigCredentialEpoch
        && actual.AdminUserCredentialEpoch == expected.AdminUserCredentialEpoch
        && string.Equals(actual.ScopeFingerprint, expected.ScopeFingerprint, StringComparison.Ordinal);

    private static bool TryValidateRequest(
        AdminBootstrapRequest request,
        out string reasonCode)
    {
        reasonCode = "preflight_rejected";
        if (!request.Interactive
            || string.IsNullOrWhiteSpace(request.Username)
            || !IPAddress.TryParse(request.AllowedLocalAddress, out _)
            || request.VerificationBudget <= TimeSpan.Zero)
        {
            return false;
        }

        if (request.AccessEndpoint.Profile == AdminAccessProfile.LocalDevelopment)
        {
            if (!string.Equals(request.EnvironmentName, "Development", StringComparison.Ordinal)
                || !request.LoopbackOnlyPublished
                || !request.AllowHttp
                || !IPAddress.IsLoopback(IPAddress.Parse(request.AllowedLocalAddress)))
            {
                reasonCode = "local_profile_precondition_failed";
                return false;
            }
        }
        else if (request.AllowHttp
                 || !request.ApprovedReverseProxy
                 || !request.ServerLocalAddressConfirmed
                 || request.EnvironmentName is not ("Production" or "Staging"))
        {
            reasonCode = "production_profile_precondition_failed";
            return false;
        }

        return true;
    }

    private static AdminBootstrapWorkflowResult PreflightFailed(
        AdminBootstrapRequest request,
        string reasonCode) =>
        Failed(
            request,
            AdminBootstrapResultCode.PreflightRejected,
            reasonCode,
            "not-observed");

    private static AdminBootstrapWorkflowResult Failed(
        AdminBootstrapRequest request,
        string code,
        string reasonCode,
        string databaseState,
        string configRollback = SetupConfigRollbackStatus.NotApplicable,
        string adminExposure = "unknown",
        string loginVerification = "not-run",
        string setupStatusVerification = "not-run",
        string sessionCleanup = "not-applicable",
        bool manualActionRequired = false) =>
        new()
        {
            Code = code,
            AccessProfile = Profile(request),
            ConfigRollback = configRollback,
            AdminDatabaseState = databaseState,
            AdminExposure = adminExposure,
            LoginVerification = loginVerification,
            SetupStatusVerification = setupStatusVerification,
            VerificationSessionCleanup = sessionCleanup,
            ManualActionRequired = manualActionRequired,
            ReasonCode = reasonCode,
        };

    private AdminBootstrapOwnershipDocument Transition(
        AdminBootstrapOwnershipDocument pending,
        string state,
        string databaseClassification) =>
        pending with
        {
            State = state,
            ObservedDatabaseClassification = databaseClassification,
            LastTransitionAt = Timestamp(),
        };

    private static string MapRollbackReason(string reasonCode) =>
        reasonCode.Contains("timeout", StringComparison.Ordinal)
            || reasonCode.Contains("cancel", StringComparison.Ordinal)
            ? "admin_verification_timeout"
            : reasonCode == "admin_session_cleanup_failed"
                ? reasonCode
                : "admin_access_verification_failed";

    private string Timestamp() =>
        _timeProvider.GetUtcNow().UtcDateTime.ToString("O");

    private static string Profile(AdminBootstrapRequest request) =>
        request.AccessEndpoint.Profile == AdminAccessProfile.LocalDevelopment
            ? "local-development"
            : "production-https";

    private bool TryReadActive(
        TrustedSetupHostLayout layout,
        out SetupActivePointer? active)
    {
        active = null;
        try
        {
            return _fileSystem.FileExists(layout.ActivePointerPath)
                && SetupActivePointer.TryParse(
                    System.Text.Encoding.UTF8.GetString(
                        _fileSystem.ReadAllBytes(layout.ActivePointerPath)),
                    out active)
                && active is not null;
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }

    private async Task<bool> ConvergeRecoveredRollbackAsync(
        TrustedSetupHostLayout layout,
        AdminBootstrapOwnershipDocument pending,
        SetupApplyResult rollback)
    {
        AdminBootstrapDatabaseSnapshot? database;
        try
        {
            database = await _database.InspectReadOnlyAsync(CancellationToken.None);
        }
        catch
        {
            database = null;
        }

        return ConvergeOwnershipAfterConfigRollback(
            layout,
            pending,
            pending.Source.AdminDisposition,
            database,
            rollback.ActivationGeneration,
            rollback.ConfigRollbackStatus == SetupConfigRollbackStatus.Succeeded,
            rollback.ReasonCode);
    }

    private static AdminBootstrapWorkflowResult RecoveryResult(
        AdminAccessProfile profile,
        string code,
        string reasonCode,
        string configRollback = SetupConfigRollbackStatus.NotApplicable,
        string adminExposure = "unknown",
        string loginVerification = "not-run",
        string setupStatusVerification = "not-run",
        string sessionCleanup = "not-applicable",
        bool manualActionRequired = false) =>
        new()
        {
            Code = code,
            AccessProfile = profile == AdminAccessProfile.LocalDevelopment
                ? "local-development"
                : "production-https",
            ConfigRollback = configRollback,
            AdminDatabaseState = "unknown",
            AdminExposure = adminExposure,
            LoginVerification = loginVerification,
            SetupStatusVerification = setupStatusVerification,
            VerificationSessionCleanup = sessionCleanup,
            ManualActionRequired = manualActionRequired,
            ReasonCode = reasonCode,
        };
}
