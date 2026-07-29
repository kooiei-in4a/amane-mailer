using System.Globalization;
using System.Text;
using System.Text.Json;

namespace Amane.Mailer.Setup;

/// <summary>
/// Managed apply / rollback / recovery orchestration (Issue #450, ADR 0021 D-03).
/// </summary>
/// <remarks>
/// <para>
/// The engine owns one ordering rule: everything that can refuse an apply happens before the ACTIVE
/// pointer moves, and everything after the switch is covered by a rollback that restores the previous
/// generation. Durable markers (<c>ACTIVE</c>, <c>PREVIOUS</c>, <c>TX.stamp</c>, the verification
/// record, and the runtime-identity binding) are written through
/// <see cref="SetupDurableAtomicWriter"/> so a crash at any point leaves a state that
/// <see cref="RecoverAsync"/> can classify.
/// </para>
/// <para>
/// Activation generations are monotonic. A rollback restores the previous bundle id under a new,
/// higher generation instead of reusing the old number, so a stale pinned operation can never be
/// mistaken for a current one.
/// </para>
/// <para>
/// Send-readiness is never asserted here: doctor is out of scope for #450, so every result reports
/// <see cref="SetupApplyResult.SendReadyAsserted"/> as <c>false</c>.
/// </para>
/// </remarks>
public sealed class SetupApplyEngine
{
    /// <summary>Rollback keeps its own budget so operator cancellation cannot strand a half-applied state.</summary>
    internal static readonly TimeSpan RollbackBudget = TimeSpan.FromSeconds(180);

    private readonly ISetupFileSystem _fileSystem;
    private readonly SetupHostDockerAdapter _adapter;
    private readonly SetupDurableAtomicWriter _writer;
    private readonly TimeProvider _timeProvider;

    public SetupApplyEngine(
        ISetupFileSystem fileSystem,
        SetupHostDockerAdapter adapter,
        TimeProvider? timeProvider = null)
    {
        _fileSystem = fileSystem ?? throw new ArgumentNullException(nameof(fileSystem));
        _adapter = adapter ?? throw new ArgumentNullException(nameof(adapter));
        _writer = new SetupDurableAtomicWriter(_fileSystem);
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public async Task<SetupApplyResult> ApplyAsync(
        TrustedSetupHostLayout layout,
        string candidateBundleId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(layout);

        try
        {
            return await ApplyCoreAsync(layout, candidateBundleId, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            return Fail(
                SetupApplyResultCode.CancelledBeforeActivation,
                SetupManagedDeploymentState.NoManaged,
                "Apply was cancelled before activation.",
                reasonCode: "cancelled");
        }
        catch (Exception)
        {
            return Fail(
                SetupApplyResultCode.FailedUnexpected,
                SetupManagedDeploymentState.NeedsIntervention,
                "Apply failed unexpectedly.",
                actionCode: SetupApplyActionCode.ManualInterventionRequired,
                reasonCode: "unexpected");
        }
    }

    public async Task<SetupApplyResult> RecoverAsync(
        TrustedSetupHostLayout layout,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(layout);

        try
        {
            return await RecoverCoreAsync(layout, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            return Fail(
                SetupApplyResultCode.RecoveryRequired,
                SetupManagedDeploymentState.RecoveryRequired,
                "Recovery was cancelled; durable state still requires recovery.",
                reasonCode: "cancelled");
        }
        catch (Exception)
        {
            return Fail(
                SetupApplyResultCode.FailedUnexpected,
                SetupManagedDeploymentState.NeedsIntervention,
                "Recovery failed unexpectedly.",
                actionCode: SetupApplyActionCode.ManualInterventionRequired,
                reasonCode: "unexpected");
        }
    }

    // ---------------------------------------------------------------- apply

    private async Task<SetupApplyResult> ApplyCoreAsync(
        TrustedSetupHostLayout layout,
        string candidateBundleId,
        CancellationToken cancellationToken)
    {
        if (!SetupActivePointer.IsSafeBundleId(candidateBundleId))
        {
            return Fail(
                SetupApplyResultCode.FailedUnexpected,
                SetupManagedDeploymentState.NoManaged,
                "Candidate bundle id is invalid.",
                reasonCode: "candidate_bundle_id_invalid");
        }

        // Step 1: durable state is read before any Docker work so a pending transaction is never
        // overwritten by a fresh apply.
        var stateRead = ReadDurableState(layout, out var state);
        if (stateRead is not null)
        {
            return stateRead;
        }

        if (state.TransactionStamp is not null)
        {
            return state.TransactionStamp.Terminal
                ? Fail(
                    SetupApplyResultCode.NeedsIntervention,
                    SetupManagedDeploymentState.NeedsIntervention,
                    "A previous apply ended in a terminal state that requires operator review.",
                    actionCode: SetupApplyActionCode.ManualInterventionRequired,
                    reasonCode: state.TransactionStamp.ReasonCode ?? "terminal_transaction_present",
                    persistentSideEffectMayRemain: state.TransactionStamp.PersistentSideEffectMayRemain,
                    persistentSideEffectKind: state.TransactionStamp.PersistentSideEffectKind)
                : Fail(
                    SetupApplyResultCode.RecoveryRequired,
                    SetupManagedDeploymentState.RecoveryRequired,
                    "An interrupted apply transaction must be recovered before applying again.",
                    reasonCode: "transaction_in_progress",
                    persistentSideEffectMayRemain: state.TransactionStamp.PersistentSideEffectMayRemain,
                    persistentSideEffectKind: state.TransactionStamp.PersistentSideEffectKind);
        }

        var isFresh = state.Active is null;
        var preFailureCode = isFresh
            ? SetupApplyResultCode.FreshApplyFailed
            : SetupApplyResultCode.IneligibleExistingActive;
        var preFailureState = isFresh
            ? SetupManagedDeploymentState.NoManaged
            : SetupManagedDeploymentState.Active;

        // Step 2: candidate host at-rest integrity (no Docker, no ACTIVE change).
        var candidateValidation = SetupBundleStaticValidator.TryValidateFinalizedBundle(
            _fileSystem,
            layout,
            candidateBundleId,
            out var candidateRecorded,
            out var candidateHostAtRest);
        if (!candidateValidation.IsSuccess || candidateRecorded is null)
        {
            return Fail(
                preFailureCode,
                preFailureState,
                "Candidate bundle failed host at-rest validation.",
                reasonCode: candidateValidation.Code);
        }

        if (cancellationToken.IsCancellationRequested)
        {
            return CancelledBeforeActivation();
        }

        // Step 3: Docker preflight and the single APPLY.lock session for the whole transaction.
        var (probeResult, binding) = await _adapter.CheckDockerAsync(cancellationToken);
        if (!probeResult.IsSuccess || binding is null)
        {
            return Fail(preFailureCode, preFailureState, "Docker preflight failed.", reasonCode: probeResult.Code);
        }

        var (sessionResult, session) = await _adapter.AcquireSessionAsync(layout, binding, cancellationToken);
        if (!sessionResult.IsSuccess || session is null)
        {
            return sessionResult.Code == SetupDockerResultCode.ConcurrentSetupRejected
                ? Fail(
                    SetupApplyResultCode.ConcurrentApplyRejected,
                    preFailureState,
                    "Another setup apply session is already running.",
                    reasonCode: sessionResult.Code)
                : Fail(preFailureCode, preFailureState, "Setup apply session could not be acquired.", reasonCode: sessionResult.Code);
        }

        await using (session)
        {
            // Step 4: pin external inputs once (checkpoint 1) and clear verifier residue.
            var pin = await _adapter.PinExternalInputsAsync(session, cancellationToken);
            if (!pin.IsSuccess || session.ExternalInputs is null)
            {
                return Fail(preFailureCode, preFailureState, "External inputs could not be pinned.", reasonCode: pin.Code);
            }

            var purge = await _adapter.PurgeStaleMountVerifiersAsync(session, cancellationToken);
            if (!purge.IsSuccess)
            {
                return Fail(
                    preFailureCode,
                    preFailureState,
                    "Managed verifier temp directory is not in a safe state.",
                    actionCode: SetupApplyActionCode.UnsafeVerifierResidue,
                    reasonCode: purge.Code);
            }

            var image = await _adapter.EnsurePinnedImageAvailableAsync(session, cancellationToken);
            if (!image.IsSuccess)
            {
                return Fail(preFailureCode, preFailureState, "Pinned image is not available.", reasonCode: image.Code);
            }

            // Step 5: migration decision before activation.
            var decision = await DecideMigrationAsync(
                layout,
                session,
                state.Active,
                candidateRecorded,
                cancellationToken);
            switch (decision.Kind)
            {
                case SetupMigrationDecisionKind.UpgradeRequired:
                    return Fail(
                        SetupApplyResultCode.UpgradeRequired,
                        preFailureState,
                        decision.Message,
                        decision.ActionCode,
                        decision.ReasonCode);
                case SetupMigrationDecisionKind.NeedsIntervention:
                    return Fail(
                        SetupApplyResultCode.NeedsIntervention,
                        SetupManagedDeploymentState.NeedsIntervention,
                        decision.Message,
                        decision.ActionCode ?? SetupApplyActionCode.ManualInterventionRequired,
                        decision.ReasonCode);
            }

            var migrationRequired = decision.Kind == SetupMigrationDecisionKind.MigrationRequired;

            var targetGeneration = (state.Active?.ActivationGeneration ?? 0) + 1;
            var candidatePointer = new SetupActivePointer
            {
                SchemaVersion = SetupActivePointer.CurrentSchemaVersion,
                BundleId = candidateBundleId,
                ActivationGeneration = targetGeneration,
            };

            if (cancellationToken.IsCancellationRequested)
            {
                return CancelledBeforeActivation();
            }

            // Step 6: durable Prepared stamp. Recovery treats Prepared as "no ACTIVE change yet".
            var stamp = new SetupTransactionStamp
            {
                SchemaVersion = SetupTransactionStamp.CurrentSchemaVersion,
                Kind = SetupTransactionKind.Apply,
                Phase = SetupTransactionPhase.Prepared,
                Terminal = false,
                CandidateBundleId = candidateBundleId,
                TargetActivationGeneration = targetGeneration,
                PreviousBundleId = state.Active?.BundleId,
                PreviousActivationGeneration = state.Active?.ActivationGeneration,
                PersistentSideEffectMayRemain = false,
                PersistentSideEffectKind = SetupPersistentSideEffectKind.None,
                StartedAt = Timestamp(),
            };
            var stampWrite = WriteStamp(layout, stamp);
            if (!stampWrite.IsSuccess)
            {
                return Fail(preFailureCode, preFailureState, "Transaction stamp could not be written.", reasonCode: stampWrite.Code);
            }

            if (cancellationToken.IsCancellationRequested)
            {
                _ = DeleteStamp(layout);
                return CancelledBeforeActivation();
            }

            // Step 7: activation. PREVIOUS is durable before ACTIVE so rollback always has a target.
            stamp = stamp with { Phase = SetupTransactionPhase.ActiveSwitchPending };
            var switchStamp = WriteStamp(layout, stamp);
            if (!switchStamp.IsSuccess)
            {
                _ = DeleteStamp(layout);
                return Fail(preFailureCode, preFailureState, "Transaction stamp could not be advanced.", reasonCode: switchStamp.Code);
            }

            if (state.Active is not null)
            {
                var previousWrite = WritePointer(layout, layout.PreviousPointerPath, state.Active);
                if (!previousWrite.IsSuccess)
                {
                    _ = DeleteStamp(layout);
                    return Fail(preFailureCode, preFailureState, "Previous pointer could not be written.", reasonCode: previousWrite.Code);
                }
            }

            var activeWrite = WritePointer(layout, layout.ActivePointerPath, candidatePointer);
            if (!activeWrite.IsSuccess)
            {
                _ = DeleteStamp(layout);
                return Fail(preFailureCode, preFailureState, "ACTIVE pointer could not be switched.", reasonCode: activeWrite.Code);
            }

            // Any committed verification now describes a generation that is no longer ACTIVE.
            _ = InvalidateVerificationRecord(layout, candidateBundleId, targetGeneration);

            var context = new ApplyContext(
                layout,
                session,
                stamp,
                candidatePointer,
                state.Active,
                candidateHostAtRest,
                candidateRecorded,
                migrationRequired);

            return await RunPostActivationAsync(context, cancellationToken);
        }
    }

    private async Task<SetupApplyResult> RunPostActivationAsync(
        ApplyContext context,
        CancellationToken cancellationToken)
    {
        var layout = context.Layout;
        var session = context.Session;

        // Checkpoint 2: external inputs must still be the ones this generation was planned against.
        var checkpoint2 = await _adapter.VerifyExternalInputsUnchangedAsync(session, cancellationToken);
        if (!checkpoint2.IsSuccess)
        {
            return await RollbackAsync(context, "external_input_changed_after_activation", migrationAttempted: false);
        }

        var compose = await _adapter.ComposeExpectedActiveInputAsync(session, context.Candidate, cancellationToken);
        if (!compose.IsSuccess)
        {
            return await RollbackAsync(context, "compose_pin_failed", migrationAttempted: false);
        }

        _ = AdvancePhase(context, SetupTransactionPhase.CandidateComposeValidating, persistentSideEffect: false);
        var validate = await _adapter.ValidateComposeAsync(session, cancellationToken);
        if (!validate.IsSuccess)
        {
            return await RollbackAsync(context, "compose_validation_failed", migrationAttempted: false);
        }

        var migrationAttempted = false;
        if (context.MigrationRequired)
        {
            _ = AdvancePhase(context, SetupTransactionPhase.MigrationPending, persistentSideEffect: false);
            _ = AdvancePhase(context, SetupTransactionPhase.Migrating, persistentSideEffect: true);
            migrationAttempted = true;
            var migrate = await _adapter.RunMigrationAsync(session, cancellationToken);
            if (!migrate.IsSuccess)
            {
                return await RollbackAsync(context, "migration_failed", migrationAttempted: true);
            }
        }

        _ = AdvancePhase(context, SetupTransactionPhase.Recreating, migrationAttempted);
        var recreate = await _adapter.StartOrRecreateMailerAsync(session, cancellationToken);
        if (!recreate.IsSuccess)
        {
            return await RollbackAsync(context, "recreate_failed", migrationAttempted);
        }

        _ = AdvancePhase(context, SetupTransactionPhase.ReadinessChecking, migrationAttempted);
        var readiness = await _adapter.AwaitMailerHealthyAsync(session, cancellationToken);
        if (!readiness.IsSuccess)
        {
            return await RollbackAsync(context, "readiness_failed", migrationAttempted);
        }

        // Checkpoint 3: last external comparison before the verification record is committed.
        var checkpoint3 = await _adapter.VerifyExternalInputsUnchangedAsync(session, cancellationToken);
        if (!checkpoint3.IsSuccess)
        {
            return await RollbackAsync(context, "external_input_changed_before_verification", migrationAttempted);
        }

        _ = AdvancePhase(context, SetupTransactionPhase.Inspecting, migrationAttempted);
        if (!SetupMountVerifierFactory.TryCreate(
                _fileSystem,
                layout,
                context.Candidate.BundleId,
                _timeProvider.GetUtcNow(),
                out var verifier,
                out var verifierResult)
            || verifier is null)
        {
            _ = verifierResult;
            return await RollbackAsync(context, "mount_verifier_unavailable", migrationAttempted);
        }

        var inspection = await _adapter.RunEffectiveInspectionAsync(session, verifier, cancellationToken);
        if (!inspection.IsSuccess || inspection.Inspection is null)
        {
            return await RollbackAsync(context, "effective_inspection_failed", migrationAttempted);
        }

        var mountAttestation = MapAttestation(inspection.Inspection.MountAttestation.Result);
        var bundleIntegrity = SetupIntegrityMerger.Merge(context.CandidateHostAtRest, mountAttestation);
        var fingerprintComparison = inspection.Inspection.Effective.FingerprintsMatchRecorded switch
        {
            true => SetupVerificationRecord.FingerprintMatched,
            false => SetupVerificationRecord.FingerprintMismatch,
            _ => SetupVerificationRecord.FingerprintNotEvaluated,
        };

        if (!string.Equals(bundleIntegrity, SetupIntegrityMerger.Matched, StringComparison.Ordinal))
        {
            _ = WriteVerificationRecord(
                context,
                SetupVerificationRecord.StatusInvalidated,
                fingerprintComparison,
                mountAttestation,
                bundleIntegrity,
                readiness: SetupVerificationRecord.ReadinessPassed,
                runtimeIdentityBinding: SetupRuntimeIdentityBindingResult.Missing,
                committedAt: null);
            return await RollbackAsync(context, "bundle_integrity_mismatch", migrationAttempted);
        }

        if (!string.Equals(fingerprintComparison, SetupVerificationRecord.FingerprintMatched, StringComparison.Ordinal))
        {
            _ = WriteVerificationRecord(
                context,
                SetupVerificationRecord.StatusInvalidated,
                fingerprintComparison,
                mountAttestation,
                bundleIntegrity,
                readiness: SetupVerificationRecord.ReadinessPassed,
                runtimeIdentityBinding: SetupRuntimeIdentityBindingResult.Missing,
                committedAt: null);
            return await RollbackAsync(context, "fingerprint_mismatch", migrationAttempted);
        }

        _ = AdvancePhase(context, SetupTransactionPhase.BindingPending, migrationAttempted);
        var bindingWrite = WriteRuntimeIdentityBinding(context);
        if (!bindingWrite.IsSuccess)
        {
            return await RollbackAsync(context, "runtime_identity_binding_failed", migrationAttempted);
        }

        _ = AdvancePhase(context, SetupTransactionPhase.VerificationPending, migrationAttempted);
        var recordWrite = WriteVerificationRecord(
            context,
            SetupVerificationRecord.StatusCommitted,
            fingerprintComparison,
            mountAttestation,
            bundleIntegrity,
            readiness: SetupVerificationRecord.ReadinessPassed,
            runtimeIdentityBinding: SetupRuntimeIdentityBindingResult.Matched,
            committedAt: Timestamp());
        if (!recordWrite.IsSuccess)
        {
            return await RollbackAsync(context, "verification_record_failed", migrationAttempted);
        }

        _ = AdvancePhase(context, SetupTransactionPhase.VerificationCommitted, migrationAttempted);
        var stampDelete = DeleteStamp(layout);
        if (!stampDelete.IsSuccess)
        {
            return Fail(
                SetupApplyResultCode.NeedsIntervention,
                SetupManagedDeploymentState.NeedsIntervention,
                "Apply completed but the transaction stamp could not be cleared.",
                actionCode: SetupApplyActionCode.ManualInterventionRequired,
                reasonCode: stampDelete.Code,
                bundleId: context.Candidate.BundleId,
                activationGeneration: context.Candidate.ActivationGeneration,
                configurationApplied: true,
                verificationCommitted: true);
        }

        return SetupApplyResult.Create(
            SetupApplyResultCode.ApplySucceeded,
            SetupManagedDeploymentState.Active,
            "Managed configuration applied and verification committed.",
            actionCode: SetupApplyActionCode.CompleteSendReadyEvaluation,
            reasonCode: null,
            bundleId: context.Candidate.BundleId,
            activationGeneration: context.Candidate.ActivationGeneration,
            configurationApplied: true,
            verificationCommitted: true);
    }

    // ------------------------------------------------------------- rollback

    private async Task<SetupApplyResult> RollbackAsync(
        ApplyContext context,
        string reasonCode,
        bool migrationAttempted)
    {
        // Rollback never inherits operator cancellation: it gets its own bounded budget so a Ctrl+C
        // after activation cannot leave ACTIVE pointing at an unverified generation.
        using var rollbackCts = new CancellationTokenSource(RollbackBudget);
        var rollbackToken = rollbackCts.Token;

        var layout = context.Layout;
        var session = context.Session;
        var sideEffectKind = migrationAttempted
            ? SetupPersistentSideEffectKind.DatabaseMigration
            : SetupPersistentSideEffectKind.None;

        var stamp = context.Stamp with
        {
            Kind = SetupTransactionKind.Rollback,
            Phase = SetupTransactionPhase.RollbackPending,
            Terminal = false,
            ReasonCode = reasonCode,
            PersistentSideEffectMayRemain = migrationAttempted,
            PersistentSideEffectKind = sideEffectKind,
        };
        context.Stamp = stamp;
        _ = WriteStamp(layout, stamp);

        // 1. Invalidate any verification claim for the candidate generation.
        _ = InvalidateVerificationRecord(layout, context.Candidate.BundleId, context.Candidate.ActivationGeneration);

        // 2. Stop the candidate container while its compose pin is still valid.
        if (session.ComposeInputs is not null)
        {
            _ = await _adapter.StopFailedMailerAsync(session, rollbackToken);
        }

        // 3. Restore ACTIVE. Fresh applies remove it; existing deployments get the previous bundle
        //    under a new, higher generation so generations stay monotonic.
        if (context.Previous is null)
        {
            var remove = _writer.TryDurableDelete(layout.ManagedRoot, layout.ActivePointerPath);
            if (!remove.IsSuccess)
            {
                return TerminalRollbackFailure(context, reasonCode, migrationAttempted, sideEffectKind, remove.Code);
            }

            var freshStampDelete = DeleteStamp(layout);
            if (!freshStampDelete.IsSuccess)
            {
                return TerminalRollbackFailure(context, reasonCode, migrationAttempted, sideEffectKind, freshStampDelete.Code);
            }

            return Fail(
                SetupApplyResultCode.FreshApplyFailed,
                SetupManagedDeploymentState.NoManaged,
                "Fresh Managed apply failed; no Managed deployment is active.",
                actionCode: migrationAttempted ? SetupApplyActionCode.ReviewDatabaseFiles : null,
                reasonCode: reasonCode,
                configRollbackStatus: SetupConfigRollbackStatus.Succeeded,
                persistentSideEffectMayRemain: migrationAttempted,
                persistentSideEffectKind: sideEffectKind);
        }

        var restoredPointer = new SetupActivePointer
        {
            SchemaVersion = SetupActivePointer.CurrentSchemaVersion,
            BundleId = context.Previous.BundleId,
            ActivationGeneration = context.Candidate.ActivationGeneration + 1,
        };

        var restore = WritePointer(layout, layout.ActivePointerPath, restoredPointer);
        if (!restore.IsSuccess)
        {
            return TerminalRollbackFailure(context, reasonCode, migrationAttempted, sideEffectKind, restore.Code);
        }

        // 4. Recompose for the restored generation and bring the previous container back.
        var recompose = await _adapter.ComposeExpectedActiveInputAsync(session, restoredPointer, rollbackToken);
        if (!recompose.IsSuccess)
        {
            return TerminalRollbackFailure(context, reasonCode, migrationAttempted, sideEffectKind, recompose.Code);
        }

        var validate = await _adapter.ValidateComposeAsync(session, rollbackToken);
        if (!validate.IsSuccess)
        {
            return TerminalRollbackFailure(context, reasonCode, migrationAttempted, sideEffectKind, validate.Code);
        }

        var recreate = await _adapter.StartOrRecreateMailerAsync(session, rollbackToken);
        if (!recreate.IsSuccess)
        {
            return TerminalRollbackFailure(context, reasonCode, migrationAttempted, sideEffectKind, recreate.Code);
        }

        var readiness = await _adapter.AwaitMailerHealthyAsync(session, rollbackToken);
        if (!readiness.IsSuccess)
        {
            return TerminalRollbackFailure(context, reasonCode, migrationAttempted, sideEffectKind, readiness.Code);
        }

        var stampDelete = DeleteStamp(layout);
        if (!stampDelete.IsSuccess)
        {
            return TerminalRollbackFailure(context, reasonCode, migrationAttempted, sideEffectKind, stampDelete.Code);
        }

        // A migration that already ran is not undone by restoring the pointer.
        if (migrationAttempted)
        {
            return Fail(
                SetupApplyResultCode.ApplyFailedRollbackSucceeded,
                SetupManagedDeploymentState.NeedsIntervention,
                "Apply failed and configuration rolled back, but a database migration may have persisted.",
                actionCode: SetupApplyActionCode.ReviewDatabaseSchema,
                reasonCode: reasonCode,
                bundleId: restoredPointer.BundleId,
                activationGeneration: restoredPointer.ActivationGeneration,
                configRollbackStatus: SetupConfigRollbackStatus.Succeeded,
                persistentSideEffectMayRemain: true,
                persistentSideEffectKind: sideEffectKind);
        }

        return Fail(
            SetupApplyResultCode.ApplyFailedRollbackSucceeded,
            SetupManagedDeploymentState.Active,
            "Apply failed and the previous Managed configuration was restored.",
            reasonCode: reasonCode,
            bundleId: restoredPointer.BundleId,
            activationGeneration: restoredPointer.ActivationGeneration,
            configRollbackStatus: SetupConfigRollbackStatus.Succeeded);
    }

    private SetupApplyResult TerminalRollbackFailure(
        ApplyContext context,
        string reasonCode,
        bool migrationAttempted,
        string sideEffectKind,
        string? rollbackFailureCode)
    {
        // Leave a terminal stamp so a later apply refuses and recovery reports intervention.
        var terminal = context.Stamp with
        {
            Kind = SetupTransactionKind.Rollback,
            Phase = SetupTransactionPhase.RollbackPending,
            Terminal = true,
            ReasonCode = rollbackFailureCode ?? reasonCode,
            PersistentSideEffectMayRemain = migrationAttempted,
            PersistentSideEffectKind = sideEffectKind,
        };
        context.Stamp = terminal;
        _ = WriteStamp(context.Layout, terminal);

        return Fail(
            SetupApplyResultCode.ApplyFailedRollbackFailed,
            SetupManagedDeploymentState.NeedsIntervention,
            "Apply failed and rollback could not complete.",
            actionCode: SetupApplyActionCode.ManualInterventionRequired,
            reasonCode: reasonCode,
            configRollbackStatus: SetupConfigRollbackStatus.Failed,
            persistentSideEffectMayRemain: migrationAttempted,
            persistentSideEffectKind: sideEffectKind);
    }

    // ------------------------------------------------------------- recovery

    private async Task<SetupApplyResult> RecoverCoreAsync(
        TrustedSetupHostLayout layout,
        CancellationToken cancellationToken)
    {
        var stateRead = ReadDurableState(layout, out var state);
        if (stateRead is not null)
        {
            return stateRead;
        }

        var stamp = state.TransactionStamp;

        // Phase A: nothing in flight  Eclassify the durable state only.
        if (stamp is null)
        {
            if (state.Active is null)
            {
                return Fail(
                    SetupApplyResultCode.RollbackSucceeded,
                    SetupManagedDeploymentState.NoManaged,
                    "No Managed deployment is active and no transaction is in flight.",
                    configRollbackStatus: SetupConfigRollbackStatus.NotApplicable);
            }

            var record = state.VerificationRecord;
            if (record is not null
                && record.IsCommittedSuccess
                && string.Equals(record.BundleId, state.Active.BundleId, StringComparison.Ordinal)
                && record.ActivationGeneration == state.Active.ActivationGeneration)
            {
                return SetupApplyResult.Create(
                    SetupApplyResultCode.ApplySucceeded,
                    SetupManagedDeploymentState.Active,
                    "Managed deployment state is consistent and verification is committed.",
                    actionCode: SetupApplyActionCode.CompleteSendReadyEvaluation,
                    bundleId: state.Active.BundleId,
                    activationGeneration: state.Active.ActivationGeneration,
                    configurationApplied: true,
                    verificationCommitted: true);
            }

            return Fail(
                SetupApplyResultCode.NeedsIntervention,
                SetupManagedDeploymentState.NeedsIntervention,
                "ACTIVE is set but no committed verification record matches it.",
                actionCode: SetupApplyActionCode.ManualInterventionRequired,
                reasonCode: "verification_record_missing",
                bundleId: state.Active.BundleId,
                activationGeneration: state.Active.ActivationGeneration,
                configurationApplied: true);
        }

        // Phase B: a terminal stamp always requires a human.
        if (stamp.Terminal)
        {
            return Fail(
                SetupApplyResultCode.NeedsIntervention,
                SetupManagedDeploymentState.NeedsIntervention,
                "The previous transaction ended in a terminal state that requires operator review.",
                actionCode: SetupApplyActionCode.ManualInterventionRequired,
                reasonCode: stamp.ReasonCode ?? "terminal_transaction_present",
                configRollbackStatus: SetupConfigRollbackStatus.Failed,
                persistentSideEffectMayRemain: stamp.PersistentSideEffectMayRemain,
                persistentSideEffectKind: stamp.PersistentSideEffectKind);
        }

        // Phase C: crash before the ACTIVE switch  Edrop the stamp and keep the current state.
        if (string.Equals(stamp.Phase, SetupTransactionPhase.Prepared, StringComparison.Ordinal))
        {
            var delete = DeleteStamp(layout);
            if (!delete.IsSuccess)
            {
                return Fail(
                    SetupApplyResultCode.NeedsIntervention,
                    SetupManagedDeploymentState.NeedsIntervention,
                    "Prepared transaction stamp could not be cleared.",
                    actionCode: SetupApplyActionCode.ManualInterventionRequired,
                    reasonCode: delete.Code);
            }

            return Fail(
                SetupApplyResultCode.RollbackSucceeded,
                state.Active is null
                    ? SetupManagedDeploymentState.NoManaged
                    : SetupManagedDeploymentState.Active,
                "Interrupted apply had not activated; no configuration change was applied.",
                reasonCode: stamp.ReasonCode,
                bundleId: state.Active?.BundleId,
                activationGeneration: state.Active?.ActivationGeneration,
                configRollbackStatus: SetupConfigRollbackStatus.NotApplicable);
        }

        // Phase D: crash after verification was committed  Ethe apply actually finished.
        if (string.Equals(stamp.Phase, SetupTransactionPhase.VerificationCommitted, StringComparison.Ordinal))
        {
            var delete = DeleteStamp(layout);
            if (!delete.IsSuccess)
            {
                return Fail(
                    SetupApplyResultCode.NeedsIntervention,
                    SetupManagedDeploymentState.NeedsIntervention,
                    "Completed transaction stamp could not be cleared.",
                    actionCode: SetupApplyActionCode.ManualInterventionRequired,
                    reasonCode: delete.Code);
            }

            return SetupApplyResult.Create(
                SetupApplyResultCode.ApplySucceeded,
                SetupManagedDeploymentState.Active,
                "Interrupted apply had already committed verification.",
                actionCode: SetupApplyActionCode.CompleteSendReadyEvaluation,
                bundleId: state.Active?.BundleId,
                activationGeneration: state.Active?.ActivationGeneration,
                configurationApplied: true,
                verificationCommitted: true);
        }

        // Phase E: crash between activation and verification  Eroll the configuration back.
        return await RecoverRollbackAsync(layout, state, stamp, cancellationToken);
    }

    private async Task<SetupApplyResult> RecoverRollbackAsync(
        TrustedSetupHostLayout layout,
        DurableState state,
        SetupTransactionStamp stamp,
        CancellationToken cancellationToken)
    {
        var (probeResult, binding) = await _adapter.CheckDockerAsync(cancellationToken);
        if (!probeResult.IsSuccess || binding is null)
        {
            return Fail(
                SetupApplyResultCode.RecoveryRequired,
                SetupManagedDeploymentState.RecoveryRequired,
                "Docker preflight failed; recovery is still required.",
                reasonCode: probeResult.Code,
                persistentSideEffectMayRemain: stamp.PersistentSideEffectMayRemain,
                persistentSideEffectKind: stamp.PersistentSideEffectKind);
        }

        var (sessionResult, session) = await _adapter.AcquireSessionAsync(layout, binding, cancellationToken);
        if (!sessionResult.IsSuccess || session is null)
        {
            return sessionResult.Code == SetupDockerResultCode.ConcurrentSetupRejected
                ? Fail(
                    SetupApplyResultCode.ConcurrentApplyRejected,
                    SetupManagedDeploymentState.RecoveryRequired,
                    "Another setup apply session is already running.",
                    reasonCode: sessionResult.Code)
                : Fail(
                    SetupApplyResultCode.RecoveryRequired,
                    SetupManagedDeploymentState.RecoveryRequired,
                    "Setup apply session could not be acquired; recovery is still required.",
                    reasonCode: sessionResult.Code);
        }

        await using (session)
        {
            var pin = await _adapter.PinExternalInputsAsync(session, cancellationToken);
            if (!pin.IsSuccess)
            {
                return Fail(
                    SetupApplyResultCode.RecoveryRequired,
                    SetupManagedDeploymentState.RecoveryRequired,
                    "External inputs could not be pinned; recovery is still required.",
                    reasonCode: pin.Code,
                    persistentSideEffectMayRemain: stamp.PersistentSideEffectMayRemain,
                    persistentSideEffectKind: stamp.PersistentSideEffectKind);
            }

            _ = await _adapter.PurgeStaleMountVerifiersAsync(session, cancellationToken);

            var candidatePointer = new SetupActivePointer
            {
                SchemaVersion = SetupActivePointer.CurrentSchemaVersion,
                BundleId = stamp.CandidateBundleId,
                ActivationGeneration = stamp.TargetActivationGeneration,
            };

            // The interrupted candidate may still be running; stop it when it is what ACTIVE names.
            if (state.Active is not null
                && string.Equals(state.Active.BundleId, stamp.CandidateBundleId, StringComparison.Ordinal)
                && state.Active.ActivationGeneration == stamp.TargetActivationGeneration)
            {
                var candidateCompose = await _adapter.ComposeExpectedActiveInputAsync(
                    session,
                    candidatePointer,
                    cancellationToken);
                if (candidateCompose.IsSuccess)
                {
                    _ = await _adapter.StopFailedMailerAsync(session, cancellationToken);
                }
            }

            var previous = state.Previous;
            if (previous is null && stamp.PreviousBundleId is not null)
            {
                return Fail(
                    SetupApplyResultCode.NeedsIntervention,
                    SetupManagedDeploymentState.NeedsIntervention,
                    "The interrupted transaction expected a previous generation that is not recorded.",
                    actionCode: SetupApplyActionCode.ManualInterventionRequired,
                    reasonCode: "previous_pointer_missing",
                    persistentSideEffectMayRemain: stamp.PersistentSideEffectMayRemain,
                    persistentSideEffectKind: stamp.PersistentSideEffectKind);
            }

            var context = new ApplyContext(
                layout,
                session,
                stamp,
                candidatePointer,
                previous,
                SetupIntegrityMerger.NotVerified,
                recorded: null,
                migrationRequired: false);

            var rollback = await RollbackAsync(
                context,
                stamp.ReasonCode ?? "recovered_interrupted_apply",
                stamp.PersistentSideEffectMayRemain);

            return MapRecoveryResult(rollback);
        }
    }

    private static SetupApplyResult MapRecoveryResult(SetupApplyResult rollback) => rollback.Code switch
    {
        SetupApplyResultCode.ApplyFailedRollbackSucceeded when rollback.PersistentSideEffectMayRemain =>
            Rewrite(rollback, SetupApplyResultCode.NeedsIntervention, SetupManagedDeploymentState.NeedsIntervention),
        SetupApplyResultCode.ApplyFailedRollbackSucceeded =>
            Rewrite(rollback, SetupApplyResultCode.RollbackSucceeded, SetupManagedDeploymentState.Active),
        SetupApplyResultCode.FreshApplyFailed =>
            Rewrite(rollback, SetupApplyResultCode.RollbackSucceeded, SetupManagedDeploymentState.NoManaged),
        SetupApplyResultCode.ApplyFailedRollbackFailed =>
            Rewrite(rollback, SetupApplyResultCode.NeedsIntervention, SetupManagedDeploymentState.NeedsIntervention),
        _ => rollback,
    };

    private static SetupApplyResult Rewrite(
        SetupApplyResult source,
        string code,
        SetupManagedDeploymentState deploymentState) =>
        SetupApplyResult.Create(
            code,
            deploymentState,
            source.Message,
            source.ActionCode,
            source.ReasonCode,
            source.BundleId,
            source.ActivationGeneration,
            source.ConfigurationApplied,
            source.VerificationCommitted,
            source.ConfigRollbackStatus,
            source.PersistentSideEffectMayRemain,
            source.PersistentSideEffectKind);

    // ------------------------------------------------------------ decisions

    private async Task<SetupMigrationDecision> DecideMigrationAsync(
        TrustedSetupHostLayout layout,
        SetupHostDockerSession session,
        SetupActivePointer? active,
        SetupRecordedMetadata candidateRecorded,
        CancellationToken cancellationToken)
    {
        if (active is null)
        {
            return SetupDatabaseFileProbe.ClassifyFreshHostDatabase(_fileSystem, session.ExternalInputs!);
        }

        var activeValidation = SetupBundleStaticValidator.TryValidateFinalizedBundle(
            _fileSystem,
            layout,
            active.BundleId,
            out var activeRecorded,
            out _);
        if (!activeValidation.IsSuccess || activeRecorded is null)
        {
            return new SetupMigrationDecision
            {
                Kind = SetupMigrationDecisionKind.NeedsIntervention,
                ActionCode = SetupApplyActionCode.ManualInterventionRequired,
                ReasonCode = "active_bundle_invalid",
                Message = "The existing ACTIVE bundle failed host at-rest validation.",
            };
        }

        var compose = await _adapter.ComposeCurrentActiveInputAsync(session, cancellationToken);
        if (!compose.IsSuccess)
        {
            return new SetupMigrationDecision
            {
                Kind = SetupMigrationDecisionKind.NeedsIntervention,
                ActionCode = SetupApplyActionCode.ManualInterventionRequired,
                ReasonCode = compose.Code,
                Message = "The existing ACTIVE environment could not be composed for inspection.",
            };
        }

        var status = await _adapter.InspectMigrationStatusAsync(session, cancellationToken);
        if (!status.IsSuccess || status.MigrationStatus is null)
        {
            return new SetupMigrationDecision
            {
                Kind = SetupMigrationDecisionKind.NeedsIntervention,
                ActionCode = SetupApplyActionCode.ReviewDatabaseSchema,
                ReasonCode = status.Code,
                Message = "Existing Managed deployment schema could not be classified safely.",
            };
        }

        return SetupDatabaseFileProbe.ClassifyExistingFromStatus(
            status.MigrationStatus.Classification,
            ImageReference(activeRecorded),
            ImageReference(candidateRecorded));
    }

    private static string ImageReference(SetupRecordedMetadata recorded) =>
        (recorded.ImageRepository ?? string.Empty) + ":" + (recorded.ImageTag ?? string.Empty);

    private static string MapAttestation(string? result) => result switch
    {
        SetupInspectIntegrityResult.Matched => SetupIntegrityMerger.Matched,
        SetupInspectIntegrityResult.Mismatch => SetupIntegrityMerger.Mismatch,
        SetupInspectIntegrityResult.NotManaged => SetupIntegrityMerger.NotManaged,
        _ => SetupIntegrityMerger.NotVerified,
    };

    // ------------------------------------------------------- durable state

    private SetupApplyResult? ReadDurableState(TrustedSetupHostLayout layout, out DurableState state)
    {
        state = new DurableState();

        try
        {
            if (!_fileSystem.DirectoryExists(layout.ManagedRoot))
            {
                return Fail(
                    SetupApplyResultCode.FreshApplyFailed,
                    SetupManagedDeploymentState.NoManaged,
                    "Managed root does not exist.",
                    reasonCode: "managed_root_missing");
            }

            if (!TryReadPointer(layout, layout.ActivePointerPath, out var active, out var activeFailure))
            {
                return activeFailure;
            }

            if (!TryReadPointer(layout, layout.PreviousPointerPath, out var previous, out var previousFailure))
            {
                return previousFailure;
            }

            state = new DurableState
            {
                Active = active,
                Previous = previous,
                TransactionStamp = ReadStamp(layout),
                VerificationRecord = ReadVerificationRecord(layout),
            };
            return null;
        }
        catch (IOException)
        {
            return Fail(
                SetupApplyResultCode.FailedUnexpected,
                SetupManagedDeploymentState.NeedsIntervention,
                "Durable Managed state could not be read.",
                actionCode: SetupApplyActionCode.ManualInterventionRequired,
                reasonCode: "state_read_failed");
        }
        catch (UnauthorizedAccessException)
        {
            return Fail(
                SetupApplyResultCode.FailedUnexpected,
                SetupManagedDeploymentState.NeedsIntervention,
                "Durable Managed state could not be read.",
                actionCode: SetupApplyActionCode.ManualInterventionRequired,
                reasonCode: "state_read_failed");
        }
    }

    private bool TryReadPointer(
        TrustedSetupHostLayout layout,
        string path,
        out SetupActivePointer? pointer,
        out SetupApplyResult? failure)
    {
        pointer = null;
        failure = null;

        if (!_fileSystem.FileExists(path))
        {
            return true;
        }

        if (!SetupPathGuard.TryEnsurePathSafeUnderRoot(_fileSystem, layout.ManagedRoot, path, out _, out _)
            || SetupPathGuard.IsUnsafeLink(_fileSystem.InspectSymlinkOrReparsePoint(path)))
        {
            failure = Fail(
                SetupApplyResultCode.NeedsIntervention,
                SetupManagedDeploymentState.NeedsIntervention,
                "A Managed state pointer path was rejected.",
                actionCode: SetupApplyActionCode.ManualInterventionRequired,
                reasonCode: SetupDockerResultCode.UnsafePath);
            return false;
        }

        var text = Encoding.UTF8.GetString(_fileSystem.ReadAllBytes(path));
        if (!SetupActivePointer.TryParse(text, out var parsed) || parsed is null)
        {
            failure = Fail(
                SetupApplyResultCode.NeedsIntervention,
                SetupManagedDeploymentState.NeedsIntervention,
                "A Managed state pointer document is invalid.",
                actionCode: SetupApplyActionCode.ManualInterventionRequired,
                reasonCode: "pointer_document_invalid");
            return false;
        }

        pointer = parsed;
        return true;
    }

    private SetupTransactionStamp? ReadStamp(TrustedSetupHostLayout layout)
    {
        var path = layout.TransactionStampPath;
        if (!_fileSystem.FileExists(path))
        {
            return null;
        }

        try
        {
            var stamp = JsonSerializer.Deserialize(
                _fileSystem.ReadAllBytes(path),
                SetupApplyJsonContext.Default.SetupTransactionStamp);
            if (stamp is null
                || stamp.SchemaVersion != SetupTransactionStamp.CurrentSchemaVersion
                || !SetupActivePointer.IsSafeBundleId(stamp.CandidateBundleId))
            {
                return UnreadableStamp();
            }

            return stamp;
        }
        catch (JsonException)
        {
            return UnreadableStamp();
        }

        // A stamp we cannot trust still means "a transaction touched this deployment".
        static SetupTransactionStamp UnreadableStamp() => new()
        {
            SchemaVersion = SetupTransactionStamp.CurrentSchemaVersion,
            Kind = SetupTransactionKind.Apply,
            Phase = SetupTransactionPhase.RollbackPending,
            Terminal = true,
            ReasonCode = "transaction_stamp_unreadable",
            CandidateBundleId = "unknown",
            TargetActivationGeneration = 1,
            PersistentSideEffectMayRemain = true,
            PersistentSideEffectKind = SetupPersistentSideEffectKind.DatabaseMigration,
            StartedAt = string.Empty,
        };
    }

    private SetupVerificationRecord? ReadVerificationRecord(TrustedSetupHostLayout layout)
    {
        var path = layout.LastRecordPath;
        if (!_fileSystem.FileExists(path))
        {
            return null;
        }

        try
        {
            var record = JsonSerializer.Deserialize(
                _fileSystem.ReadAllBytes(path),
                SetupApplyJsonContext.Default.SetupVerificationRecord);
            return record is null || record.SchemaVersion != SetupVerificationRecord.CurrentSchemaVersion
                ? null
                : record;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private SetupDockerResult WritePointer(
        TrustedSetupHostLayout layout,
        string path,
        SetupActivePointer pointer) =>
        _writer.TryAtomicReplaceJson(
            layout.ManagedRoot,
            path,
            pointer,
            SetupApplyJsonContext.Default.SetupActivePointer);

    private SetupDockerResult WriteStamp(TrustedSetupHostLayout layout, SetupTransactionStamp stamp) =>
        _writer.TryAtomicReplaceJson(
            layout.ManagedRoot,
            layout.TransactionStampPath,
            stamp,
            SetupApplyJsonContext.Default.SetupTransactionStamp);

    private SetupDockerResult DeleteStamp(TrustedSetupHostLayout layout) =>
        _writer.TryDurableDelete(layout.ManagedRoot, layout.TransactionStampPath);

    private SetupDockerResult AdvancePhase(ApplyContext context, string phase, bool persistentSideEffect)
    {
        var kind = persistentSideEffect
            ? SetupPersistentSideEffectKind.DatabaseMigration
            : SetupPersistentSideEffectKind.None;
        context.Stamp = context.Stamp with
        {
            Phase = phase,
            PersistentSideEffectMayRemain = persistentSideEffect,
            PersistentSideEffectKind = kind,
        };
        return WriteStamp(context.Layout, context.Stamp);
    }

    /// <summary>
    /// Drops any verification claim for <paramref name="activationGeneration"/> by writing a
    /// nothing-verified invalidated record. A detailed invalidated record already written for the
    /// same generation is kept as-is: it carries why verification failed, and overwriting it here
    /// would only erase that without changing the invalidated verdict.
    /// </summary>
    private SetupDockerResult InvalidateVerificationRecord(
        TrustedSetupHostLayout layout,
        string bundleId,
        long activationGeneration)
    {
        var existing = ReadVerificationRecord(layout);
        if (existing is not null
            && string.Equals(existing.Status, SetupVerificationRecord.StatusInvalidated, StringComparison.Ordinal)
            && string.Equals(existing.BundleId, bundleId, StringComparison.Ordinal)
            && existing.ActivationGeneration == activationGeneration)
        {
            return SetupDockerResult.Ok();
        }

        var record = new SetupVerificationRecord
        {
            SchemaVersion = SetupVerificationRecord.CurrentSchemaVersion,
            Status = SetupVerificationRecord.StatusInvalidated,
            BundleId = bundleId,
            ActivationGeneration = activationGeneration,
            FingerprintComparison = SetupVerificationRecord.FingerprintNotEvaluated,
            HostAtRest = SetupIntegrityMerger.NotVerified,
            MountAttestation = SetupIntegrityMerger.NotVerified,
            BundleIntegrity = SetupIntegrityMerger.NotVerified,
            RuntimeIdentityBinding = SetupRuntimeIdentityBindingResult.Missing,
            Readiness = SetupVerificationRecord.ReadinessNotEvaluated,
            SendReadyEvaluation = SetupVerificationRecord.SendReadyNotEvaluated,
        };

        return _writer.TryAtomicReplaceJson(
            layout.ManagedRoot,
            layout.LastRecordPath,
            record,
            SetupApplyJsonContext.Default.SetupVerificationRecord);
    }

    private SetupDockerResult WriteVerificationRecord(
        ApplyContext context,
        string status,
        string fingerprintComparison,
        string mountAttestation,
        string bundleIntegrity,
        string readiness,
        string runtimeIdentityBinding,
        string? committedAt)
    {
        var record = new SetupVerificationRecord
        {
            SchemaVersion = SetupVerificationRecord.CurrentSchemaVersion,
            Status = status,
            BundleId = context.Candidate.BundleId,
            ActivationGeneration = context.Candidate.ActivationGeneration,
            FingerprintComparison = fingerprintComparison,
            HostAtRest = context.CandidateHostAtRest,
            MountAttestation = mountAttestation,
            BundleIntegrity = bundleIntegrity,
            ImageReference = context.Layout.ReleaseInventory.PinnedMailerImageReference,
            ComposeIdentity = SetupBundleStaticValidator.ComputeComposeIdentity(context.Layout.ReleaseInventory),
            RecordedSchemaVersion = context.Recorded?.SchemaVersion,
            RuntimeIdentityBinding = runtimeIdentityBinding,
            Readiness = readiness,
            SendReadyEvaluation = SetupVerificationRecord.SendReadyNotEvaluated,
            CommittedAt = committedAt,
        };

        return _writer.TryAtomicReplaceJson(
            context.Layout.ManagedRoot,
            context.Layout.LastRecordPath,
            record,
            SetupApplyJsonContext.Default.SetupVerificationRecord);
    }

    private SetupDockerResult WriteRuntimeIdentityBinding(ApplyContext context)
    {
        var external = context.Session.ExternalInputs;
        if (external is null)
        {
            return SetupDockerResult.Fail(
                SetupDockerResultCode.ExternalInputNotPinned,
                "External inputs must be pinned before writing the runtime-identity binding.");
        }

        var stampDocument = new SetupRuntimeIdentityBindingStamp
        {
            SchemaVersion = SetupRuntimeIdentityBindingStamp.CurrentSchemaVersion,
            BundleId = context.Candidate.BundleId,
            ActivationGeneration = context.Candidate.ActivationGeneration,
            BindingMac = external.BindingMac,
        };

        return _writer.TryAtomicReplaceJson(
            context.Layout.ManagedRoot,
            context.Layout.RuntimeIdentityBindPath,
            stampDocument,
            SetupApplyJsonContext.Default.SetupRuntimeIdentityBindingStamp);
    }

    private string Timestamp() =>
        _timeProvider.GetUtcNow().ToString("yyyy-MM-ddTHH:mm:ss.fffffffZ", CultureInfo.InvariantCulture);

    private static SetupApplyResult CancelledBeforeActivation() =>
        SetupApplyResult.Create(
            SetupApplyResultCode.CancelledBeforeActivation,
            SetupManagedDeploymentState.NoManaged,
            "Apply was cancelled before the ACTIVE pointer changed.",
            reasonCode: "cancelled");

    private static SetupApplyResult Fail(
        string code,
        SetupManagedDeploymentState deploymentState,
        string? message,
        string? actionCode = null,
        string? reasonCode = null,
        string? bundleId = null,
        long? activationGeneration = null,
        bool configurationApplied = false,
        bool verificationCommitted = false,
        string configRollbackStatus = SetupConfigRollbackStatus.NotApplicable,
        bool persistentSideEffectMayRemain = false,
        string persistentSideEffectKind = SetupPersistentSideEffectKind.None) =>
        SetupApplyResult.Create(
            code,
            deploymentState,
            message,
            actionCode,
            reasonCode,
            bundleId,
            activationGeneration,
            configurationApplied,
            verificationCommitted,
            configRollbackStatus,
            persistentSideEffectMayRemain,
            persistentSideEffectKind);

    private sealed class DurableState
    {
        public SetupActivePointer? Active { get; init; }
        public SetupActivePointer? Previous { get; init; }
        public SetupTransactionStamp? TransactionStamp { get; init; }
        public SetupVerificationRecord? VerificationRecord { get; init; }
    }

    private sealed class ApplyContext
    {
        public ApplyContext(
            TrustedSetupHostLayout layout,
            SetupHostDockerSession session,
            SetupTransactionStamp stamp,
            SetupActivePointer candidate,
            SetupActivePointer? previous,
            string candidateHostAtRest,
            SetupRecordedMetadata? recorded,
            bool migrationRequired)
        {
            Layout = layout;
            Session = session;
            Stamp = stamp;
            Candidate = candidate;
            Previous = previous;
            CandidateHostAtRest = candidateHostAtRest;
            Recorded = recorded;
            MigrationRequired = migrationRequired;
        }

        public TrustedSetupHostLayout Layout { get; }
        public SetupHostDockerSession Session { get; }
        public SetupTransactionStamp Stamp { get; set; }
        public SetupActivePointer Candidate { get; }
        public SetupActivePointer? Previous { get; }
        public string CandidateHostAtRest { get; }
        public SetupRecordedMetadata? Recorded { get; }
        public bool MigrationRequired { get; }
    }
}
