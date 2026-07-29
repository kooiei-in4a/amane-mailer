using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using Amane.Mailer.Operations;
using Amane.Mailer.Setup;

namespace Amane.Mailer.Tests.Setup;

/// <summary>
/// Behaviour tests for the Managed apply / rollback / recovery engine (#450).
/// Docker is replaced by a scripted process runner; everything else — bundles, seals, pointers,
/// stamps, and verification records — is real on-disk state so ordering bugs surface.
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

        var result = await harness.Engine.ApplyAsync(harness.Layout, CandidateBundleId, TestContext.Current.CancellationToken);

        Assert.Equal(SetupApplyResultCode.ApplySucceeded, result.Code);
        Assert.Equal(SetupManagedDeploymentState.Active, result.DeploymentState);
        Assert.Equal(CandidateBundleId, result.BundleId);
        Assert.Equal(1, result.ActivationGeneration);
        Assert.True(result.ConfigurationApplied);
        Assert.True(result.VerificationCommitted);
        Assert.Equal(CandidateBundleId, harness.ReadActive()!.BundleId);
        Assert.False(File.Exists(harness.Layout.TransactionStampPath));
        Assert.True(File.Exists(harness.Layout.RuntimeIdentityBindPath));

        var record = harness.ReadRecord();
        Assert.NotNull(record);
        Assert.True(record!.IsCommittedSuccess);
        Assert.Equal(SetupIntegrityMerger.Matched, record.BundleIntegrity);
    }

    [Fact]
    public async Task Fresh_apply_runs_the_migration_because_the_database_is_absent()
    {
        using var harness = ApplyHarness.Create();
        harness.SeedBundle(CandidateBundleId);

        var result = await harness.Engine.ApplyAsync(harness.Layout, CandidateBundleId, TestContext.Current.CancellationToken);

        Assert.Equal(SetupApplyResultCode.ApplySucceeded, result.Code);
        Assert.Contains(harness.Invocations, args => IsMigrationRun(args));
    }

    [Fact]
    public async Task Fresh_apply_refuses_when_a_database_file_already_exists()
    {
        using var harness = ApplyHarness.Create();
        harness.SeedBundle(CandidateBundleId);
        File.WriteAllText(Path.Combine(harness.DataPath, "mailer.db"), string.Empty);

        var result = await harness.Engine.ApplyAsync(harness.Layout, CandidateBundleId, TestContext.Current.CancellationToken);

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

        var result = await harness.Engine.ApplyAsync(harness.Layout, CandidateBundleId, TestContext.Current.CancellationToken);

        Assert.Equal(SetupApplyResultCode.NeedsIntervention, result.Code);
        Assert.Equal("sqlite_sidecar_residue", result.ReasonCode);
        Assert.Equal(SetupApplyActionCode.ReviewDatabaseFiles, result.ActionCode);
        Assert.Null(harness.ReadActive());
    }

    [Fact]
    public async Task Candidate_bundle_id_must_be_safe()
    {
        using var harness = ApplyHarness.Create();

        var result = await harness.Engine.ApplyAsync(harness.Layout, "../escape", TestContext.Current.CancellationToken);

        Assert.Equal(SetupApplyResultCode.FailedUnexpected, result.Code);
        Assert.Equal("candidate_bundle_id_invalid", result.ReasonCode);
        Assert.Empty(harness.Invocations);
    }

    [Fact]
    public async Task Candidate_bundle_must_pass_host_at_rest_validation_before_docker_runs()
    {
        using var harness = ApplyHarness.Create();
        harness.SeedBundle(CandidateBundleId);
        harness.CorruptBundleSeal(CandidateBundleId);

        var result = await harness.Engine.ApplyAsync(harness.Layout, CandidateBundleId, TestContext.Current.CancellationToken);

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
        harness.SeedBundle(ActiveBundleId);
        harness.SeedBundle(CandidateBundleId);
        harness.WriteActive(ActiveBundleId, generation: 4);
        harness.Runner.MigrationClassification = SetupSchemaClassification.Current;

        var result = await harness.Engine.ApplyAsync(harness.Layout, CandidateBundleId, TestContext.Current.CancellationToken);

        Assert.Equal(SetupApplyResultCode.ApplySucceeded, result.Code);
        Assert.Equal(5, result.ActivationGeneration);
        Assert.Equal(CandidateBundleId, harness.ReadActive()!.BundleId);
        Assert.Equal(ActiveBundleId, harness.ReadPrevious()!.BundleId);
        Assert.DoesNotContain(harness.Invocations, IsMigrationRun);
    }

    [Fact]
    public async Task Existing_apply_refuses_when_the_schema_is_behind()
    {
        using var harness = ApplyHarness.Create();
        harness.SeedBundle(ActiveBundleId);
        harness.SeedBundle(CandidateBundleId);
        harness.WriteActive(ActiveBundleId, generation: 1);
        harness.Runner.MigrationClassification = SetupSchemaClassification.Behind;

        var result = await harness.Engine.ApplyAsync(harness.Layout, CandidateBundleId, TestContext.Current.CancellationToken);

        Assert.Equal(SetupApplyResultCode.UpgradeRequired, result.Code);
        Assert.Equal("schema_behind", result.ReasonCode);
        Assert.Equal(ActiveBundleId, harness.ReadActive()!.BundleId);
    }

    [Fact]
    public async Task Existing_apply_refuses_when_the_schema_is_ahead_or_unsupported()
    {
        using var harness = ApplyHarness.Create();
        harness.SeedBundle(ActiveBundleId);
        harness.SeedBundle(CandidateBundleId);
        harness.WriteActive(ActiveBundleId, generation: 1);
        harness.Runner.MigrationClassification = SetupSchemaClassification.AheadOrUnsupported;

        var result = await harness.Engine.ApplyAsync(harness.Layout, CandidateBundleId, TestContext.Current.CancellationToken);

        Assert.Equal(SetupApplyResultCode.UpgradeRequired, result.Code);
        Assert.Equal("schema_ahead_or_unsupported", result.ReasonCode);
    }

    [Fact]
    public async Task Existing_apply_needs_intervention_when_the_schema_cannot_be_classified()
    {
        using var harness = ApplyHarness.Create();
        harness.SeedBundle(ActiveBundleId);
        harness.SeedBundle(CandidateBundleId);
        harness.WriteActive(ActiveBundleId, generation: 1);
        harness.Runner.MigrationClassification = SetupSchemaClassification.Unknown;

        var result = await harness.Engine.ApplyAsync(harness.Layout, CandidateBundleId, TestContext.Current.CancellationToken);

        Assert.Equal(SetupApplyResultCode.NeedsIntervention, result.Code);
        Assert.Equal(SetupApplyActionCode.ReviewDatabaseSchema, result.ActionCode);
        Assert.Equal(ActiveBundleId, harness.ReadActive()!.BundleId);
    }

    [Fact]
    public async Task Existing_apply_needs_intervention_when_the_active_bundle_is_no_longer_valid()
    {
        using var harness = ApplyHarness.Create();
        harness.SeedBundle(ActiveBundleId);
        harness.SeedBundle(CandidateBundleId);
        harness.WriteActive(ActiveBundleId, generation: 1);
        harness.CorruptBundleSeal(ActiveBundleId);

        var result = await harness.Engine.ApplyAsync(harness.Layout, CandidateBundleId, TestContext.Current.CancellationToken);

        Assert.Equal(SetupApplyResultCode.NeedsIntervention, result.Code);
        Assert.Equal("active_bundle_invalid", result.ReasonCode);
    }

    [Fact]
    public async Task Apply_refuses_while_an_interrupted_transaction_is_present()
    {
        using var harness = ApplyHarness.Create();
        harness.SeedBundle(CandidateBundleId);
        harness.WriteStamp(SetupTransactionPhase.Recreating, terminal: false);

        var result = await harness.Engine.ApplyAsync(harness.Layout, CandidateBundleId, TestContext.Current.CancellationToken);

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

        var result = await harness.Engine.ApplyAsync(harness.Layout, CandidateBundleId, TestContext.Current.CancellationToken);

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
            var result = await harness.Engine.ApplyAsync(harness.Layout, CandidateBundleId, TestContext.Current.CancellationToken);
            Assert.Equal(SetupApplyResultCode.ConcurrentApplyRejected, result.Code);
        }
    }

    [Fact]
    public async Task Apply_refuses_unsafe_residue_in_the_verifier_temp_directory()
    {
        using var harness = ApplyHarness.Create();
        harness.SeedBundle(CandidateBundleId);
        Directory.CreateDirectory(harness.Layout.VerifierTempDir);
        File.WriteAllText(Path.Combine(harness.Layout.VerifierTempDir, "attacker.json"), "{}");

        var result = await harness.Engine.ApplyAsync(harness.Layout, CandidateBundleId, TestContext.Current.CancellationToken);

        Assert.Equal(SetupApplyResultCode.FreshApplyFailed, result.Code);
        Assert.Equal(SetupApplyActionCode.UnsafeVerifierResidue, result.ActionCode);
        Assert.Null(harness.ReadActive());
    }

    [Fact]
    public async Task Apply_refuses_when_the_pinned_image_is_unavailable()
    {
        using var harness = ApplyHarness.Create();
        harness.SeedBundle(CandidateBundleId);
        harness.Runner.FailWhen = static args => args.Contains("pull", StringComparer.Ordinal);

        var result = await harness.Engine.ApplyAsync(harness.Layout, CandidateBundleId, TestContext.Current.CancellationToken);

        Assert.Equal(SetupApplyResultCode.FreshApplyFailed, result.Code);
        Assert.Null(harness.ReadActive());
    }

    // ------------------------------------------------- external input drift

    [Fact]
    public async Task External_input_change_before_activation_stops_the_apply()
    {
        using var harness = ApplyHarness.Create();
        harness.SeedBundle(CandidateBundleId);

        // The image pull is the last pre-activation Docker step, so mutate external.env there.
        harness.Runner.BeforeRun = args =>
        {
            if (args.Contains("pull", StringComparer.Ordinal))
            {
                harness.MutateExternalEnv();
            }
        };

        var result = await harness.Engine.ApplyAsync(harness.Layout, CandidateBundleId, TestContext.Current.CancellationToken);

        Assert.Equal(SetupApplyResultCode.FreshApplyFailed, result.Code);
        Assert.Equal("external_input_changed_after_activation", result.ReasonCode);
        Assert.Null(harness.ReadActive());
    }

    [Fact]
    public async Task External_input_change_after_readiness_invalidates_the_apply()
    {
        using var harness = ApplyHarness.Create();
        harness.SeedBundle(ActiveBundleId);
        harness.SeedBundle(CandidateBundleId);
        harness.WriteActive(ActiveBundleId, generation: 2);
        harness.Runner.MigrationClassification = SetupSchemaClassification.Current;
        harness.Runner.BeforeRun = args =>
        {
            if (IsHealthCheck(args))
            {
                harness.MutateExternalEnv();
            }
        };

        var result = await harness.Engine.ApplyAsync(harness.Layout, CandidateBundleId, TestContext.Current.CancellationToken);

        Assert.Equal(SetupApplyResultCode.ApplyFailedRollbackSucceeded, result.Code);
        Assert.Equal("external_input_changed_before_verification", result.ReasonCode);
    }

    // -------------------------------------------------------------- rollback

    [Fact]
    public async Task Failed_readiness_on_a_fresh_apply_removes_active()
    {
        using var harness = ApplyHarness.Create(advanceClock: true);
        harness.SeedBundle(CandidateBundleId);
        harness.Runner.FailWhen = IsHealthCheck;

        var result = await harness.Engine.ApplyAsync(harness.Layout, CandidateBundleId, TestContext.Current.CancellationToken);

        Assert.Equal(SetupApplyResultCode.FreshApplyFailed, result.Code);
        Assert.Equal(SetupManagedDeploymentState.NoManaged, result.DeploymentState);
        Assert.Equal("readiness_failed", result.ReasonCode);
        Assert.Equal(SetupConfigRollbackStatus.Succeeded, result.ConfigRollbackStatus);
        Assert.Null(harness.ReadActive());
        Assert.False(File.Exists(harness.Layout.TransactionStampPath));
    }

    [Fact]
    public async Task Readiness_that_never_recovers_reports_a_failed_rollback()
    {
        using var harness = ApplyHarness.Create(advanceClock: true);
        harness.SeedBundle(ActiveBundleId);
        harness.SeedBundle(CandidateBundleId);
        harness.WriteActive(ActiveBundleId, generation: 3);
        harness.Runner.MigrationClassification = SetupSchemaClassification.Current;

        // The health check stays broken, so the restored previous deployment cannot pass readiness
        // either. The pointer is still restored, but the rollback itself is reported as failed.
        harness.Runner.FailWhen = IsHealthCheck;

        var result = await harness.Engine.ApplyAsync(harness.Layout, CandidateBundleId, TestContext.Current.CancellationToken);

        Assert.Equal(SetupApplyResultCode.ApplyFailedRollbackFailed, result.Code);
        Assert.Equal(SetupConfigRollbackStatus.Failed, result.ConfigRollbackStatus);
        Assert.Equal(ActiveBundleId, harness.ReadActive()!.BundleId);
        Assert.Equal(5, harness.ReadActive()!.ActivationGeneration);
    }

    [Fact]
    public async Task Rollback_restores_the_previous_generation_monotonically()
    {
        using var harness = ApplyHarness.Create();
        harness.SeedBundle(ActiveBundleId);
        harness.SeedBundle(CandidateBundleId);
        harness.WriteActive(ActiveBundleId, generation: 3);
        harness.Runner.MigrationClassification = SetupSchemaClassification.Current;

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

        var result = await harness.Engine.ApplyAsync(harness.Layout, CandidateBundleId, TestContext.Current.CancellationToken);

        Assert.Equal(SetupApplyResultCode.ApplyFailedRollbackSucceeded, result.Code);
        Assert.Equal(SetupManagedDeploymentState.Active, result.DeploymentState);
        Assert.Equal("recreate_failed", result.ReasonCode);
        Assert.Equal(SetupConfigRollbackStatus.Succeeded, result.ConfigRollbackStatus);
        Assert.Equal(ActiveBundleId, result.BundleId);
        Assert.Equal(5, result.ActivationGeneration);
        Assert.Equal(5, harness.ReadActive()!.ActivationGeneration);
        Assert.False(File.Exists(harness.Layout.TransactionStampPath));
    }

    [Fact]
    public async Task Rollback_after_a_migration_reports_a_persistent_side_effect()
    {
        using var harness = ApplyHarness.Create();
        harness.SeedBundle(CandidateBundleId);
        harness.Runner.FailWhen = static args => IsComposeSubcommand(args, "up");

        var result = await harness.Engine.ApplyAsync(harness.Layout, CandidateBundleId, TestContext.Current.CancellationToken);

        Assert.Equal(SetupApplyResultCode.FreshApplyFailed, result.Code);
        Assert.True(result.PersistentSideEffectMayRemain);
        Assert.Equal(SetupPersistentSideEffectKind.DatabaseMigration, result.PersistentSideEffectKind);
        Assert.Equal(SetupApplyActionCode.ReviewDatabaseFiles, result.ActionCode);
    }

    [Fact]
    public async Task Failed_migration_rolls_the_configuration_back()
    {
        using var harness = ApplyHarness.Create();
        harness.SeedBundle(CandidateBundleId);
        harness.Runner.FailWhen = IsMigrationRun;

        var result = await harness.Engine.ApplyAsync(harness.Layout, CandidateBundleId, TestContext.Current.CancellationToken);

        Assert.Equal(SetupApplyResultCode.FreshApplyFailed, result.Code);
        Assert.Equal("migration_failed", result.ReasonCode);
        Assert.True(result.PersistentSideEffectMayRemain);
        Assert.Null(harness.ReadActive());
    }

    [Fact]
    public async Task Compose_validation_failure_after_activation_rolls_back_without_side_effects()
    {
        using var harness = ApplyHarness.Create();
        harness.SeedBundle(CandidateBundleId);
        harness.Runner.FailWhen = static args => IsComposeSubcommand(args, "config");

        var result = await harness.Engine.ApplyAsync(harness.Layout, CandidateBundleId, TestContext.Current.CancellationToken);

        Assert.Equal(SetupApplyResultCode.FreshApplyFailed, result.Code);
        Assert.Equal("compose_validation_failed", result.ReasonCode);
        Assert.False(result.PersistentSideEffectMayRemain);
        Assert.Equal(SetupPersistentSideEffectKind.None, result.PersistentSideEffectKind);
    }

    [Fact]
    public async Task Mount_attestation_mismatch_invalidates_the_verification_record_and_rolls_back()
    {
        using var harness = ApplyHarness.Create();
        harness.SeedBundle(CandidateBundleId);
        harness.Runner.MountAttestationResult = SetupInspectIntegrityResult.Mismatch;

        var result = await harness.Engine.ApplyAsync(harness.Layout, CandidateBundleId, TestContext.Current.CancellationToken);

        Assert.Equal(SetupApplyResultCode.FreshApplyFailed, result.Code);
        Assert.Equal("bundle_integrity_mismatch", result.ReasonCode);
        Assert.False(result.VerificationCommitted);

        var record = harness.ReadRecord();
        Assert.NotNull(record);
        Assert.Equal(SetupVerificationRecord.StatusInvalidated, record!.Status);
        Assert.False(record.IsCommittedSuccess);
    }

    [Fact]
    public async Task Fingerprint_mismatch_rolls_back_even_when_integrity_matches()
    {
        using var harness = ApplyHarness.Create();
        harness.SeedBundle(CandidateBundleId);
        harness.Runner.FingerprintsMatchRecorded = false;

        var result = await harness.Engine.ApplyAsync(harness.Layout, CandidateBundleId, TestContext.Current.CancellationToken);

        Assert.Equal(SetupApplyResultCode.FreshApplyFailed, result.Code);
        Assert.Equal("fingerprint_mismatch", result.ReasonCode);
        Assert.Equal(
            SetupVerificationRecord.FingerprintMismatch,
            harness.ReadRecord()!.FingerprintComparison);
    }

    [Fact]
    public async Task Failed_effective_inspection_rolls_back()
    {
        using var harness = ApplyHarness.Create();
        harness.SeedBundle(CandidateBundleId);
        harness.Runner.FailWhen = IsInspectEffective;

        var result = await harness.Engine.ApplyAsync(harness.Layout, CandidateBundleId, TestContext.Current.CancellationToken);

        Assert.Equal(SetupApplyResultCode.FreshApplyFailed, result.Code);
        Assert.Equal("effective_inspection_failed", result.ReasonCode);
    }

    // ------------------------------------------------------------ invariants

    [Fact]
    public async Task Apply_never_asserts_send_readiness()
    {
        using var harness = ApplyHarness.Create();
        harness.SeedBundle(CandidateBundleId);

        var result = await harness.Engine.ApplyAsync(harness.Layout, CandidateBundleId, TestContext.Current.CancellationToken);

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

        var result = await harness.Engine.ApplyAsync(harness.Layout, CandidateBundleId, TestContext.Current.CancellationToken);

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

        var result = await harness.Engine.ApplyAsync(harness.Layout, CandidateBundleId, TestContext.Current.CancellationToken);
        Assert.Equal(SetupApplyResultCode.ApplySucceeded, result.Code);

        var raw = File.ReadAllText(harness.Layout.LastRecordPath);
        Assert.DoesNotContain("synthetic-mail-token-not-real", raw, StringComparison.Ordinal);
        Assert.DoesNotContain("synthetic-metrics-token-not-real", raw, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Runtime_identity_binding_is_owner_only_and_holds_no_raw_paths()
    {
        using var harness = ApplyHarness.Create();
        harness.SeedBundle(CandidateBundleId);

        var result = await harness.Engine.ApplyAsync(harness.Layout, CandidateBundleId, TestContext.Current.CancellationToken);
        Assert.Equal(SetupApplyResultCode.ApplySucceeded, result.Code);

        var path = harness.Layout.RuntimeIdentityBindPath;
        Assert.True(File.Exists(path));
        Assert.True(new HostSetupFileSystem().IsOwnerOnlyFile(path));
        Assert.DoesNotContain(harness.DataPath, File.ReadAllText(path), StringComparison.OrdinalIgnoreCase);
    }

    // -------------------------------------------------------------- recovery

    [Fact]
    public async Task Recovery_reports_success_when_active_and_verification_agree()
    {
        using var harness = ApplyHarness.Create();
        harness.SeedBundle(CandidateBundleId);
        Assert.Equal(
            SetupApplyResultCode.ApplySucceeded,
            (await harness.Engine.ApplyAsync(harness.Layout, CandidateBundleId, TestContext.Current.CancellationToken)).Code);

        var result = await harness.Engine.RecoverAsync(harness.Layout, TestContext.Current.CancellationToken);

        Assert.Equal(SetupApplyResultCode.ApplySucceeded, result.Code);
        Assert.Equal(SetupManagedDeploymentState.Active, result.DeploymentState);
        Assert.True(result.VerificationCommitted);
    }

    [Fact]
    public async Task Recovery_reports_no_managed_deployment_when_nothing_is_in_flight()
    {
        using var harness = ApplyHarness.Create();

        var result = await harness.Engine.RecoverAsync(harness.Layout, TestContext.Current.CancellationToken);

        Assert.Equal(SetupApplyResultCode.RollbackSucceeded, result.Code);
        Assert.Equal(SetupManagedDeploymentState.NoManaged, result.DeploymentState);
    }

    [Fact]
    public async Task Recovery_needs_intervention_when_active_has_no_matching_verification()
    {
        using var harness = ApplyHarness.Create();
        harness.SeedBundle(ActiveBundleId);
        harness.WriteActive(ActiveBundleId, generation: 1);

        var result = await harness.Engine.RecoverAsync(harness.Layout, TestContext.Current.CancellationToken);

        Assert.Equal(SetupApplyResultCode.NeedsIntervention, result.Code);
        Assert.Equal("verification_record_missing", result.ReasonCode);
    }

    [Fact]
    public async Task Recovery_of_a_prepared_stamp_clears_it_without_touching_active()
    {
        using var harness = ApplyHarness.Create();
        harness.SeedBundle(ActiveBundleId);
        harness.WriteActive(ActiveBundleId, generation: 2);
        harness.WriteStamp(SetupTransactionPhase.Prepared, terminal: false);

        var result = await harness.Engine.RecoverAsync(harness.Layout, TestContext.Current.CancellationToken);

        Assert.Equal(SetupApplyResultCode.RollbackSucceeded, result.Code);
        Assert.Equal(SetupManagedDeploymentState.Active, result.DeploymentState);
        Assert.Equal(SetupConfigRollbackStatus.NotApplicable, result.ConfigRollbackStatus);
        Assert.Equal(ActiveBundleId, harness.ReadActive()!.BundleId);
        Assert.Equal(2, harness.ReadActive()!.ActivationGeneration);
        Assert.False(File.Exists(harness.Layout.TransactionStampPath));
        Assert.Empty(harness.Invocations);
    }

    [Fact]
    public async Task Recovery_of_a_verification_committed_stamp_reports_the_apply_as_finished()
    {
        using var harness = ApplyHarness.Create();
        harness.SeedBundle(CandidateBundleId);
        harness.WriteActive(CandidateBundleId, generation: 1);
        harness.WriteStamp(SetupTransactionPhase.VerificationCommitted, terminal: false);

        var result = await harness.Engine.RecoverAsync(harness.Layout, TestContext.Current.CancellationToken);

        Assert.Equal(SetupApplyResultCode.ApplySucceeded, result.Code);
        Assert.True(result.VerificationCommitted);
        Assert.False(File.Exists(harness.Layout.TransactionStampPath));
    }

    [Fact]
    public async Task Recovery_of_a_terminal_stamp_always_requires_a_human()
    {
        using var harness = ApplyHarness.Create();
        harness.SeedBundle(CandidateBundleId);
        harness.WriteActive(CandidateBundleId, generation: 1);
        harness.WriteStamp(SetupTransactionPhase.RollbackPending, terminal: true, migrationSideEffect: true);

        var result = await harness.Engine.RecoverAsync(harness.Layout, TestContext.Current.CancellationToken);

        Assert.Equal(SetupApplyResultCode.NeedsIntervention, result.Code);
        Assert.Equal(SetupApplyActionCode.ManualInterventionRequired, result.ActionCode);
        Assert.Equal(SetupConfigRollbackStatus.Failed, result.ConfigRollbackStatus);
        Assert.True(result.PersistentSideEffectMayRemain);
        Assert.True(File.Exists(harness.Layout.TransactionStampPath));
    }

    [Fact]
    public async Task Recovery_after_activation_rolls_the_configuration_back_to_previous()
    {
        using var harness = ApplyHarness.Create();
        harness.SeedBundle(ActiveBundleId);
        harness.SeedBundle(CandidateBundleId);
        harness.WriteActive(CandidateBundleId, generation: 6);
        harness.WritePrevious(ActiveBundleId, generation: 5);
        harness.WriteStamp(
            SetupTransactionPhase.ReadinessChecking,
            terminal: false,
            previousBundleId: ActiveBundleId,
            previousGeneration: 5,
            targetGeneration: 6);

        var result = await harness.Engine.RecoverAsync(harness.Layout, TestContext.Current.CancellationToken);

        Assert.Equal(SetupApplyResultCode.RollbackSucceeded, result.Code);
        Assert.Equal(SetupManagedDeploymentState.Active, result.DeploymentState);
        Assert.Equal(ActiveBundleId, harness.ReadActive()!.BundleId);
        Assert.Equal(7, harness.ReadActive()!.ActivationGeneration);
        Assert.False(File.Exists(harness.Layout.TransactionStampPath));
    }

    [Fact]
    public async Task Recovery_after_a_migration_reports_intervention_for_the_persistent_side_effect()
    {
        using var harness = ApplyHarness.Create();
        harness.SeedBundle(ActiveBundleId);
        harness.SeedBundle(CandidateBundleId);
        harness.WriteActive(CandidateBundleId, generation: 6);
        harness.WritePrevious(ActiveBundleId, generation: 5);
        harness.WriteStamp(
            SetupTransactionPhase.Migrating,
            terminal: false,
            migrationSideEffect: true,
            previousBundleId: ActiveBundleId,
            previousGeneration: 5,
            targetGeneration: 6);

        var result = await harness.Engine.RecoverAsync(harness.Layout, TestContext.Current.CancellationToken);

        Assert.Equal(SetupApplyResultCode.NeedsIntervention, result.Code);
        Assert.True(result.PersistentSideEffectMayRemain);
        Assert.Equal(SetupPersistentSideEffectKind.DatabaseMigration, result.PersistentSideEffectKind);
    }

    [Fact]
    public async Task Recovery_of_a_fresh_apply_after_activation_removes_active()
    {
        using var harness = ApplyHarness.Create();
        harness.SeedBundle(CandidateBundleId);
        harness.WriteActive(CandidateBundleId, generation: 1);
        harness.WriteStamp(SetupTransactionPhase.Recreating, terminal: false);

        var result = await harness.Engine.RecoverAsync(harness.Layout, TestContext.Current.CancellationToken);

        Assert.Equal(SetupApplyResultCode.RollbackSucceeded, result.Code);
        Assert.Equal(SetupManagedDeploymentState.NoManaged, result.DeploymentState);
        Assert.Null(harness.ReadActive());
        Assert.False(File.Exists(harness.Layout.TransactionStampPath));
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

        var result = await harness.Engine.RecoverAsync(harness.Layout, TestContext.Current.CancellationToken);

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

        var result = await harness.Engine.ApplyAsync(harness.Layout, CandidateBundleId, TestContext.Current.CancellationToken);

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

        var first = await harness.Engine.RecoverAsync(harness.Layout, TestContext.Current.CancellationToken);
        Assert.Equal(SetupApplyResultCode.RollbackSucceeded, first.Code);

        var second = await harness.Engine.RecoverAsync(harness.Layout, TestContext.Current.CancellationToken);
        Assert.Equal(SetupApplyResultCode.RollbackSucceeded, second.Code);
        Assert.Equal(SetupManagedDeploymentState.NoManaged, second.DeploymentState);
    }

    // --------------------------------------------------------------- helpers

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
        private readonly HostSetupFileSystem _fileSystem = new();

        private ApplyHarness(
            string root,
            TrustedSetupHostLayout layout,
            SetupHostDockerAdapter adapter,
            SetupApplyEngine engine,
            ApplyProcessRunner runner,
            string dataPath)
        {
            Root = root;
            Layout = layout;
            Adapter = adapter;
            Engine = engine;
            Runner = runner;
            DataPath = dataPath;
        }

        public string Root { get; }
        public TrustedSetupHostLayout Layout { get; }
        public SetupHostDockerAdapter Adapter { get; }
        public SetupApplyEngine Engine { get; }
        public ApplyProcessRunner Runner { get; }
        public string DataPath { get; }

        public IReadOnlyList<IReadOnlyList<string>> Invocations => Runner.OperationInvocations;

        /// <param name="advanceClock">
        /// Advance the clock on every read so readiness retry loops reach their deadline without
        /// real waiting. Success paths do not need it because the first attempt passes.
        /// </param>
        public static ApplyHarness Create(bool advanceClock = false)
        {
            var root = Path.GetFullPath(
                Path.Combine(Path.GetTempPath(), "amane-apply-" + Guid.NewGuid().ToString("N")));
            var fileSystem = new HostSetupFileSystem();
            var layoutResult = TrustedSetupHostLayoutResolver.CreateLayoutForTests(
                fileSystem,
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

            var runner = new ApplyProcessRunner();
            var timeProvider = advanceClock
                ? new SteppingTimeProvider(TimeSpan.FromSeconds(200))
                : new SteppingTimeProvider(TimeSpan.Zero);
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

            return new ApplyHarness(root, layout, adapter, engine, runner, dataPath);
        }

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

        public void WriteStamp(
            string phase,
            bool terminal,
            bool migrationSideEffect = false,
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
                CandidateBundleId = CandidateBundleId,
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

        public SetupVerificationRecord? ReadRecord()
        {
            if (!File.Exists(Layout.LastRecordPath))
            {
                return null;
            }

            return JsonSerializer.Deserialize(
                File.ReadAllBytes(Layout.LastRecordPath),
                SetupApplyJsonContext.Default.SetupVerificationRecord);
        }

        public void Dispose()
        {
            _ = _fileSystem;
            try
            {
                Directory.Delete(Root, recursive: true);
            }
            catch
            {
                // Best-effort temp cleanup.
            }
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
    /// Scripted Docker replacement. Binding probes always succeed; every other invocation is
    /// recorded so tests can assert ordering, and can be failed selectively via
    /// <see cref="FailWhen"/>.
    /// </summary>
    private sealed class ApplyProcessRunner : IHostProcessRunner
    {
        public const string SecretCanary = "canary-token-value-must-not-leak";

        private readonly ConcurrentQueue<IReadOnlyList<string>> _operations = new();

        public Func<IReadOnlyList<string>, bool>? FailWhen { get; set; }
        public Action<IReadOnlyList<string>>? BeforeRun { get; set; }
        public string MigrationClassification { get; set; } = SetupSchemaClassification.DatabaseAbsent;
        public string MountAttestationResult { get; set; } = SetupInspectIntegrityResult.Matched;
        public bool FingerprintsMatchRecorded { get; set; } = true;
        public bool LeakCanaries { get; set; }

        public IReadOnlyList<IReadOnlyList<string>> OperationInvocations => _operations.ToArray();

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

        private string InspectionJson() =>
            $$"""
            {
              "schemaVersion": 1,
              "mailerVersion": "1.0.0",
              "managed": true,
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
    private sealed class SteppingTimeProvider(TimeSpan step) : TimeProvider
    {
        private DateTimeOffset _now = new(2026, 7, 29, 0, 0, 0, TimeSpan.Zero);

        public override DateTimeOffset GetUtcNow()
        {
            var current = _now;
            _now += step;
            return current;
        }
    }
}
