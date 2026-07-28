using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Amane.Mailer.Configuration;
using Amane.Mailer.Operations;

namespace Amane.Mailer.Setup;

/// <summary>
/// UI-independent Setup Core: validates mode 1-4 input, materializes existing-format artifacts,
/// computes configuration fingerprint, writes an immutable Managed bundle, and seals secret
/// members at rest. Does not activate ACTIVE, operate Docker, call ACS, or bootstrap Admin.
/// </summary>
public sealed class SetupCore
{
    private readonly ISetupFileSystem _fileSystem;
    private readonly TimeProvider _timeProvider;
    private readonly Func<string> _bundleIdFactory;

    public SetupCore(
        ISetupFileSystem? fileSystem = null,
        TimeProvider? timeProvider = null,
        Func<string>? bundleIdFactory = null)
    {
        _fileSystem = fileSystem ?? new HostSetupFileSystem();
        _timeProvider = timeProvider ?? TimeProvider.System;
        _bundleIdFactory = bundleIdFactory ?? CreateDefaultBundleId;
    }

    public SetupResult GenerateBundle(SetupRequest request)
    {
        try
        {
            if (!SetupRequestValidator.TryValidate(request, out var validationCode, out var validationMessage))
            {
                return SetupResult.Fail(validationCode, validationMessage);
            }

            string managedRootFull;
            try
            {
                managedRootFull = Path.GetFullPath(request.ManagedRootPath);
            }
            catch
            {
                return SetupResult.Fail(SetupResultCode.RejectedPathUnsafe, "Managed root path could not be resolved.");
            }

            if (!SetupPathGuard.TryEnsureManagedRootSafe(
                    _fileSystem,
                    managedRootFull,
                    out var pathCode,
                    out var pathMessage))
            {
                return SetupResult.Fail(pathCode, pathMessage);
            }

            var bundleId = _bundleIdFactory();

            // Safety/conflict preflight runs for dry-run and write paths alike.
            if (!SetupConflictDetector.TryDetectConflicts(
                    _fileSystem,
                    managedRootFull,
                    bundleId,
                    out var conflictCode,
                    out var conflictMessage))
            {
                return SetupResult.Fail(conflictCode, conflictMessage);
            }

            if (!TryValidateSealingKeyPreflight(managedRootFull, out var sealCode, out var sealMessage))
            {
                return SetupResult.Fail(sealCode, sealMessage);
            }

            var createdAt = _timeProvider.GetUtcNow();
            var materialized = SetupConfigurationMaterializer.Materialize(request, bundleId, createdAt);
            var plan = BuildPlan(request.Mode, materialized);

            if (request.DryRun)
            {
                return SetupResult.Ok(
                    SetupResultCode.DryRunPlan,
                    bundleId,
                    materialized.ConfigurationFingerprint,
                    plan,
                    "Dry-run plan generated; no files were written.");
            }

            return WriteBundle(managedRootFull, request.RuntimeFileOwnership, materialized, plan);
        }
        catch (SetupCoreException ex)
        {
            return SetupResult.Fail(ex.Code, ex.SafeMessage);
        }
        catch
        {
            return SetupResult.Fail(SetupResultCode.FailedUnexpected, "Setup Core failed unexpectedly.");
        }
    }

    private bool TryValidateSealingKeyPreflight(
        string managedRootFull,
        out string failureCode,
        out string message)
    {
        failureCode = string.Empty;
        message = string.Empty;
        var sealingKeyPath = SetupBundleLayout.HostSealingKeyPath(managedRootFull);
        var keyExists = _fileSystem.FileExists(sealingKeyPath);
        var bundlesDir = Path.Combine(managedRootFull, SetupBundleLayout.BundlesDirectoryName);
        var hasBundles = SetupConflictDetector.HasExistingFinalizedBundles(_fileSystem, managedRootFull)
            || (_fileSystem.DirectoryExists(bundlesDir)
                && _fileSystem.EnumerateFileSystemEntries(bundlesDir).Any());

        if (!keyExists)
        {
            if (hasBundles)
            {
                failureCode = SetupResultCode.RejectedSealingKeyMissing;
                message = "Host sealing key is missing while Managed bundles already exist.";
                return false;
            }

            return true;
        }

        if (!SetupPathGuard.TryEnsurePathSafeUnderRoot(
                _fileSystem,
                managedRootFull,
                sealingKeyPath,
                out failureCode,
                out message))
        {
            return false;
        }

        if (_fileSystem.InspectSymlinkOrReparsePoint(sealingKeyPath) is not SetupLinkInspectionResult.NotALink)
        {
            failureCode = SetupResultCode.RejectedPathUnsafe;
            message = "Sealing key path must not be a symlink or reparse point.";
            return false;
        }

        try
        {
            var existing = _fileSystem.ReadAllBytes(sealingKeyPath);
            if (existing.Length != SetupIntegritySealer.SealingKeyLength)
            {
                failureCode = SetupResultCode.RejectedSealingKeyUnsafe;
                message = "Host sealing key has an unexpected length.";
                return false;
            }
        }
        catch
        {
            failureCode = SetupResultCode.RejectedSealingKeyUnsafe;
            message = "Host sealing key could not be read safely.";
            return false;
        }

        if (!_fileSystem.IsOwnerOnlyFile(sealingKeyPath))
        {
            failureCode = SetupResultCode.RejectedSealingKeyUnsafe;
            message = "Host sealing key permissions are not owner-only.";
            return false;
        }

        return true;
    }

    private SetupResult WriteBundle(
        string managedRootFull,
        SetupRuntimeFileOwnership? ownership,
        SetupConfigurationMaterializer.MaterializedBundleContent materialized,
        SetupPlan plan)
    {
        var bundleRoot = SetupBundleLayout.BundleRoot(managedRootFull, materialized.BundleId);
        var writtenFiles = new List<string>();
        var createdDirs = new List<string>();

        try
        {
            // Sealing key must exist before any bundles/<id> directory is created so a fresh
            // install is not misclassified as "bundles exist without a sealing key".
            EnsureDirectory(managedRootFull, managedRootFull, createdDirs, ownership);
            var sealingDir = SetupBundleLayout.SealingDir(managedRootFull);
            EnsureDirectory(managedRootFull, sealingDir, createdDirs, ownership);
            var sealingKeyPath = SetupBundleLayout.HostSealingKeyPath(managedRootFull);
            var sealingKey = EnsureSealingKey(managedRootFull, sealingKeyPath, ownership, writtenFiles);

            // ADR D-03 / host sealing key durability: persist sealing/ before any bundles/<id> work.
            try
            {
                _fileSystem.FlushDirectory(sealingDir);
                _fileSystem.FlushDirectory(managedRootFull);
            }
            catch
            {
                throw new SetupCoreException(
                    SetupResultCode.RejectedDurabilityFailed,
                    "Sealing directory durability flush failed before bundle generation.");
            }

            EnsureDirectory(
                managedRootFull,
                Path.Combine(managedRootFull, SetupBundleLayout.BundlesDirectoryName),
                createdDirs,
                ownership);
            EnsureDirectory(managedRootFull, bundleRoot, createdDirs, ownership);
            EnsureDirectory(managedRootFull, SetupBundleLayout.ConfigDir(bundleRoot), createdDirs, ownership);
            EnsureDirectory(managedRootFull, SetupBundleLayout.EnvDir(bundleRoot), createdDirs, ownership);
            EnsureDirectory(managedRootFull, SetupBundleLayout.MetadataDir(bundleRoot), createdDirs, ownership);
            if (materialized.AcsConnectionStringBytes is not null)
            {
                EnsureDirectory(managedRootFull, SetupBundleLayout.SecretsDir(bundleRoot), createdDirs, ownership);
            }

            var composeEnvText = SetupConfigurationMaterializer.FormatEnvFile(materialized.ComposeEnv);
            var secretsEnvText = SetupConfigurationMaterializer.FormatEnvFile(materialized.SecretsEnv);
            var recordedJson = JsonSerializer.Serialize(materialized.Recorded, SetupJsonContext.Default.SetupRecordedMetadata);

            WriteNew(
                managedRootFull,
                Path.Combine(SetupBundleLayout.ConfigDir(bundleRoot), SetupBundleLayout.TenantsFileName),
                materialized.TenantsJson,
                ownership,
                writtenFiles);
            WriteNew(
                managedRootFull,
                Path.Combine(SetupBundleLayout.EnvDir(bundleRoot), SetupBundleLayout.ComposeEnvFileName),
                composeEnvText,
                ownership,
                writtenFiles);
            WriteNew(
                managedRootFull,
                Path.Combine(SetupBundleLayout.EnvDir(bundleRoot), SetupBundleLayout.SecretsEnvFileName),
                secretsEnvText,
                ownership,
                writtenFiles);

            if (materialized.PlatformSenderJson is not null)
            {
                WriteNew(
                    managedRootFull,
                    Path.Combine(SetupBundleLayout.ConfigDir(bundleRoot), PlatformSenderFile.CanonicalFileName),
                    materialized.PlatformSenderJson,
                    ownership,
                    writtenFiles);
            }

            var secretMembers = new List<(string RelativePath, byte[] Content)>
            {
                (
                    $"{SetupBundleLayout.EnvDirectoryName}/{SetupBundleLayout.SecretsEnvFileName}",
                    Encoding.UTF8.GetBytes(secretsEnvText)),
            };

            if (materialized.AcsConnectionStringBytes is not null)
            {
                var acsRelative = $"{SetupBundleLayout.SecretsDirectoryName}/{AcsSecretFileNames.CanonicalFileName}";
                var acsPath = Path.Combine(
                    SetupBundleLayout.SecretsDir(bundleRoot),
                    AcsSecretFileNames.CanonicalFileName);
                WriteNewBytes(managedRootFull, acsPath, materialized.AcsConnectionStringBytes, ownership, writtenFiles);
                secretMembers.Add((acsRelative, materialized.AcsConnectionStringBytes));
            }

            var seal = SetupIntegritySealer.CreateSeal(
                sealingKey,
                materialized.BundleId,
                materialized.ConfigurationFingerprint,
                SetupBundleLayout.RecordedSchemaVersion,
                secretMembers);
            WriteNewBytes(
                managedRootFull,
                Path.Combine(SetupBundleLayout.MetadataDir(bundleRoot), SetupBundleLayout.IntegritySealFileName),
                seal,
                ownership,
                writtenFiles);

            WriteNew(
                managedRootFull,
                Path.Combine(SetupBundleLayout.MetadataDir(bundleRoot), SetupBundleLayout.RecordedMetadataFileName),
                recordedJson,
                ownership,
                writtenFiles);

            // ADR D-03: flush files, fsync child dirs, bundle root, then parents (bundles/, managed root),
            // then write FINALIZED, then fsync parents again.
            var bundlesDir = Path.Combine(managedRootFull, SetupBundleLayout.BundlesDirectoryName);
            try
            {
                _fileSystem.FlushDirectory(SetupBundleLayout.ConfigDir(bundleRoot));
                _fileSystem.FlushDirectory(SetupBundleLayout.EnvDir(bundleRoot));
                _fileSystem.FlushDirectory(SetupBundleLayout.MetadataDir(bundleRoot));
                if (materialized.AcsConnectionStringBytes is not null)
                {
                    _fileSystem.FlushDirectory(SetupBundleLayout.SecretsDir(bundleRoot));
                }

                _fileSystem.FlushDirectory(bundleRoot);
                _fileSystem.FlushDirectory(bundlesDir);
                _fileSystem.FlushDirectory(managedRootFull);
            }
            catch
            {
                throw new SetupCoreException(
                    SetupResultCode.RejectedDurabilityFailed,
                    "Bundle directory durability flush failed before FINALIZED.");
            }

            WriteNew(
                managedRootFull,
                Path.Combine(bundleRoot, SetupBundleLayout.FinalizedMarkerFileName),
                materialized.BundleId + "\n",
                ownership,
                writtenFiles);

            try
            {
                _fileSystem.FlushDirectory(bundleRoot);
                _fileSystem.FlushDirectory(bundlesDir);
                _fileSystem.FlushDirectory(managedRootFull);
            }
            catch
            {
                throw new SetupCoreException(
                    SetupResultCode.RejectedDurabilityFailed,
                    "Bundle directory durability flush failed after FINALIZED.");
            }

            CryptographicOperations.ZeroMemory(seal);
            CryptographicOperations.ZeroMemory(sealingKey);

            return SetupResult.Ok(
                SetupResultCode.Succeeded,
                materialized.BundleId,
                materialized.ConfigurationFingerprint,
                plan,
                "Managed bundle generated; not activated.");
        }
        catch (SetupCoreException ex) when (
            ex.Code is SetupResultCode.RejectedCleanupFailed
                or SetupResultCode.RejectedRollbackFailed
                or SetupResultCode.RejectedDurabilityFailed
                or SetupResultCode.RejectedOwnershipFailed
                or SetupResultCode.RejectedSealingKeyMissing
                or SetupResultCode.RejectedSealingKeyUnsafe
                or SetupResultCode.RejectedPathUnsafe
                or SetupResultCode.RejectedBundleExists)
        {
            // Cleanup failure always wins so callers learn that secret/partial state may remain.
            if (ex.Code is SetupResultCode.RejectedCleanupFailed or SetupResultCode.RejectedRollbackFailed)
            {
                return SetupResult.Fail(ex.Code, ex.SafeMessage);
            }

            return FailAfterPartialCleanup(writtenFiles, createdDirs, ex.Code, ex.SafeMessage);
        }
        catch
        {
            return FailAfterPartialCleanup(
                writtenFiles,
                createdDirs,
                SetupResultCode.RejectedPartialWrite,
                "Bundle write failed and partial output was removed.");
        }
    }

    private SetupResult FailAfterPartialCleanup(
        List<string> writtenFiles,
        List<string> createdDirs,
        string primaryCode,
        string primaryMessage)
    {
        if (!TryCleanupPartial(writtenFiles, createdDirs, out _))
        {
            return SetupResult.Fail(
                SetupResultCode.RejectedCleanupFailed,
                "Partial write cleanup failed; manual intervention may be required.");
        }

        return SetupResult.Fail(primaryCode, primaryMessage);
    }

    private static SetupPlan BuildPlan(
        SetupMode mode,
        SetupConfigurationMaterializer.MaterializedBundleContent materialized)
    {
        var files = new List<SetupPlannedFile>
        {
            Plan(
                $"{SetupBundleLayout.ConfigDirectoryName}/{SetupBundleLayout.TenantsFileName}",
                SetupPlannedFileKind.PublicConfig,
                Encoding.UTF8.GetByteCount(materialized.TenantsJson)),
            Plan(
                $"{SetupBundleLayout.EnvDirectoryName}/{SetupBundleLayout.ComposeEnvFileName}",
                SetupPlannedFileKind.PublicConfig,
                Encoding.UTF8.GetByteCount(SetupConfigurationMaterializer.FormatEnvFile(materialized.ComposeEnv))),
            Plan(
                $"{SetupBundleLayout.EnvDirectoryName}/{SetupBundleLayout.SecretsEnvFileName}",
                SetupPlannedFileKind.SecretValuedEnv,
                Encoding.UTF8.GetByteCount(SetupConfigurationMaterializer.FormatEnvFile(materialized.SecretsEnv))),
            Plan(
                $"{SetupBundleLayout.MetadataDirectoryName}/{SetupBundleLayout.RecordedMetadataFileName}",
                SetupPlannedFileKind.Metadata,
                Encoding.UTF8.GetByteCount(
                    JsonSerializer.Serialize(materialized.Recorded, SetupJsonContext.Default.SetupRecordedMetadata))),
            Plan(
                $"{SetupBundleLayout.MetadataDirectoryName}/{SetupBundleLayout.IntegritySealFileName}",
                SetupPlannedFileKind.Metadata,
                SetupIntegritySealer.MagicLength + 1 + SetupIntegritySealer.MacLength),
            Plan(
                SetupBundleLayout.FinalizedMarkerFileName,
                SetupPlannedFileKind.FinalizedMarker,
                Encoding.UTF8.GetByteCount(materialized.BundleId + "\n")),
        };

        if (materialized.PlatformSenderJson is not null)
        {
            files.Insert(
                1,
                Plan(
                    $"{SetupBundleLayout.ConfigDirectoryName}/{PlatformSenderFile.CanonicalFileName}",
                    SetupPlannedFileKind.PublicConfig,
                    Encoding.UTF8.GetByteCount(materialized.PlatformSenderJson)));
        }

        if (materialized.AcsConnectionStringBytes is not null)
        {
            files.Add(
                Plan(
                    $"{SetupBundleLayout.SecretsDirectoryName}/{AcsSecretFileNames.CanonicalFileName}",
                    SetupPlannedFileKind.FileSecret,
                    materialized.AcsConnectionStringBytes.Length));
        }

        return new SetupPlan
        {
            BundleId = materialized.BundleId,
            ConfigurationFingerprint = materialized.ConfigurationFingerprint,
            Mode = mode,
            Files = files,
        };
    }

    private static SetupPlannedFile Plan(string relativePath, SetupPlannedFileKind kind, int length) =>
        new()
        {
            RelativePath = relativePath,
            Kind = kind,
            ContentLength = length,
        };

    private void EnsureDirectory(
        string managedRootFull,
        string path,
        List<string> createdDirs,
        SetupRuntimeFileOwnership? ownership)
    {
        if (!SetupPathGuard.TryEnsurePathSafeUnderRoot(
                _fileSystem,
                managedRootFull,
                path,
                out var code,
                out var message))
        {
            throw new SetupCoreException(code, message);
        }

        if (_fileSystem.DirectoryExists(path))
        {
            if (SetupPathGuard.IsUnsafeLink(_fileSystem.InspectSymlinkOrReparsePoint(path)))
            {
                throw new SetupCoreException(
                    SetupResultCode.RejectedPathUnsafe,
                    "Directory path must not be a symlink or reparse point.");
            }

            return;
        }

        _fileSystem.CreateOwnerOnlyDirectory(path);
        createdDirs.Add(path);
        ApplyOwnership(path, ownership, directory: true);
    }

    private byte[] EnsureSealingKey(
        string managedRootFull,
        string sealingKeyPath,
        SetupRuntimeFileOwnership? ownership,
        List<string> writtenFiles)
    {
        if (_fileSystem.FileExists(sealingKeyPath))
        {
            if (!SetupPathGuard.TryEnsurePathSafeUnderRoot(
                    _fileSystem,
                    managedRootFull,
                    sealingKeyPath,
                    out var code,
                    out var message))
            {
                throw new SetupCoreException(code, message);
            }

            var existing = _fileSystem.ReadAllBytes(sealingKeyPath);
            if (existing.Length != SetupIntegritySealer.SealingKeyLength)
            {
                throw new SetupCoreException(
                    SetupResultCode.RejectedSealingKeyUnsafe,
                    "Host sealing key has an unexpected length.");
            }

            if (!_fileSystem.IsOwnerOnlyFile(sealingKeyPath))
            {
                throw new SetupCoreException(
                    SetupResultCode.RejectedSealingKeyUnsafe,
                    "Host sealing key permissions are not owner-only.");
            }

            return existing;
        }

        var bundlesDir = Path.Combine(managedRootFull, SetupBundleLayout.BundlesDirectoryName);
        if (SetupConflictDetector.HasExistingFinalizedBundles(_fileSystem, managedRootFull)
            || (_fileSystem.DirectoryExists(bundlesDir)
                && _fileSystem.EnumerateFileSystemEntries(bundlesDir).Any()))
        {
            throw new SetupCoreException(
                SetupResultCode.RejectedSealingKeyMissing,
                "Host sealing key is missing while Managed bundles already exist.");
        }

        var key = SetupIntegritySealer.CreateSealingKey();
        WriteNewBytes(managedRootFull, sealingKeyPath, key, ownership, writtenFiles);
        return key;
    }

    private void WriteNew(
        string managedRootFull,
        string path,
        string content,
        SetupRuntimeFileOwnership? ownership,
        List<string> writtenFiles)
    {
        WriteNewBytes(managedRootFull, path, Encoding.UTF8.GetBytes(content), ownership, writtenFiles);
    }

    private void WriteNewBytes(
        string managedRootFull,
        string path,
        byte[] content,
        SetupRuntimeFileOwnership? ownership,
        List<string> writtenFiles)
    {
        if (!SetupPathGuard.TryEnsurePathSafeUnderRoot(
                _fileSystem,
                managedRootFull,
                path,
                out var code,
                out var message))
        {
            throw new SetupCoreException(code, message);
        }

        if (_fileSystem.FileExists(path))
        {
            throw new SetupCoreException(
                SetupResultCode.RejectedBundleExists,
                "Refusing to overwrite an existing bundle file.");
        }

        if (SetupPathGuard.IsUnsafeLink(_fileSystem.InspectSymlinkOrReparsePoint(path)))
        {
            throw new SetupCoreException(
                SetupResultCode.RejectedPathUnsafe,
                "Target path must not be a symlink or reparse point.");
        }

        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory)
            && SetupPathGuard.IsUnsafeLink(_fileSystem.InspectSymlinkOrReparsePoint(directory)))
        {
            throw new SetupCoreException(
                SetupResultCode.RejectedPathUnsafe,
                "Target directory must not be a symlink or reparse point.");
        }

        try
        {
            _fileSystem.WriteProtectedFileCreateNew(path, content);
        }
        catch (SecureFileWriteException ex) when (ex.CreatedFileCleanupFailed)
        {
            // Incomplete file may remain; track it so FailAfterPartialCleanup can surface cleanup_failed.
            writtenFiles.Add(path);
            throw new SetupCoreException(
                SetupResultCode.RejectedCleanupFailed,
                "Partial write cleanup failed; manual intervention may be required.");
        }

        writtenFiles.Add(path);
        ApplyOwnership(path, ownership, directory: false);
    }

    private void ApplyOwnership(string path, SetupRuntimeFileOwnership? ownership, bool directory)
    {
        if (ownership is null || (!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS()))
        {
            return;
        }

        try
        {
            var euid = _fileSystem.GetEffectiveUnixUserId();
            if (euid is not null
                && euid.Value != 0
                && euid.Value != ownership.UnixUserId)
            {
                throw new SetupCoreException(
                    SetupResultCode.RejectedOwnershipFailed,
                    "Current process UID cannot assign the required runtime file ownership.");
            }

            _fileSystem.SetUnixOwnership(path, ownership.UnixUserId, ownership.UnixGroupId);
            _fileSystem.SetUnixFileModeOwnerOnly(path, executableDirectory: directory);
        }
        catch (SetupCoreException)
        {
            throw;
        }
        catch
        {
            throw new SetupCoreException(
                SetupResultCode.RejectedOwnershipFailed,
                "Failed to apply runtime file ownership to a generated path.");
        }
    }

    private bool TryCleanupPartial(List<string> writtenFiles, List<string> createdDirs, out bool cleanupFailed)
    {
        cleanupFailed = false;
        for (var i = writtenFiles.Count - 1; i >= 0; i--)
        {
            try
            {
                if (_fileSystem.FileExists(writtenFiles[i]))
                {
                    _fileSystem.DeleteFile(writtenFiles[i]);
                }
            }
            catch
            {
                cleanupFailed = true;
            }
        }

        for (var i = createdDirs.Count - 1; i >= 0; i--)
        {
            try
            {
                if (_fileSystem.DirectoryExists(createdDirs[i]))
                {
                    _fileSystem.DeleteDirectoryRecursive(createdDirs[i]);
                }
            }
            catch
            {
                cleanupFailed = true;
            }
        }

        return !cleanupFailed;
    }

    private static string CreateDefaultBundleId()
    {
        Span<byte> raw = stackalloc byte[4];
        RandomNumberGenerator.Fill(raw);
        return $"{DateTimeOffset.UtcNow:yyyyMMddHHmmss}-{Convert.ToHexString(raw).ToLowerInvariant()}";
    }
}

internal sealed class SetupCoreException(string code, string safeMessage) : Exception(safeMessage)
{
    public string Code { get; } = code;
    public string SafeMessage { get; } = safeMessage;
}
