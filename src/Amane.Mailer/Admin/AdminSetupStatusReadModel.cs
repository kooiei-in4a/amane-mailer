using System.Text.Json;
using Amane.Mailer.Configuration;
using Amane.Mailer.Setup;
using Microsoft.Extensions.Configuration;

namespace Amane.Mailer.Admin;

/// <summary>
/// Read-only setup status projection for <c>/admin/setup-status</c> (ADR 0021 D-06 / #454).
/// Aggregates #447 effective inspection and runtime-visible recorded metadata.
/// Host-only ACTIVE / verification / Staging summary inputs are optional overlays for
/// tests or future host-visible adapters; Admin runtime does not open the Managed root.
/// </summary>
public sealed record AdminSetupStatusReadModel
{
    public const string VerificationUnavailableReason = "host-verification-not-observable";
    public const string StagingUnavailableReason = "host-staging-summary-not-observable";
    public const string StagingNotApplicableReason = "mode-not-staging-verification";
    public const string StagingStaleReason = "staging-summary-not-bound-to-active";
    public const string SendReadyUnavailableReason = "host-send-ready-inputs-not-observable";
    public const string SendReadyAuthorityMismatchReason = "send-ready-authority-not-bound-to-active";
    public const string ObservedIntegrityBlocksReadyReason = "observed-integrity-blocks-send-ready";

    public required AdminSetupDeploymentKind DeploymentKind { get; init; }
    public required string MailerVersion { get; init; }
    public string? SetupBundleId { get; init; }
    public string? Mode { get; init; }
    public string? ProviderSummary { get; init; }
    public string? SenderEmail { get; init; }
    public bool PlatformSenderPresent { get; init; }
    public required string CredentialStatus { get; init; }
    public bool? LiveSendingEnabled { get; init; }
    public string? RecordedFingerprint { get; init; }
    public string? EffectiveFingerprint { get; init; }
    public bool? FingerprintsMatchRecorded { get; init; }
    public required string BundleIntegrityResult { get; init; }
    public string? BundleIntegrityReason { get; init; }
    public string? HostCanonicalBundleIntegrity { get; init; }
    public string? RecordedCreatedAt { get; init; }
    public string? ImageRepository { get; init; }
    public string? ImageTag { get; init; }
    public string? ComposeIdentity { get; init; }
    public string? InspectReason { get; init; }

    public required AdminSetupVerificationFreshness VerificationFreshness { get; init; }
    public string? VerificationStatus { get; init; }
    public string? VerificationReason { get; init; }
    public string? VerificationCommittedAt { get; init; }
    public long? VerificationActivationGeneration { get; init; }
    public long? ActiveActivationGeneration { get; init; }
    public bool DisplayVerificationCommittedPass { get; init; }

    public required AdminSetupConfigurationAppliedDisplay ConfigurationApplied { get; init; }
    public required AdminSetupSendReadyDisplay SendReady { get; init; }
    public string? SendReadyReasonCode { get; init; }

    public bool StagingSummaryApplicable { get; init; }
    public required AdminSetupStagingSummaryAvailability StagingSummaryAvailability { get; init; }
    public string? StagingSummaryReason { get; init; }
    public string? StagingVerificationCode { get; init; }
    public string? StagingMailboxCheckStatus { get; init; }
    public bool? StagingSendRequestAccepted { get; init; }
    public bool? StagingOperationCompleted { get; init; }

    /// <summary>
    /// Builds the runtime Admin projection. Does not read host Managed root paths.
    /// Named CreateFromConfiguration (not Load) so it is not treated as a startup-validated
    /// options entry point by MailerStartupValidationInventoryTests.
    /// </summary>
    public static AdminSetupStatusReadModel CreateFromConfiguration(IConfiguration configuration)
    {
        var inspection = SetupInspectEffectiveEngine.Inspect(configuration);
        TryLoadRecordedExtras(configuration, out var recordedExtra, out var senderEmail);
        return FromInspection(inspection, recordedExtra, senderEmail, hostObservation: null);
    }

    /// <summary>
    /// Pure projection used by Admin rendering and focused tests.
    /// </summary>
    public static AdminSetupStatusReadModel FromInspection(
        SetupInspectEffectiveResult inspection,
        SetupRecordedMetadata? recordedExtra = null,
        string? senderEmail = null,
        AdminSetupHostObservation? hostObservation = null)
    {
        ArgumentNullException.ThrowIfNull(inspection);

        var deploymentKind = ClassifyDeployment(inspection);
        var showManagedFields = deploymentKind == AdminSetupDeploymentKind.Managed;

        var mode = inspection.Recorded?.Mode ?? recordedExtra?.Mode;
        var stagingApplicable = IsStagingVerificationMode(mode);

        var verification = EvaluateVerification(
            deploymentKind,
            hostObservation,
            inspection,
            recordedExtra);
        var configurationApplied = EvaluateConfigurationApplied(
            deploymentKind,
            verification,
            hostObservation,
            inspection);
        var sendReady = EvaluateSendReady(
            deploymentKind,
            mode,
            inspection,
            verification,
            hostObservation);

        var staging = EvaluateStagingSummary(
            stagingApplicable,
            deploymentKind,
            hostObservation,
            inspection,
            recordedExtra);

        return new AdminSetupStatusReadModel
        {
            DeploymentKind = deploymentKind,
            MailerVersion = inspection.MailerVersion,
            SetupBundleId = showManagedFields
                ? inspection.Recorded?.SetupBundleId ?? recordedExtra?.BundleId
                : null,
            Mode = showManagedFields ? mode : null,
            ProviderSummary = inspection.Effective.ProviderSummary,
            SenderEmail = senderEmail,
            PlatformSenderPresent = recordedExtra?.PlatformSenderPresent == true,
            CredentialStatus = inspection.Effective.CredentialStatus,
            LiveSendingEnabled = inspection.Effective.LiveSendingEnabled,
            RecordedFingerprint = showManagedFields
                ? inspection.Recorded?.ConfigurationFingerprint
                : null,
            EffectiveFingerprint = inspection.Effective.ConfigurationFingerprint,
            FingerprintsMatchRecorded = showManagedFields
                ? inspection.Effective.FingerprintsMatchRecorded
                : null,
            BundleIntegrityResult = inspection.BundleIntegrity.Result,
            BundleIntegrityReason = inspection.BundleIntegrity.Reason,
            HostCanonicalBundleIntegrity = hostObservation?.Record?.BundleIntegrity,
            RecordedCreatedAt = showManagedFields ? recordedExtra?.CreatedAt : null,
            ImageRepository = showManagedFields ? recordedExtra?.ImageRepository : null,
            ImageTag = showManagedFields ? recordedExtra?.ImageTag : null,
            ComposeIdentity = showManagedFields ? hostObservation?.Record?.ComposeIdentity : null,
            InspectReason = inspection.Reason,
            VerificationFreshness = verification.Freshness,
            VerificationStatus = verification.Status,
            VerificationReason = verification.Reason,
            VerificationCommittedAt = verification.CommittedAt,
            VerificationActivationGeneration = verification.RecordActivationGeneration,
            ActiveActivationGeneration = verification.ActiveActivationGeneration,
            DisplayVerificationCommittedPass = verification.DisplayCommittedPass,
            ConfigurationApplied = configurationApplied,
            SendReady = sendReady.Display,
            SendReadyReasonCode = sendReady.ReasonCode,
            StagingSummaryApplicable = stagingApplicable,
            StagingSummaryAvailability = staging.Availability,
            StagingSummaryReason = staging.Reason,
            StagingVerificationCode = staging.Code,
            StagingMailboxCheckStatus = staging.MailboxCheckStatus,
            StagingSendRequestAccepted = staging.SendRequestAccepted,
            StagingOperationCompleted = staging.OperationCompleted,
        };
    }

    public static AdminSetupDeploymentKind ClassifyDeployment(SetupInspectEffectiveResult inspection)
    {
        ArgumentNullException.ThrowIfNull(inspection);

        if (IsInvalidManagedMetadata(inspection))
            return AdminSetupDeploymentKind.InvalidManagedMetadata;

        return inspection.Managed
            ? AdminSetupDeploymentKind.Managed
            : AdminSetupDeploymentKind.Manual;
    }

    public static AdminSetupVerificationEvaluation EvaluateVerificationFreshness(
        bool managed,
        SetupActivePointer? active,
        SetupVerificationRecord? record,
        bool transactionInProgress,
        SetupInspectEffectiveResult? inspection = null,
        SetupRecordedMetadata? recordedExtra = null)
    {
        var deploymentKind = inspection is null
            ? (managed ? AdminSetupDeploymentKind.Managed : AdminSetupDeploymentKind.Manual)
            : ClassifyDeployment(inspection);

        return EvaluateVerificationFreshnessCore(
            deploymentKind,
            active,
            record,
            transactionInProgress,
            inspection,
            recordedExtra);
    }

    private static AdminSetupVerificationEvaluation EvaluateVerification(
        AdminSetupDeploymentKind deploymentKind,
        AdminSetupHostObservation? hostObservation,
        SetupInspectEffectiveResult inspection,
        SetupRecordedMetadata? recordedExtra) =>
        EvaluateVerificationFreshnessCore(
            deploymentKind,
            hostObservation?.Active,
            hostObservation?.Record,
            hostObservation?.TransactionInProgress == true,
            inspection,
            recordedExtra);

    private static AdminSetupVerificationEvaluation EvaluateVerificationFreshnessCore(
        AdminSetupDeploymentKind deploymentKind,
        SetupActivePointer? active,
        SetupVerificationRecord? record,
        bool transactionInProgress,
        SetupInspectEffectiveResult? inspection,
        SetupRecordedMetadata? recordedExtra)
    {
        if (deploymentKind == AdminSetupDeploymentKind.Manual)
        {
            return new AdminSetupVerificationEvaluation(
                AdminSetupVerificationFreshness.NotManaged,
                Status: null,
                Reason: null,
                CommittedAt: null,
                RecordActivationGeneration: null,
                ActiveActivationGeneration: null,
                DisplayCommittedPass: false);
        }

        if (deploymentKind == AdminSetupDeploymentKind.InvalidManagedMetadata)
        {
            return new AdminSetupVerificationEvaluation(
                AdminSetupVerificationFreshness.Invalid,
                Status: null,
                Reason: inspection?.Reason ?? SetupInspectReason.MetadataMalformed,
                CommittedAt: null,
                RecordActivationGeneration: null,
                ActiveActivationGeneration: null,
                DisplayCommittedPass: false);
        }

        if (active is null && record is null && !transactionInProgress)
        {
            return new AdminSetupVerificationEvaluation(
                AdminSetupVerificationFreshness.Unavailable,
                Status: null,
                Reason: VerificationUnavailableReason,
                CommittedAt: null,
                RecordActivationGeneration: null,
                ActiveActivationGeneration: null,
                DisplayCommittedPass: false);
        }

        if (transactionInProgress)
        {
            return new AdminSetupVerificationEvaluation(
                AdminSetupVerificationFreshness.Pending,
                Status: record?.Status,
                Reason: "apply-or-recovery-in-progress",
                CommittedAt: null,
                RecordActivationGeneration: record?.ActivationGeneration,
                ActiveActivationGeneration: active?.ActivationGeneration,
                DisplayCommittedPass: false);
        }

        if (active is null)
        {
            return new AdminSetupVerificationEvaluation(
                AdminSetupVerificationFreshness.Missing,
                Status: record?.Status,
                Reason: "active-pointer-missing",
                CommittedAt: null,
                RecordActivationGeneration: record?.ActivationGeneration,
                ActiveActivationGeneration: null,
                DisplayCommittedPass: false);
        }

        if (record is null)
        {
            return new AdminSetupVerificationEvaluation(
                AdminSetupVerificationFreshness.Missing,
                Status: null,
                Reason: "verification-record-missing",
                CommittedAt: null,
                RecordActivationGeneration: null,
                ActiveActivationGeneration: active.ActivationGeneration,
                DisplayCommittedPass: false);
        }

        if (string.Equals(record.Status, SetupVerificationRecord.StatusInvalidated, StringComparison.Ordinal))
        {
            return FailFreshness(
                AdminSetupVerificationFreshness.Invalid,
                record,
                active,
                "verification-record-invalidated");
        }

        if (!string.Equals(record.Status, SetupVerificationRecord.StatusCommitted, StringComparison.Ordinal)
            && !string.Equals(record.Status, SetupVerificationRecord.StatusInvalidated, StringComparison.Ordinal))
        {
            return FailFreshness(
                AdminSetupVerificationFreshness.Invalid,
                record,
                active,
                "verification-record-invalid-status");
        }

        if (!string.Equals(record.BundleId, active.BundleId, StringComparison.Ordinal)
            || record.ActivationGeneration != active.ActivationGeneration)
        {
            return FailFreshness(
                AdminSetupVerificationFreshness.Stale,
                record,
                active,
                "active-generation-or-bundle-mismatch");
        }

        var recordedBundleId = inspection?.Recorded?.SetupBundleId ?? recordedExtra?.BundleId;
        if (!string.IsNullOrWhiteSpace(recordedBundleId)
            && !string.Equals(record.BundleId, recordedBundleId, StringComparison.Ordinal))
        {
            return FailFreshness(
                AdminSetupVerificationFreshness.Stale,
                record,
                active,
                "recorded-bundle-mismatch");
        }

        if (inspection?.Effective.FingerprintsMatchRecorded == false)
        {
            return FailFreshness(
                AdminSetupVerificationFreshness.Stale,
                record,
                active,
                "effective-fingerprint-mismatch");
        }

        if (IsObservedIntegrityBlocking(inspection?.BundleIntegrity.Result))
        {
            return FailFreshness(
                AdminSetupVerificationFreshness.Stale,
                record,
                active,
                "observed-integrity-mismatch-or-invalid");
        }

        if (record.RecordedSchemaVersion is int recordedSchema
            && inspection?.Recorded is { } recorded
            && recorded.SchemaVersion != recordedSchema)
        {
            return FailFreshness(
                AdminSetupVerificationFreshness.Stale,
                record,
                active,
                "recorded-schema-mismatch");
        }

        if (!string.Equals(
                record.RuntimeIdentityBinding,
                SetupRuntimeIdentityBindingResult.Matched,
                StringComparison.Ordinal))
        {
            return FailFreshness(
                AdminSetupVerificationFreshness.Stale,
                record,
                active,
                "runtime-identity-binding-mismatch");
        }

        if (!string.IsNullOrWhiteSpace(record.ImageReference)
            && recordedExtra is { ImageRepository: { Length: > 0 } repo, ImageTag: { Length: > 0 } tag })
        {
            var expectedImage = repo + ":" + tag;
            if (!string.Equals(record.ImageReference, expectedImage, StringComparison.Ordinal))
            {
                return FailFreshness(
                    AdminSetupVerificationFreshness.Stale,
                    record,
                    active,
                    "image-reference-mismatch");
            }
        }

        var hostCanonicalMatched = string.Equals(
            record.BundleIntegrity,
            SetupIntegrityMerger.Matched,
            StringComparison.Ordinal);
        var displayPass = record.IsCommittedSuccess && hostCanonicalMatched;
        return new AdminSetupVerificationEvaluation(
            AdminSetupVerificationFreshness.Current,
            Status: record.Status,
            Reason: displayPass ? null : "verification-not-committed-success",
            CommittedAt: record.CommittedAt,
            RecordActivationGeneration: record.ActivationGeneration,
            ActiveActivationGeneration: active.ActivationGeneration,
            DisplayCommittedPass: displayPass);
    }

    private static AdminSetupVerificationEvaluation FailFreshness(
        AdminSetupVerificationFreshness freshness,
        SetupVerificationRecord record,
        SetupActivePointer active,
        string reason) =>
        new(
            freshness,
            Status: record.Status,
            Reason: reason,
            CommittedAt: record.CommittedAt,
            RecordActivationGeneration: record.ActivationGeneration,
            ActiveActivationGeneration: active.ActivationGeneration,
            DisplayCommittedPass: false);

    private static AdminSetupConfigurationAppliedDisplay EvaluateConfigurationApplied(
        AdminSetupDeploymentKind deploymentKind,
        AdminSetupVerificationEvaluation verification,
        AdminSetupHostObservation? hostObservation,
        SetupInspectEffectiveResult inspection)
    {
        if (deploymentKind == AdminSetupDeploymentKind.Manual)
            return AdminSetupConfigurationAppliedDisplay.NotManaged;

        if (deploymentKind == AdminSetupDeploymentKind.InvalidManagedMetadata)
            return AdminSetupConfigurationAppliedDisplay.No;

        if (hostObservation is null)
            return AdminSetupConfigurationAppliedDisplay.Unavailable;

        if (verification.Freshness is AdminSetupVerificationFreshness.Pending)
            return AdminSetupConfigurationAppliedDisplay.Unavailable;

        if (IsObservedIntegrityBlocking(inspection.BundleIntegrity.Result)
            || inspection.Effective.FingerprintsMatchRecorded == false)
        {
            return AdminSetupConfigurationAppliedDisplay.No;
        }

        if (verification.Freshness is AdminSetupVerificationFreshness.Current
            && verification.DisplayCommittedPass)
        {
            return AdminSetupConfigurationAppliedDisplay.Yes;
        }

        if (verification.Freshness is AdminSetupVerificationFreshness.Missing
            or AdminSetupVerificationFreshness.Invalid
            or AdminSetupVerificationFreshness.Stale)
        {
            return AdminSetupConfigurationAppliedDisplay.No;
        }

        return AdminSetupConfigurationAppliedDisplay.Unavailable;
    }

    private static (AdminSetupSendReadyDisplay Display, string? ReasonCode) EvaluateSendReady(
        AdminSetupDeploymentKind deploymentKind,
        string? modeWire,
        SetupInspectEffectiveResult inspection,
        AdminSetupVerificationEvaluation verification,
        AdminSetupHostObservation? hostObservation)
    {
        if (deploymentKind == AdminSetupDeploymentKind.Manual)
            return (AdminSetupSendReadyDisplay.NotManaged, null);

        if (deploymentKind == AdminSetupDeploymentKind.InvalidManagedMetadata)
            return (AdminSetupSendReadyDisplay.NotReady, SetupInspectReason.MetadataMalformed);

        if (hostObservation is null)
            return (AdminSetupSendReadyDisplay.Unavailable, SendReadyUnavailableReason);

        if (verification.Freshness is AdminSetupVerificationFreshness.Pending
            || hostObservation.TransactionInProgress)
        {
            return (AdminSetupSendReadyDisplay.Pending, "apply-or-recovery-in-progress");
        }

        if (verification.Freshness is not AdminSetupVerificationFreshness.Current
            || !verification.DisplayCommittedPass)
        {
            return (AdminSetupSendReadyDisplay.NotReady, "verification-not-current");
        }

        if (inspection.Effective.FingerprintsMatchRecorded == false)
            return (AdminSetupSendReadyDisplay.NotReady, "effective-fingerprint-mismatch");

        if (IsObservedIntegrityBlocking(inspection.BundleIntegrity.Result))
            return (AdminSetupSendReadyDisplay.NotReady, ObservedIntegrityBlocksReadyReason);

        if (!string.Equals(
                hostObservation.Record?.BundleIntegrity,
                SetupIntegrityMerger.Matched,
                StringComparison.Ordinal))
        {
            return (AdminSetupSendReadyDisplay.NotReady, "host-canonical-integrity-not-matched");
        }

        if (!SetupModeParser.TryParse(modeWire, out var mode) || mode != SetupMode.ProductionAcs)
            return (AdminSetupSendReadyDisplay.NotReady, AcsSendReadyEvaluator.ReasonWrongMode);

        if (inspection.Effective.LiveSendingEnabled != true)
            return (AdminSetupSendReadyDisplay.NotReady, AcsSendReadyEvaluator.ReasonLiveSendingDisabled);

        var authority = hostObservation.SendReadyAuthority;
        if (authority is null)
            return (AdminSetupSendReadyDisplay.Unavailable, SendReadyUnavailableReason);

        if (hostObservation.Active is null || hostObservation.Record is null)
            return (AdminSetupSendReadyDisplay.Unavailable, SendReadyUnavailableReason);

        if (!AuthorityMatchesActive(authority, hostObservation.Active, hostObservation.Record, inspection))
            return (AdminSetupSendReadyDisplay.NotReady, SendReadyAuthorityMismatchReason);

        if (!authority.DoctorPassed)
            return (AdminSetupSendReadyDisplay.NotReady, AcsSendReadyEvaluator.ReasonDoctorChecksFailed);

        if (!authority.ApplySucceededWithCommittedVerification)
            return (AdminSetupSendReadyDisplay.NotReady, AcsSendReadyEvaluator.ReasonApplyNotSucceeded);

        return (AdminSetupSendReadyDisplay.Ready, AcsSendReadyEvaluator.SendReadyReady);
    }

    private static (
        AdminSetupStagingSummaryAvailability Availability,
        string? Reason,
        string? Code,
        string? MailboxCheckStatus,
        bool? SendRequestAccepted,
        bool? OperationCompleted) EvaluateStagingSummary(
        bool stagingApplicable,
        AdminSetupDeploymentKind deploymentKind,
        AdminSetupHostObservation? hostObservation,
        SetupInspectEffectiveResult inspection,
        SetupRecordedMetadata? recordedExtra)
    {
        if (!stagingApplicable || deploymentKind != AdminSetupDeploymentKind.Managed)
        {
            return (
                AdminSetupStagingSummaryAvailability.NotApplicable,
                StagingNotApplicableReason,
                null,
                null,
                null,
                null);
        }

        if (hostObservation?.StagingSummary is not { } staging)
        {
            return (
                AdminSetupStagingSummaryAvailability.Unavailable,
                StagingUnavailableReason,
                null,
                null,
                null,
                null);
        }

        if (hostObservation.Active is null
            || !StagingSummaryBoundToActive(staging, hostObservation.Active, inspection, recordedExtra))
        {
            return (
                AdminSetupStagingSummaryAvailability.Stale,
                StagingStaleReason,
                null,
                null,
                null,
                null);
        }

        return (
            AdminSetupStagingSummaryAvailability.Available,
            null,
            staging.StagingVerificationCode,
            staging.StagingMailboxCheckStatus,
            staging.StagingSendRequestAccepted,
            staging.StagingOperationCompleted);
    }

    private static bool AuthorityMatchesActive(
        AdminSetupSendReadyAuthority authority,
        SetupActivePointer active,
        SetupVerificationRecord record,
        SetupInspectEffectiveResult inspection)
    {
        if (!string.Equals(authority.BundleId, active.BundleId, StringComparison.Ordinal)
            || authority.ActivationGeneration != active.ActivationGeneration)
        {
            return false;
        }

        if (!string.Equals(authority.BundleId, record.BundleId, StringComparison.Ordinal)
            || authority.ActivationGeneration != record.ActivationGeneration)
        {
            return false;
        }

        var expectedFingerprint = inspection.Recorded?.ConfigurationFingerprint
            ?? inspection.Effective.ConfigurationFingerprint;
        if (string.IsNullOrWhiteSpace(expectedFingerprint)
            || string.IsNullOrWhiteSpace(authority.ConfigurationFingerprint)
            || !string.Equals(authority.ConfigurationFingerprint, expectedFingerprint, StringComparison.Ordinal))
        {
            return false;
        }

        return true;
    }

    private static bool StagingSummaryBoundToActive(
        AcsSetupWorkflowResult staging,
        SetupActivePointer active,
        SetupInspectEffectiveResult inspection,
        SetupRecordedMetadata? recordedExtra)
    {
        if (string.IsNullOrWhiteSpace(staging.BundleId)
            || staging.ActivationGeneration is null
            || string.IsNullOrWhiteSpace(staging.ConfigurationFingerprint))
        {
            return false;
        }

        if (!string.Equals(staging.BundleId, active.BundleId, StringComparison.Ordinal)
            || staging.ActivationGeneration != active.ActivationGeneration)
        {
            return false;
        }

        var expectedBundle = inspection.Recorded?.SetupBundleId ?? recordedExtra?.BundleId;
        if (!string.IsNullOrWhiteSpace(expectedBundle)
            && !string.Equals(staging.BundleId, expectedBundle, StringComparison.Ordinal))
        {
            return false;
        }

        var expectedFingerprint = inspection.Recorded?.ConfigurationFingerprint
            ?? recordedExtra?.ConfigurationFingerprint
            ?? inspection.Effective.ConfigurationFingerprint;
        if (string.IsNullOrWhiteSpace(expectedFingerprint)
            || !string.Equals(staging.ConfigurationFingerprint, expectedFingerprint, StringComparison.Ordinal))
        {
            return false;
        }

        return true;
    }

    private static bool IsInvalidManagedMetadata(SetupInspectEffectiveResult inspection)
    {
        if (string.Equals(
                inspection.BundleIntegrity.Result,
                SetupInspectIntegrityResult.InvalidMetadata,
                StringComparison.Ordinal))
        {
            return true;
        }

        return inspection.Reason is SetupInspectReason.MetadataMalformed
            or SetupInspectReason.UnsupportedSchemaVersion;
    }

    private static bool IsObservedIntegrityBlocking(string? observedIntegrity) =>
        string.Equals(observedIntegrity, SetupInspectIntegrityResult.Mismatch, StringComparison.Ordinal)
        || string.Equals(observedIntegrity, SetupInspectIntegrityResult.InvalidMetadata, StringComparison.Ordinal);

    private static bool IsStagingVerificationMode(string? modeWire) =>
        SetupModeParser.TryParse(modeWire, out var mode) && mode == SetupMode.StagingVerification;

    private static void TryLoadRecordedExtras(
        IConfiguration configuration,
        out SetupRecordedMetadata? recorded,
        out string? senderEmail)
    {
        recorded = null;
        senderEmail = null;

        var recordedPath = configuration[SetupInspectEffectiveEngine.RecordedMetadataPathEnv]
            ?? Environment.GetEnvironmentVariable(SetupInspectEffectiveEngine.RecordedMetadataPathEnv);
        if (!string.IsNullOrWhiteSpace(recordedPath) && File.Exists(recordedPath))
        {
            try
            {
                recorded = JsonSerializer.Deserialize(
                    File.ReadAllText(recordedPath),
                    SetupJsonContext.Default.SetupRecordedMetadata);
            }
            catch (JsonException)
            {
                recorded = null;
            }
            catch (IOException)
            {
                recorded = null;
            }
        }

        var load = MailerConfigurationSnapshot.TryLoad(configuration);
        if (!load.Succeeded || load.Snapshot is null)
            return;

        var senderPath = Path.Combine(
            Path.GetDirectoryName(load.Snapshot.TenantsPath) ?? string.Empty,
            PlatformSenderFile.CanonicalFileName);
        if (!File.Exists(senderPath))
            return;

        try
        {
            var senderFile = JsonSerializer.Deserialize(
                File.ReadAllText(senderPath),
                SetupJsonContext.Default.PlatformSenderFile);
            senderEmail = senderFile?.Sender.Email;
        }
        catch (JsonException)
        {
            senderEmail = null;
        }
        catch (IOException)
        {
            senderEmail = null;
        }
        catch (InvalidOperationException)
        {
            senderEmail = null;
        }
    }
}

public enum AdminSetupDeploymentKind
{
    Manual,
    Managed,
    InvalidManagedMetadata,
}

public enum AdminSetupVerificationFreshness
{
    NotManaged,
    Unavailable,
    Missing,
    Invalid,
    Pending,
    Stale,
    Current,
}

public enum AdminSetupConfigurationAppliedDisplay
{
    NotManaged,
    Unavailable,
    No,
    Yes,
}

public enum AdminSetupSendReadyDisplay
{
    NotManaged,
    Unavailable,
    Pending,
    NotReady,
    Ready,
}

public enum AdminSetupStagingSummaryAvailability
{
    NotApplicable,
    Unavailable,
    Stale,
    Available,
}

/// <summary>
/// Optional host-side observation overlay. Admin runtime leaves this null (ADR 0021 D-04).
/// </summary>
public sealed record AdminSetupHostObservation
{
    public SetupActivePointer? Active { get; init; }
    public SetupVerificationRecord? Record { get; init; }
    public bool TransactionInProgress { get; init; }
    public AcsSetupWorkflowResult? StagingSummary { get; init; }
    public AdminSetupSendReadyAuthority? SendReadyAuthority { get; init; }
}

/// <summary>
/// #451 typed send-ready authority bound to a specific ACTIVE apply result.
/// </summary>
public sealed record AdminSetupSendReadyAuthority
{
    public required string BundleId { get; init; }
    public required long ActivationGeneration { get; init; }
    public required string ConfigurationFingerprint { get; init; }
    public required bool DoctorPassed { get; init; }
    public required bool ApplySucceededWithCommittedVerification { get; init; }
}

public readonly record struct AdminSetupVerificationEvaluation(
    AdminSetupVerificationFreshness Freshness,
    string? Status,
    string? Reason,
    string? CommittedAt,
    long? RecordActivationGeneration,
    long? ActiveActivationGeneration,
    bool DisplayCommittedPass);
