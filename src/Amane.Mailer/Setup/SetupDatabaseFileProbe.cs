using Amane.Mailer.Operations;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace Amane.Mailer.Setup;

/// <summary>
/// Host-side DB path probe and migration decision for Issue #450 (no Docker required).
/// </summary>
public static class SetupDatabaseFileProbe
{
    public static SetupMigrationDecision ClassifyFreshHostDatabase(
        ISetupFileSystem fileSystem,
        SetupExternalInputSnapshot external)
    {
        ArgumentNullException.ThrowIfNull(fileSystem);
        ArgumentNullException.ThrowIfNull(external);

        if (!TryResolveHostDatabasePath(
                external.NormalizedDataPath,
                external.NormalizedConnectionString,
                out var dbPath,
                out var resolveFailure))
        {
            return resolveFailure!;
        }

        var wal = dbPath + "-wal";
        var shm = dbPath + "-shm";
        var journal = dbPath + "-journal";

        var mainExists = fileSystem.FileExists(dbPath);
        var walExists = fileSystem.FileExists(wal);
        var shmExists = fileSystem.FileExists(shm);
        var journalExists = fileSystem.FileExists(journal);

        if (!mainExists && (walExists || shmExists || journalExists))
        {
            return new SetupMigrationDecision
            {
                Kind = SetupMigrationDecisionKind.NeedsIntervention,
                ActionCode = SetupApplyActionCode.ReviewDatabaseFiles,
                ReasonCode = "sqlite_sidecar_residue",
                Message = "SQLite sidecar files exist without a main database file.",
            };
        }

        if (mainExists)
        {
            return new SetupMigrationDecision
            {
                Kind = SetupMigrationDecisionKind.UpgradeRequired,
                ReasonCode = "fresh_database_exists",
                Message = "Fresh Managed apply refuses an existing database file.",
            };
        }

        var parent = Path.GetDirectoryName(dbPath);
        if (string.IsNullOrEmpty(parent) || !fileSystem.DirectoryExists(parent))
        {
            return new SetupMigrationDecision
            {
                Kind = SetupMigrationDecisionKind.UpgradeRequired,
                ReasonCode = "database_parent_missing",
                Message = "Database parent directory is missing or unusable.",
            };
        }

        if (SetupPathGuard.IsUnsafeLink(fileSystem.InspectSymlinkOrReparsePoint(parent)))
        {
            return new SetupMigrationDecision
            {
                Kind = SetupMigrationDecisionKind.UpgradeRequired,
                ReasonCode = "database_parent_unsafe",
                Message = "Database parent path is a symlink or reparse point.",
            };
        }

        return new SetupMigrationDecision
        {
            Kind = SetupMigrationDecisionKind.MigrationRequired,
            Message = "Fresh database is absent; migration required after ACTIVE switch.",
        };
    }

    public static SetupMigrationDecision ClassifyExistingFromStatus(
        string classification,
        string previousImageReference,
        string candidateImageReference)
    {
        if (!string.Equals(previousImageReference, candidateImageReference, StringComparison.Ordinal))
        {
            return new SetupMigrationDecision
            {
                Kind = SetupMigrationDecisionKind.UpgradeRequired,
                ReasonCode = "image_digest_mismatch",
                Message = "Image digest changed; image upgrade is out of Easy Setup scope.",
            };
        }

        return classification switch
        {
            SetupSchemaClassification.Current => new SetupMigrationDecision
            {
                Kind = SetupMigrationDecisionKind.MigrationNotRequired,
                Message = "Schema is current for the active image.",
            },
            SetupSchemaClassification.Behind => new SetupMigrationDecision
            {
                Kind = SetupMigrationDecisionKind.UpgradeRequired,
                ReasonCode = "schema_behind",
                Message = "Schema is behind; upgrade is required.",
            },
            SetupSchemaClassification.AheadOrUnsupported => new SetupMigrationDecision
            {
                Kind = SetupMigrationDecisionKind.UpgradeRequired,
                ReasonCode = "schema_ahead_or_unsupported",
                Message = "Schema is ahead or unsupported.",
            },
            SetupSchemaClassification.DatabaseAbsent or SetupSchemaClassification.Unknown =>
                new SetupMigrationDecision
                {
                    Kind = SetupMigrationDecisionKind.NeedsIntervention,
                    ActionCode = SetupApplyActionCode.ReviewDatabaseSchema,
                    ReasonCode = classification.ToLowerInvariant(),
                    Message = "Existing Managed deployment schema could not be classified safely.",
                },
            _ => new SetupMigrationDecision
            {
                Kind = SetupMigrationDecisionKind.NeedsIntervention,
                ActionCode = SetupApplyActionCode.ReviewDatabaseSchema,
                ReasonCode = "schema_classification_unknown",
                Message = "Existing Managed deployment schema could not be classified safely.",
            },
        };
    }

    public static bool TryResolveHostDatabasePath(
        string normalizedDataPath,
        string? connectionString,
        out string dbPath,
        out SetupMigrationDecision? failure)
    {
        dbPath = string.Empty;
        failure = null;

        var containerRelative = "mailer.db";
        if (!string.IsNullOrWhiteSpace(connectionString))
        {
            if (!TryParseContainerRelativeDbFile(connectionString, out containerRelative, out failure))
            {
                return false;
            }
        }

        dbPath = Path.GetFullPath(Path.Combine(normalizedDataPath, containerRelative));
        return true;
    }

    internal static bool TryParseContainerRelativeDbFile(
        string connectionString,
        out string relativeFileName,
        out SetupMigrationDecision? failure)
    {
        relativeFileName = string.Empty;
        failure = null;
        try
        {
            var builder = new SqliteConnectionStringBuilder(connectionString);
            var dataSource = builder.DataSource?.Trim() ?? string.Empty;
            if (string.IsNullOrWhiteSpace(dataSource)
                || string.Equals(dataSource, ":memory:", StringComparison.OrdinalIgnoreCase)
                || dataSource.Contains("mode=memory", StringComparison.OrdinalIgnoreCase)
                || dataSource.StartsWith("file:", StringComparison.OrdinalIgnoreCase))
            {
                failure = UpgradeRequired("connection_string_unsupported");
                return false;
            }

            // Container contract: under /app/data
            const string containerDataRoot = "/app/data";
            var normalized = dataSource.Replace('\\', '/');
            if (!normalized.StartsWith(containerDataRoot + "/", StringComparison.Ordinal)
                && !string.Equals(normalized, containerDataRoot, StringComparison.Ordinal))
            {
                // Also accept relative mailer.db under data mount.
                if (normalized.Contains('/') || normalized.Contains("..", StringComparison.Ordinal))
                {
                    failure = UpgradeRequired("connection_string_path_unsupported");
                    return false;
                }

                relativeFileName = Path.GetFileName(normalized);
                return !string.IsNullOrWhiteSpace(relativeFileName);
            }

            var relative = normalized[containerDataRoot.Length..].TrimStart('/');
            if (string.IsNullOrWhiteSpace(relative)
                || relative.Contains("..", StringComparison.Ordinal)
                || relative.Contains('\\')
                || relative.EndsWith('/'))
            {
                failure = UpgradeRequired("connection_string_path_unsupported");
                return false;
            }

            relativeFileName = relative;
            return true;
        }
        catch (ArgumentException)
        {
            failure = UpgradeRequired("connection_string_unsupported");
            return false;
        }
        catch (FormatException)
        {
            failure = UpgradeRequired("connection_string_unsupported");
            return false;
        }
    }

    private static SetupMigrationDecision UpgradeRequired(string reasonCode) =>
        new()
        {
            Kind = SetupMigrationDecisionKind.UpgradeRequired,
            ReasonCode = reasonCode,
            Message = "Connection string cannot be mapped to a safe host database path.",
        };
}

/// <summary>
/// Loads seal members and verifies host at-rest integrity for a finalized bundle.
/// </summary>
public static class SetupBundleStaticValidator
{
    public static string? ClassifyImageCompatibility(
        TrustedSetupHostLayout layout,
        SetupRecordedMetadata candidateRecorded)
    {
        ArgumentNullException.ThrowIfNull(layout);
        ArgumentNullException.ThrowIfNull(candidateRecorded);

        var allowed = layout.ReleaseInventory.AllowedImageRepository;
        var recorded = candidateRecorded.ImageRepository;
        if (string.IsNullOrWhiteSpace(recorded) || string.IsNullOrWhiteSpace(allowed))
            return "image_repository_unknown";

        return string.Equals(recorded, allowed, StringComparison.Ordinal)
            ? null
            : "image_repository_mismatch";
    }

    public static SetupDockerResult TryValidateFinalizedBundle(
        ISetupFileSystem fileSystem,
        TrustedSetupHostLayout layout,
        string bundleId,
        out SetupRecordedMetadata? recorded,
        out string hostAtRest)
    {
        recorded = null;
        hostAtRest = SetupIntegrityMerger.NotVerified;

        if (!SetupActivePointer.IsSafeBundleId(bundleId))
        {
            return SetupDockerResult.Fail(
                SetupDockerResultCode.InvalidBundleInventory,
                "Bundle id is invalid.");
        }

        var bundleRoot = Path.GetFullPath(SetupBundleLayout.BundleRoot(layout.ManagedRoot, bundleId));
        if (!SetupPathGuard.TryEnsurePathSafeUnderRoot(
                fileSystem, layout.ManagedRoot, bundleRoot, out _, out _))
        {
            return SetupDockerResult.Fail(
                SetupDockerResultCode.UnsafePath,
                "Bundle path rejected.");
        }

        var finalized = Path.Combine(bundleRoot, SetupBundleLayout.FinalizedMarkerFileName);
        if (!fileSystem.FileExists(finalized))
        {
            return SetupDockerResult.Fail(
                SetupDockerResultCode.InvalidBundleInventory,
                "Bundle is not finalized.");
        }

        var recordedPath = Path.Combine(
            SetupBundleLayout.MetadataDir(bundleRoot),
            SetupBundleLayout.RecordedMetadataFileName);
        if (!fileSystem.FileExists(recordedPath))
        {
            return SetupDockerResult.Fail(
                SetupDockerResultCode.InvalidBundleInventory,
                "Recorded metadata is missing.");
        }

        try
        {
            recorded = JsonSerializer.Deserialize(
                fileSystem.ReadAllBytes(recordedPath),
                SetupJsonContext.Default.SetupRecordedMetadata);
        }
        catch (JsonException)
        {
            return SetupDockerResult.Fail(
                SetupDockerResultCode.OutputMalformed,
                "Recorded metadata is malformed.");
        }

        if (recorded is null
            || !SetupBundleLayout.IsSupportedRecordedSchemaVersion(recorded.SchemaVersion)
            || !string.Equals(recorded.BundleId, bundleId, StringComparison.Ordinal)
            || (recorded.SchemaVersion == 1 && recorded.AdminBootstrapExpectation is not null)
            || (recorded.AdminBootstrapRequested && recorded.AdminBootstrapExpectation is null))
        {
            return SetupDockerResult.Fail(
                SetupDockerResultCode.InvalidBundleInventory,
                "Recorded metadata does not match the bundle.");
        }

        var sealingKeyPath = SetupBundleLayout.HostSealingKeyPath(layout.ManagedRoot);
        if (!fileSystem.FileExists(sealingKeyPath) || !fileSystem.IsOwnerOnlyFile(sealingKeyPath))
        {
            hostAtRest = SetupIntegrityMerger.NotVerified;
            return SetupDockerResult.Fail(
                SetupDockerResultCode.InvalidBundleInventory,
                "Host sealing key is missing or not owner-only.");
        }

        var sealPath = Path.Combine(
            SetupBundleLayout.MetadataDir(bundleRoot),
            SetupBundleLayout.IntegritySealFileName);
        if (!fileSystem.FileExists(sealPath))
        {
            hostAtRest = SetupIntegrityMerger.NotVerified;
            return SetupDockerResult.Fail(
                SetupDockerResultCode.InvalidBundleInventory,
                "Integrity seal is missing.");
        }

        byte[]? sealingKey = null;
        byte[]? seal = null;
        List<(string RelativePath, byte[] Content)>? members = null;
        try
        {
            sealingKey = fileSystem.ReadAllBytes(sealingKeyPath);
            seal = fileSystem.ReadAllBytes(sealPath);
            if (!TryLoadSecretMembers(fileSystem, bundleRoot, out members))
            {
                hostAtRest = SetupIntegrityMerger.NotVerified;
                return SetupDockerResult.Fail(
                    SetupDockerResultCode.InvalidBundleInventory,
                    "Secret members could not be loaded for seal verification.");
            }

            var matched = SetupIntegritySealer.TryVerifySeal(
                sealingKey,
                seal,
                bundleId,
                recorded.ConfigurationFingerprint,
                recorded.SchemaVersion,
                members);
            hostAtRest = matched ? SetupIntegrityMerger.Matched : SetupIntegrityMerger.Mismatch;
            if (!matched)
            {
                return SetupDockerResult.Fail(
                    SetupDockerResultCode.InvalidBundleInventory,
                    "Host at-rest integrity seal mismatch.");
            }

            return SetupDockerResult.Ok();
        }
        finally
        {
            if (sealingKey is not null)
            {
                CryptographicOperations.ZeroMemory(sealingKey);
            }

            if (seal is not null)
            {
                CryptographicOperations.ZeroMemory(seal);
            }

            if (members is not null)
            {
                foreach (var member in members)
                {
                    CryptographicOperations.ZeroMemory(member.Content);
                }
            }
        }
    }

    /// <summary>
    /// Non-secret, order-stable identity of the trusted Compose file set. Recorded in the
    /// verification record so a later inspect can tell whether Compose inputs moved underneath
    /// an activation. Digests only — never operator paths.
    /// </summary>
    public static string ComputeComposeIdentity(TrustedReleaseInventory inventory)
    {
        ArgumentNullException.ThrowIfNull(inventory);

        return string.Join(
            ':',
            "compose",
            TrustedReleaseInventory.CurrentSchemaVersion.ToString(CultureInfo.InvariantCulture),
            inventory.ComposeBundleVersion,
            inventory.ComposeSha256 ?? "-",
            inventory.ComposeImageDigestSha256 ?? "-",
            inventory.ComposeRecordedMetadataSha256 ?? "-",
            inventory.ComposeMailpitSha256 ?? "-");
    }

    private static bool TryLoadSecretMembers(
        ISetupFileSystem fileSystem,
        string bundleRoot,
        out List<(string RelativePath, byte[] Content)> members)
    {
        members = [];
        var secretsEnv = Path.Combine(
            SetupBundleLayout.EnvDir(bundleRoot),
            SetupBundleLayout.SecretsEnvFileName);
        if (!fileSystem.FileExists(secretsEnv))
        {
            return false;
        }

        members.Add((
            $"{SetupBundleLayout.EnvDirectoryName}/{SetupBundleLayout.SecretsEnvFileName}",
            fileSystem.ReadAllBytes(secretsEnv)));

        var acs = Path.Combine(
            SetupBundleLayout.SecretsDir(bundleRoot),
            AcsSecretFileNames.CanonicalFileName);
        if (fileSystem.FileExists(acs))
        {
            members.Add((
                $"{SetupBundleLayout.SecretsDirectoryName}/{AcsSecretFileNames.CanonicalFileName}",
                fileSystem.ReadAllBytes(acs)));
        }

        return true;
    }
}
