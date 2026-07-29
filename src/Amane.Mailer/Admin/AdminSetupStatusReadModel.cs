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
    public const string SendReadyUnavailableReason = "host-send-ready-inputs-not-observable";

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
    public string? RecordedCreatedAt { get; init; }
    public string? ImageRepository { get; init; }
    public string? ImageTag { get; init; }
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

        var managed = inspection.Managed;
        var deploymentKind = managed
            ? AdminSetupDeploymentKind.Managed
            : AdminSetupDeploymentKind.Manual;

        var mode = inspection.Recorded?.Mode ?? recordedExtra?.Mode;
        var stagingApplicable = IsStagingVerificationMode(mode);

        var verification = EvaluateVerification(managed, hostObservation, inspection, recordedExtra);
        var configurationApplied = EvaluateConfigurationApplied(managed, verification, hostObservation);
        var sendReady = EvaluateSendReady(
            managed,
            mode,
            inspection.Effective.LiveSendingEnabled,
            verification,
            hostObservation);

        AdminSetupStagingSummaryAvailability stagingAvailability;
        string? stagingCode = null;
        string? stagingMailbox = null;
        bool? stagingAccepted = null;
        bool? stagingCompleted = null;

        if (!stagingApplicable)
        {
            stagingAvailability = AdminSetupStagingSummaryAvailability.NotApplicable;
        }
        else if (hostObservation?.StagingSummary is { } staging)
        {
            stagingAvailability = AdminSetupStagingSummaryAvailability.Available;
            stagingCode = staging.StagingVerificationCode;
            stagingMailbox = staging.StagingMailboxCheckStatus;
            stagingAccepted = staging.StagingSendRequestAccepted;
            stagingCompleted = staging.StagingOperationCompleted;
        }
        else
        {
            stagingAvailability = AdminSetupStagingSummaryAvailability.Unavailable;
        }

        return new AdminSetupStatusReadModel
        {
            DeploymentKind = deploymentKind,
            MailerVersion = inspection.MailerVersion,
            SetupBundleId = managed ? inspection.Recorded?.SetupBundleId ?? recordedExtra?.BundleId : null,
            Mode = managed ? mode : null,
            ProviderSummary = inspection.Effective.ProviderSummary,
            SenderEmail = senderEmail,
            PlatformSenderPresent = recordedExtra?.PlatformSenderPresent == true,
            CredentialStatus = inspection.Effective.CredentialStatus,
            LiveSendingEnabled = inspection.Effective.LiveSendingEnabled,
            RecordedFingerprint = managed ? inspection.Recorded?.ConfigurationFingerprint : null,
            EffectiveFingerprint = inspection.Effective.ConfigurationFingerprint,
            FingerprintsMatchRecorded = managed ? inspection.Effective.FingerprintsMatchRecorded : null,
            BundleIntegrityResult = inspection.BundleIntegrity.Result,
            BundleIntegrityReason = inspection.BundleIntegrity.Reason,
            RecordedCreatedAt = managed ? recordedExtra?.CreatedAt : null,
            ImageRepository = managed ? recordedExtra?.ImageRepository : null,
            ImageTag = managed ? recordedExtra?.ImageTag : null,
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
            StagingSummaryAvailability = stagingAvailability,
            StagingVerificationCode = stagingCode,
            StagingMailboxCheckStatus = stagingMailbox,
            StagingSendRequestAccepted = stagingAccepted,
            StagingOperationCompleted = stagingCompleted,
        };
    }

    public static AdminSetupVerificationEvaluation EvaluateVerificationFreshness(
        bool managed,
        SetupActivePointer? active,
        SetupVerificationRecord? record,
        bool transactionInProgress,
        SetupInspectEffectiveResult? inspection = null,
        SetupRecordedMetadata? recordedExtra = null)
    {
        if (!managed)
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

        if (active is null && record is null && !transactionInProgress)
        {
            // Admin runtime cannot open host Managed root (ADR 0021 D-04).
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
            return new AdminSetupVerificationEvaluation(
                AdminSetupVerificationFreshness.Invalid,
                Status: record.Status,
                Reason: "verification-record-invalidated",
                CommittedAt: record.CommittedAt,
                RecordActivationGeneration: record.ActivationGeneration,
                ActiveActivationGeneration: active.ActivationGeneration,
                DisplayCommittedPass: false);
        }

        if (!string.Equals(record.Status, SetupVerificationRecord.StatusCommitted, StringComparison.Ordinal)
            && !string.Equals(record.Status, SetupVerificationRecord.StatusInvalidated, StringComparison.Ordinal))
        {
            return new AdminSetupVerificationEvaluation(
                AdminSetupVerificationFreshness.Invalid,
                Status: record.Status,
                Reason: "verification-record-invalid-status",
                CommittedAt: record.CommittedAt,
                RecordActivationGeneration: record.ActivationGeneration,
                ActiveActivationGeneration: active.ActivationGeneration,
                DisplayCommittedPass: false);
        }

        if (!string.Equals(record.BundleId, active.BundleId, StringComparison.Ordinal)
            || record.ActivationGeneration != active.ActivationGeneration)
        {
            return new AdminSetupVerificationEvaluation(
                AdminSetupVerificationFreshness.Stale,
                Status: record.Status,
                Reason: "active-generation-or-bundle-mismatch",
                CommittedAt: record.CommittedAt,
                RecordActivationGeneration: record.ActivationGeneration,
                ActiveActivationGeneration: active.ActivationGeneration,
                DisplayCommittedPass: false);
        }

        var recordedBundleId = inspection?.Recorded?.SetupBundleId ?? recordedExtra?.BundleId;
        if (!string.IsNullOrWhiteSpace(recordedBundleId)
            && !string.Equals(record.BundleId, recordedBundleId, StringComparison.Ordinal))
        {
            return new AdminSetupVerificationEvaluation(
                AdminSetupVerificationFreshness.Stale,
                Status: record.Status,
                Reason: "recorded-bundle-mismatch",
                CommittedAt: record.CommittedAt,
                RecordActivationGeneration: record.ActivationGeneration,
                ActiveActivationGeneration: active.ActivationGeneration,
                DisplayCommittedPass: false);
        }

        if (inspection?.Recorded is { } recordedSummary
            && !string.IsNullOrWhiteSpace(recordedSummary.ConfigurationFingerprint)
            && string.Equals(record.FingerprintComparison, SetupVerificationRecord.FingerprintMatched, StringComparison.Ordinal)
            && inspection.Effective.FingerprintsMatchRecorded == false)
        {
            return new AdminSetupVerificationEvaluation(
                AdminSetupVerificationFreshness.Stale,
                Status: record.Status,
                Reason: "effective-fingerprint-mismatch",
                CommittedAt: record.CommittedAt,
                RecordActivationGeneration: record.ActivationGeneration,
                ActiveActivationGeneration: active.ActivationGeneration,
                DisplayCommittedPass: false);
        }

        if (record.RecordedSchemaVersion is int recordedSchema
            && inspection?.Recorded is { } recorded
            && recorded.SchemaVersion != recordedSchema)
        {
            return new AdminSetupVerificationEvaluation(
                AdminSetupVerificationFreshness.Stale,
                Status: record.Status,
                Reason: "recorded-schema-mismatch",
                CommittedAt: record.CommittedAt,
                RecordActivationGeneration: record.ActivationGeneration,
                ActiveActivationGeneration: active.ActivationGeneration,
                DisplayCommittedPass: false);
        }

        if (!string.Equals(
                record.RuntimeIdentityBinding,
                SetupRuntimeIdentityBindingResult.Matched,
                StringComparison.Ordinal))
        {
            return new AdminSetupVerificationEvaluation(
                AdminSetupVerificationFreshness.Stale,
                Status: record.Status,
                Reason: "runtime-identity-binding-mismatch",
                CommittedAt: record.CommittedAt,
                RecordActivationGeneration: record.ActivationGeneration,
                ActiveActivationGeneration: active.ActivationGeneration,
                DisplayCommittedPass: false);
        }

        if (!string.IsNullOrWhiteSpace(record.ImageReference)
            && recordedExtra is { ImageRepository: { Length: > 0 } repo, ImageTag: { Length: > 0 } tag })
        {
            var expectedImage = repo + ":" + tag;
            if (!string.Equals(record.ImageReference, expectedImage, StringComparison.Ordinal))
            {
                return new AdminSetupVerificationEvaluation(
                    AdminSetupVerificationFreshness.Stale,
                    Status: record.Status,
                    Reason: "image-reference-mismatch",
                    CommittedAt: record.CommittedAt,
                    RecordActivationGeneration: record.ActivationGeneration,
                    ActiveActivationGeneration: active.ActivationGeneration,
                    DisplayCommittedPass: false);
            }
        }

        var displayPass = record.IsCommittedSuccess;
        return new AdminSetupVerificationEvaluation(
            AdminSetupVerificationFreshness.Current,
            Status: record.Status,
            Reason: displayPass ? null : "verification-not-committed-success",
            CommittedAt: record.CommittedAt,
            RecordActivationGeneration: record.ActivationGeneration,
            ActiveActivationGeneration: active.ActivationGeneration,
            DisplayCommittedPass: displayPass);
    }

    private static AdminSetupVerificationEvaluation EvaluateVerification(
        bool managed,
        AdminSetupHostObservation? hostObservation,
        SetupInspectEffectiveResult inspection,
        SetupRecordedMetadata? recordedExtra) =>
        EvaluateVerificationFreshness(
            managed,
            hostObservation?.Active,
            hostObservation?.Record,
            hostObservation?.TransactionInProgress == true,
            inspection,
            recordedExtra);

    private static AdminSetupConfigurationAppliedDisplay EvaluateConfigurationApplied(
        bool managed,
        AdminSetupVerificationEvaluation verification,
        AdminSetupHostObservation? hostObservation)
    {
        if (!managed)
            return AdminSetupConfigurationAppliedDisplay.NotManaged;

        if (hostObservation is null)
            return AdminSetupConfigurationAppliedDisplay.Unavailable;

        if (verification.Freshness is AdminSetupVerificationFreshness.Pending)
            return AdminSetupConfigurationAppliedDisplay.Unavailable;

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
        bool managed,
        string? modeWire,
        bool? liveSendingEnabled,
        AdminSetupVerificationEvaluation verification,
        AdminSetupHostObservation? hostObservation)
    {
        if (!managed)
            return (AdminSetupSendReadyDisplay.NotManaged, null);

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

        if (!SetupModeParser.TryParse(modeWire, out var mode) || mode != SetupMode.ProductionAcs)
            return (AdminSetupSendReadyDisplay.NotReady, AcsSendReadyEvaluator.ReasonWrongMode);

        if (liveSendingEnabled != true)
            return (AdminSetupSendReadyDisplay.NotReady, AcsSendReadyEvaluator.ReasonLiveSendingDisabled);

        if (hostObservation.DoctorPassed is null)
            return (AdminSetupSendReadyDisplay.Unavailable, SendReadyUnavailableReason);

        if (hostObservation.DoctorPassed != true)
            return (AdminSetupSendReadyDisplay.NotReady, AcsSendReadyEvaluator.ReasonDoctorChecksFailed);

        if (hostObservation.ApplySucceededWithCommittedVerification != true)
            return (AdminSetupSendReadyDisplay.NotReady, AcsSendReadyEvaluator.ReasonApplyNotSucceeded);

        return (AdminSetupSendReadyDisplay.Ready, AcsSendReadyEvaluator.SendReadyReady);
    }

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
    public bool? DoctorPassed { get; init; }
    public bool? ApplySucceededWithCommittedVerification { get; init; }
}

public readonly record struct AdminSetupVerificationEvaluation(
    AdminSetupVerificationFreshness Freshness,
    string? Status,
    string? Reason,
    string? CommittedAt,
    long? RecordActivationGeneration,
    long? ActiveActivationGeneration,
    bool DisplayCommittedPass);
