using System.Text.Json;
using System.Text.Json.Serialization;

namespace Amane.Mailer.Setup;

internal enum SourceAdminDisposition
{
    Unknown = 0,
    DisabledMain = 1,
    EnabledManagedSameUser = 2,
}

internal static class AdminBootstrapOwnershipState
{
    internal const string Prepared = "prepared";
    internal const string Armed = "armed";
    internal const string DatabaseObserved = "database-observed";
    internal const string AccessVerified = "access-verified";
    internal const string SessionCleaned = "session-cleaned";
    internal const string Succeeded = "succeeded";
    internal const string AbortedBeforeActivation = "aborted-before-activation";
    internal const string ResidualAfterConfigRollback = "residual-after-config-rollback";
    internal const string NeedsIntervention = "needs-intervention";

    internal static bool IsKnown(string state) =>
        state is Prepared
            or Armed
            or DatabaseObserved
            or AccessVerified
            or SessionCleaned
            or Succeeded
            or AbortedBeforeActivation
            or ResidualAfterConfigRollback
            or NeedsIntervention;
}

internal sealed record AdminBootstrapOwnershipDocument
{
    internal const int CurrentSchemaVersion = 1;

    [JsonPropertyName("schemaVersion")]
    public int SchemaVersion { get; init; } = CurrentSchemaVersion;

    [JsonPropertyName("operationId")]
    public required string OperationId { get; init; }

    [JsonPropertyName("state")]
    public required string State { get; init; }

    [JsonPropertyName("source")]
    public required AdminBootstrapSourceAuthority Source { get; init; }

    [JsonPropertyName("candidate")]
    public required AdminBootstrapCandidateAuthority Candidate { get; init; }

    [JsonPropertyName("expectedDatabase")]
    public required SetupAdminBootstrapExpectation ExpectedDatabase { get; init; }

    [JsonPropertyName("observedDatabaseClassification")]
    public string? ObservedDatabaseClassification { get; init; }

    [JsonPropertyName("lastTransitionAt")]
    public required string LastTransitionAt { get; init; }
}

internal sealed record AdminBootstrapSourceAuthority
{
    [JsonPropertyName("bundleId")]
    public required string BundleId { get; init; }

    [JsonPropertyName("activationGeneration")]
    public long ActivationGeneration { get; init; }

    [JsonPropertyName("configurationFingerprint")]
    public required string ConfigurationFingerprint { get; init; }

    [JsonPropertyName("recordedSchemaVersion")]
    public int RecordedSchemaVersion { get; init; }

    [JsonPropertyName("imageIdentity")]
    public required string ImageIdentity { get; init; }

    [JsonPropertyName("composeIdentity")]
    public required string ComposeIdentity { get; init; }

    [JsonPropertyName("runtimeIdentityBindingDigest")]
    public required string RuntimeIdentityBindingDigest { get; init; }

    [JsonPropertyName("adminDisposition")]
    public SourceAdminDisposition AdminDisposition { get; init; }

    [JsonPropertyName("capturedAt")]
    public required string CapturedAt { get; init; }
}

internal sealed record AdminBootstrapCandidateAuthority
{
    [JsonPropertyName("bundleId")]
    public required string BundleId { get; init; }

    [JsonPropertyName("expectedActivationGeneration")]
    public long ExpectedActivationGeneration { get; init; }
}

internal enum AdminBootstrapOwnershipReadKind
{
    Missing = 0,
    Valid = 1,
    NeedsIntervention = 2,
}

internal readonly record struct AdminBootstrapOwnershipReadResult(
    AdminBootstrapOwnershipReadKind Kind,
    AdminBootstrapOwnershipDocument? Document);

internal enum AdminBootstrapPromotionKind
{
    NotCommitted = 0,
    CurrentCommittedAndPendingDeleted = 1,
    CurrentCommittedPendingCleanupRequired = 2,
}

internal readonly record struct AdminBootstrapPromotionResult(
    AdminBootstrapPromotionKind Kind,
    string? ReasonCode)
{
    internal bool CurrentCommitted =>
        Kind is AdminBootstrapPromotionKind.CurrentCommittedAndPendingDeleted
            or AdminBootstrapPromotionKind.CurrentCommittedPendingCleanupRequired;

    internal bool IsFullySucceeded =>
        Kind == AdminBootstrapPromotionKind.CurrentCommittedAndPendingDeleted;
}

/// <summary>
/// Owner-only current/pending state. New attempts never overwrite current until the pending
/// operation has reached session-cleaned and candidate authority is re-verified.
/// </summary>
internal sealed class AdminBootstrapOwnershipStore
{
    private readonly ISetupFileSystem _fileSystem;
    private readonly SetupDurableAtomicWriter _writer;

    internal AdminBootstrapOwnershipStore(ISetupFileSystem fileSystem)
    {
        _fileSystem = fileSystem;
        _writer = new SetupDurableAtomicWriter(fileSystem);
    }

    internal AdminBootstrapOwnershipReadResult ReadCurrent(string managedRoot) =>
        Read(managedRoot, SetupBundleLayout.AdminBootstrapCurrentPath(managedRoot));

    internal AdminBootstrapOwnershipReadResult ReadPending(string managedRoot) =>
        Read(managedRoot, SetupBundleLayout.AdminBootstrapPendingPath(managedRoot));

    internal SetupDockerResult WritePending(
        string managedRoot,
        AdminBootstrapOwnershipDocument document) =>
        Write(managedRoot, SetupBundleLayout.AdminBootstrapPendingPath(managedRoot), document);

    /// <summary>
    /// Creates pending ownership only when no pending file exists. Existing pending is fail-closed
    /// so crash-recovery authority cannot be overwritten by a new operation.
    /// </summary>
    internal SetupDockerResult WritePendingPrepared(
        string managedRoot,
        AdminBootstrapOwnershipDocument document)
    {
        if (!string.Equals(document.State, AdminBootstrapOwnershipState.Prepared, StringComparison.Ordinal))
        {
            return SetupDockerResult.Fail(
                SetupDockerResultCode.InvalidBundleInventory,
                "Prepared ownership state is invalid.");
        }

        var path = SetupBundleLayout.AdminBootstrapPendingPath(managedRoot);
        var stateDir = SetupBundleLayout.StateDir(managedRoot);
        if (!EnsureSafeStateDirectory(managedRoot, stateDir))
        {
            return SetupDockerResult.Fail(
                SetupDockerResultCode.UnsafePath,
                "Ownership state directory rejected.");
        }

        if (_fileSystem.FileExists(path))
        {
            return SetupDockerResult.Fail(
                SetupDockerResultCode.InvalidBundleInventory,
                "An unfinished Admin bootstrap pending operation already exists.");
        }

        return Write(managedRoot, path, document);
    }

    internal AdminBootstrapPromotionResult PromotePendingToCurrent(
        string managedRoot,
        AdminBootstrapOwnershipDocument document)
    {
        if (!string.Equals(document.State, AdminBootstrapOwnershipState.Succeeded, StringComparison.Ordinal)
            && !string.Equals(
                document.State,
                AdminBootstrapOwnershipState.ResidualAfterConfigRollback,
                StringComparison.Ordinal))
        {
            return new(
                AdminBootstrapPromotionKind.NotCommitted,
                SetupDockerResultCode.InvalidBundleInventory);
        }

        var write = Write(managedRoot, SetupBundleLayout.AdminBootstrapCurrentPath(managedRoot), document);
        if (!write.IsSuccess)
            return new(AdminBootstrapPromotionKind.NotCommitted, write.Code);

        var delete = DeletePending(managedRoot);
        return delete.IsSuccess
            ? new(AdminBootstrapPromotionKind.CurrentCommittedAndPendingDeleted, null)
            : new(AdminBootstrapPromotionKind.CurrentCommittedPendingCleanupRequired, delete.Code);
    }

    internal SetupDockerResult DeletePending(string managedRoot) =>
        _writer.TryDurableDelete(
            managedRoot,
            SetupBundleLayout.AdminBootstrapPendingPath(managedRoot));

    /// <summary>
    /// Updates the succeeded current ownership candidate generation after a ManagedSameUser
    /// source rollback that reactivates the same bundle under a newer activation generation.
    /// </summary>
    internal SetupDockerResult TryUpdateSucceededCurrentGeneration(
        string managedRoot,
        string expectedOperationId,
        string expectedBundleId,
        long newActivationGeneration)
    {
        var current = ReadCurrent(managedRoot);
        if (current.Kind != AdminBootstrapOwnershipReadKind.Valid
            || current.Document is not { } document
            || !string.Equals(
                document.State,
                AdminBootstrapOwnershipState.Succeeded,
                StringComparison.Ordinal)
            || !string.Equals(document.OperationId, expectedOperationId, StringComparison.Ordinal)
            || !string.Equals(document.Candidate.BundleId, expectedBundleId, StringComparison.Ordinal))
        {
            return SetupDockerResult.Fail(
                SetupDockerResultCode.InvalidBundleInventory,
                "Current ownership could not be refreshed after source rollback.");
        }

        return Write(
            managedRoot,
            SetupBundleLayout.AdminBootstrapCurrentPath(managedRoot),
            document with
            {
                Candidate = document.Candidate with
                {
                    ExpectedActivationGeneration = newActivationGeneration,
                },
                Source = document.Source with
                {
                    ActivationGeneration = newActivationGeneration,
                },
                LastTransitionAt = DateTime.UtcNow.ToString("O"),
            });
    }

    private SetupDockerResult Write(
        string managedRoot,
        string path,
        AdminBootstrapOwnershipDocument document)
    {
        if (!IsValid(document))
        {
            return SetupDockerResult.Fail(
                SetupDockerResultCode.InvalidBundleInventory,
                "Ownership document is invalid.");
        }

        var stateDir = SetupBundleLayout.StateDir(managedRoot);
        if (!EnsureSafeStateDirectory(managedRoot, stateDir))
        {
            return SetupDockerResult.Fail(
                SetupDockerResultCode.UnsafePath,
                "Ownership state directory rejected.");
        }

        return _writer.TryAtomicReplaceJson(
            managedRoot,
            path,
            document,
            AdminBootstrapOwnershipJsonContext.Default.AdminBootstrapOwnershipDocument);
    }

    private AdminBootstrapOwnershipReadResult Read(string managedRoot, string path)
    {
        var stateDir = SetupBundleLayout.StateDir(managedRoot);
        if (!EnsureSafeStateDirectory(managedRoot, stateDir))
            return new(AdminBootstrapOwnershipReadKind.NeedsIntervention, null);

        if (!_fileSystem.FileExists(path))
            return new(AdminBootstrapOwnershipReadKind.Missing, null);

        if (SetupPathGuard.IsUnsafeLink(_fileSystem.InspectSymlinkOrReparsePoint(path))
            || !_fileSystem.IsOwnerOnlyFile(path))
        {
            return new(AdminBootstrapOwnershipReadKind.NeedsIntervention, null);
        }

        try
        {
            var document = JsonSerializer.Deserialize(
                _fileSystem.ReadAllBytes(path),
                AdminBootstrapOwnershipJsonContext.Default.AdminBootstrapOwnershipDocument);
            return document is not null && IsValid(document)
                ? new(AdminBootstrapOwnershipReadKind.Valid, document)
                : new(AdminBootstrapOwnershipReadKind.NeedsIntervention, null);
        }
        catch (JsonException)
        {
            return new(AdminBootstrapOwnershipReadKind.NeedsIntervention, null);
        }
        catch (IOException)
        {
            return new(AdminBootstrapOwnershipReadKind.NeedsIntervention, null);
        }
        catch (UnauthorizedAccessException)
        {
            return new(AdminBootstrapOwnershipReadKind.NeedsIntervention, null);
        }
    }

    private bool EnsureSafeStateDirectory(string managedRoot, string stateDir)
    {
        if (!SetupPathGuard.TryEnsurePathSafeUnderRoot(
                _fileSystem,
                Path.GetFullPath(managedRoot),
                Path.GetFullPath(stateDir),
                out _,
                out _))
        {
            return false;
        }

        if (!_fileSystem.DirectoryExists(stateDir))
            _fileSystem.CreateOwnerOnlyDirectory(stateDir);

        return !SetupPathGuard.IsUnsafeLink(_fileSystem.InspectSymlinkOrReparsePoint(stateDir))
            && _fileSystem.IsOwnerOnlyFile(stateDir);
    }

    private static bool IsValid(AdminBootstrapOwnershipDocument document) =>
        document.SchemaVersion == AdminBootstrapOwnershipDocument.CurrentSchemaVersion
        && AdminBootstrapOperationId.TryParse(document.OperationId, out _)
        && AdminBootstrapOwnershipState.IsKnown(document.State)
        && string.Equals(
            document.OperationId,
            document.ExpectedDatabase.OperationId,
            StringComparison.Ordinal)
        && SetupActivePointer.IsSafeBundleId(document.Source.BundleId)
        && SetupActivePointer.IsSafeBundleId(document.Candidate.BundleId)
        && document.Source.ActivationGeneration >= 1
        && document.Candidate.ExpectedActivationGeneration >= 1
        && SetupBundleLayout.IsSupportedRecordedSchemaVersion(document.Source.RecordedSchemaVersion)
        && document.Source.AdminDisposition != SourceAdminDisposition.Unknown;
}

[JsonSerializable(typeof(AdminBootstrapOwnershipDocument))]
[JsonSerializable(typeof(AdminBootstrapSourceAuthority))]
[JsonSerializable(typeof(AdminBootstrapCandidateAuthority))]
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    WriteIndented = false)]
internal partial class AdminBootstrapOwnershipJsonContext : JsonSerializerContext;
