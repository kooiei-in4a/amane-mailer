using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using Amane.Mailer.Operations;
using Amane.Mailer.Setup;

namespace Amane.Mailer.Tests.Setup;

/// <summary>
/// Behaviour tests for the Managed apply / rollback / recovery engine (#450).
/// Docker is replaced by a scripted process runner and the filesystem by an instrumented decorator,
/// so durable-write failures and mid-transaction drift can be injected at exact points. Everything
/// else — bundles, seals, pointers, stamps, verification records, bindings — is real on-disk state,
/// so ordering bugs surface instead of being stubbed away.
/// </summary>
public sealed class SetupApplyEngineTests
{
    private const string ActiveBundleId = "bundle-active01";
    private const string CandidateBundleId = "bundle-candidate01";

    private const string TestDigest =
        "sha256:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";

    // ------------------------------------------------------------ fresh apply

    [Fact]
    public async Task Fresh_apply_activates_the_candidate_and_commits_verification()
    {
        using var harness = ApplyHarness.Create();
        harness.SeedBundle(CandidateBundleId);

        var result = await harness.ApplyAsync(CandidateBundleId);

        Assert.Equal(SetupApplyResultCode.ApplySucceeded, result.Code);
        Assert.Equal(SetupManagedDeploymentState.Active, result.DeploymentState);
        Assert.Equal(CandidateBundleId, result.BundleId);
        Assert.Equal(1, result.ActivationGeneration);
        Assert.True(result.ConfigurationApplied);
        Assert.True(result.VerificationCommitted);
        Assert.Equal(CandidateBundleId, harness.ReadActive()!.BundleId);
        Assert.False(File.Exists(harness.Layout.TransactionStampPath));
        Assert.False(File.Exists(harness.Layout.PreviousPointerPath));
        Assert.True(File.Exists(harness.Layout.RuntimeIdentityBindPath));

        var record = harness.ReadRecord();
        Assert.NotNull(record);
        Assert.True(record!.IsCommittedSuccess);
        Assert.Equal(SetupIntegrityMerger.Matched, record.BundleIntegrity);
    }

    [Fact]
    public async Task Committed_record_carries_the_identity_the_container_reported()
    {
        using var harness = ApplyHarness.Create();
        harness.SeedBundle(CandidateBundleId);

        Assert.Equal(SetupApplyResultCode.ApplySucceeded, (await harness.ApplyAsync(CandidateBundleId)).Code);

        var record = harness.ReadRecord()!;
        Assert.Equal(CandidateBundleId, record.ObservedBundleId);
        Assert.Equal(ApplyProcessRunner.MailerVersion, record.ObservedMailerVersion);
        Assert.Equal(SetupBundleLayout.RecordedSchemaVersion, record.RecordedSchemaVersion);
    }

    [Fact]
    public async Task Fresh_apply_runs_the_migration_because_the_database_is_absent()
    {
        using var harness = ApplyHarness.Create();
        harness.SeedBundle(CandidateBundleId);

        var result = await harness.ApplyAsync(CandidateBundleId);

        Assert.Equal(SetupApplyResultCode.ApplySucceeded, result.Code);
        Assert.Contains(harness.Invocations, IsMigrationRun);
    }

    [Fact]
    public async Task Fresh_apply_refuses_when_a_database_file_already_exists()
    {
        using var harness = ApplyHarness.Create();
        harness.SeedBundle(CandidateBundleId);
        File.WriteAllText(Path.Combine(harness.DataPath, "mailer.db"), string.Empty);

        var result = await harness.ApplyAsync(CandidateBundleId);

        Assert.Equal(SetupApplyResultCode.UpgradeRequired, result.Code);
        Assert.Equal("fresh_database_exists", result.ReasonCode);
        Assert.Null(harness.ReadActive());
    }

    [Fact]
    public async Task Fresh_apply_refuses_sqlite_sidecar_residue_without_a_main_database()
    {
        using var harness = ApplyHarness.Create();
        harness.SeedBundle(CandidateBundleId);
        File.WriteAllText(Path.Combine(harness.DataPath, "mailer.db-wal"), string.Empty);

        var result = await harness.ApplyAsync(CandidateBundleId);

        Assert.Equal(SetupApplyResultCode.NeedsIntervention, result.Code);
        Assert.Equal("sqlite_sidecar_residue", result.ReasonCode);
        Assert.Equal(SetupApplyActionCode.ReviewDatabaseFiles, result.ActionCode);
        Assert.Null(harness.ReadActive());
    }

    [Fact]
    public async Task Candidate_bundle_id_must_be_safe()
    {
        using var harness = ApplyHarness.Create();

        var result = await harness.ApplyAsync("../escape");

        Assert.Equal(SetupApplyResultCode.FailedUnexpected, result.Code);
        Assert.Equal("candidate_bundle_id_invalid", result.ReasonCode);
        Assert.Equal(SetupManagedDeploymentState.NotInspected, result.DeploymentState);
        Assert.Empty(harness.Invocations);
    }

    [Fact]
    public async Task Candidate_bundle_must_pass_host_at_rest_validation_before_anything_is_started()
    {
        using var harness = ApplyHarness.Create();
        harness.SeedBundle(CandidateBundleId);
        harness.CorruptBundleSeal(CandidateBundleId);

        var result = await harness.ApplyAsync(CandidateBundleId);

        Assert.Equal(SetupApplyResultCode.FreshApplyFailed, result.Code);
        Assert.Equal(SetupDockerResultCode.InvalidBundleInventory, result.ReasonCode);
        Assert.Null(harness.ReadActive());
        Assert.Empty(harness.Invocations);
    }

    // --------------------------------------------------------- existing active

    [Fact]
    public async Task Existing_apply_activates_a_new_generation_when_the_schema_is_current()
    {
        using var harness = ApplyHarness.Create();
        await harness.SeedActiveDeploymentAsync(ActiveBundleId);
        harness.SeedBundle(CandidateBundleId);

        var result = await harness.ApplyAsync(CandidateBundleId);

        Assert.Equal(SetupApplyResultCode.ApplySucceeded, result.Code);
        Assert.Equal(2, result.ActivationGeneration);
        Assert.Equal(CandidateBundleId, harness.ReadActive()!.BundleId);
        Assert.DoesNotContain(harness.Invocations, IsMigrationRun);

        // PREVIOUS is transaction scoped: a finished apply must not leave a rollback target behind.
        Assert.False(File.Exists(harness.Layout.PreviousPointerPath));
        Assert.True(harness.ReadRecord()!.IsCommittedSuccess);
        Assert.Equal(2, harness.ReadRecord()!.ActivationGeneration);
    }

    [Fact]
    public async Task Existing_apply_refuses_when_the_active_generation_has_no_committed_record()
    {
        using var harness = ApplyHarness.Create();
        await harness.SeedActiveDeploymentAsync(ActiveBundleId);
        harness.SeedBundle(CandidateBundleId);
        File.Delete(harness.Layout.LastRecordPath);

        var result = await harness.ApplyAsync(CandidateBundleId);

        Assert.Equal(SetupApplyResultCode.IneligibleExistingActive, result.Code);
        Assert.Equal(SetupManagedDeploymentState.RecoveryRequired, result.DeploymentState);
        Assert.Equal("verification_record_missing", result.ReasonCode);
        Assert.Equal(ActiveBundleId, harness.ReadActive()!.BundleId);
    }

    [Fact]
    public async Task Existing_apply_refuses_when_the_runtime_identity_binding_is_missing()
    {
        using var harness = ApplyHarness.Create();
        await harness.SeedActiveDeploymentAsync(ActiveBundleId);
        harness.SeedBundle(CandidateBundleId);
        File.Delete(harness.Layout.RuntimeIdentityBindPath);

        var result = await harness.ApplyAsync(CandidateBundleId);

        Assert.Equal(SetupApplyResultCode.IneligibleExistingActive, result.Code);
        Assert.Equal("runtime_identity_binding_missing", result.ReasonCode);
    }

    [Fact]
    public async Task Existing_apply_refuses_when_the_runtime_identity_binding_no_longer_matches()
    {
        using var harness = ApplyHarness.Create();
        await harness.SeedActiveDeploymentAsync(ActiveBundleId);
        harness.SeedBundle(CandidateBundleId);

        // A data path move changes the binding input, so the stored stamp no longer describes it.
        harness.MutateExternalEnv();

        var result = await harness.ApplyAsync(CandidateBundleId);

        Assert.Equal(SetupApplyResultCode.IneligibleExistingActive, result.Code);
        Assert.Equal("runtime_identity_binding_mismatch", result.ReasonCode);
    }

    [Fact]
    public async Task Existing_apply_refuses_while_an_orphaned_previous_pointer_exists()
    {
        using var harness = ApplyHarness.Create();
        await harness.SeedActiveDeploymentAsync(ActiveBundleId);
        harness.SeedBundle(CandidateBundleId);
        harness.WritePrevious(ActiveBundleId, generation: 1);

        var result = await harness.ApplyAsync(CandidateBundleId);

        Assert.Equal(SetupApplyResultCode.IneligibleExistingActive, result.Code);
        Assert.Equal("previous_pointer_orphan", result.ReasonCode);
    }

    [Fact]
    public async Task Existing_apply_refuses_when_the_schema_is_behind()
    {
        using var harness = ApplyHarness.Create();
        await harness.SeedActiveDeploymentAsync(ActiveBundleId);
        harness.SeedBundle(CandidateBundleId);
        harness.Runner.MigrationClassification = SetupSchemaClassification.Behind;

        var result = await harness.ApplyAsync(CandidateBundleId);

        Assert.Equal(SetupApplyResultCode.UpgradeRequired, result.Code);
        Assert.Equal("schema_behind", result.ReasonCode);
        Assert.Equal(ActiveBundleId, harness.ReadActive()!.BundleId);
    }

    [Fact]
    public async Task Existing_apply_refuses_when_the_schema_is_ahead_or_unsupported()
    {
        using var harness = ApplyHarness.Create();
        await harness.SeedActiveDeploymentAsync(ActiveBundleId);
        harness.SeedBundle(CandidateBundleId);
        harness.Runner.MigrationClassification = SetupSchemaClassification.AheadOrUnsupported;

        var result = await harness.ApplyAsync(CandidateBundleId);

        Assert.Equal(SetupApplyResultCode.UpgradeRequired, result.Code);
        Assert.Equal("schema_ahead_or_unsupported", result.ReasonCode);
    }

    [Fact]
    public async Task Existing_apply_needs_intervention_when_the_schema_cannot_be_classified()
    {
        using var harness = ApplyHarness.Create();
        await harness.SeedActiveDeploymentAsync(ActiveBundleId);
        harness.SeedBundle(CandidateBundleId);
        harness.Runner.MigrationClassification = SetupSchemaClassification.Unknown;

        var result = await harness.ApplyAsync(CandidateBundleId);

        Assert.Equal(SetupApplyResultCode.NeedsIntervention, result.Code);
        Assert.Equal(SetupApplyActionCode.ReviewDatabaseSchema, result.ActionCode);
        Assert.Equal(ActiveBundleId, harness.ReadActive()!.BundleId);
    }

    [Fact]
    public async Task Existing_apply_needs_intervention_when_the_active_bundle_is_no_longer_valid()
    {
        using var harness = ApplyHarness.Create();
        await harness.SeedActiveDeploymentAsync(ActiveBundleId);
        harness.SeedBundle(CandidateBundleId);
        harness.CorruptBundleSeal(ActiveBundleId);

        var result = await harness.ApplyAsync(CandidateBundleId);

        Assert.Equal(SetupApplyResultCode.NeedsIntervention, result.Code);
        Assert.Equal("active_bundle_invalid", result.ReasonCode);
    }

    // ------------------------------------------------------ lock and ordering

    [Fact]
    public async Task Durable_state_is_read_only_after_the_apply_lock_is_held()
    {
        using var harness = ApplyHarness.Create();
        harness.SeedBundle(CandidateBundleId);

        // Another apply finishes while this one waits for the lock. Reading state before the lock
        // would miss the transaction it left behind and happily start a second one.
        harness.FileSystem.OnOpenExclusiveGenerationLock = path =>
        {
            if (string.Equals(path, harness.Layout.ApplyLockPath, StringComparison.OrdinalIgnoreCase))
            {
                harness.WriteStamp(SetupTransactionPhase.Recreating, terminal: false);
            }
        };

        var result = await harness.ApplyAsync(CandidateBundleId);

        Assert.Equal(SetupApplyResultCode.RecoveryRequired, result.Code);
        Assert.Equal("transaction_in_progress", result.ReasonCode);
        Assert.Null(harness.ReadActive());
    }

    [Fact]
    public async Task Apply_refuses_while_an_interrupted_transaction_is_present()
    {
        using var harness = ApplyHarness.Create();
        harness.SeedBundle(CandidateBundleId);
        harness.WriteStamp(SetupTransactionPhase.Recreating, terminal: false);

        var result = await harness.ApplyAsync(CandidateBundleId);

        Assert.Equal(SetupApplyResultCode.RecoveryRequired, result.Code);
        Assert.Equal("transaction_in_progress", result.ReasonCode);
        Assert.Empty(harness.Invocations);
    }

    [Fact]
    public async Task Apply_refuses_while_a_terminal_transaction_is_present()
    {
        using var harness = ApplyHarness.Create();
        harness.SeedBundle(CandidateBundleId);
        harness.WriteStamp(SetupTransactionPhase.RollbackPending, terminal: true);

        var result = await harness.ApplyAsync(CandidateBundleId);

        Assert.Equal(SetupApplyResultCode.NeedsIntervention, result.Code);
        Assert.Equal(SetupApplyActionCode.ManualInterventionRequired, result.ActionCode);
    }

    [Fact]
    public async Task Apply_reports_concurrent_rejection_when_the_apply_lock_is_held()
    {
        using var harness = ApplyHarness.Create();
        harness.SeedBundle(CandidateBundleId);

        var (probe, binding) = await harness.Adapter.CheckDockerAsync(TestContext.Current.CancellationToken);
        Assert.True(probe.IsSuccess);
        var (held, session) = await harness.Adapter.AcquireSessionAsync(
            harness.Layout,
            binding!,
            TestContext.Current.CancellationToken);
        Assert.True(held.IsSuccess);

        await using (session)
        {
            var result = await harness.ApplyAsync(CandidateBundleId);
            Assert.Equal(SetupApplyResultCode.ConcurrentApplyRejected, result.Code);
            Assert.Equal(SetupManagedDeploymentState.NotInspected, result.DeploymentState);
        }
    }

    [Fact]
    public async Task Apply_refuses_unsafe_residue_in_the_verifier_temp_directory()
    {
        using var harness = ApplyHarness.Create();
        harness.SeedBundle(CandidateBundleId);
        Directory.CreateDirectory(harness.Layout.VerifierTempDir);
        File.WriteAllText(Path.Combine(harness.Layout.VerifierTempDir, "attacker.json"), "{}");

        var result = await harness.ApplyAsync(CandidateBundleId);

        Assert.Equal(SetupApplyResultCode.NeedsIntervention, result.Code);
        Assert.Equal(SetupApplyActionCode.UnsafeVerifierResidue, result.ActionCode);
        Assert.Equal("unsafe_verifier_residue", result.ReasonCode);
        Assert.Null(harness.ReadActive());
    }

    [Fact]
    public async Task Apply_refuses_when_the_pinned_image_is_unavailable()
    {
        using var harness = ApplyHarness.Create();
        harness.SeedBundle(CandidateBundleId);
        harness.Runner.FailWhen = static args => args.Contains("pull", StringComparer.Ordinal);

        var result = await harness.ApplyAsync(CandidateBundleId);

        Assert.Equal(SetupApplyResultCode.FreshApplyFailed, result.Code);
        Assert.Null(harness.ReadActive());
    }

    // ------------------------------------------- external input checkpoints

    [Fact]
    public async Task Checkpoint_one_stops_the_apply_before_the_transaction_is_created()
    {
        using var harness = ApplyHarness.Create();
        harness.SeedBundle(CandidateBundleId);

        // The image pull is the last step before the first checkpoint.
        harness.Runner.BeforeRun = args =>
        {
            if (args.Contains("pull", StringComparer.Ordinal))
            {
                harness.MutateExternalEnv();
            }
        };

        var result = await harness.ApplyAsync(CandidateBundleId);

        Assert.Equal(SetupApplyResultCode.FreshApplyFailed, result.Code);
        Assert.Equal("external_input_changed_before_transaction", result.ReasonCode);
        Assert.Null(harness.ReadActive());
        Assert.False(File.Exists(harness.Layout.TransactionStampPath));
    }

    [Fact]
    public async Task Checkpoint_two_stops_the_apply_before_active_is_replaced()
    {
        using var harness = ApplyHarness.Create();
        harness.SeedBundle(CandidateBundleId);

        // Drift injected exactly between the ActiveSwitchPending stamp and the ACTIVE replace.
        harness.FileSystem.OnCommit = (path, content) =>
        {
            if (IsStamp(harness, path) && content.Contains(Phase(SetupTransactionPhase.ActiveSwitchPending), StringComparison.Ordinal))
            {
                harness.MutateExternalEnv();
            }
        };

        var result = await harness.ApplyAsync(CandidateBundleId);

        Assert.Equal(SetupApplyResultCode.FreshApplyFailed, result.Code);
        Assert.Equal("external_input_changed_before_activation", result.ReasonCode);
        Assert.Null(harness.ReadActive());
        Assert.False(File.Exists(harness.Layout.TransactionStampPath));
        Assert.False(File.Exists(harness.Layout.LastRecordPath));
        Assert.False(File.Exists(harness.Layout.RuntimeIdentityBindPath));
    }

    [Fact]
    public async Task Checkpoint_three_stops_the_apply_before_the_record_is_committed()
    {
        using var harness = ApplyHarness.Create();
        await harness.SeedActiveDeploymentAsync(ActiveBundleId);
        harness.SeedBundle(CandidateBundleId);

        // Readiness is the last step before the final checkpoint, and inspection already ran.
        harness.Runner.BeforeRun = args =>
        {
            if (IsHealthCheck(args))
            {
                harness.MutateExternalEnv();
            }
        };

        var result = await harness.ApplyAsync(CandidateBundleId);

        // Drifted external input also blocks the rollback: restoring the previous generation on top
        // of inputs nobody verified would be a guess, so this converges on intervention instead.
        Assert.Equal(SetupApplyResultCode.ApplyFailedRollbackFailed, result.Code);
        Assert.Equal("external_input_changed_before_verification", result.ReasonCode);
        Assert.Equal(SetupManagedDeploymentState.NeedsIntervention, result.DeploymentState);
        Assert.Equal(SetupConfigRollbackStatus.Failed, result.ConfigRollbackStatus);
        Assert.Equal(CandidateBundleId, harness.ReadRecord()!.BundleId);
        Assert.Equal(SetupVerificationRecord.StatusInvalidated, harness.ReadRecord()!.Status);
        Assert.True(File.Exists(harness.Layout.TransactionStampPath));
    }

    // ------------------------------------------------- durable write failures

    [Fact]
    public async Task A_migration_never_runs_when_its_write_ahead_record_cannot_be_stored()
    {
        using var harness = ApplyHarness.Create();
        harness.SeedBundle(CandidateBundleId);
        harness.FileSystem.FailCommitWhen = (path, content) =>
            IsStamp(harness, path) && content.Contains(Phase(SetupTransactionPhase.Migrating), StringComparison.Ordinal);

        var result = await harness.ApplyAsync(CandidateBundleId);

        Assert.DoesNotContain(harness.Invocations, IsMigrationRun);
        Assert.Equal(SetupApplyResultCode.FreshApplyFailed, result.Code);
        Assert.Equal("durable_write_failed", result.ReasonCode);
        Assert.False(result.PersistentSideEffectMayRemain);
        Assert.Null(harness.ReadActive());
    }

    [Fact]
    public async Task A_failed_verification_record_commit_is_not_reported_as_success()
    {
        using var harness = ApplyHarness.Create();
        await harness.SeedActiveDeploymentAsync(ActiveBundleId);
        harness.SeedBundle(CandidateBundleId);
        harness.FileSystem.FailCommitWhen = (path, content) =>
            IsRecord(harness, path) && content.Contains("\"status\":\"committed\"", StringComparison.Ordinal);

        var result = await harness.ApplyAsync(CandidateBundleId);

        Assert.NotEqual(SetupApplyResultCode.ApplySucceeded, result.Code);
        Assert.False(result.VerificationCommitted);
        Assert.Equal("verification_record_failed", result.ReasonCode);
    }

    [Fact]
    public async Task A_failed_binding_commit_stops_the_apply_before_the_record_is_written()
    {
        using var harness = ApplyHarness.Create();
        await harness.SeedActiveDeploymentAsync(ActiveBundleId);
        harness.SeedBundle(CandidateBundleId);
        harness.FileSystem.FailCommitWhen = (path, _) => IsBinding(harness, path);

        var result = await harness.ApplyAsync(CandidateBundleId);

        Assert.NotEqual(SetupApplyResultCode.ApplySucceeded, result.Code);
        Assert.Equal("runtime_identity_binding_failed", result.ReasonCode);
        Assert.False(result.VerificationCommitted);
    }

    [Fact]
    public async Task A_failed_rollback_stamp_write_reports_a_failed_rollback()
    {
        using var harness = ApplyHarness.Create();
        await harness.SeedActiveDeploymentAsync(ActiveBundleId);
        harness.SeedBundle(CandidateBundleId);
        harness.Runner.FailWhen = static args => IsComposeSubcommand(args, "config");
        harness.FileSystem.FailCommitWhen = (path, content) =>
            IsStamp(harness, path) && content.Contains("\"kind\":\"Rollback\"", StringComparison.Ordinal);

        var result = await harness.ApplyAsync(CandidateBundleId);

        Assert.Equal(SetupApplyResultCode.ApplyFailedRollbackFailed, result.Code);
        Assert.Equal(SetupConfigRollbackStatus.Failed, result.ConfigRollbackStatus);
        Assert.Equal(SetupManagedDeploymentState.NeedsIntervention, result.DeploymentState);
    }

    // --------------------------------------------- container identity proof

    [Fact]
    public async Task A_container_serving_a_different_bundle_is_never_committed()
    {
        using var harness = ApplyHarness.Create();
        harness.SeedBundle(CandidateBundleId);
        harness.Runner.RecordedBundleIdOverride = "bundle-someother1";

        var result = await harness.ApplyAsync(CandidateBundleId);

        Assert.Equal("runtime_bundle_identity_mismatch", result.ReasonCode);
        Assert.False(result.VerificationCommitted);
        Assert.Equal(SetupVerificationRecord.StatusInvalidated, harness.ReadRecord()!.Status);
    }

    [Fact]
    public async Task A_container_reporting_a_different_configuration_is_never_committed()
    {
        using var harness = ApplyHarness.Create();
        harness.SeedBundle(CandidateBundleId);
        harness.Runner.RecordedFingerprintOverride = new string('b', 64);

        var result = await harness.ApplyAsync(CandidateBundleId);

        Assert.Equal("runtime_configuration_fingerprint_mismatch", result.ReasonCode);
        Assert.False(result.VerificationCommitted);
    }

    [Fact]
    public async Task A_container_reporting_an_unsupported_recorded_schema_is_never_committed()
    {
        using var harness = ApplyHarness.Create();
        harness.SeedBundle(CandidateBundleId);
        harness.Runner.RecordedSchemaVersionOverride = SetupBundleLayout.RecordedSchemaVersion + 1;

        var result = await harness.ApplyAsync(CandidateBundleId);

        Assert.Equal("runtime_schema_unsupported", result.ReasonCode);
        Assert.False(result.VerificationCommitted);
    }

    [Fact]
    public async Task An_inspection_without_runtime_identity_is_never_committed()
    {
        using var harness = ApplyHarness.Create();
        harness.SeedBundle(CandidateBundleId);
        harness.Runner.OmitRecorded = true;

        var result = await harness.ApplyAsync(CandidateBundleId);

        Assert.Equal("runtime_bundle_identity_missing", result.ReasonCode);
        Assert.False(result.VerificationCommitted);
    }

    [Fact]
    public async Task An_unmanaged_container_is_never_committed()
    {
        using var harness = ApplyHarness.Create();
        harness.SeedBundle(CandidateBundleId);
        harness.Runner.Managed = false;

        var result = await harness.ApplyAsync(CandidateBundleId);

        Assert.Equal("runtime_not_managed", result.ReasonCode);
        Assert.False(result.VerificationCommitted);
    }

    [Fact]
    public async Task Mount_attestation_mismatch_invalidates_the_verification_record()
    {
        using var harness = ApplyHarness.Create();
        harness.SeedBundle(CandidateBundleId);
        harness.Runner.MountAttestationResult = SetupInspectIntegrityResult.Mismatch;

        var result = await harness.ApplyAsync(CandidateBundleId);

        Assert.Equal("bundle_integrity_mismatch", result.ReasonCode);
        Assert.False(result.VerificationCommitted);

        var record = harness.ReadRecord();
        Assert.NotNull(record);
        Assert.Equal(SetupVerificationRecord.StatusInvalidated, record!.Status);
        Assert.False(record.IsCommittedSuccess);
    }

    [Fact]
    public async Task Fingerprint_mismatch_is_refused_even_when_integrity_matches()
    {
        using var harness = ApplyHarness.Create();
        harness.SeedBundle(CandidateBundleId);
        harness.Runner.FingerprintsMatchRecorded = false;

        var result = await harness.ApplyAsync(CandidateBundleId);

        Assert.Equal("fingerprint_mismatch", result.ReasonCode);
        Assert.Equal(
            SetupVerificationRecord.FingerprintMismatch,
            harness.ReadRecord()!.FingerprintComparison);
    }

    [Fact]
    public async Task Failed_effective_inspection_is_refused()
    {
        using var harness = ApplyHarness.Create();
        harness.SeedBundle(CandidateBundleId);
        harness.Runner.FailWhen = IsInspectEffective;

        var result = await harness.ApplyAsync(CandidateBundleId);

        Assert.Equal("effective_inspection_failed", result.ReasonCode);
        Assert.False(result.VerificationCommitted);
    }

    // -------------------------------------------------------------- rollback

    [Fact]
    public async Task Rollback_restores_the_previous_generation_and_re_earns_its_record()
    {
        using var harness = ApplyHarness.Create();
        await harness.SeedActiveDeploymentAsync(ActiveBundleId);
        harness.SeedBundle(CandidateBundleId);

        // Only the candidate's recreate fails; the rollback's own recreate must be allowed to
        // succeed or this would test a failed rollback instead of a successful one.
        var candidateRecreateFailed = false;
        harness.Runner.FailWhen = args =>
        {
            if (candidateRecreateFailed || !IsComposeSubcommand(args, "up"))
            {
                return false;
            }

            candidateRecreateFailed = true;
            return true;
        };

        var result = await harness.ApplyAsync(CandidateBundleId);

        Assert.Equal(SetupApplyResultCode.ApplyFailedRollbackSucceeded, result.Code);
        Assert.Equal(SetupManagedDeploymentState.Active, result.DeploymentState);
        Assert.Equal("recreate_failed", result.ReasonCode);
        Assert.Equal(SetupConfigRollbackStatus.Succeeded, result.ConfigRollbackStatus);
        Assert.Equal(ActiveBundleId, result.BundleId);
        Assert.Equal(3, result.ActivationGeneration);

        var active = harness.ReadActive()!;
        Assert.Equal(ActiveBundleId, active.BundleId);
        Assert.Equal(3, active.ActivationGeneration);

        // The restored generation is only trustworthy because it was verified again.
        var record = harness.ReadRecord()!;
        Assert.True(record.IsCommittedSuccess);
        Assert.Equal(ActiveBundleId, record.BundleId);
        Assert.Equal(3, record.ActivationGeneration);
        Assert.Equal(ActiveBundleId, record.ObservedBundleId);
        Assert.Equal(3, harness.ReadBinding()!.ActivationGeneration);
        Assert.False(File.Exists(harness.Layout.PreviousPointerPath));
        Assert.False(File.Exists(harness.Layout.TransactionStampPath));
    }

    [Fact]
    public async Task Rollback_is_not_called_successful_when_the_restored_generation_fails_verification()
    {
        using var harness = ApplyHarness.Create();
        await harness.SeedActiveDeploymentAsync(ActiveBundleId);
        harness.SeedBundle(CandidateBundleId);
        harness.Runner.FailWhen = static args => IsComposeSubcommand(args, "config");
        harness.Runner.MountAttestationResult = SetupInspectIntegrityResult.Mismatch;

        var result = await harness.ApplyAsync(CandidateBundleId);

        Assert.Equal(SetupApplyResultCode.ApplyFailedRollbackFailed, result.Code);
        Assert.Equal(SetupConfigRollbackStatus.Failed, result.ConfigRollbackStatus);
        Assert.Equal(SetupManagedDeploymentState.NeedsIntervention, result.DeploymentState);
        Assert.True(File.Exists(harness.Layout.TransactionStampPath));
    }

    [Fact]
    public async Task Readiness_that_never_recovers_reports_a_failed_rollback()
    {
        using var harness = ApplyHarness.Create();
        await harness.SeedActiveDeploymentAsync(ActiveBundleId);
        harness.SeedBundle(CandidateBundleId);
        harness.AdvanceClock();

        // The health check stays broken, so the restored previous deployment cannot pass readiness
        // either. The pointer is still restored, but the rollback itself is reported as failed.
        harness.Runner.FailWhen = IsHealthCheck;

        var result = await harness.ApplyAsync(CandidateBundleId);

        Assert.Equal(SetupApplyResultCode.ApplyFailedRollbackFailed, result.Code);
        Assert.Equal(SetupConfigRollbackStatus.Failed, result.ConfigRollbackStatus);
        Assert.Equal(ActiveBundleId, harness.ReadActive()!.BundleId);
        Assert.Equal(3, harness.ReadActive()!.ActivationGeneration);
    }

    [Fact]
    public async Task Compose_validation_failure_on_a_fresh_apply_removes_active_without_side_effects()
    {
        using var harness = ApplyHarness.Create();
        harness.SeedBundle(CandidateBundleId);
        harness.Runner.FailWhen = static args => IsComposeSubcommand(args, "config");

        var result = await harness.ApplyAsync(CandidateBundleId);

        Assert.Equal(SetupApplyResultCode.FreshApplyFailed, result.Code);
        Assert.Equal(SetupManagedDeploymentState.NoManaged, result.DeploymentState);
        Assert.Equal("compose_validation_failed", result.ReasonCode);
        Assert.Equal(SetupConfigRollbackStatus.Succeeded, result.ConfigRollbackStatus);
        Assert.False(result.PersistentSideEffectMayRemain);
        Assert.Null(harness.ReadActive());
        Assert.False(File.Exists(harness.Layout.TransactionStampPath));
        Assert.False(File.Exists(harness.Layout.LastRecordPath));
        Assert.False(File.Exists(harness.Layout.RuntimeIdentityBindPath));
    }

    [Fact]
    public async Task A_fresh_apply_that_already_migrated_stops_for_review_instead_of_claiming_a_rollback()
    {
        using var harness = ApplyHarness.Create();
        harness.SeedBundle(CandidateBundleId);
        harness.Runner.FailWhen = static args => IsComposeSubcommand(args, "up");

        var result = await harness.ApplyAsync(CandidateBundleId);

        Assert.Equal(SetupApplyResultCode.NeedsIntervention, result.Code);
        Assert.Equal(SetupManagedDeploymentState.NeedsIntervention, result.DeploymentState);
        Assert.Equal(SetupApplyActionCode.ReviewDatabaseSchema, result.ActionCode);
        Assert.True(result.PersistentSideEffectMayRemain);
        Assert.Equal(SetupPersistentSideEffectKind.DatabaseMigration, result.PersistentSideEffectKind);
        Assert.NotEqual(SetupConfigRollbackStatus.Succeeded, result.ConfigRollbackStatus);

        // The transaction stays terminal so a later apply refuses instead of guessing.
        Assert.True(File.Exists(harness.Layout.TransactionStampPath));
    }

    [Fact]
    public async Task A_failed_migration_on_a_fresh_apply_stops_for_review()
    {
        using var harness = ApplyHarness.Create();
        harness.SeedBundle(CandidateBundleId);
        harness.Runner.FailWhen = IsMigrationRun;

        var result = await harness.ApplyAsync(CandidateBundleId);

        Assert.Equal(SetupApplyResultCode.NeedsIntervention, result.Code);
        Assert.Equal("migration_failed", result.ReasonCode);
        Assert.True(result.PersistentSideEffectMayRemain);
        Assert.Equal(SetupApplyActionCode.ReviewDatabaseSchema, result.ActionCode);
    }

    // ------------------------------------------------------------ invariants

    [Fact]
    public async Task Apply_never_asserts_send_readiness()
    {
        using var harness = ApplyHarness.Create();
        harness.SeedBundle(CandidateBundleId);

        var result = await harness.ApplyAsync(CandidateBundleId);

        Assert.Equal(SetupApplyResultCode.ApplySucceeded, result.Code);
        Assert.False(result.SendReadyAsserted);
        Assert.Equal(SetupApplyActionCode.CompleteSendReadyEvaluation, result.ActionCode);
        Assert.Equal(
            SetupVerificationRecord.SendReadyNotEvaluated,
            harness.ReadRecord()!.SendReadyEvaluation);
    }

    [Fact]
    public async Task Cancellation_before_activation_reports_cancelled_and_leaves_no_state()
    {
        using var harness = ApplyHarness.Create();
        harness.SeedBundle(CandidateBundleId);
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        var result = await harness.Engine.ApplyAsync(harness.Layout, CandidateBundleId, cts.Token);

        Assert.Equal(SetupApplyResultCode.CancelledBeforeActivation, result.Code);
        Assert.Null(harness.ReadActive());
        Assert.False(File.Exists(harness.Layout.TransactionStampPath));
        Assert.False(result.SendReadyAsserted);
    }

    [Fact]
    public async Task Apply_results_never_carry_secrets_or_host_paths()
    {
        using var harness = ApplyHarness.Create();
        harness.SeedBundle(CandidateBundleId);
        harness.Runner.FailWhen = IsMigrationRun;
        harness.Runner.LeakCanaries = true;

        var result = await harness.ApplyAsync(CandidateBundleId);

        var text = string.Join(
            '\n',
            result.Code,
            result.Message,
            result.ActionCode,
            result.ReasonCode,
            result.BundleId,
            result.SendReadyReasonCode);
        Assert.DoesNotContain(ApplyProcessRunner.SecretCanary, text, StringComparison.Ordinal);
        Assert.DoesNotContain(harness.Root, text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("synthetic-mail-token-not-real", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Verification_record_never_carries_secrets()
    {
        using var harness = ApplyHarness.Create();
        harness.SeedBundle(CandidateBundleId);

        var result = await harness.ApplyAsync(CandidateBundleId);
        Assert.Equal(SetupApplyResultCode.ApplySucceeded, result.Code);

        var raw = File.ReadAllText(harness.Layout.LastRecordPath);
        Assert.DoesNotContain("synthetic-mail-token-not-real", raw, StringComparison.Ordinal);
        Assert.DoesNotContain("synthetic-metrics-token-not-real", raw, StringComparison.Ordinal);
        Assert.DoesNotContain(harness.DataPath, raw, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Runtime_identity_binding_is_owner_only_and_holds_no_raw_paths()
    {
        using var harness = ApplyHarness.Create();
        harness.SeedBundle(CandidateBundleId);

        var result = await harness.ApplyAsync(CandidateBundleId);
        Assert.Equal(SetupApplyResultCode.ApplySucceeded, result.Code);

        var path = harness.Layout.RuntimeIdentityBindPath;
        Assert.True(File.Exists(path));
        Assert.True(new HostSetupFileSystem().IsOwnerOnlyFile(path));
        Assert.DoesNotContain(harness.DataPath, File.ReadAllText(path), StringComparison.OrdinalIgnoreCase);
    }

    // -------------------------------------------------------------- recovery

    [Fact]
    public async Task Recovery_reports_success_when_active_record_and_binding_agree()
    {
        using var harness = ApplyHarness.Create();
        await harness.SeedActiveDeploymentAsync(ActiveBundleId);

        var result = await harness.RecoverAsync();

        Assert.Equal(SetupApplyResultCode.ApplySucceeded, result.Code);
        Assert.Equal(SetupManagedDeploymentState.Active, result.DeploymentState);
        Assert.True(result.VerificationCommitted);
    }

    [Fact]
    public async Task Recovery_reports_no_managed_deployment_when_nothing_is_in_flight()
    {
        using var harness = ApplyHarness.Create();

        var result = await harness.RecoverAsync();

        Assert.Equal(SetupApplyResultCode.RollbackSucceeded, result.Code);
        Assert.Equal(SetupManagedDeploymentState.NoManaged, result.DeploymentState);
    }

    [Fact]
    public async Task Recovery_clears_an_orphaned_record_and_binding_when_nothing_is_active()
    {
        using var harness = ApplyHarness.Create();
        await harness.SeedActiveDeploymentAsync(ActiveBundleId);
        File.Delete(harness.Layout.ActivePointerPath);

        var result = await harness.RecoverAsync();

        Assert.Equal(SetupApplyResultCode.RollbackSucceeded, result.Code);
        Assert.Equal(SetupManagedDeploymentState.NoManaged, result.DeploymentState);
        Assert.False(File.Exists(harness.Layout.LastRecordPath));
        Assert.False(File.Exists(harness.Layout.RuntimeIdentityBindPath));
    }

    [Fact]
    public async Task Recovery_clears_an_orphaned_previous_pointer_when_the_active_generation_is_consistent()
    {
        using var harness = ApplyHarness.Create();
        await harness.SeedActiveDeploymentAsync(ActiveBundleId);
        harness.WritePrevious(ActiveBundleId, generation: 1);

        var result = await harness.RecoverAsync();

        Assert.Equal(SetupApplyResultCode.ApplySucceeded, result.Code);
        Assert.Equal(SetupManagedDeploymentState.Active, result.DeploymentState);
        Assert.False(File.Exists(harness.Layout.PreviousPointerPath));
    }

    [Fact]
    public async Task Recovery_needs_intervention_when_active_has_no_matching_verification()
    {
        using var harness = ApplyHarness.Create();
        harness.SeedBundle(ActiveBundleId);
        harness.WriteActive(ActiveBundleId, generation: 1);

        var result = await harness.RecoverAsync();

        Assert.Equal(SetupApplyResultCode.NeedsIntervention, result.Code);
        Assert.Equal("verification_record_missing", result.ReasonCode);
    }

    [Fact]
    public async Task Recovery_needs_intervention_when_the_binding_does_not_match_active()
    {
        using var harness = ApplyHarness.Create();
        await harness.SeedActiveDeploymentAsync(ActiveBundleId);
        File.Delete(harness.Layout.RuntimeIdentityBindPath);

        var result = await harness.RecoverAsync();

        Assert.Equal(SetupApplyResultCode.NeedsIntervention, result.Code);
        Assert.Equal("runtime_identity_binding_mismatch", result.ReasonCode);
    }

    [Fact]
    public async Task Recovery_of_a_prepared_stamp_re_verifies_and_re_commits_the_previous_generation()
    {
        using var harness = ApplyHarness.Create();
        await harness.SeedActiveDeploymentAsync(ActiveBundleId);
        harness.SeedBundle(CandidateBundleId);

        // Crash shape: PREVIOUS written, record invalidated, ACTIVE never moved.
        harness.WritePrevious(ActiveBundleId, generation: 1);
        harness.WriteInvalidatedRecord(CandidateBundleId, generation: 2);
        harness.WriteStamp(
            SetupTransactionPhase.Prepared,
            terminal: false,
            previousBundleId: ActiveBundleId,
            previousGeneration: 1,
            targetGeneration: 2);

        var result = await harness.RecoverAsync();

        Assert.Equal(SetupApplyResultCode.RollbackSucceeded, result.Code);
        Assert.Equal(SetupManagedDeploymentState.Active, result.DeploymentState);
        Assert.Equal(ActiveBundleId, harness.ReadActive()!.BundleId);
        Assert.Equal(1, harness.ReadActive()!.ActivationGeneration);

        // The invalidated record had to be earned back, not simply deleted.
        var record = harness.ReadRecord()!;
        Assert.True(record.IsCommittedSuccess);
        Assert.Equal(ActiveBundleId, record.BundleId);
        Assert.Equal(1, record.ActivationGeneration);
        Assert.False(File.Exists(harness.Layout.PreviousPointerPath));
        Assert.False(File.Exists(harness.Layout.TransactionStampPath));
    }

    [Fact]
    public async Task Recovery_of_a_prepared_fresh_stamp_reports_no_managed_deployment()
    {
        using var harness = ApplyHarness.Create();
        harness.SeedBundle(CandidateBundleId);
        harness.WriteInvalidatedRecord(CandidateBundleId, generation: 1);
        harness.WriteStamp(SetupTransactionPhase.Prepared, terminal: false);

        var result = await harness.RecoverAsync();

        Assert.Equal(SetupApplyResultCode.RollbackSucceeded, result.Code);
        Assert.Equal(SetupManagedDeploymentState.NoManaged, result.DeploymentState);
        Assert.False(File.Exists(harness.Layout.TransactionStampPath));
        Assert.False(File.Exists(harness.Layout.LastRecordPath));
    }

    [Fact]
    public async Task Recovery_of_a_verification_pending_stamp_re_verifies_and_commits()
    {
        using var harness = ApplyHarness.Create();
        harness.SeedBundle(CandidateBundleId);
        harness.WriteActive(CandidateBundleId, generation: 1);
        harness.WriteInvalidatedRecord(CandidateBundleId, generation: 1);
        harness.WriteStamp(SetupTransactionPhase.VerificationPending, terminal: false);

        var result = await harness.RecoverAsync();

        Assert.Equal(SetupApplyResultCode.ApplySucceeded, result.Code);
        Assert.True(result.VerificationCommitted);
        Assert.True(harness.ReadRecord()!.IsCommittedSuccess);
        Assert.Equal(1, harness.ReadBinding()!.ActivationGeneration);
        Assert.False(File.Exists(harness.Layout.TransactionStampPath));
    }

    [Fact]
    public async Task Recovery_of_a_binding_pending_stamp_rolls_back_when_re_verification_fails()
    {
        using var harness = ApplyHarness.Create();
        harness.SeedBundle(CandidateBundleId);
        harness.WriteActive(CandidateBundleId, generation: 1);
        harness.WriteStamp(SetupTransactionPhase.BindingPending, terminal: false);
        harness.Runner.MountAttestationResult = SetupInspectIntegrityResult.Mismatch;

        var result = await harness.RecoverAsync();

        Assert.Equal(SetupApplyResultCode.RollbackSucceeded, result.Code);
        Assert.Equal(SetupManagedDeploymentState.NoManaged, result.DeploymentState);
        Assert.Null(harness.ReadActive());
    }

    [Fact]
    public async Task Recovery_of_a_verification_committed_stamp_requires_a_matching_record_and_binding()
    {
        using var harness = ApplyHarness.Create();
        await harness.SeedActiveDeploymentAsync(ActiveBundleId);
        harness.WriteStamp(
            SetupTransactionPhase.VerificationCommitted,
            terminal: false,
            candidateBundleId: ActiveBundleId);

        var result = await harness.RecoverAsync();

        Assert.Equal(SetupApplyResultCode.ApplySucceeded, result.Code);
        Assert.True(result.VerificationCommitted);
        Assert.False(File.Exists(harness.Layout.TransactionStampPath));
    }

    [Fact]
    public async Task Recovery_of_a_verification_committed_stamp_without_a_record_needs_intervention()
    {
        using var harness = ApplyHarness.Create();
        await harness.SeedActiveDeploymentAsync(ActiveBundleId);
        File.Delete(harness.Layout.LastRecordPath);
        harness.WriteStamp(
            SetupTransactionPhase.VerificationCommitted,
            terminal: false,
            candidateBundleId: ActiveBundleId);

        var result = await harness.RecoverAsync();

        Assert.Equal(SetupApplyResultCode.NeedsIntervention, result.Code);
        Assert.Equal("verification_record_missing", result.ReasonCode);
        Assert.True(File.Exists(harness.Layout.TransactionStampPath));
    }

    [Fact]
    public async Task Recovery_of_a_terminal_stamp_always_requires_a_human()
    {
        using var harness = ApplyHarness.Create();
        harness.SeedBundle(CandidateBundleId);
        harness.WriteActive(CandidateBundleId, generation: 1);
        harness.WriteStamp(SetupTransactionPhase.RollbackPending, terminal: true, migrationSideEffect: true);

        var result = await harness.RecoverAsync();

        Assert.Equal(SetupApplyResultCode.NeedsIntervention, result.Code);
        Assert.Equal(SetupApplyActionCode.ManualInterventionRequired, result.ActionCode);
        Assert.Equal(SetupConfigRollbackStatus.Failed, result.ConfigRollbackStatus);
        Assert.True(result.PersistentSideEffectMayRemain);
        Assert.True(File.Exists(harness.Layout.TransactionStampPath));
    }

    [Fact]
    public async Task Recovery_after_activation_rolls_back_and_re_verifies_the_previous_generation()
    {
        using var harness = ApplyHarness.Create();
        await harness.SeedActiveDeploymentAsync(ActiveBundleId);
        harness.SeedBundle(CandidateBundleId);
        harness.WritePrevious(ActiveBundleId, generation: 1);
        harness.WriteActive(CandidateBundleId, generation: 2);
        harness.WriteInvalidatedRecord(CandidateBundleId, generation: 2);
        harness.WriteStamp(
            SetupTransactionPhase.ReadinessChecking,
            terminal: false,
            previousBundleId: ActiveBundleId,
            previousGeneration: 1,
            targetGeneration: 2);

        var result = await harness.RecoverAsync();

        Assert.Equal(SetupApplyResultCode.RollbackSucceeded, result.Code);
        Assert.Equal(SetupManagedDeploymentState.Active, result.DeploymentState);
        Assert.Equal(ActiveBundleId, harness.ReadActive()!.BundleId);
        Assert.Equal(3, harness.ReadActive()!.ActivationGeneration);

        var record = harness.ReadRecord()!;
        Assert.True(record.IsCommittedSuccess);
        Assert.Equal(3, record.ActivationGeneration);
        Assert.False(File.Exists(harness.Layout.TransactionStampPath));
    }

    [Fact]
    public async Task Recovery_after_a_migration_reports_intervention_for_the_persistent_side_effect()
    {
        using var harness = ApplyHarness.Create();
        await harness.SeedActiveDeploymentAsync(ActiveBundleId);
        harness.SeedBundle(CandidateBundleId);
        harness.WritePrevious(ActiveBundleId, generation: 1);
        harness.WriteActive(CandidateBundleId, generation: 2);
        harness.WriteStamp(
            SetupTransactionPhase.Migrating,
            terminal: false,
            migrationSideEffect: true,
            previousBundleId: ActiveBundleId,
            previousGeneration: 1,
            targetGeneration: 2);

        var result = await harness.RecoverAsync();

        Assert.Equal(SetupApplyResultCode.NeedsIntervention, result.Code);
        Assert.True(result.PersistentSideEffectMayRemain);
        Assert.Equal(SetupPersistentSideEffectKind.DatabaseMigration, result.PersistentSideEffectKind);
    }

    [Fact]
    public async Task Recovery_of_an_interrupted_fresh_migration_never_reports_no_managed()
    {
        using var harness = ApplyHarness.Create();
        harness.SeedBundle(CandidateBundleId);
        harness.WriteActive(CandidateBundleId, generation: 1);
        harness.WriteStamp(SetupTransactionPhase.Migrating, terminal: false, migrationSideEffect: true);

        var result = await harness.RecoverAsync();

        Assert.Equal(SetupApplyResultCode.NeedsIntervention, result.Code);
        Assert.Equal(SetupApplyActionCode.ReviewDatabaseSchema, result.ActionCode);
        Assert.True(result.PersistentSideEffectMayRemain);
        Assert.NotNull(harness.ReadActive());
    }

    [Fact]
    public async Task Recovery_of_a_fresh_apply_after_activation_removes_active()
    {
        using var harness = ApplyHarness.Create();
        harness.SeedBundle(CandidateBundleId);
        harness.WriteActive(CandidateBundleId, generation: 1);
        harness.WriteStamp(SetupTransactionPhase.Recreating, terminal: false);

        var result = await harness.RecoverAsync();

        Assert.Equal(SetupApplyResultCode.RollbackSucceeded, result.Code);
        Assert.Equal(SetupManagedDeploymentState.NoManaged, result.DeploymentState);
        Assert.Null(harness.ReadActive());
        Assert.False(File.Exists(harness.Layout.TransactionStampPath));
        Assert.False(File.Exists(harness.Layout.LastRecordPath));
        Assert.False(File.Exists(harness.Layout.RuntimeIdentityBindPath));
    }

    [Fact]
    public async Task Recovery_needs_intervention_when_the_expected_previous_pointer_is_missing()
    {
        using var harness = ApplyHarness.Create();
        harness.SeedBundle(CandidateBundleId);
        harness.WriteActive(CandidateBundleId, generation: 6);
        harness.WriteStamp(
            SetupTransactionPhase.Recreating,
            terminal: false,
            previousBundleId: ActiveBundleId,
            previousGeneration: 5,
            targetGeneration: 6);

        var result = await harness.RecoverAsync();

        Assert.Equal(SetupApplyResultCode.NeedsIntervention, result.Code);
        Assert.Equal("previous_pointer_missing", result.ReasonCode);
    }

    [Fact]
    public async Task Malformed_active_pointer_is_rejected_instead_of_being_guessed()
    {
        using var harness = ApplyHarness.Create();
        harness.SeedBundle(CandidateBundleId);

        // A bare bundle id was the legacy shape; the strict document must reject it.
        File.WriteAllText(harness.Layout.ActivePointerPath, ActiveBundleId + "\n");

        var result = await harness.ApplyAsync(CandidateBundleId);

        Assert.Equal(SetupApplyResultCode.NeedsIntervention, result.Code);
        Assert.Equal("pointer_document_invalid", result.ReasonCode);
        Assert.Empty(harness.Invocations);
    }

    [Fact]
    public async Task Recovery_is_idempotent_once_the_state_is_clean()
    {
        using var harness = ApplyHarness.Create();
        harness.SeedBundle(CandidateBundleId);
        harness.WriteActive(CandidateBundleId, generation: 1);
        harness.WriteStamp(SetupTransactionPhase.Recreating, terminal: false);

        var first = await harness.RecoverAsync();
        Assert.Equal(SetupApplyResultCode.RollbackSucceeded, first.Code);

        var second = await harness.RecoverAsync();
        Assert.Equal(SetupApplyResultCode.RollbackSucceeded, second.Code);
        Assert.Equal(SetupManagedDeploymentState.NoManaged, second.DeploymentState);
    }

    // ------------------------------------------ Agent B re-review regressions

    [Fact]
    public async Task Fresh_apply_refuses_when_an_orphaned_verification_record_remains()
    {
        using var harness = ApplyHarness.Create();
        harness.SeedBundle(CandidateBundleId);
        harness.WriteInvalidatedRecord(CandidateBundleId, generation: 1);

        var result = await harness.ApplyAsync(CandidateBundleId);

        Assert.Equal(SetupApplyResultCode.RecoveryRequired, result.Code);
        Assert.Equal(SetupManagedDeploymentState.RecoveryRequired, result.DeploymentState);
        Assert.Equal("orphan_managed_state", result.ReasonCode);
        Assert.True(File.Exists(harness.Layout.LastRecordPath));
        Assert.Empty(harness.Invocations);
    }

    [Fact]
    public async Task Rollback_stops_when_active_no_longer_matches_the_failed_candidate()
    {
        using var harness = ApplyHarness.Create();
        await harness.SeedActiveDeploymentAsync(ActiveBundleId);
        harness.SeedBundle(CandidateBundleId);
        harness.AdvanceClock();

        harness.Runner.FailWhen = IsHealthCheck;
        harness.FileSystem.OnCommit = (path, content) =>
        {
            if (IsStamp(harness, path)
                && content.Contains("\"kind\":\"Rollback\"", StringComparison.Ordinal)
                && content.Contains(Phase(SetupTransactionPhase.RollbackPending), StringComparison.Ordinal))
            {
                // Simulate an out-of-band ACTIVE change after the candidate failed.
                harness.WriteActive("bundle-unexpected01", generation: 99);
            }
        };

        var result = await harness.ApplyAsync(CandidateBundleId);

        Assert.Equal(SetupApplyResultCode.ApplyFailedRollbackFailed, result.Code);
        Assert.Equal(SetupManagedDeploymentState.NeedsIntervention, result.DeploymentState);
        Assert.Equal(SetupConfigRollbackStatus.Failed, result.ConfigRollbackStatus);
        Assert.Equal(SetupDockerResultCode.ActiveGenerationMismatch, harness.ReadStamp()!.ReasonCode);
        Assert.Equal("bundle-unexpected01", harness.ReadActive()!.BundleId);
        Assert.NotEqual(ActiveBundleId, harness.ReadActive()!.BundleId);
    }

    [Fact]
    public async Task A_stale_compose_identity_is_not_treated_as_an_active_authority()
    {
        using var harness = ApplyHarness.Create();
        await harness.SeedActiveDeploymentAsync(ActiveBundleId);
        harness.SeedBundle(CandidateBundleId);

        var record = harness.ReadRecord()!;
        var stale = new SetupVerificationRecord
        {
            SchemaVersion = record.SchemaVersion,
            Status = record.Status,
            BundleId = record.BundleId,
            ActivationGeneration = record.ActivationGeneration,
            FingerprintComparison = record.FingerprintComparison,
            HostAtRest = record.HostAtRest,
            MountAttestation = record.MountAttestation,
            BundleIntegrity = record.BundleIntegrity,
            ImageReference = record.ImageReference,
            ComposeIdentity = "stale-compose-identity",
            ObservedBundleId = record.ObservedBundleId,
            ObservedMailerVersion = record.ObservedMailerVersion,
            RecordedSchemaVersion = record.RecordedSchemaVersion,
            RuntimeIdentityBinding = record.RuntimeIdentityBinding,
            Readiness = record.Readiness,
            SendReadyEvaluation = record.SendReadyEvaluation,
            CommittedAt = record.CommittedAt,
        };
        File.WriteAllText(
            harness.Layout.LastRecordPath,
            JsonSerializer.Serialize(stale, SetupApplyJsonContext.Default.SetupVerificationRecord));

        var recover = await harness.RecoverAsync();
        Assert.Equal(SetupApplyResultCode.NeedsIntervention, recover.Code);
        Assert.Equal("verification_record_stale", recover.ReasonCode);

        var apply = await harness.ApplyAsync(CandidateBundleId);
        Assert.Equal(SetupApplyResultCode.IneligibleExistingActive, apply.Code);
        Assert.Equal("verification_record_stale", apply.ReasonCode);
    }

    [Fact]
    public async Task A_binding_that_is_not_owner_only_needs_intervention()
    {
        using var harness = ApplyHarness.Create();
        await harness.SeedActiveDeploymentAsync(ActiveBundleId);
        harness.FileSystem.OwnerOnlyOverride = path =>
            SamePath(path, harness.Layout.RuntimeIdentityBindPath) ? false : null;

        var result = await harness.RecoverAsync();

        Assert.Equal(SetupApplyResultCode.NeedsIntervention, result.Code);
        Assert.Equal("durable_state_unreadable", result.ReasonCode);
    }

    [Fact]
    public async Task Recovery_retargets_previous_so_a_failed_committed_phase_write_can_still_finish()
    {
        using var harness = ApplyHarness.Create();
        await harness.SeedActiveDeploymentAsync(ActiveBundleId);
        harness.SeedBundle(CandidateBundleId);
        harness.WritePrevious(ActiveBundleId, generation: 1);
        harness.WriteInvalidatedRecord(CandidateBundleId, generation: 2);
        harness.WriteStamp(
            SetupTransactionPhase.Prepared,
            terminal: false,
            previousBundleId: ActiveBundleId,
            previousGeneration: 1,
            targetGeneration: 2);

        var blockCommittedPhase = true;
        harness.FileSystem.FailCommitWhen = (path, content) =>
            blockCommittedPhase
            && IsStamp(harness, path)
            && content.Contains(Phase(SetupTransactionPhase.VerificationCommitted), StringComparison.Ordinal);

        var first = await harness.RecoverAsync();

        Assert.Equal(SetupApplyResultCode.NeedsIntervention, first.Code);
        Assert.True(first.VerificationCommitted);
        Assert.Equal(ActiveBundleId, harness.ReadActive()!.BundleId);
        Assert.True(harness.ReadRecord()!.IsCommittedSuccess);
        Assert.Equal(ActiveBundleId, harness.ReadRecord()!.BundleId);
        Assert.Equal(ActiveBundleId, harness.ReadStamp()!.CandidateBundleId);
        Assert.True(File.Exists(harness.Layout.TransactionStampPath));

        blockCommittedPhase = false;
        harness.FileSystem.Reset();

        var second = await harness.RecoverAsync();

        Assert.True(
            second.Code is SetupApplyResultCode.ApplySucceeded or SetupApplyResultCode.RollbackSucceeded);
        Assert.Equal(SetupManagedDeploymentState.Active, second.DeploymentState);
        Assert.False(File.Exists(harness.Layout.TransactionStampPath));
        Assert.Equal(ActiveBundleId, harness.ReadActive()!.BundleId);
        Assert.True(harness.ReadRecord()!.IsCommittedSuccess);
    }

    [Fact]
    public async Task An_invalidation_failure_that_cannot_clear_the_stamp_needs_intervention()
    {
        using var harness = ApplyHarness.Create();
        await harness.SeedActiveDeploymentAsync(ActiveBundleId);
        harness.SeedBundle(CandidateBundleId);
        harness.FileSystem.FailCommitWhen = (path, _) => IsRecord(harness, path);
        harness.FileSystem.FailDeleteWhen = path => SamePath(path, harness.Layout.TransactionStampPath);

        var result = await harness.ApplyAsync(CandidateBundleId);

        Assert.Equal(SetupApplyResultCode.NeedsIntervention, result.Code);
        Assert.Equal(SetupManagedDeploymentState.NeedsIntervention, result.DeploymentState);
        Assert.NotEqual(SetupManagedDeploymentState.NoManaged, result.DeploymentState);
        Assert.True(File.Exists(harness.Layout.TransactionStampPath));
        Assert.Equal(ActiveBundleId, harness.ReadActive()!.BundleId);
    }

    // --------------------------------------------------------------- helpers

    private static string Phase(string phase) => "\"phase\":\"" + phase + "\"";

    private static bool IsStamp(ApplyHarness harness, string path) =>
        SamePath(path, harness.Layout.TransactionStampPath);

    private static bool IsRecord(ApplyHarness harness, string path) =>
        SamePath(path, harness.Layout.LastRecordPath);

    private static bool IsBinding(ApplyHarness harness, string path) =>
        SamePath(path, harness.Layout.RuntimeIdentityBindPath);

    private static bool SamePath(string left, string right) =>
        string.Equals(Path.GetFullPath(left), Path.GetFullPath(right), StringComparison.OrdinalIgnoreCase);

    private static bool IsMigrationRun(IReadOnlyList<string> args) =>
        args.Contains(SetupDockerInventory.ServiceMailerMigrate, StringComparer.Ordinal)
        && !args.Contains("--status", StringComparer.Ordinal);

    private static bool IsMigrationStatus(IReadOnlyList<string> args) =>
        args.Contains("--status", StringComparer.Ordinal);

    private static bool IsHealthCheck(IReadOnlyList<string> args) =>
        args.Contains("healthcheck", StringComparer.Ordinal);

    private static bool IsInspectEffective(IReadOnlyList<string> args) =>
        args.Contains("inspect-effective", StringComparer.Ordinal);

    private static bool IsComposeSubcommand(IReadOnlyList<string> args, string subcommand)
    {
        var composeIndex = -1;
        for (var i = 0; i < args.Count; i++)
        {
            if (string.Equals(args[i], "compose", StringComparison.Ordinal))
            {
                composeIndex = i;
                break;
            }
        }

        if (composeIndex < 0)
        {
            return false;
        }

        for (var i = composeIndex + 1; i < args.Count; i++)
        {
            if (string.Equals(args[i], subcommand, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    private sealed class ApplyHarness : IDisposable
    {
        private ApplyHarness(
            string root,
            TrustedSetupHostLayout layout,
            InstrumentedSetupFileSystem fileSystem,
            SetupHostDockerAdapter adapter,
            SetupApplyEngine engine,
            ApplyProcessRunner runner,
            SteppingTimeProvider timeProvider,
            string dataPath)
        {
            Root = root;
            Layout = layout;
            FileSystem = fileSystem;
            Adapter = adapter;
            Engine = engine;
            Runner = runner;
            TimeProvider = timeProvider;
            DataPath = dataPath;
        }

        public string Root { get; }
        public TrustedSetupHostLayout Layout { get; }
        public InstrumentedSetupFileSystem FileSystem { get; }
        public SetupHostDockerAdapter Adapter { get; }
        public SetupApplyEngine Engine { get; }
        public ApplyProcessRunner Runner { get; }
        public SteppingTimeProvider TimeProvider { get; }
        public string DataPath { get; }

        public IReadOnlyList<IReadOnlyList<string>> Invocations => Runner.OperationInvocations;

        public static ApplyHarness Create()
        {
            var root = Path.GetFullPath(
                Path.Combine(Path.GetTempPath(), "amane-apply-" + Guid.NewGuid().ToString("N")));
            var hostFileSystem = new HostSetupFileSystem();
            var layoutResult = TrustedSetupHostLayoutResolver.CreateLayoutForTests(
                hostFileSystem,
                root,
                SetupMode.LocalMailpit,
                CreateInventory(),
                "applytest",
                MinimalCompose,
                mailpitOverlayContents: "services:\n  mailpit:\n    image: ${MAILPIT_IMAGE}\n",
                out var layout);
            Assert.True(layoutResult.IsSuccess);
            Assert.NotNull(layout);

            Directory.CreateDirectory(layout!.ManagedRoot);
            Directory.CreateDirectory(layout.StatePath);

            var dataPath = Path.Combine(layout.ManagedRoot, "data");
            Directory.CreateDirectory(dataPath);
            File.WriteAllText(layout.ExternalEnvPath, $"MAILER_DATA_PATH={dataPath}\n");

            var fileSystem = new InstrumentedSetupFileSystem(hostFileSystem);
            var runner = new ApplyProcessRunner();
            var timeProvider = new SteppingTimeProvider();
            var probe = new DockerEnvironmentProbe(
                runner,
                getDockerHost: static () => null,
                getDockerContextEnv: static () => null,
                resolveDockerExecutable: static () => "docker");
            var adapter = new SetupHostDockerAdapter(
                fileSystem,
                runner,
                probe,
                envComposer: null,
                timeProvider);
            var engine = new SetupApplyEngine(fileSystem, adapter, timeProvider);

            var harness = new ApplyHarness(
                root,
                layout,
                fileSystem,
                adapter,
                engine,
                runner,
                timeProvider,
                dataPath);
            runner.RecordedProvider = harness.BuildRecordedSummary;
            return harness;
        }

        public Task<SetupApplyResult> ApplyAsync(string bundleId) =>
            Engine.ApplyAsync(Layout, bundleId, TestContext.Current.CancellationToken);

        public Task<SetupApplyResult> RecoverAsync() =>
            Engine.RecoverAsync(Layout, TestContext.Current.CancellationToken);

        /// <summary>
        /// Advance the clock on every read so readiness retry loops reach their deadline without
        /// real waiting. Success paths do not need it because the first attempt passes.
        /// </summary>
        public void AdvanceClock() => TimeProvider.Step = TimeSpan.FromSeconds(200);

        /// <summary>
        /// Generates a real finalized, sealed bundle so host at-rest verification is exercised
        /// rather than stubbed.
        /// </summary>
        public void SeedBundle(string bundleId)
        {
            var request = SetupTestFixtures.LocalMailpitRequest(Layout.ManagedRoot);
            var generated = new SetupCore(bundleIdFactory: () => bundleId).GenerateBundle(request);
            Assert.Equal(SetupResultCode.Succeeded, generated.Code);
        }

        /// <summary>
        /// Produces a genuinely verified existing deployment by running a real apply, so ACTIVE, the
        /// committed record, and the runtime-identity binding are consistent the way the engine
        /// requires. Hand-written pointers would let ineligible states pass as eligible.
        /// </summary>
        public async Task SeedActiveDeploymentAsync(string bundleId)
        {
            SeedBundle(bundleId);
            var applied = await ApplyAsync(bundleId);
            Assert.Equal(SetupApplyResultCode.ApplySucceeded, applied.Code);
            Runner.Reset();
            FileSystem.Reset();
        }

        public void CorruptBundleSeal(string bundleId)
        {
            var sealPath = Path.Combine(
                SetupBundleLayout.MetadataDir(SetupBundleLayout.BundleRoot(Layout.ManagedRoot, bundleId)),
                SetupBundleLayout.IntegritySealFileName);
            var bytes = File.ReadAllBytes(sealPath);
            bytes[^1] ^= 0xFF;
            File.WriteAllBytes(sealPath, bytes);
        }

        public void WriteActive(string bundleId, long generation) =>
            WritePointer(Layout.ActivePointerPath, bundleId, generation);

        public void WritePrevious(string bundleId, long generation) =>
            WritePointer(Layout.PreviousPointerPath, bundleId, generation);

        public void WriteInvalidatedRecord(string bundleId, long generation)
        {
            var record = new SetupVerificationRecord
            {
                SchemaVersion = SetupVerificationRecord.CurrentSchemaVersion,
                Status = SetupVerificationRecord.StatusInvalidated,
                BundleId = bundleId,
                ActivationGeneration = generation,
                FingerprintComparison = SetupVerificationRecord.FingerprintNotEvaluated,
                HostAtRest = SetupIntegrityMerger.NotVerified,
                MountAttestation = SetupIntegrityMerger.NotVerified,
                BundleIntegrity = SetupIntegrityMerger.NotVerified,
                RuntimeIdentityBinding = SetupRuntimeIdentityBindingResult.Missing,
                Readiness = SetupVerificationRecord.ReadinessNotEvaluated,
                SendReadyEvaluation = SetupVerificationRecord.SendReadyNotEvaluated,
            };
            Directory.CreateDirectory(Layout.VerificationDir);
            File.WriteAllText(
                Layout.LastRecordPath,
                JsonSerializer.Serialize(record, SetupApplyJsonContext.Default.SetupVerificationRecord));
        }

        public void WriteStamp(
            string phase,
            bool terminal,
            bool migrationSideEffect = false,
            string? candidateBundleId = null,
            string? previousBundleId = null,
            long? previousGeneration = null,
            long targetGeneration = 1)
        {
            var stamp = new SetupTransactionStamp
            {
                SchemaVersion = SetupTransactionStamp.CurrentSchemaVersion,
                Kind = SetupTransactionKind.Apply,
                Phase = phase,
                Terminal = terminal,
                CandidateBundleId = candidateBundleId ?? CandidateBundleId,
                TargetActivationGeneration = targetGeneration,
                PreviousBundleId = previousBundleId,
                PreviousActivationGeneration = previousGeneration,
                PersistentSideEffectMayRemain = migrationSideEffect,
                PersistentSideEffectKind = migrationSideEffect
                    ? SetupPersistentSideEffectKind.DatabaseMigration
                    : SetupPersistentSideEffectKind.None,
                StartedAt = "2026-07-29T00:00:00Z",
            };
            File.WriteAllText(
                Layout.TransactionStampPath,
                JsonSerializer.Serialize(stamp, SetupApplyJsonContext.Default.SetupTransactionStamp));
        }

        public void MutateExternalEnv() =>
            File.WriteAllText(Layout.ExternalEnvPath, $"MAILER_DATA_PATH={DataPath}-moved\n");

        public SetupActivePointer? ReadActive() => ReadPointer(Layout.ActivePointerPath);

        public SetupActivePointer? ReadPrevious() => ReadPointer(Layout.PreviousPointerPath);

        public SetupVerificationRecord? ReadRecord() =>
            File.Exists(Layout.LastRecordPath)
                ? JsonSerializer.Deserialize(
                    File.ReadAllBytes(Layout.LastRecordPath),
                    SetupApplyJsonContext.Default.SetupVerificationRecord)
                : null;

        public SetupTransactionStamp? ReadStamp() =>
            File.Exists(Layout.TransactionStampPath)
                ? JsonSerializer.Deserialize(
                    File.ReadAllBytes(Layout.TransactionStampPath),
                    SetupApplyJsonContext.Default.SetupTransactionStamp)
                : null;

        public SetupRuntimeIdentityBindingStamp? ReadBinding() =>
            File.Exists(Layout.RuntimeIdentityBindPath)
                ? JsonSerializer.Deserialize(
                    File.ReadAllBytes(Layout.RuntimeIdentityBindPath),
                    SetupApplyJsonContext.Default.SetupRuntimeIdentityBindingStamp)
                : null;

        public void Dispose()
        {
            try
            {
                Directory.Delete(Root, recursive: true);
            }
            catch
            {
                // Best-effort temp cleanup.
            }
        }

        /// <summary>
        /// Mirrors what a real container would report: the recorded metadata of whatever bundle
        /// ACTIVE currently names. Tests override individual members to simulate a container that
        /// resolved something other than the generation the host believes it activated.
        /// </summary>
        private SetupInspectRecordedSummary? BuildRecordedSummary()
        {
            if (Runner.OmitRecorded)
            {
                return null;
            }

            var active = ReadActive();
            if (active is null)
            {
                return null;
            }

            var recordedPath = Path.Combine(
                SetupBundleLayout.MetadataDir(SetupBundleLayout.BundleRoot(Layout.ManagedRoot, active.BundleId)),
                SetupBundleLayout.RecordedMetadataFileName);
            if (!File.Exists(recordedPath))
            {
                return null;
            }

            var metadata = JsonSerializer.Deserialize(
                File.ReadAllBytes(recordedPath),
                SetupJsonContext.Default.SetupRecordedMetadata);
            if (metadata is null)
            {
                return null;
            }

            return new SetupInspectRecordedSummary
            {
                SetupBundleId = Runner.RecordedBundleIdOverride ?? metadata.BundleId,
                ConfigurationFingerprint = Runner.RecordedFingerprintOverride ?? metadata.ConfigurationFingerprint,
                Mode = metadata.Mode,
                SchemaVersion = Runner.RecordedSchemaVersionOverride ?? metadata.SchemaVersion,
            };
        }

        private void WritePointer(string path, string bundleId, long generation)
        {
            var pointer = new SetupActivePointer
            {
                SchemaVersion = SetupActivePointer.CurrentSchemaVersion,
                BundleId = bundleId,
                ActivationGeneration = generation,
            };
            File.WriteAllText(path, pointer.ToCanonicalJson());
        }

        private static SetupActivePointer? ReadPointer(string path)
        {
            if (!File.Exists(path))
            {
                return null;
            }

            return SetupActivePointer.TryParse(File.ReadAllText(path), out var pointer)
                ? pointer
                : null;
        }

        private static TrustedReleaseInventory CreateInventory() =>
            new()
            {
                AllowedImageRepository = SetupImageDefaults.DefaultRepository,
                RequiredImageDigest = TestDigest,
                AllowedDisplayTag = "test-synthetic-image-tag",
                ComposeBundleVersion = "1",
                LauncherVersionMin = "1.0.0",
                LauncherVersionMax = "1.0.0",
                ProjectNamePrefix = "amane",
                MailpitImageReference = "axllent/mailpit@" + TestDigest,
            };

        private const string MinimalCompose =
            """
            services:
              mailer-migrate:
                image: ${MAILER_IMAGE_REPOSITORY}:${MAILER_IMAGE_TAG}
                profiles: [ops]
              mailer:
                image: ${MAILER_IMAGE_REPOSITORY}:${MAILER_IMAGE_TAG}
              mailer-acs-admin:
                image: ${MAILER_IMAGE_REPOSITORY}:${MAILER_IMAGE_TAG}
                profiles: [acs-admin]
            """;
    }

    /// <summary>
    /// Wraps the real filesystem so a test can watch or break one specific durable commit. Durable
    /// writes land through a temp file plus <see cref="MoveReplace"/>, so the content staged for a
    /// path is remembered and handed to the predicates: a test names the write it cares about by
    /// what the document says, not by counting invocations.
    /// </summary>
    private sealed class InstrumentedSetupFileSystem(HostSetupFileSystem inner) : ISetupFileSystem
    {
        private readonly Dictionary<string, string> _staged = new(StringComparer.OrdinalIgnoreCase);

        public Func<string, string, bool>? FailCommitWhen { get; set; }
        public Func<string, bool>? FailDeleteWhen { get; set; }
        public Func<string, bool?>? OwnerOnlyOverride { get; set; }
        public Action<string, string>? OnCommit { get; set; }
        public Action<string>? OnOpenExclusiveGenerationLock { get; set; }

        public void Reset()
        {
            FailCommitWhen = null;
            FailDeleteWhen = null;
            OwnerOnlyOverride = null;
            OnCommit = null;
            OnOpenExclusiveGenerationLock = null;
            _staged.Clear();
        }

        public void WriteProtectedFileCreateNew(string path, ReadOnlySpan<byte> content)
        {
            Stage(path, Encoding.UTF8.GetString(content));
            inner.WriteProtectedFileCreateNew(path, content);
        }

        public void WriteProtectedFileCreateNew(string path, string content)
        {
            Stage(path, content);
            inner.WriteProtectedFileCreateNew(path, content);
        }

        public void MoveReplace(string sourcePath, string destinationPath)
        {
            var content = _staged.TryGetValue(Path.GetFullPath(sourcePath), out var staged) ? staged : string.Empty;
            _staged.Remove(Path.GetFullPath(sourcePath));

            if (FailCommitWhen?.Invoke(destinationPath, content) == true)
            {
                throw new IOException("Injected durable commit failure.");
            }

            inner.MoveReplace(sourcePath, destinationPath);
            OnCommit?.Invoke(destinationPath, content);
        }

        public FileStream OpenExclusiveGenerationLock(string path)
        {
            OnOpenExclusiveGenerationLock?.Invoke(path);
            return inner.OpenExclusiveGenerationLock(path);
        }

        public bool DirectoryExists(string path) => inner.DirectoryExists(path);
        public bool FileExists(string path) => inner.FileExists(path);
        public SetupLinkInspectionResult InspectSymlinkOrReparsePoint(string path) =>
            inner.InspectSymlinkOrReparsePoint(path);
        public IEnumerable<string> EnumerateFileSystemEntries(string path) =>
            inner.EnumerateFileSystemEntries(path);
        public void CreateOwnerOnlyDirectory(string path) => inner.CreateOwnerOnlyDirectory(path);
        public byte[] ReadAllBytes(string path) => inner.ReadAllBytes(path);
        public void DeleteFile(string path)
        {
            if (FailDeleteWhen?.Invoke(path) == true)
            {
                throw new IOException("Injected durable delete failure.");
            }

            inner.DeleteFile(path);
        }

        public void DeleteDirectoryRecursive(string path) => inner.DeleteDirectoryRecursive(path);
        public void FlushDirectory(string path) => inner.FlushDirectory(path);
        public void FlushFile(string path) => inner.FlushFile(path);
        public void SetUnixOwnership(string path, uint userId, uint groupId) =>
            inner.SetUnixOwnership(path, userId, groupId);
        public void SetUnixFileModeOwnerOnly(string path, bool executableDirectory) =>
            inner.SetUnixFileModeOwnerOnly(path, executableDirectory);
        public bool TryGetUnixFileMode(string path, out UnixFileMode mode) =>
            inner.TryGetUnixFileMode(path, out mode);
        public bool IsOwnerOnlyFile(string path) =>
            OwnerOnlyOverride?.Invoke(path) ?? inner.IsOwnerOnlyFile(path);
        public uint? GetEffectiveUnixUserId() => inner.GetEffectiveUnixUserId();

        private void Stage(string path, string content) => _staged[Path.GetFullPath(path)] = content;
    }

    /// <summary>
    /// Scripted Docker replacement. Binding probes always succeed; every other invocation is
    /// recorded so tests can assert ordering, and can be failed selectively via
    /// <see cref="FailWhen"/>.
    /// </summary>
    private sealed class ApplyProcessRunner : IHostProcessRunner
    {
        public const string SecretCanary = "canary-token-value-must-not-leak";
        public const string MailerVersion = "1.0.0";

        private readonly ConcurrentQueue<IReadOnlyList<string>> _operations = new();

        public Func<IReadOnlyList<string>, bool>? FailWhen { get; set; }
        public Action<IReadOnlyList<string>>? BeforeRun { get; set; }
        public Func<SetupInspectRecordedSummary?>? RecordedProvider { get; set; }
        public string MigrationClassification { get; set; } = SetupSchemaClassification.DatabaseAbsent;
        public string MountAttestationResult { get; set; } = SetupInspectIntegrityResult.Matched;
        public bool FingerprintsMatchRecorded { get; set; } = true;
        public bool Managed { get; set; } = true;
        public bool OmitRecorded { get; set; }
        public string? RecordedBundleIdOverride { get; set; }
        public string? RecordedFingerprintOverride { get; set; }
        public int? RecordedSchemaVersionOverride { get; set; }
        public bool LeakCanaries { get; set; }

        public IReadOnlyList<IReadOnlyList<string>> OperationInvocations => _operations.ToArray();

        /// <summary>Returns to a healthy steady state after a deployment has been seeded.</summary>
        public void Reset()
        {
            _operations.Clear();
            FailWhen = null;
            BeforeRun = null;
            LeakCanaries = false;
            MountAttestationResult = SetupInspectIntegrityResult.Matched;
            FingerprintsMatchRecorded = true;
            Managed = true;
            OmitRecorded = false;
            RecordedBundleIdOverride = null;
            RecordedFingerprintOverride = null;
            RecordedSchemaVersionOverride = null;

            // A seeded deployment has already migrated, so the schema is current from now on.
            MigrationClassification = SetupSchemaClassification.Current;
        }

        public Task<HostProcessResult> RunAsync(HostProcessSpec spec, CancellationToken cancellationToken)
        {
            var args = spec.ArgumentList.ToArray();
            var joined = string.Join(' ', args);

            if (joined.Contains("context show", StringComparison.Ordinal))
            {
                return Task.FromResult(Ok("default\n"));
            }

            if (joined.Contains("context inspect", StringComparison.Ordinal))
            {
                var endpoint = OperatingSystem.IsWindows()
                    ? "npipe:////./pipe/docker_engine"
                    : "unix:///var/run/docker.sock";
                return Task.FromResult(Ok($"{{\"Endpoints\":{{\"docker\":{{\"Host\":\"{endpoint}\"}}}}}}"));
            }

            if (joined.Contains("version --format", StringComparison.Ordinal))
            {
                return Task.FromResult(Ok("27.0.0\n"));
            }

            if (joined.Contains("compose version", StringComparison.Ordinal))
            {
                return Task.FromResult(Ok("v2.29.0\n"));
            }

            BeforeRun?.Invoke(args);
            _operations.Enqueue(args);

            if (FailWhen is not null && FailWhen(args))
            {
                return Task.FromResult(new HostProcessResult
                {
                    Outcome = HostProcessOutcome.Completed,
                    ExitCode = 1,
                    StandardOutput = LeakCanaries ? "token=" + SecretCanary : string.Empty,
                    StandardError = LeakCanaries ? "path=/private/secret/dir" : string.Empty,
                });
            }

            if (IsMigrationStatus(args))
            {
                return Task.FromResult(Ok(
                    $"{{\"schemaVersion\":1,\"classification\":\"{MigrationClassification}\"}}"));
            }

            if (IsInspectEffective(args))
            {
                return Task.FromResult(Ok(InspectionJson()));
            }

            return Task.FromResult(Ok(string.Empty));
        }

        private string InspectionJson()
        {
            var recorded = RecordedProvider?.Invoke();
            var recordedJson = recorded is null
                ? string.Empty
                : $$"""
                  "recorded": {
                    "setupBundleId": "{{recorded.SetupBundleId}}",
                    "configurationFingerprint": "{{recorded.ConfigurationFingerprint}}",
                    "mode": "{{recorded.Mode}}",
                    "schemaVersion": {{recorded.SchemaVersion}}
                  },
                """;

            return $$"""
            {
              "schemaVersion": 1,
              "mailerVersion": "{{MailerVersion}}",
              "managed": {{(Managed ? "true" : "false")}},
            {{recordedJson}}
              "effective": {
                "credentialStatus": "loaded",
                "fingerprintsMatchRecorded": {{(FingerprintsMatchRecorded ? "true" : "false")}}
              },
              "mountAttestation": {
                "result": "{{MountAttestationResult}}"
              },
              "bundleIntegrity": {
                "result": "not-verified",
                "reason": "host-at-rest-pending"
              },
              "tenantConfigurationSource": "managed",
              "credentialSource": "not-applicable"
            }
            """;
        }

        private static HostProcessResult Ok(string stdout) =>
            new()
            {
                Outcome = HostProcessOutcome.Completed,
                ExitCode = 0,
                StandardOutput = stdout,
                StandardError = string.Empty,
            };
    }

    /// <summary>
    /// Advances its own clock by a fixed step on every read so retry deadlines can be reached
    /// without sleeping. A zero step behaves like a frozen clock.
    /// </summary>
    private sealed class SteppingTimeProvider : TimeProvider
    {
        private DateTimeOffset _now = new(2026, 7, 29, 0, 0, 0, TimeSpan.Zero);

        public TimeSpan Step { get; set; } = TimeSpan.Zero;

        public override DateTimeOffset GetUtcNow()
        {
            var current = _now;
            _now += Step;
            return current;
        }
    }
}
