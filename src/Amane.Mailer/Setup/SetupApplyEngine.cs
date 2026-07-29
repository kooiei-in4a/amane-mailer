using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Amane.Mailer.Setup;

/// <summary>
/// Managed apply / rollback / recovery orchestration (Issue #450, ADR 0021 D-03).
/// </summary>
/// <remarks>
/// <para>
/// Three rules shape this type. Durable state is only ever read while <c>APPLY.lock</c> is held, so a
/// transaction can never be planned against a generation another apply has already replaced.
/// Every durable marker (<c>ACTIVE</c>, <c>PREVIOUS</c>, <c>TX.stamp</c>, the verification record, and
/// the runtime-identity binding) is written through <see cref="SetupDurableAtomicWriter"/> before the
/// side effect it describes, and a failed write stops the operation instead of being ignored. Finally,
/// a generation is only called applied after the running container itself has been proven to resolve
/// that bundle, fingerprint, and recorded schema.
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
            return CancelledBeforeActivation();
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
                SetupManagedDeploymentState.NotInspected,
                "Candidate bundle id is invalid.",
                reasonCode: "candidate_bundle_id_invalid");
        }

        var (probeResult, binding) = await _adapter.CheckDockerAsync(cancellationToken);
        if (!probeResult.IsSuccess || binding is null)
        {
            return Fail(
                SetupApplyResultCode.PreflightFailed,
                SetupManagedDeploymentState.NotInspected,
                "Docker preflight failed before Managed state was inspected.",
                reasonCode: probeResult.Code);
        }

        var (sessionResult, session) = await _adapter.AcquireSessionAsync(layout, binding, cancellationToken);
        if (!sessionResult.IsSuccess || session is null)
        {
            return sessionResult.Code == SetupDockerResultCode.ConcurrentSetupRejected
                ? Fail(
                    SetupApplyResultCode.ConcurrentApplyRejected,
                    SetupManagedDeploymentState.NotInspected,
                    "Another setup apply session is already running.",
                    reasonCode: sessionResult.Code)
                : Fail(
                    SetupApplyResultCode.PreflightFailed,
                    SetupManagedDeploymentState.NotInspected,
                    "Setup apply session could not be acquired.",
                    reasonCode: sessionResult.Code);
        }

        await using (session)
        {
            return await ApplyUnderLockAsync(layout, session, candidateBundleId, cancellationToken);
        }
    }

    private async Task<SetupApplyResult> ApplyUnderLockAsync(
        TrustedSetupHostLayout layout,
        SetupHostDockerSession session,
        string candidateBundleId,
        CancellationToken cancellationToken)
    {
        // Step 2. Durable state is read only now: anything observed before the lock could already
        // belong to a transaction that finished while this call was waiting.
        var stateRead = ReadDurableState(layout, out var state);
        if (stateRead is not null)
        {
            return stateRead;
        }

        if (state.TransactionStamp is not null)
        {
            return TransactionPresentResult(state.TransactionStamp);
        }

        var isFresh = state.Active is null;
        var preFailureCode = isFresh
            ? SetupApplyResultCode.FreshApplyFailed
            : SetupApplyResultCode.IneligibleExistingActive;
        var preFailureState = isFresh
            ? SetupManagedDeploymentState.NoManaged
            : SetupManagedDeploymentState.Active;

        if (cancellationToken.IsCancellationRequested)
        {
            return CancelledBeforeActivation();
        }

        // Step 3. No inspection may run until managed/tmp is proven clean (plan §10).
        var purge = await _adapter.PurgeStaleMountVerifiersAsync(session, cancellationToken);
        if (!purge.IsSuccess)
        {
            // A cancelled purge proves nothing about residue, so it is not reported as residue.
            return cancellationToken.IsCancellationRequested
                ? CancelledBeforeActivation()
                : Fail(
                    SetupApplyResultCode.NeedsIntervention,
                    SetupManagedDeploymentState.NeedsIntervention,
                    "Managed verifier temp directory is not in a safe state.",
                    actionCode: SetupApplyActionCode.UnsafeVerifierResidue,
                    reasonCode: "unsafe_verifier_residue");
        }

        // Step 4. Pin the ACTIVE-independent external layer once for the whole transaction.
        var pin = await _adapter.PinExternalInputsAsync(session, cancellationToken);
        if (!pin.IsSuccess || session.ExternalInputs is null)
        {
            return Fail(preFailureCode, preFailureState, "External inputs could not be pinned.", reasonCode: pin.Code);
        }

        var external = session.ExternalInputs;

        // Step 5. Candidate host at-rest integrity. No Docker, no ACTIVE change.
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

        // Step 6. Image compatibility is decided before anything moves.
        var incompatibleReason = ClassifyImageCompatibility(layout, candidateRecorded);
        if (incompatibleReason is not null)
        {
            return Fail(
                SetupApplyResultCode.UpgradeRequired,
                preFailureState,
                "Candidate bundle is not compatible with the trusted release inventory.",
                reasonCode: incompatibleReason);
        }

        if (cancellationToken.IsCancellationRequested)
        {
            return CancelledBeforeActivation();
        }

        // Step 7.
        var image = await _adapter.EnsurePinnedImageAvailableAsync(session, cancellationToken);
        if (!image.IsSuccess)
        {
            return Fail(preFailureCode, preFailureState, "Pinned image is not available.", reasonCode: image.Code);
        }

        // Step 8. Migration route depends on whether a verified ACTIVE exists.
        SetupActivePointer? previousPointer = null;
        SetupMigrationDecision decision;
        if (isFresh)
        {
            decision = SetupDatabaseFileProbe.ClassifyFreshHostDatabase(_fileSystem, external);
        }
        else
        {
            var ineligible = ClassifyExistingActive(layout, state, external, out var activeRecorded);
            if (ineligible is not null)
            {
                return ineligible;
            }

            var currentCompose = await _adapter.ComposeCurrentActiveInputAsync(session, cancellationToken);
            if (!currentCompose.IsSuccess)
            {
                return Fail(
                    SetupApplyResultCode.NeedsIntervention,
                    SetupManagedDeploymentState.NeedsIntervention,
                    "The existing ACTIVE environment could not be composed for inspection.",
                    actionCode: SetupApplyActionCode.ManualInterventionRequired,
                    reasonCode: currentCompose.Code);
            }

            var status = await _adapter.InspectMigrationStatusAsync(session, cancellationToken);
            if (!status.IsSuccess || status.MigrationStatus is null)
            {
                return Fail(
                    SetupApplyResultCode.NeedsIntervention,
                    SetupManagedDeploymentState.NeedsIntervention,
                    "Existing Managed deployment schema could not be classified safely.",
                    actionCode: SetupApplyActionCode.ReviewDatabaseSchema,
                    reasonCode: status.Code);
            }

            decision = SetupDatabaseFileProbe.ClassifyExistingFromStatus(
                status.MigrationStatus.Classification,
                ImageReference(activeRecorded!),
                ImageReference(candidateRecorded));
            previousPointer = state.Active;
        }

        var refusal = MapMigrationDecision(decision, preFailureState);
        if (refusal is not null)
        {
            return refusal;
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

        // Step 9. PREVIOUS is durable before the transaction exists, so a crash here leaves an
        // orphan that recovery can classify rather than a rollback without a target.
        if (previousPointer is not null)
        {
            var previousWrite = WritePointer(layout, layout.PreviousPointerPath, previousPointer);
            if (!previousWrite.IsSuccess)
            {
                return Fail(preFailureCode, preFailureState, "Previous pointer could not be written.", reasonCode: previousWrite.Code);
            }
        }

        // Step 10. Checkpoint 1: external inputs must be unchanged before the transaction exists.
        var checkpoint1 = await _adapter.VerifyExternalInputsUnchangedAsync(session, cancellationToken);
        if (!checkpoint1.IsSuccess)
        {
            var cleanup = DiscardPreviousPointer(layout, previousPointer);
            return cleanup ?? Fail(
                preFailureCode,
                preFailureState,
                "Allowlisted external input changed before the transaction was created.",
                reasonCode: "external_input_changed_before_transaction");
        }

        // Step 11. Write-ahead: from here a crash is visible to recovery.
        var stamp = new SetupTransactionStamp
        {
            SchemaVersion = SetupTransactionStamp.CurrentSchemaVersion,
            Kind = SetupTransactionKind.Apply,
            Phase = SetupTransactionPhase.Prepared,
            Terminal = false,
            CandidateBundleId = candidateBundleId,
            TargetActivationGeneration = targetGeneration,
            PreviousBundleId = previousPointer?.BundleId,
            PreviousActivationGeneration = previousPointer?.ActivationGeneration,
            PersistentSideEffectMayRemain = false,
            PersistentSideEffectKind = SetupPersistentSideEffectKind.None,
            StartedAt = Timestamp(),
        };
        var stampWrite = WriteStamp(layout, stamp);
        if (!stampWrite.IsSuccess)
        {
            var cleanup = DiscardPreviousPointer(layout, previousPointer);
            return cleanup ?? Fail(
                preFailureCode,
                preFailureState,
                "Transaction stamp could not be written.",
                reasonCode: stampWrite.Code);
        }

        // Step 12. Any committed verification now describes a generation that is being replaced.
        var invalidate = InvalidateVerificationRecord(layout, candidateBundleId, targetGeneration);
        if (!invalidate.IsSuccess)
        {
            // The record write is atomic, so the previous committed record is still intact.
            _ = DeleteStamp(layout);
            var cleanup = DiscardPreviousPointer(layout, previousPointer);
            return cleanup ?? Fail(
                preFailureCode,
                preFailureState,
                "Verification record could not be invalidated.",
                reasonCode: invalidate.Code);
        }

        var context = new ApplyContext(
            layout,
            session,
            stamp,
            candidatePointer,
            previousPointer,
            candidateHostAtRest,
            candidateRecorded,
            migrationRequired);

        // Step 13.
        var switchPending = AdvancePhase(context, SetupTransactionPhase.ActiveSwitchPending, persistentSideEffect: false);
        if (!switchPending.IsSuccess)
        {
            return AbortBeforeActivation(context, "durable_write_failed");
        }

        // Step 14. Checkpoint 2: last comparison before ACTIVE is replaced.
        var checkpoint2 = await _adapter.VerifyExternalInputsUnchangedAsync(session, cancellationToken);
        if (!checkpoint2.IsSuccess)
        {
            return AbortBeforeActivation(context, "external_input_changed_before_activation");
        }

        // Step 15.
        var activeWrite = WritePointer(layout, layout.ActivePointerPath, candidatePointer);
        if (!activeWrite.IsSuccess)
        {
            return AbortBeforeActivation(context, activeWrite.Code ?? "active_pointer_write_failed");
        }

        return await RunPostActivationAsync(context, cancellationToken);
    }

    private async Task<SetupApplyResult> RunPostActivationAsync(
        ApplyContext context,
        CancellationToken cancellationToken)
    {
        var layout = context.Layout;
        var session = context.Session;

        // Step 16. A concurrent ACTIVE change is detected here, before anything is started.
        var compose = await _adapter.ComposeExpectedActiveInputAsync(session, context.Candidate, cancellationToken);
        if (!compose.IsSuccess)
        {
            return await RollbackAsync(context, "compose_pin_failed", migrationAttempted: false);
        }

        // Step 17.
        var gate = await GateAsync(context, SetupTransactionPhase.CandidateComposeValidating, false, false);
        if (gate is not null)
        {
            return gate;
        }

        var validate = await _adapter.ValidateComposeAsync(session, cancellationToken);
        if (!validate.IsSuccess)
        {
            return await RollbackAsync(context, "compose_validation_failed", migrationAttempted: false);
        }

        // Step 18. The side effect is durable before it happens, never after.
        var migrationAttempted = false;
        if (context.MigrationRequired)
        {
            gate = await GateAsync(context, SetupTransactionPhase.MigrationPending, false, false);
            if (gate is not null)
            {
                return gate;
            }

            gate = await GateAsync(context, SetupTransactionPhase.Migrating, true, false);
            if (gate is not null)
            {
                return gate;
            }

            migrationAttempted = true;
            var migrate = await _adapter.RunMigrationAsync(session, cancellationToken);
            if (!migrate.IsSuccess)
            {
                return await RollbackAsync(context, "migration_failed", migrationAttempted: true);
            }
        }

        // Step 19.
        gate = await GateAsync(context, SetupTransactionPhase.Recreating, migrationAttempted, migrationAttempted);
        if (gate is not null)
        {
            return gate;
        }

        var recreate = await _adapter.StartOrRecreateMailerAsync(session, cancellationToken);
        if (!recreate.IsSuccess)
        {
            return await RollbackAsync(context, "recreate_failed", migrationAttempted);
        }

        // Step 20. The running container must prove it resolved this bundle before anything is committed.
        gate = await GateAsync(context, SetupTransactionPhase.Inspecting, migrationAttempted, migrationAttempted);
        if (gate is not null)
        {
            return gate;
        }

        var verification = await VerifyGenerationAsync(
            layout,
            session,
            context.Candidate,
            context.CandidateRecorded!,
            context.CandidateHostAtRest,
            cancellationToken);
        if (!verification.IsSuccess)
        {
            _ = WriteVerificationRecord(
                layout,
                context.Candidate,
                SetupVerificationRecord.StatusInvalidated,
                verification,
                context.CandidateHostAtRest,
                SetupVerificationRecord.ReadinessNotEvaluated,
                SetupRuntimeIdentityBindingResult.Missing,
                committedAt: null);
            return await RollbackAsync(context, verification.ReasonCode!, migrationAttempted);
        }

        // Step 21.
        gate = await GateAsync(context, SetupTransactionPhase.ReadinessChecking, migrationAttempted, migrationAttempted);
        if (gate is not null)
        {
            return gate;
        }

        var readiness = await _adapter.AwaitMailerHealthyAsync(session, cancellationToken);
        if (!readiness.IsSuccess)
        {
            return await RollbackAsync(context, "readiness_failed", migrationAttempted);
        }

        // Step 22. Checkpoint 3: last comparison before the verification record is committed.
        var checkpoint3 = await _adapter.VerifyExternalInputsUnchangedAsync(session, cancellationToken);
        if (!checkpoint3.IsSuccess)
        {
            return await RollbackAsync(context, "external_input_changed_before_verification", migrationAttempted);
        }

        // Steps 23-25. Binding first, record second: the record is the final success authority.
        return await CommitGenerationAsync(
            context,
            context.Candidate,
            verification,
            context.CandidateHostAtRest,
            migrationAttempted,
            SetupApplyResultCode.ApplySucceeded,
            "Managed configuration applied and verification committed.");
    }

    /// <summary>
    /// Commits the runtime-identity binding, then the verification record, then clears the
    /// transaction. Used by apply, rollback, and re-verifying recovery so all three reach
    /// "applied" through exactly the same durable sequence.
    /// </summary>
    private async Task<SetupApplyResult> CommitGenerationAsync(
        ApplyContext context,
        SetupActivePointer pointer,
        GenerationVerification verification,
        string hostAtRest,
        bool migrationAttempted,
        string successCode,
        string successMessage)
    {
        var layout = context.Layout;

        var gate = await GateAsync(context, SetupTransactionPhase.BindingPending, migrationAttempted, migrationAttempted);
        if (gate is not null)
        {
            return gate;
        }

        var bindingWrite = WriteRuntimeIdentityBinding(layout, context.Session, pointer);
        if (!bindingWrite.IsSuccess)
        {
            return await RollbackAsync(context, "runtime_identity_binding_failed", migrationAttempted);
        }

        gate = await GateAsync(context, SetupTransactionPhase.VerificationPending, migrationAttempted, migrationAttempted);
        if (gate is not null)
        {
            return gate;
        }

        var recordWrite = WriteVerificationRecord(
            layout,
            pointer,
            SetupVerificationRecord.StatusCommitted,
            verification,
            hostAtRest,
            SetupVerificationRecord.ReadinessPassed,
            SetupRuntimeIdentityBindingResult.Matched,
            Timestamp());
        if (!recordWrite.IsSuccess)
        {
            return await RollbackAsync(context, "verification_record_failed", migrationAttempted);
        }

        // Past this point the deployment is verified, so a durable failure asks for a human instead
        // of undoing a generation that is already the committed truth.
        var committed = AdvancePhase(context, SetupTransactionPhase.VerificationCommitted, migrationAttempted);
        if (!committed.IsSuccess)
        {
            return CommittedButUnfinished(pointer, committed.Code, migrationAttempted);
        }

        var previousDelete = _writer.TryDurableDelete(layout.ManagedRoot, layout.PreviousPointerPath);
        if (!previousDelete.IsSuccess)
        {
            return CommittedButUnfinished(pointer, previousDelete.Code, migrationAttempted);
        }

        var stampDelete = DeleteStamp(layout);
        if (!stampDelete.IsSuccess)
        {
            return CommittedButUnfinished(pointer, stampDelete.Code, migrationAttempted);
        }

        return SetupApplyResult.Create(
            successCode,
            SetupManagedDeploymentState.Active,
            successMessage,
            actionCode: SetupApplyActionCode.CompleteSendReadyEvaluation,
            reasonCode: null,
            bundleId: pointer.BundleId,
            activationGeneration: pointer.ActivationGeneration,
            configurationApplied: true,
            verificationCommitted: true,
            persistentSideEffectMayRemain: migrationAttempted,
            persistentSideEffectKind: migrationAttempted
                ? SetupPersistentSideEffectKind.DatabaseMigration
                : SetupPersistentSideEffectKind.None);
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
        var token = rollbackCts.Token;

        var layout = context.Layout;
        var session = context.Session;
        var sideEffectKind = migrationAttempted
            ? SetupPersistentSideEffectKind.DatabaseMigration
            : SetupPersistentSideEffectKind.None;

        // 1. Announce the rollback before doing any of it.
        context.Stamp = context.Stamp with
        {
            Kind = SetupTransactionKind.Rollback,
            Phase = SetupTransactionPhase.RollbackPending,
            Terminal = false,
            ReasonCode = reasonCode,
            PersistentSideEffectMayRemain = migrationAttempted,
            PersistentSideEffectKind = sideEffectKind,
        };
        var stampWrite = WriteStamp(layout, context.Stamp);
        if (!stampWrite.IsSuccess)
        {
            return TerminalRollbackFailure(context, reasonCode, migrationAttempted, sideEffectKind, stampWrite.Code);
        }

        var invalidate = InvalidateVerificationRecord(
            layout,
            context.Candidate.BundleId,
            context.Candidate.ActivationGeneration);
        if (!invalidate.IsSuccess)
        {
            return TerminalRollbackFailure(context, reasonCode, migrationAttempted, sideEffectKind, invalidate.Code);
        }

        // Stop the candidate while a compose pin for its generation is still obtainable.
        if (!ComposeMatches(session, context.Candidate))
        {
            _ = await _adapter.ComposeExpectedActiveInputAsync(session, context.Candidate, token);
        }

        if (ComposeMatches(session, context.Candidate))
        {
            _ = await _adapter.StopFailedMailerAsync(session, token);
        }

        return context.Previous is null
            ? RollbackFresh(context, reasonCode, migrationAttempted, sideEffectKind)
            : await RollbackToPreviousAsync(context, reasonCode, migrationAttempted, sideEffectKind, token);
    }

    /// <summary>
    /// A fresh apply has nothing to restore. Removing ACTIVE is only honest while no persistent side
    /// effect happened; a migration that already ran is not undone by a pointer, so that case stops
    /// for review instead of reporting a clean rollback.
    /// </summary>
    private SetupApplyResult RollbackFresh(
        ApplyContext context,
        string reasonCode,
        bool migrationAttempted,
        string sideEffectKind)
    {
        var layout = context.Layout;

        if (migrationAttempted)
        {
            context.Stamp = context.Stamp with
            {
                Terminal = true,
                ReasonCode = reasonCode,
                PersistentSideEffectMayRemain = true,
                PersistentSideEffectKind = sideEffectKind,
            };
            _ = WriteStamp(layout, context.Stamp);

            return Fail(
                SetupApplyResultCode.NeedsIntervention,
                SetupManagedDeploymentState.NeedsIntervention,
                "Fresh Managed apply failed after a database migration had already run.",
                actionCode: SetupApplyActionCode.ReviewDatabaseSchema,
                reasonCode: reasonCode,
                bundleId: context.Candidate.BundleId,
                activationGeneration: context.Candidate.ActivationGeneration,
                configRollbackStatus: SetupConfigRollbackStatus.NotApplicable,
                persistentSideEffectMayRemain: true,
                persistentSideEffectKind: sideEffectKind);
        }

        var bindingDelete = DeleteBindingForGeneration(layout, context.Candidate);
        if (!bindingDelete.IsSuccess)
        {
            return TerminalRollbackFailure(context, reasonCode, migrationAttempted, sideEffectKind, bindingDelete.Code);
        }

        var activeDelete = _writer.TryDurableDelete(layout.ManagedRoot, layout.ActivePointerPath);
        if (!activeDelete.IsSuccess)
        {
            return TerminalRollbackFailure(context, reasonCode, migrationAttempted, sideEffectKind, activeDelete.Code);
        }

        var previousDelete = _writer.TryDurableDelete(layout.ManagedRoot, layout.PreviousPointerPath);
        if (!previousDelete.IsSuccess)
        {
            return TerminalRollbackFailure(context, reasonCode, migrationAttempted, sideEffectKind, previousDelete.Code);
        }

        var stampDelete = DeleteStamp(layout);
        if (!stampDelete.IsSuccess)
        {
            return TerminalRollbackFailure(context, reasonCode, migrationAttempted, sideEffectKind, stampDelete.Code);
        }

        return Fail(
            SetupApplyResultCode.FreshApplyFailed,
            SetupManagedDeploymentState.NoManaged,
            "Fresh Managed apply failed; no Managed deployment is active.",
            reasonCode: reasonCode,
            configRollbackStatus: SetupConfigRollbackStatus.Succeeded);
    }

    /// <summary>
    /// Restores the previous bundle under a new generation and re-earns the verification record for
    /// it. Restoring the pointer alone is not a rollback: the record stays the only success authority.
    /// </summary>
    private async Task<SetupApplyResult> RollbackToPreviousAsync(
        ApplyContext context,
        string reasonCode,
        bool migrationAttempted,
        string sideEffectKind,
        CancellationToken token)
    {
        var layout = context.Layout;
        var session = context.Session;
        var previous = context.Previous!;

        // 2. The bundle we are about to activate must still be intact.
        var validation = SetupBundleStaticValidator.TryValidateFinalizedBundle(
            _fileSystem,
            layout,
            previous.BundleId,
            out var previousRecorded,
            out var previousHostAtRest);
        if (!validation.IsSuccess || previousRecorded is null)
        {
            return TerminalRollbackFailure(context, reasonCode, migrationAttempted, sideEffectKind, validation.Code);
        }

        // 3.
        var drift = await _adapter.VerifyExternalInputsUnchangedAsync(session, token);
        if (!drift.IsSuccess)
        {
            return TerminalRollbackFailure(context, reasonCode, migrationAttempted, sideEffectKind, drift.Code);
        }

        // 4. Generations never move backwards, so a stale pin can never look current.
        var restoredGeneration = Math.Max(
            context.Candidate.ActivationGeneration,
            previous.ActivationGeneration) + 1;
        var restored = new SetupActivePointer
        {
            SchemaVersion = SetupActivePointer.CurrentSchemaVersion,
            BundleId = previous.BundleId,
            ActivationGeneration = restoredGeneration,
        };

        context.Stamp = context.Stamp with { TargetActivationGeneration = restoredGeneration };
        var stampWrite = WriteStamp(layout, context.Stamp);
        if (!stampWrite.IsSuccess)
        {
            return TerminalRollbackFailure(context, reasonCode, migrationAttempted, sideEffectKind, stampWrite.Code);
        }

        // 5.
        var restore = WritePointer(layout, layout.ActivePointerPath, restored);
        if (!restore.IsSuccess)
        {
            return TerminalRollbackFailure(context, reasonCode, migrationAttempted, sideEffectKind, restore.Code);
        }

        // 6.
        var recompose = await _adapter.ComposeExpectedActiveInputAsync(session, restored, token);
        if (!recompose.IsSuccess)
        {
            return TerminalRollbackFailure(context, reasonCode, migrationAttempted, sideEffectKind, recompose.Code);
        }

        // 8. Rollback never runs a migration.
        var recreatePhase = AdvanceRollbackPhase(context, SetupTransactionPhase.Recreating);
        if (!recreatePhase.IsSuccess)
        {
            return TerminalRollbackFailure(context, reasonCode, migrationAttempted, sideEffectKind, recreatePhase.Code);
        }

        var recreate = await _adapter.StartOrRecreateMailerAsync(session, token);
        if (!recreate.IsSuccess)
        {
            return TerminalRollbackFailure(context, reasonCode, migrationAttempted, sideEffectKind, recreate.Code);
        }

        // 9.
        var inspectPhase = AdvanceRollbackPhase(context, SetupTransactionPhase.Inspecting);
        if (!inspectPhase.IsSuccess)
        {
            return TerminalRollbackFailure(context, reasonCode, migrationAttempted, sideEffectKind, inspectPhase.Code);
        }

        var verification = await VerifyGenerationAsync(
            layout,
            session,
            restored,
            previousRecorded,
            previousHostAtRest,
            token);
        if (!verification.IsSuccess)
        {
            _ = WriteVerificationRecord(
                layout,
                restored,
                SetupVerificationRecord.StatusInvalidated,
                verification,
                previousHostAtRest,
                SetupVerificationRecord.ReadinessNotEvaluated,
                SetupRuntimeIdentityBindingResult.Missing,
                committedAt: null);
            return TerminalRollbackFailure(context, reasonCode, migrationAttempted, sideEffectKind, verification.ReasonCode);
        }

        // 10.
        var readinessPhase = AdvanceRollbackPhase(context, SetupTransactionPhase.ReadinessChecking);
        if (!readinessPhase.IsSuccess)
        {
            return TerminalRollbackFailure(context, reasonCode, migrationAttempted, sideEffectKind, readinessPhase.Code);
        }

        var readiness = await _adapter.AwaitMailerHealthyAsync(session, token);
        if (!readiness.IsSuccess)
        {
            return TerminalRollbackFailure(context, reasonCode, migrationAttempted, sideEffectKind, readiness.Code);
        }

        // 11.
        var commitDrift = await _adapter.VerifyExternalInputsUnchangedAsync(session, token);
        if (!commitDrift.IsSuccess)
        {
            return TerminalRollbackFailure(context, reasonCode, migrationAttempted, sideEffectKind, commitDrift.Code);
        }

        // 12-15. Binding first, record second: the record stays the final success authority.
        var bindingPhase = AdvanceRollbackPhase(context, SetupTransactionPhase.BindingPending);
        if (!bindingPhase.IsSuccess)
        {
            return TerminalRollbackFailure(context, reasonCode, migrationAttempted, sideEffectKind, bindingPhase.Code);
        }

        var bindingWrite = WriteRuntimeIdentityBinding(layout, session, restored);
        if (!bindingWrite.IsSuccess)
        {
            return TerminalRollbackFailure(context, reasonCode, migrationAttempted, sideEffectKind, bindingWrite.Code);
        }

        var pendingPhase = AdvanceRollbackPhase(context, SetupTransactionPhase.VerificationPending);
        if (!pendingPhase.IsSuccess)
        {
            return TerminalRollbackFailure(context, reasonCode, migrationAttempted, sideEffectKind, pendingPhase.Code);
        }

        var recordWrite = WriteVerificationRecord(
            layout,
            restored,
            SetupVerificationRecord.StatusCommitted,
            verification,
            previousHostAtRest,
            SetupVerificationRecord.ReadinessPassed,
            SetupRuntimeIdentityBindingResult.Matched,
            Timestamp());
        if (!recordWrite.IsSuccess)
        {
            return TerminalRollbackFailure(context, reasonCode, migrationAttempted, sideEffectKind, recordWrite.Code);
        }

        var committedPhase = AdvanceRollbackPhase(context, SetupTransactionPhase.VerificationCommitted);
        if (!committedPhase.IsSuccess)
        {
            return TerminalRollbackFailure(context, reasonCode, migrationAttempted, sideEffectKind, committedPhase.Code);
        }

        var previousDelete = _writer.TryDurableDelete(layout.ManagedRoot, layout.PreviousPointerPath);
        if (!previousDelete.IsSuccess)
        {
            return TerminalRollbackFailure(context, reasonCode, migrationAttempted, sideEffectKind, previousDelete.Code);
        }

        var stampDelete = DeleteStamp(layout);
        if (!stampDelete.IsSuccess)
        {
            return TerminalRollbackFailure(context, reasonCode, migrationAttempted, sideEffectKind, stampDelete.Code);
        }

        // A migration that already ran is not undone by restoring the pointer.
        return migrationAttempted
            ? Fail(
                SetupApplyResultCode.ApplyFailedRollbackSucceeded,
                SetupManagedDeploymentState.NeedsIntervention,
                "Apply failed and configuration rolled back, but a database migration may have persisted.",
                actionCode: SetupApplyActionCode.ReviewDatabaseSchema,
                reasonCode: reasonCode,
                bundleId: restored.BundleId,
                activationGeneration: restoredGeneration,
                configRollbackStatus: SetupConfigRollbackStatus.Succeeded,
                persistentSideEffectMayRemain: true,
                persistentSideEffectKind: sideEffectKind)
            : Fail(
                SetupApplyResultCode.ApplyFailedRollbackSucceeded,
                SetupManagedDeploymentState.Active,
                "Apply failed and the previous Managed configuration was restored and re-verified.",
                reasonCode: reasonCode,
                bundleId: restored.BundleId,
                activationGeneration: restoredGeneration,
                configRollbackStatus: SetupConfigRollbackStatus.Succeeded);
    }

    private SetupApplyResult TerminalRollbackFailure(
        ApplyContext context,
        string reasonCode,
        bool migrationAttempted,
        string sideEffectKind,
        string? rollbackFailureCode)
    {
        // Leave a terminal stamp so a later apply refuses and recovery reports intervention. Which
        // generation is effective is deliberately not guessed.
        context.Stamp = context.Stamp with
        {
            Kind = SetupTransactionKind.Rollback,
            Phase = SetupTransactionPhase.RollbackPending,
            Terminal = true,
            ReasonCode = rollbackFailureCode ?? reasonCode,
            PersistentSideEffectMayRemain = migrationAttempted,
            PersistentSideEffectKind = sideEffectKind,
        };
        _ = WriteStamp(context.Layout, context.Stamp);

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
        var (probeResult, binding) = await _adapter.CheckDockerAsync(cancellationToken);
        if (!probeResult.IsSuccess || binding is null)
        {
            return Fail(
                SetupApplyResultCode.RecoveryRequired,
                SetupManagedDeploymentState.NotInspected,
                "Docker preflight failed; Managed state was not inspected.",
                reasonCode: probeResult.Code);
        }

        var (sessionResult, session) = await _adapter.AcquireSessionAsync(layout, binding, cancellationToken);
        if (!sessionResult.IsSuccess || session is null)
        {
            return sessionResult.Code == SetupDockerResultCode.ConcurrentSetupRejected
                ? Fail(
                    SetupApplyResultCode.ConcurrentApplyRejected,
                    SetupManagedDeploymentState.NotInspected,
                    "Another setup apply session is already running.",
                    reasonCode: sessionResult.Code)
                : Fail(
                    SetupApplyResultCode.RecoveryRequired,
                    SetupManagedDeploymentState.NotInspected,
                    "Setup apply session could not be acquired; recovery is still required.",
                    reasonCode: sessionResult.Code);
        }

        await using (session)
        {
            return await RecoverUnderLockAsync(layout, session, cancellationToken);
        }
    }

    private async Task<SetupApplyResult> RecoverUnderLockAsync(
        TrustedSetupHostLayout layout,
        SetupHostDockerSession session,
        CancellationToken cancellationToken)
    {
        var stateRead = ReadDurableState(layout, out var state);
        if (stateRead is not null)
        {
            return stateRead;
        }

        var stamp = state.TransactionStamp;
        if (stamp is not null && stamp.Terminal)
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

        var purge = await _adapter.PurgeStaleMountVerifiersAsync(session, cancellationToken);
        if (!purge.IsSuccess)
        {
            if (stamp is not null)
            {
                _ = WriteStamp(layout, stamp with { Terminal = true, ReasonCode = "unsafe_verifier_residue" });
            }

            return Fail(
                SetupApplyResultCode.NeedsIntervention,
                SetupManagedDeploymentState.NeedsIntervention,
                "Managed verifier temp directory is not in a safe state.",
                actionCode: SetupApplyActionCode.UnsafeVerifierResidue,
                reasonCode: "unsafe_verifier_residue",
                persistentSideEffectMayRemain: stamp?.PersistentSideEffectMayRemain ?? false,
                persistentSideEffectKind: stamp?.PersistentSideEffectKind ?? SetupPersistentSideEffectKind.None);
        }

        // Nothing to compare external inputs against when neither a transaction nor ACTIVE exists,
        // and pinning would need bundle material that a never-applied host does not have yet.
        if (stamp is not null || state.Active is not null)
        {
            var pin = await _adapter.PinExternalInputsAsync(session, cancellationToken);
            if (!pin.IsSuccess || session.ExternalInputs is null)
            {
                return Fail(
                    SetupApplyResultCode.RecoveryRequired,
                    SetupManagedDeploymentState.RecoveryRequired,
                    "External inputs could not be pinned; recovery is still required.",
                    reasonCode: pin.Code,
                    persistentSideEffectMayRemain: stamp?.PersistentSideEffectMayRemain ?? false,
                    persistentSideEffectKind: stamp?.PersistentSideEffectKind ?? SetupPersistentSideEffectKind.None);
            }
        }

        return stamp is null
            ? RecoverWithoutTransaction(layout, session, state)
            : await RecoverPhaseAsync(layout, session, state, stamp, cancellationToken);
    }

    /// <summary>
    /// No transaction is in flight, so recovery only classifies what is on disk and clears orphans
    /// it can prove are orphans. Nothing is inferred from an unobserved runtime.
    /// </summary>
    private SetupApplyResult RecoverWithoutTransaction(
        TrustedSetupHostLayout layout,
        SetupHostDockerSession session,
        DurableState state)
    {
        if (state.RecordUnreadable || state.BindingUnreadable)
        {
            return Fail(
                SetupApplyResultCode.NeedsIntervention,
                SetupManagedDeploymentState.NeedsIntervention,
                "Durable verification state could not be read.",
                actionCode: SetupApplyActionCode.ManualInterventionRequired,
                reasonCode: "durable_state_unreadable");
        }

        if (state.Active is null)
        {
            foreach (var orphan in new[] { layout.PreviousPointerPath, layout.LastRecordPath, layout.RuntimeIdentityBindPath })
            {
                var delete = _writer.TryDurableDelete(layout.ManagedRoot, orphan);
                if (!delete.IsSuccess)
                {
                    return Fail(
                        SetupApplyResultCode.NeedsIntervention,
                        SetupManagedDeploymentState.NeedsIntervention,
                        "Orphaned Managed state could not be cleared.",
                        actionCode: SetupApplyActionCode.ManualInterventionRequired,
                        reasonCode: delete.Code);
                }
            }

            return Fail(
                SetupApplyResultCode.RollbackSucceeded,
                SetupManagedDeploymentState.NoManaged,
                "No Managed deployment is active and no transaction is in flight.",
                configRollbackStatus: SetupConfigRollbackStatus.NotApplicable);
        }

        var active = state.Active;
        if (!IsCommittedFor(state.VerificationRecord, active))
        {
            return Fail(
                SetupApplyResultCode.NeedsIntervention,
                SetupManagedDeploymentState.NeedsIntervention,
                "ACTIVE is set but no committed verification record matches it.",
                actionCode: SetupApplyActionCode.ManualInterventionRequired,
                reasonCode: "verification_record_missing",
                bundleId: active.BundleId,
                activationGeneration: active.ActivationGeneration,
                configurationApplied: true);
        }

        if (!BindingMatches(state.RuntimeIdentityBinding, active, session.ExternalInputs!))
        {
            return Fail(
                SetupApplyResultCode.NeedsIntervention,
                SetupManagedDeploymentState.NeedsIntervention,
                "ACTIVE is set but the runtime-identity binding does not match it.",
                actionCode: SetupApplyActionCode.ManualInterventionRequired,
                reasonCode: "runtime_identity_binding_mismatch",
                bundleId: active.BundleId,
                activationGeneration: active.ActivationGeneration,
                configurationApplied: true);
        }

        if (state.Previous is not null)
        {
            var delete = _writer.TryDurableDelete(layout.ManagedRoot, layout.PreviousPointerPath);
            if (!delete.IsSuccess)
            {
                return Fail(
                    SetupApplyResultCode.NeedsIntervention,
                    SetupManagedDeploymentState.NeedsIntervention,
                    "The orphaned previous pointer could not be cleared.",
                    actionCode: SetupApplyActionCode.ManualInterventionRequired,
                    reasonCode: delete.Code,
                    bundleId: active.BundleId,
                    activationGeneration: active.ActivationGeneration);
            }
        }

        return SetupApplyResult.Create(
            SetupApplyResultCode.ApplySucceeded,
            SetupManagedDeploymentState.Active,
            "Managed deployment state is consistent and verification is committed.",
            actionCode: SetupApplyActionCode.CompleteSendReadyEvaluation,
            bundleId: active.BundleId,
            activationGeneration: active.ActivationGeneration,
            configurationApplied: true,
            verificationCommitted: true);
    }

    private async Task<SetupApplyResult> RecoverPhaseAsync(
        TrustedSetupHostLayout layout,
        SetupHostDockerSession session,
        DurableState state,
        SetupTransactionStamp stamp,
        CancellationToken cancellationToken)
    {
        var candidate = new SetupActivePointer
        {
            SchemaVersion = SetupActivePointer.CurrentSchemaVersion,
            BundleId = stamp.CandidateBundleId,
            ActivationGeneration = stamp.TargetActivationGeneration,
        };

        var previous = state.Previous;
        if (stamp.PreviousBundleId is not null)
        {
            if (previous is null)
            {
                return RecoveryIntervention(stamp, "previous_pointer_missing");
            }

            if (!string.Equals(previous.BundleId, stamp.PreviousBundleId, StringComparison.Ordinal)
                || previous.ActivationGeneration != stamp.PreviousActivationGeneration)
            {
                return RecoveryIntervention(stamp, "previous_pointer_mismatch");
            }
        }
        else
        {
            previous = null;
        }

        var sideEffect = stamp.PersistentSideEffectMayRemain;
        var context = new ApplyContext(
            layout,
            session,
            stamp,
            candidate,
            previous,
            SetupIntegrityMerger.NotVerified,
            recorded: null,
            migrationRequired: false);

        switch (stamp.Phase)
        {
            case SetupTransactionPhase.Prepared:
            case SetupTransactionPhase.ActiveSwitchPending:
                if (state.Active is null)
                {
                    return previous is null
                        ? DiscardFreshTransaction(layout)
                        : RecoveryIntervention(stamp, "active_pointer_missing");
                }

                if (SamePointer(state.Active, candidate))
                {
                    return MapRecoveryResult(await RollbackAsync(context, RecoveryReason(stamp), sideEffect));
                }

                if (previous is not null && SamePointer(state.Active, previous))
                {
                    return await RestoreOldActiveAsync(layout, session, context, previous, cancellationToken);
                }

                return RecoveryIntervention(stamp, "active_pointer_unexpected");

            case SetupTransactionPhase.CandidateComposeValidating:
            case SetupTransactionPhase.MigrationPending:
            case SetupTransactionPhase.Migrating:
            case SetupTransactionPhase.Recreating:
            case SetupTransactionPhase.Inspecting:
            case SetupTransactionPhase.ReadinessChecking:
            case SetupTransactionPhase.RollbackPending:
                return MapRecoveryResult(await RollbackAsync(context, RecoveryReason(stamp), sideEffect));

            case SetupTransactionPhase.BindingPending:
            case SetupTransactionPhase.VerificationPending:
                return await ReverifyCandidateAsync(layout, session, state, context, sideEffect, cancellationToken);

            case SetupTransactionPhase.VerificationCommitted:
                return FinishCommittedTransaction(layout, session, state, stamp);

            default:
                return RecoveryIntervention(stamp, "transaction_phase_unknown");
        }
    }

    /// <summary>
    /// The interrupted apply never moved ACTIVE, so the previous generation is still the running one.
    /// Its verification record was already invalidated, so it has to be re-earned rather than assumed.
    /// </summary>
    private async Task<SetupApplyResult> RestoreOldActiveAsync(
        TrustedSetupHostLayout layout,
        SetupHostDockerSession session,
        ApplyContext context,
        SetupActivePointer previous,
        CancellationToken cancellationToken)
    {
        var validation = SetupBundleStaticValidator.TryValidateFinalizedBundle(
            _fileSystem,
            layout,
            previous.BundleId,
            out var recorded,
            out var hostAtRest);
        if (!validation.IsSuccess || recorded is null)
        {
            return RecoveryIntervention(context.Stamp, validation.Code ?? "previous_bundle_invalid");
        }

        var compose = await _adapter.ComposeExpectedActiveInputAsync(session, previous, cancellationToken);
        if (!compose.IsSuccess)
        {
            return RecoveryStillRequired(context.Stamp, compose.Code);
        }

        var verification = await VerifyGenerationAsync(layout, session, previous, recorded, hostAtRest, cancellationToken);
        if (!verification.IsSuccess)
        {
            _ = WriteVerificationRecord(
                layout,
                previous,
                SetupVerificationRecord.StatusInvalidated,
                verification,
                hostAtRest,
                SetupVerificationRecord.ReadinessNotEvaluated,
                SetupRuntimeIdentityBindingResult.Missing,
                committedAt: null);
            return RecoveryIntervention(context.Stamp, verification.ReasonCode!);
        }

        var readiness = await _adapter.AwaitMailerHealthyAsync(session, cancellationToken);
        if (!readiness.IsSuccess)
        {
            return RecoveryStillRequired(context.Stamp, "readiness_failed");
        }

        var drift = await _adapter.VerifyExternalInputsUnchangedAsync(session, cancellationToken);
        if (!drift.IsSuccess)
        {
            return RecoveryStillRequired(context.Stamp, drift.Code);
        }

        var commit = await CommitGenerationAsync(
            context,
            previous,
            verification,
            hostAtRest,
            migrationAttempted: false,
            SetupApplyResultCode.RollbackSucceeded,
            "The interrupted apply never activated; the previous generation was re-verified.");
        return commit;
    }

    /// <summary>
    /// The crash happened between readiness and the record commit, so the candidate may well be
    /// healthy. It is proven again from scratch instead of trusting the half-written transaction.
    /// </summary>
    private async Task<SetupApplyResult> ReverifyCandidateAsync(
        TrustedSetupHostLayout layout,
        SetupHostDockerSession session,
        DurableState state,
        ApplyContext context,
        bool sideEffect,
        CancellationToken cancellationToken)
    {
        if (state.Active is null || !SamePointer(state.Active, context.Candidate))
        {
            return RecoveryIntervention(context.Stamp, "active_pointer_unexpected");
        }

        var validation = SetupBundleStaticValidator.TryValidateFinalizedBundle(
            _fileSystem,
            layout,
            context.Candidate.BundleId,
            out var recorded,
            out var hostAtRest);
        if (!validation.IsSuccess || recorded is null)
        {
            return MapRecoveryResult(await RollbackAsync(context, validation.Code ?? "candidate_bundle_invalid", sideEffect));
        }

        var compose = await _adapter.ComposeExpectedActiveInputAsync(session, context.Candidate, cancellationToken);
        if (!compose.IsSuccess)
        {
            return MapRecoveryResult(await RollbackAsync(context, compose.Code ?? "compose_pin_failed", sideEffect));
        }

        var verification = await VerifyGenerationAsync(
            layout,
            session,
            context.Candidate,
            recorded,
            hostAtRest,
            cancellationToken);
        if (!verification.IsSuccess)
        {
            _ = WriteVerificationRecord(
                layout,
                context.Candidate,
                SetupVerificationRecord.StatusInvalidated,
                verification,
                hostAtRest,
                SetupVerificationRecord.ReadinessNotEvaluated,
                SetupRuntimeIdentityBindingResult.Missing,
                committedAt: null);
            return MapRecoveryResult(await RollbackAsync(context, verification.ReasonCode!, sideEffect));
        }

        var readiness = await _adapter.AwaitMailerHealthyAsync(session, cancellationToken);
        if (!readiness.IsSuccess)
        {
            return MapRecoveryResult(await RollbackAsync(context, "readiness_failed", sideEffect));
        }

        var drift = await _adapter.VerifyExternalInputsUnchangedAsync(session, cancellationToken);
        if (!drift.IsSuccess)
        {
            return MapRecoveryResult(await RollbackAsync(context, "external_input_changed_before_verification", sideEffect));
        }

        return await CommitGenerationAsync(
            context,
            context.Candidate,
            verification,
            hostAtRest,
            sideEffect,
            SetupApplyResultCode.ApplySucceeded,
            "The interrupted apply was re-verified and verification is committed.");
    }

    /// <summary>
    /// The record was already committed before the crash, so the transaction only has to be closed.
    /// ACTIVE, the record, and the binding must still agree; nothing is assumed from the stamp alone.
    /// </summary>
    private SetupApplyResult FinishCommittedTransaction(
        TrustedSetupHostLayout layout,
        SetupHostDockerSession session,
        DurableState state,
        SetupTransactionStamp stamp)
    {
        if (state.RecordUnreadable || state.BindingUnreadable)
        {
            return RecoveryIntervention(stamp, "durable_state_unreadable");
        }

        var active = state.Active;
        if (active is null)
        {
            return RecoveryIntervention(stamp, "active_pointer_missing");
        }

        if (!IsCommittedFor(state.VerificationRecord, active))
        {
            return RecoveryIntervention(stamp, "verification_record_missing");
        }

        if (!BindingMatches(state.RuntimeIdentityBinding, active, session.ExternalInputs!))
        {
            return RecoveryIntervention(stamp, "runtime_identity_binding_mismatch");
        }

        var previousDelete = _writer.TryDurableDelete(layout.ManagedRoot, layout.PreviousPointerPath);
        if (!previousDelete.IsSuccess)
        {
            return RecoveryIntervention(stamp, previousDelete.Code ?? "previous_pointer_delete_failed");
        }

        var stampDelete = DeleteStamp(layout);
        if (!stampDelete.IsSuccess)
        {
            return RecoveryIntervention(stamp, stampDelete.Code ?? "transaction_stamp_delete_failed");
        }

        return SetupApplyResult.Create(
            SetupApplyResultCode.ApplySucceeded,
            SetupManagedDeploymentState.Active,
            "The interrupted apply had already committed verification.",
            actionCode: SetupApplyActionCode.CompleteSendReadyEvaluation,
            bundleId: active.BundleId,
            activationGeneration: active.ActivationGeneration,
            configurationApplied: true,
            verificationCommitted: true,
            persistentSideEffectMayRemain: stamp.PersistentSideEffectMayRemain,
            persistentSideEffectKind: stamp.PersistentSideEffectKind);
    }

    private SetupApplyResult DiscardFreshTransaction(TrustedSetupHostLayout layout)
    {
        foreach (var path in new[] { layout.LastRecordPath, layout.RuntimeIdentityBindPath, layout.TransactionStampPath })
        {
            var delete = _writer.TryDurableDelete(layout.ManagedRoot, path);
            if (!delete.IsSuccess)
            {
                return Fail(
                    SetupApplyResultCode.NeedsIntervention,
                    SetupManagedDeploymentState.NeedsIntervention,
                    "Interrupted fresh apply state could not be cleared.",
                    actionCode: SetupApplyActionCode.ManualInterventionRequired,
                    reasonCode: delete.Code);
            }
        }

        return Fail(
            SetupApplyResultCode.RollbackSucceeded,
            SetupManagedDeploymentState.NoManaged,
            "The interrupted fresh apply had not activated; no Managed deployment is active.",
            configRollbackStatus: SetupConfigRollbackStatus.NotApplicable);
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

    private static string RecoveryReason(SetupTransactionStamp stamp) =>
        stamp.ReasonCode ?? "recovered_interrupted_apply";

    private static SetupApplyResult RecoveryIntervention(SetupTransactionStamp stamp, string reasonCode) =>
        Fail(
            SetupApplyResultCode.NeedsIntervention,
            SetupManagedDeploymentState.NeedsIntervention,
            "The interrupted transaction could not be converged automatically.",
            actionCode: stamp.PersistentSideEffectMayRemain
                ? SetupApplyActionCode.ReviewDatabaseSchema
                : SetupApplyActionCode.ManualInterventionRequired,
            reasonCode: reasonCode,
            persistentSideEffectMayRemain: stamp.PersistentSideEffectMayRemain,
            persistentSideEffectKind: stamp.PersistentSideEffectKind);

    private static SetupApplyResult RecoveryStillRequired(SetupTransactionStamp stamp, string? reasonCode) =>
        Fail(
            SetupApplyResultCode.RecoveryRequired,
            SetupManagedDeploymentState.RecoveryRequired,
            "Recovery could not complete; the transaction is still in flight.",
            reasonCode: reasonCode,
            persistentSideEffectMayRemain: stamp.PersistentSideEffectMayRemain,
            persistentSideEffectKind: stamp.PersistentSideEffectKind);

    // ------------------------------------------------------------ decisions

    private static SetupApplyResult TransactionPresentResult(SetupTransactionStamp stamp) =>
        stamp.Terminal
            ? Fail(
                SetupApplyResultCode.NeedsIntervention,
                SetupManagedDeploymentState.NeedsIntervention,
                "A previous apply ended in a terminal state that requires operator review.",
                actionCode: SetupApplyActionCode.ManualInterventionRequired,
                reasonCode: stamp.ReasonCode ?? "terminal_transaction_present",
                persistentSideEffectMayRemain: stamp.PersistentSideEffectMayRemain,
                persistentSideEffectKind: stamp.PersistentSideEffectKind)
            : Fail(
                SetupApplyResultCode.RecoveryRequired,
                SetupManagedDeploymentState.RecoveryRequired,
                "An interrupted apply transaction must be recovered before applying again.",
                reasonCode: "transaction_in_progress",
                persistentSideEffectMayRemain: stamp.PersistentSideEffectMayRemain,
                persistentSideEffectKind: stamp.PersistentSideEffectKind);

    /// <summary>
    /// Decides whether the existing ACTIVE generation is a legitimate rollback target. An ACTIVE we
    /// could not roll back to is a reason to refuse the apply, never a reason to switch anyway.
    /// </summary>
    private SetupApplyResult? ClassifyExistingActive(
        TrustedSetupHostLayout layout,
        DurableState state,
        SetupExternalInputSnapshot external,
        out SetupRecordedMetadata? activeRecorded)
    {
        activeRecorded = null;
        var active = state.Active!;

        if (state.RecordUnreadable || state.BindingUnreadable)
        {
            return Ineligible("durable_state_unreadable");
        }

        if (state.Previous is not null)
        {
            return Ineligible("previous_pointer_orphan");
        }

        if (!IsCommittedFor(state.VerificationRecord, active))
        {
            return Ineligible("verification_record_missing");
        }

        if (state.RuntimeIdentityBinding is null)
        {
            return Ineligible("runtime_identity_binding_missing");
        }

        if (!BindingMatches(state.RuntimeIdentityBinding, active, external))
        {
            return Ineligible("runtime_identity_binding_mismatch");
        }

        var validation = SetupBundleStaticValidator.TryValidateFinalizedBundle(
            _fileSystem,
            layout,
            active.BundleId,
            out activeRecorded,
            out _);
        if (!validation.IsSuccess || activeRecorded is null)
        {
            return Fail(
                SetupApplyResultCode.NeedsIntervention,
                SetupManagedDeploymentState.NeedsIntervention,
                "The existing ACTIVE bundle failed host at-rest validation.",
                actionCode: SetupApplyActionCode.ManualInterventionRequired,
                reasonCode: "active_bundle_invalid",
                bundleId: active.BundleId,
                activationGeneration: active.ActivationGeneration);
        }

        return null;

        SetupApplyResult Ineligible(string reasonCode) => Fail(
            SetupApplyResultCode.IneligibleExistingActive,
            SetupManagedDeploymentState.RecoveryRequired,
            "The existing Managed deployment must be recovered before another apply can start.",
            reasonCode: reasonCode,
            bundleId: active.BundleId,
            activationGeneration: active.ActivationGeneration);
    }

    private static SetupApplyResult? MapMigrationDecision(
        SetupMigrationDecision decision,
        SetupManagedDeploymentState preFailureState) => decision.Kind switch
        {
            SetupMigrationDecisionKind.UpgradeRequired => Fail(
                SetupApplyResultCode.UpgradeRequired,
                preFailureState,
                decision.Message,
                decision.ActionCode,
                decision.ReasonCode),
            SetupMigrationDecisionKind.NeedsIntervention => Fail(
                SetupApplyResultCode.NeedsIntervention,
                SetupManagedDeploymentState.NeedsIntervention,
                decision.Message,
                decision.ActionCode ?? SetupApplyActionCode.ManualInterventionRequired,
                decision.ReasonCode),
            _ => null,
        };

    private static string? ClassifyImageCompatibility(
        TrustedSetupHostLayout layout,
        SetupRecordedMetadata candidateRecorded)
    {
        var allowed = layout.ReleaseInventory.AllowedImageRepository;
        var recorded = candidateRecorded.ImageRepository;
        if (string.IsNullOrWhiteSpace(recorded) || string.IsNullOrWhiteSpace(allowed))
        {
            return "image_repository_unknown";
        }

        return string.Equals(recorded, allowed, StringComparison.Ordinal)
            ? null
            : "image_repository_mismatch";
    }

    private static string ImageReference(SetupRecordedMetadata recorded) =>
        (recorded.ImageRepository ?? string.Empty) + ":" + (recorded.ImageTag ?? string.Empty);

    // -------------------------------------------------------- verification

    /// <summary>
    /// Proves that the container the compose pin just started is really running this bundle. Host
    /// at-rest integrity, mount attestation, and the fingerprint comparison are merged with the
    /// runtime's own recorded identity, so a healthy container serving a different bundle, a
    /// different configuration, or an unsupported recorded schema cannot be committed as applied.
    /// </summary>
    private async Task<GenerationVerification> VerifyGenerationAsync(
        TrustedSetupHostLayout layout,
        SetupHostDockerSession session,
        SetupActivePointer pointer,
        SetupRecordedMetadata expected,
        string hostAtRest,
        CancellationToken cancellationToken)
    {
        if (!session.StaleVerifiersPurged)
        {
            return GenerationVerification.Failed("verifier_purge_not_asserted");
        }

        if (!SetupMountVerifierFactory.TryCreate(
                _fileSystem,
                layout,
                pointer.BundleId,
                _timeProvider.GetUtcNow(),
                out var verifier,
                out _)
            || verifier is null)
        {
            return GenerationVerification.Failed("mount_verifier_unavailable");
        }

        var inspection = await _adapter.RunEffectiveInspectionAsync(session, verifier, cancellationToken);
        if (!inspection.IsSuccess || inspection.Inspection is null)
        {
            return GenerationVerification.Failed("effective_inspection_failed");
        }

        var document = inspection.Inspection;
        var mountAttestation = MapAttestation(document.MountAttestation.Result);
        var bundleIntegrity = SetupIntegrityMerger.Merge(hostAtRest, mountAttestation);
        var fingerprintComparison = document.Effective.FingerprintsMatchRecorded switch
        {
            true => SetupVerificationRecord.FingerprintMatched,
            false => SetupVerificationRecord.FingerprintMismatch,
            _ => SetupVerificationRecord.FingerprintNotEvaluated,
        };

        var runtime = document.Recorded;
        var reasonCode = document switch
        {
            { Managed: false } => "runtime_not_managed",
            _ when runtime is null => "runtime_bundle_identity_missing",
            _ when !string.Equals(runtime.SetupBundleId, pointer.BundleId, StringComparison.Ordinal) =>
                "runtime_bundle_identity_mismatch",
            _ when !string.Equals(
                runtime.ConfigurationFingerprint,
                expected.ConfigurationFingerprint,
                StringComparison.Ordinal) => "runtime_configuration_fingerprint_mismatch",
            _ when runtime.SchemaVersion != SetupBundleLayout.RecordedSchemaVersion =>
                "runtime_schema_unsupported",
            _ when string.IsNullOrWhiteSpace(document.MailerVersion) => "runtime_version_unknown",
            _ when !string.Equals(bundleIntegrity, SetupIntegrityMerger.Matched, StringComparison.Ordinal) =>
                "bundle_integrity_mismatch",
            _ when !string.Equals(
                fingerprintComparison,
                SetupVerificationRecord.FingerprintMatched,
                StringComparison.Ordinal) => "fingerprint_mismatch",
            _ => null,
        };

        return new GenerationVerification
        {
            IsSuccess = reasonCode is null,
            ReasonCode = reasonCode,
            FingerprintComparison = fingerprintComparison,
            MountAttestation = mountAttestation,
            BundleIntegrity = bundleIntegrity,
            ObservedBundleId = runtime?.SetupBundleId,
            ObservedMailerVersion = document.MailerVersion,
            ObservedSchemaVersion = runtime?.SchemaVersion,
        };
    }

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

            var record = ReadVerificationRecord(layout, out var recordUnreadable);
            var bindingStamp = ReadRuntimeIdentityBinding(layout, out var bindingUnreadable);

            state = new DurableState
            {
                Active = active,
                Previous = previous,
                TransactionStamp = ReadStamp(layout),
                VerificationRecord = record,
                RecordUnreadable = recordUnreadable,
                RuntimeIdentityBinding = bindingStamp,
                BindingUnreadable = bindingUnreadable,
            };
            return null;
        }
        catch (IOException)
        {
            return StateReadFailure();
        }
        catch (UnauthorizedAccessException)
        {
            return StateReadFailure();
        }

        static SetupApplyResult StateReadFailure() => Fail(
            SetupApplyResultCode.FailedUnexpected,
            SetupManagedDeploymentState.NeedsIntervention,
            "Durable Managed state could not be read.",
            actionCode: SetupApplyActionCode.ManualInterventionRequired,
            reasonCode: "state_read_failed");
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

    private SetupVerificationRecord? ReadVerificationRecord(TrustedSetupHostLayout layout, out bool unreadable)
    {
        unreadable = false;
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
            if (record is null || record.SchemaVersion != SetupVerificationRecord.CurrentSchemaVersion)
            {
                unreadable = true;
                return null;
            }

            return record;
        }
        catch (JsonException)
        {
            unreadable = true;
            return null;
        }
    }

    private SetupRuntimeIdentityBindingStamp? ReadRuntimeIdentityBinding(
        TrustedSetupHostLayout layout,
        out bool unreadable)
    {
        unreadable = false;
        var path = layout.RuntimeIdentityBindPath;
        if (!_fileSystem.FileExists(path))
        {
            return null;
        }

        try
        {
            var stamp = JsonSerializer.Deserialize(
                _fileSystem.ReadAllBytes(path),
                SetupApplyJsonContext.Default.SetupRuntimeIdentityBindingStamp);
            if (stamp is null || stamp.SchemaVersion != SetupRuntimeIdentityBindingStamp.CurrentSchemaVersion)
            {
                unreadable = true;
                return null;
            }

            return stamp;
        }
        catch (JsonException)
        {
            unreadable = true;
            return null;
        }
    }

    private static bool IsCommittedFor(SetupVerificationRecord? record, SetupActivePointer active) =>
        record is not null
        && record.IsCommittedSuccess
        && string.Equals(record.BundleId, active.BundleId, StringComparison.Ordinal)
        && record.ActivationGeneration == active.ActivationGeneration;

    private static bool BindingMatches(
        SetupRuntimeIdentityBindingStamp? binding,
        SetupActivePointer active,
        SetupExternalInputSnapshot external) =>
        binding is not null
        && string.Equals(binding.BundleId, active.BundleId, StringComparison.Ordinal)
        && binding.ActivationGeneration == active.ActivationGeneration
        && CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(binding.BindingMac),
            Encoding.UTF8.GetBytes(external.BindingMac));

    private static bool SamePointer(SetupActivePointer left, SetupActivePointer right) =>
        string.Equals(left.BundleId, right.BundleId, StringComparison.Ordinal)
        && left.ActivationGeneration == right.ActivationGeneration;

    private static bool ComposeMatches(SetupHostDockerSession session, SetupActivePointer pointer) =>
        session.ComposeInputs is not null
        && string.Equals(session.ComposeInputs.ExpectedActiveBundleId, pointer.BundleId, StringComparison.Ordinal)
        && session.ComposeInputs.ExpectedActivationGeneration == pointer.ActivationGeneration;

    // ------------------------------------------------------ durable writes

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
        context.Stamp = context.Stamp with
        {
            Phase = phase,
            PersistentSideEffectMayRemain = persistentSideEffect,
            PersistentSideEffectKind = persistentSideEffect
                ? SetupPersistentSideEffectKind.DatabaseMigration
                : SetupPersistentSideEffectKind.None,
        };
        return WriteStamp(context.Layout, context.Stamp);
    }

    private SetupDockerResult AdvanceRollbackPhase(ApplyContext context, string phase)
    {
        context.Stamp = context.Stamp with { Phase = phase };
        return WriteStamp(context.Layout, context.Stamp);
    }

    /// <summary>
    /// Advances the transaction phase and turns a failed write into a rollback. A side effect whose
    /// write-ahead record did not land must not happen, so the caller never continues past this.
    /// </summary>
    private async Task<SetupApplyResult?> GateAsync(
        ApplyContext context,
        string phase,
        bool persistentSideEffect,
        bool migrationAttempted)
    {
        var write = AdvancePhase(context, phase, persistentSideEffect);
        return write.IsSuccess
            ? null
            : await RollbackAsync(context, "durable_write_failed", migrationAttempted);
    }

    /// <summary>
    /// A pre-activation failure after the verification record was invalidated. ACTIVE has not moved,
    /// but its record no longer vouches for it, so the transaction stays for recovery instead of
    /// being silently dropped.
    /// </summary>
    private SetupApplyResult AbortBeforeActivation(ApplyContext context, string reasonCode)
    {
        if (context.Previous is not null)
        {
            return Fail(
                SetupApplyResultCode.RecoveryRequired,
                SetupManagedDeploymentState.RecoveryRequired,
                "The apply stopped before activation; the existing generation must be re-verified.",
                reasonCode: reasonCode,
                bundleId: context.Previous.BundleId,
                activationGeneration: context.Previous.ActivationGeneration);
        }

        var stampDelete = DeleteStamp(context.Layout);
        if (!stampDelete.IsSuccess)
        {
            return Fail(
                SetupApplyResultCode.NeedsIntervention,
                SetupManagedDeploymentState.NeedsIntervention,
                "The abandoned transaction stamp could not be cleared.",
                actionCode: SetupApplyActionCode.ManualInterventionRequired,
                reasonCode: stampDelete.Code);
        }

        return Fail(
            SetupApplyResultCode.FreshApplyFailed,
            SetupManagedDeploymentState.NoManaged,
            "Fresh Managed apply stopped before activation.",
            reasonCode: reasonCode,
            configRollbackStatus: SetupConfigRollbackStatus.NotApplicable);
    }

    private SetupApplyResult? DiscardPreviousPointer(
        TrustedSetupHostLayout layout,
        SetupActivePointer? previousPointer)
    {
        if (previousPointer is null)
        {
            return null;
        }

        var delete = _writer.TryDurableDelete(layout.ManagedRoot, layout.PreviousPointerPath);
        return delete.IsSuccess
            ? null
            : Fail(
                SetupApplyResultCode.NeedsIntervention,
                SetupManagedDeploymentState.NeedsIntervention,
                "The previous pointer written for an abandoned transaction could not be cleared.",
                actionCode: SetupApplyActionCode.ManualInterventionRequired,
                reasonCode: delete.Code);
    }

    private static SetupApplyResult CommittedButUnfinished(
        SetupActivePointer pointer,
        string? reasonCode,
        bool migrationAttempted) =>
        Fail(
            SetupApplyResultCode.NeedsIntervention,
            SetupManagedDeploymentState.NeedsIntervention,
            "Verification was committed but the transaction could not be finalized.",
            actionCode: SetupApplyActionCode.ManualInterventionRequired,
            reasonCode: reasonCode,
            bundleId: pointer.BundleId,
            activationGeneration: pointer.ActivationGeneration,
            configurationApplied: true,
            verificationCommitted: true,
            persistentSideEffectMayRemain: migrationAttempted,
            persistentSideEffectKind: migrationAttempted
                ? SetupPersistentSideEffectKind.DatabaseMigration
                : SetupPersistentSideEffectKind.None);

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
        var existing = ReadVerificationRecord(layout, out _);
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
        TrustedSetupHostLayout layout,
        SetupActivePointer pointer,
        string status,
        GenerationVerification verification,
        string hostAtRest,
        string readiness,
        string runtimeIdentityBinding,
        string? committedAt)
    {
        var record = new SetupVerificationRecord
        {
            SchemaVersion = SetupVerificationRecord.CurrentSchemaVersion,
            Status = status,
            BundleId = pointer.BundleId,
            ActivationGeneration = pointer.ActivationGeneration,
            FingerprintComparison = verification.FingerprintComparison,
            HostAtRest = hostAtRest,
            MountAttestation = verification.MountAttestation,
            BundleIntegrity = verification.BundleIntegrity,
            ImageReference = layout.ReleaseInventory.PinnedMailerImageReference,
            ComposeIdentity = SetupBundleStaticValidator.ComputeComposeIdentity(layout.ReleaseInventory),
            ObservedBundleId = verification.ObservedBundleId,
            ObservedMailerVersion = verification.ObservedMailerVersion,
            RecordedSchemaVersion = verification.ObservedSchemaVersion,
            RuntimeIdentityBinding = runtimeIdentityBinding,
            Readiness = readiness,
            SendReadyEvaluation = SetupVerificationRecord.SendReadyNotEvaluated,
            CommittedAt = committedAt,
        };

        return _writer.TryAtomicReplaceJson(
            layout.ManagedRoot,
            layout.LastRecordPath,
            record,
            SetupApplyJsonContext.Default.SetupVerificationRecord);
    }

    private SetupDockerResult WriteRuntimeIdentityBinding(
        TrustedSetupHostLayout layout,
        SetupHostDockerSession session,
        SetupActivePointer pointer)
    {
        var external = session.ExternalInputs;
        if (external is null)
        {
            return SetupDockerResult.Fail(
                SetupDockerResultCode.ExternalInputNotPinned,
                "External inputs must be pinned before writing the runtime-identity binding.");
        }

        var stampDocument = new SetupRuntimeIdentityBindingStamp
        {
            SchemaVersion = SetupRuntimeIdentityBindingStamp.CurrentSchemaVersion,
            BundleId = pointer.BundleId,
            ActivationGeneration = pointer.ActivationGeneration,
            BindingMac = external.BindingMac,
        };

        return _writer.TryAtomicReplaceJson(
            layout.ManagedRoot,
            layout.RuntimeIdentityBindPath,
            stampDocument,
            SetupApplyJsonContext.Default.SetupRuntimeIdentityBindingStamp);
    }

    private SetupDockerResult DeleteBindingForGeneration(
        TrustedSetupHostLayout layout,
        SetupActivePointer pointer)
    {
        var existing = ReadRuntimeIdentityBinding(layout, out var unreadable);
        if (!unreadable
            && (existing is null
                || !string.Equals(existing.BundleId, pointer.BundleId, StringComparison.Ordinal)
                || existing.ActivationGeneration != pointer.ActivationGeneration))
        {
            return SetupDockerResult.Ok();
        }

        return _writer.TryDurableDelete(layout.ManagedRoot, layout.RuntimeIdentityBindPath);
    }

    private string Timestamp() =>
        _timeProvider.GetUtcNow().ToString("yyyy-MM-ddTHH:mm:ss.fffffffZ", CultureInfo.InvariantCulture);

    private static SetupApplyResult CancelledBeforeActivation() =>
        SetupApplyResult.Create(
            SetupApplyResultCode.CancelledBeforeActivation,
            SetupManagedDeploymentState.NotInspected,
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
        public bool RecordUnreadable { get; init; }
        public SetupRuntimeIdentityBindingStamp? RuntimeIdentityBinding { get; init; }
        public bool BindingUnreadable { get; init; }
    }

    private sealed record GenerationVerification
    {
        public required bool IsSuccess { get; init; }
        public string? ReasonCode { get; init; }
        public required string FingerprintComparison { get; init; }
        public required string MountAttestation { get; init; }
        public required string BundleIntegrity { get; init; }
        public string? ObservedBundleId { get; init; }
        public string? ObservedMailerVersion { get; init; }
        public int? ObservedSchemaVersion { get; init; }

        public static GenerationVerification Failed(string reasonCode) => new()
        {
            IsSuccess = false,
            ReasonCode = reasonCode,
            FingerprintComparison = SetupVerificationRecord.FingerprintNotEvaluated,
            MountAttestation = SetupIntegrityMerger.NotVerified,
            BundleIntegrity = SetupIntegrityMerger.NotVerified,
        };
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
            CandidateRecorded = recorded;
            MigrationRequired = migrationRequired;
        }

        public TrustedSetupHostLayout Layout { get; }
        public SetupHostDockerSession Session { get; }
        public SetupTransactionStamp Stamp { get; set; }
        public SetupActivePointer Candidate { get; }
        public SetupActivePointer? Previous { get; }
        public string CandidateHostAtRest { get; }
        public SetupRecordedMetadata? CandidateRecorded { get; }
        public bool MigrationRequired { get; }
    }
}
