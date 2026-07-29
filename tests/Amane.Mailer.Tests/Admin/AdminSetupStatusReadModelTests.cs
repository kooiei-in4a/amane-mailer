using Amane.Mailer.Admin;
using Amane.Mailer.Setup;

namespace Amane.Mailer.Tests.Admin;

public sealed class AdminSetupStatusReadModelTests
{
    private const string BundleId = "20260729-abcd1234";
    private const string Fingerprint = "sha256:0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";

    [Fact]
    public void Manual_deployment_is_not_treated_as_error_and_omits_bundle_guesses()
    {
        var model = AdminSetupStatusReadModel.FromInspection(ManualInspection());

        Assert.Equal(AdminSetupDeploymentKind.Manual, model.DeploymentKind);
        Assert.Null(model.SetupBundleId);
        Assert.Null(model.RecordedFingerprint);
        Assert.Equal(AdminSetupVerificationFreshness.NotManaged, model.VerificationFreshness);
        Assert.Equal(AdminSetupConfigurationAppliedDisplay.NotManaged, model.ConfigurationApplied);
        Assert.Equal(AdminSetupSendReadyDisplay.NotManaged, model.SendReady);
        Assert.Equal(SetupInspectIntegrityResult.NotManaged, model.BundleIntegrityResult);
    }

    [Fact]
    public void Invalid_managed_metadata_is_not_classified_as_manual_deployment()
    {
        var inspection = InvalidMetadataInspection();

        var model = AdminSetupStatusReadModel.FromInspection(inspection);

        Assert.Equal(AdminSetupDeploymentKind.InvalidManagedMetadata, model.DeploymentKind);
        Assert.Null(model.SetupBundleId);
        Assert.Equal(AdminSetupVerificationFreshness.Invalid, model.VerificationFreshness);
        Assert.Equal(AdminSetupConfigurationAppliedDisplay.No, model.ConfigurationApplied);
        Assert.Equal(AdminSetupSendReadyDisplay.NotReady, model.SendReady);
        Assert.False(model.DisplayVerificationCommittedPass);
    }

    [Fact]
    public void Managed_runtime_without_host_observation_marks_verification_and_send_ready_unavailable()
    {
        var model = AdminSetupStatusReadModel.FromInspection(ManagedInspection());

        Assert.Equal(AdminSetupDeploymentKind.Managed, model.DeploymentKind);
        Assert.Equal(BundleId, model.SetupBundleId);
        Assert.Equal(AdminSetupVerificationFreshness.Unavailable, model.VerificationFreshness);
        Assert.Equal(AdminSetupStatusReadModel.VerificationUnavailableReason, model.VerificationReason);
        Assert.False(model.DisplayVerificationCommittedPass);
        Assert.Equal(AdminSetupConfigurationAppliedDisplay.Unavailable, model.ConfigurationApplied);
        Assert.Equal(AdminSetupSendReadyDisplay.Unavailable, model.SendReady);
        Assert.Equal(AdminSetupStatusReadModel.SendReadyUnavailableReason, model.SendReadyReasonCode);
    }

    [Fact]
    public void Active_generation_mismatch_marks_verification_stale_and_hides_past_pass()
    {
        var model = AdminSetupStatusReadModel.FromInspection(
            ManagedInspection(),
            hostObservation: CurrentHostObservation(activationGeneration: 2, recordGeneration: 1));

        Assert.Equal(AdminSetupVerificationFreshness.Stale, model.VerificationFreshness);
        Assert.False(model.DisplayVerificationCommittedPass);
        Assert.Equal(AdminSetupConfigurationAppliedDisplay.No, model.ConfigurationApplied);
        Assert.Equal(AdminSetupSendReadyDisplay.NotReady, model.SendReady);
    }

    [Fact]
    public void Observed_integrity_mismatch_blocks_current_pass_and_send_ready()
    {
        var model = AdminSetupStatusReadModel.FromInspection(
            ManagedInspection(
                mode: "production-acs",
                liveSending: true,
                fingerprintsMatch: true,
                integrity: SetupInspectIntegrityResult.Mismatch,
                integrityReason: SetupInspectReason.MountMismatch),
            hostObservation: CurrentHostObservation(includeSendReadyAuthority: true));

        Assert.Equal(AdminSetupVerificationFreshness.Stale, model.VerificationFreshness);
        Assert.False(model.DisplayVerificationCommittedPass);
        Assert.Equal(AdminSetupConfigurationAppliedDisplay.No, model.ConfigurationApplied);
        Assert.Equal(AdminSetupSendReadyDisplay.NotReady, model.SendReady);
        Assert.NotEqual(AdminSetupSendReadyDisplay.Ready, model.SendReady);
        Assert.Equal(SetupInspectIntegrityResult.Mismatch, model.BundleIntegrityResult);
        Assert.Equal(SetupIntegrityMerger.Matched, model.HostCanonicalBundleIntegrity);
    }

    [Fact]
    public void Provisional_not_verified_keeps_host_canonical_separate_and_can_still_be_send_ready()
    {
        var model = AdminSetupStatusReadModel.FromInspection(
            ManagedInspection(mode: "production-acs", liveSending: true, fingerprintsMatch: true),
            hostObservation: CurrentHostObservation(includeSendReadyAuthority: true));

        Assert.Equal(SetupInspectIntegrityResult.NotVerified, model.BundleIntegrityResult);
        Assert.Equal(SetupIntegrityMerger.Matched, model.HostCanonicalBundleIntegrity);
        Assert.Equal(AdminSetupVerificationFreshness.Current, model.VerificationFreshness);
        Assert.True(model.DisplayVerificationCommittedPass);
        Assert.Equal(AdminSetupConfigurationAppliedDisplay.Yes, model.ConfigurationApplied);
        Assert.Equal(AdminSetupSendReadyDisplay.Ready, model.SendReady);
    }

    [Fact]
    public void Send_ready_rejects_authority_from_a_previous_bundle_or_generation()
    {
        var model = AdminSetupStatusReadModel.FromInspection(
            ManagedInspection(mode: "production-acs", liveSending: true, fingerprintsMatch: true),
            hostObservation: CurrentHostObservation(
                includeSendReadyAuthority: true,
                authorityBundleId: "20260728-deadbeef",
                authorityGeneration: 1));

        Assert.Equal(AdminSetupSendReadyDisplay.NotReady, model.SendReady);
        Assert.Equal(
            AdminSetupStatusReadModel.SendReadyAuthorityMismatchReason,
            model.SendReadyReasonCode);
    }

    [Fact]
    public void Current_committed_success_does_not_imply_send_ready_without_bound_authority()
    {
        var model = AdminSetupStatusReadModel.FromInspection(
            ManagedInspection(mode: "production-acs", liveSending: true, fingerprintsMatch: true),
            hostObservation: CurrentHostObservation(includeSendReadyAuthority: false));

        Assert.Equal(AdminSetupVerificationFreshness.Current, model.VerificationFreshness);
        Assert.True(model.DisplayVerificationCommittedPass);
        Assert.Equal(AdminSetupConfigurationAppliedDisplay.Yes, model.ConfigurationApplied);
        Assert.Equal(AdminSetupSendReadyDisplay.Unavailable, model.SendReady);
        Assert.NotEqual(AdminSetupSendReadyDisplay.Ready, model.SendReady);
    }

    [Fact]
    public void Send_ready_requires_bound_authority_live_sending_doctor_and_apply()
    {
        var model = AdminSetupStatusReadModel.FromInspection(
            ManagedInspection(mode: "production-acs", liveSending: true, fingerprintsMatch: true),
            hostObservation: CurrentHostObservation(includeSendReadyAuthority: true));

        Assert.Equal(AdminSetupSendReadyDisplay.Ready, model.SendReady);
        Assert.Equal(AcsSendReadyEvaluator.SendReadyReady, model.SendReadyReasonCode);
    }

    [Fact]
    public void Fingerprint_match_alone_does_not_make_send_ready()
    {
        var model = AdminSetupStatusReadModel.FromInspection(
            ManagedInspection(mode: "production-acs", liveSending: true, fingerprintsMatch: true));

        Assert.True(model.FingerprintsMatchRecorded);
        Assert.Equal(AdminSetupSendReadyDisplay.Unavailable, model.SendReady);
    }

    [Fact]
    public void Staging_summary_is_not_fabricated_outside_staging_verification_mode()
    {
        var model = AdminSetupStatusReadModel.FromInspection(
            ManagedInspection(mode: "production-acs"),
            hostObservation: new AdminSetupHostObservation
            {
                StagingSummary = BoundStagingSummary(),
            });

        Assert.False(model.StagingSummaryApplicable);
        Assert.Equal(AdminSetupStagingSummaryAvailability.NotApplicable, model.StagingSummaryAvailability);
        Assert.Null(model.StagingVerificationCode);
    }

    [Fact]
    public void Staging_verification_mode_shows_unavailable_without_host_summary()
    {
        var model = AdminSetupStatusReadModel.FromInspection(
            ManagedInspection(mode: "staging-verification"));

        Assert.True(model.StagingSummaryApplicable);
        Assert.Equal(AdminSetupStagingSummaryAvailability.Unavailable, model.StagingSummaryAvailability);
    }

    [Fact]
    public void Staging_summary_without_active_binding_is_stale_and_hides_details()
    {
        var model = AdminSetupStatusReadModel.FromInspection(
            ManagedInspection(mode: "staging-verification"),
            hostObservation: new AdminSetupHostObservation
            {
                Active = new SetupActivePointer
                {
                    SchemaVersion = 1,
                    BundleId = BundleId,
                    ActivationGeneration = 2,
                },
                StagingSummary = BoundStagingSummary(activationGeneration: 1),
            });

        Assert.Equal(AdminSetupStagingSummaryAvailability.Stale, model.StagingSummaryAvailability);
        Assert.Equal(AdminSetupStatusReadModel.StagingStaleReason, model.StagingSummaryReason);
        Assert.Null(model.StagingVerificationCode);
        Assert.Null(model.StagingMailboxCheckStatus);
    }

    [Fact]
    public void Staging_verification_mode_can_surface_bound_host_summary()
    {
        var model = AdminSetupStatusReadModel.FromInspection(
            ManagedInspection(mode: "staging-verification"),
            hostObservation: new AdminSetupHostObservation
            {
                Active = new SetupActivePointer
                {
                    SchemaVersion = 1,
                    BundleId = BundleId,
                    ActivationGeneration = 1,
                },
                StagingSummary = BoundStagingSummary(),
            });

        Assert.Equal(AdminSetupStagingSummaryAvailability.Available, model.StagingSummaryAvailability);
        Assert.Equal("staging_ok", model.StagingVerificationCode);
        Assert.Equal("ACTION", model.StagingMailboxCheckStatus);
    }

    private static AdminSetupHostObservation CurrentHostObservation(
        long activationGeneration = 1,
        long recordGeneration = 1,
        bool includeSendReadyAuthority = false,
        string? authorityBundleId = null,
        long? authorityGeneration = null) =>
        new()
        {
            Active = new SetupActivePointer
            {
                SchemaVersion = 1,
                BundleId = BundleId,
                ActivationGeneration = activationGeneration,
            },
            Record = CommittedRecord(recordGeneration),
            SendReadyAuthority = includeSendReadyAuthority
                ? new AdminSetupSendReadyAuthority
                {
                    BundleId = authorityBundleId ?? BundleId,
                    ActivationGeneration = authorityGeneration ?? activationGeneration,
                    ConfigurationFingerprint = Fingerprint,
                    DoctorPassed = true,
                    ApplySucceededWithCommittedVerification = true,
                }
                : null,
        };

    private static AcsSetupWorkflowResult BoundStagingSummary(long activationGeneration = 1) =>
        new()
        {
            Code = AcsSetupResultCode.StagingVerificationSucceeded,
            State = AcsSetupWorkflowState.StagingVerificationSucceeded,
            BundleId = BundleId,
            ActivationGeneration = activationGeneration,
            ConfigurationFingerprint = Fingerprint,
            StagingVerificationCode = "staging_ok",
            StagingMailboxCheckStatus = "ACTION",
            StagingSendRequestAccepted = true,
            StagingOperationCompleted = true,
            MaskedRecipientEmail = "a***@e***.com",
        };


    private static SetupInspectEffectiveResult InvalidMetadataInspection() =>
        new()
        {
            MailerVersion = "1.2.0-test",
            Managed = false,
            Recorded = null,
            Effective = new SetupInspectEffectiveSummary
            {
                ConfigurationFingerprint = null,
                ProviderSummary = null,
                LiveSendingEnabled = null,
                CredentialStatus = SetupInspectCredentialStatus.NotApplicable,
                FingerprintsMatchRecorded = null,
            },
            MountAttestation = new SetupInspectAttestationSummary
            {
                Result = SetupInspectIntegrityResult.InvalidMetadata,
                Reason = SetupInspectReason.MetadataMalformed,
            },
            BundleIntegrity = new SetupInspectAttestationSummary
            {
                Result = SetupInspectIntegrityResult.InvalidMetadata,
                Reason = SetupInspectReason.MetadataMalformed,
            },
            TenantConfigurationSource = SetupInspectSourceIds.NotApplicable,
            CredentialSource = SetupInspectSourceIds.NotApplicable,
            Reason = SetupInspectReason.MetadataMalformed,
        };
    private static SetupInspectEffectiveResult ManualInspection() =>
        new()
        {
            MailerVersion = "1.2.0-test",
            Managed = false,
            Recorded = null,
            Effective = new SetupInspectEffectiveSummary
            {
                ConfigurationFingerprint = Fingerprint,
                ProviderSummary = "mailpit",
                LiveSendingEnabled = false,
                CredentialStatus = SetupInspectCredentialStatus.NotApplicable,
                FingerprintsMatchRecorded = null,
            },
            MountAttestation = new SetupInspectAttestationSummary
            {
                Result = SetupInspectIntegrityResult.NotManaged,
            },
            BundleIntegrity = new SetupInspectAttestationSummary
            {
                Result = SetupInspectIntegrityResult.NotManaged,
            },
            TenantConfigurationSource = SetupInspectSourceIds.ContainerTenants,
            CredentialSource = SetupInspectSourceIds.NotApplicable,
        };

    private static SetupInspectEffectiveResult ManagedInspection(
        string mode = "local-mailpit",
        bool liveSending = false,
        bool fingerprintsMatch = true,
        string integrity = SetupInspectIntegrityResult.NotVerified,
        string? integrityReason = SetupInspectReason.HostAtRestPending) =>
        new()
        {
            MailerVersion = "1.2.0-test",
            Managed = true,
            Recorded = new SetupInspectRecordedSummary
            {
                SetupBundleId = BundleId,
                ConfigurationFingerprint = Fingerprint,
                Mode = mode,
                SchemaVersion = 1,
            },
            Effective = new SetupInspectEffectiveSummary
            {
                ConfigurationFingerprint = Fingerprint,
                ProviderSummary = "acs",
                LiveSendingEnabled = liveSending,
                CredentialStatus = SetupInspectCredentialStatus.Loaded,
                FingerprintsMatchRecorded = fingerprintsMatch,
            },
            MountAttestation = new SetupInspectAttestationSummary
            {
                Result = integrity,
                Reason = integrityReason,
            },
            BundleIntegrity = new SetupInspectAttestationSummary
            {
                Result = integrity,
                Reason = integrityReason,
            },
            TenantConfigurationSource = SetupInspectSourceIds.ContainerTenants,
            CredentialSource = SetupInspectSourceIds.ContainerAcsFile,
        };

    private static SetupVerificationRecord CommittedRecord(long activationGeneration = 1) =>
        new()
        {
            SchemaVersion = 1,
            Status = SetupVerificationRecord.StatusCommitted,
            BundleId = BundleId,
            ActivationGeneration = activationGeneration,
            FingerprintComparison = SetupVerificationRecord.FingerprintMatched,
            HostAtRest = SetupIntegrityMerger.Matched,
            MountAttestation = SetupIntegrityMerger.Matched,
            BundleIntegrity = SetupIntegrityMerger.Matched,
            RuntimeIdentityBinding = SetupRuntimeIdentityBindingResult.Matched,
            Readiness = SetupVerificationRecord.ReadinessPassed,
            SendReadyEvaluation = SetupVerificationRecord.SendReadyNotEvaluated,
            CommittedAt = "2026-07-29T12:00:00Z",
            RecordedSchemaVersion = 1,
            ObservedBundleId = BundleId,
            ComposeIdentity = "compose-identity-test",
        };
}
