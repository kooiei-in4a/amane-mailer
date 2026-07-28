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

            if (_fileSystem.DirectoryExists(managedRootFull) && _fileSystem.IsSymlinkOrReparsePoint(managedRootFull))
            {
                return SetupResult.Fail(SetupResultCode.RejectedPathUnsafe, "Managed root must not be a symlink or reparse point.");
            }

            var bundleId = _bundleIdFactory();
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

            if (!SetupConflictDetector.TryDetectConflicts(
                    _fileSystem,
                    managedRootFull,
                    bundleId,
                    out var conflictCode,
                    out var conflictMessage))
            {
                return SetupResult.Fail(conflictCode, conflictMessage);
            }

            return WriteBundle(managedRootFull, materialized, plan);
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

    private SetupResult WriteBundle(
        string managedRootFull,
        SetupConfigurationMaterializer.MaterializedBundleContent materialized,
        SetupPlan plan)
    {
        var bundleRoot = SetupBundleLayout.BundleRoot(managedRootFull, materialized.BundleId);
        var writtenFiles = new List<string>();
        var createdDirs = new List<string>();

        try
        {
            EnsureDirectory(managedRootFull, createdDirs);
            EnsureDirectory(Path.Combine(managedRootFull, SetupBundleLayout.BundlesDirectoryName), createdDirs);
            EnsureDirectory(bundleRoot, createdDirs);
            EnsureDirectory(SetupBundleLayout.ConfigDir(bundleRoot), createdDirs);
            EnsureDirectory(SetupBundleLayout.EnvDir(bundleRoot), createdDirs);
            EnsureDirectory(SetupBundleLayout.MetadataDir(bundleRoot), createdDirs);
            if (materialized.AcsConnectionStringBytes is not null)
            {
                EnsureDirectory(SetupBundleLayout.SecretsDir(bundleRoot), createdDirs);
            }

            var sealingDir = SetupBundleLayout.SealingDir(managedRootFull);
            EnsureDirectory(sealingDir, createdDirs);
            var sealingKeyPath = SetupBundleLayout.HostSealingKeyPath(managedRootFull);
            var sealingKey = EnsureSealingKey(sealingKeyPath, writtenFiles);

            var composeEnvText = SetupConfigurationMaterializer.FormatEnvFile(materialized.ComposeEnv);
            var secretsEnvText = SetupConfigurationMaterializer.FormatEnvFile(materialized.SecretsEnv);
            var recordedJson = JsonSerializer.Serialize(materialized.Recorded, SetupJsonContext.Default.SetupRecordedMetadata);

            WriteNew(
                Path.Combine(SetupBundleLayout.ConfigDir(bundleRoot), SetupBundleLayout.TenantsFileName),
                materialized.TenantsJson,
                writtenFiles);
            WriteNew(
                Path.Combine(SetupBundleLayout.EnvDir(bundleRoot), SetupBundleLayout.ComposeEnvFileName),
                composeEnvText,
                writtenFiles);
            WriteNew(
                Path.Combine(SetupBundleLayout.EnvDir(bundleRoot), SetupBundleLayout.SecretsEnvFileName),
                secretsEnvText,
                writtenFiles);

            if (materialized.PlatformSenderJson is not null)
            {
                WriteNew(
                    Path.Combine(SetupBundleLayout.ConfigDir(bundleRoot), PlatformSenderFile.CanonicalFileName),
                    materialized.PlatformSenderJson,
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
                WriteNewBytes(acsPath, materialized.AcsConnectionStringBytes, writtenFiles);
                secretMembers.Add((acsRelative, materialized.AcsConnectionStringBytes));
            }

            var seal = SetupIntegritySealer.CreateSeal(sealingKey, secretMembers);
            WriteNewBytes(
                Path.Combine(SetupBundleLayout.MetadataDir(bundleRoot), SetupBundleLayout.IntegritySealFileName),
                seal,
                writtenFiles);

            WriteNew(
                Path.Combine(SetupBundleLayout.MetadataDir(bundleRoot), SetupBundleLayout.RecordedMetadataFileName),
                recordedJson,
                writtenFiles);

            WriteNew(
                Path.Combine(bundleRoot, SetupBundleLayout.FinalizedMarkerFileName),
                materialized.BundleId + "\n",
                writtenFiles);

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
            ex.Code is SetupResultCode.RejectedCleanupFailed or SetupResultCode.RejectedRollbackFailed)
        {
            return SetupResult.Fail(ex.Code, ex.SafeMessage);
        }
        catch
        {
            if (!TryCleanupPartial(writtenFiles, createdDirs, out var cleanupFailed))
            {
                return SetupResult.Fail(
                    cleanupFailed
                        ? SetupResultCode.RejectedCleanupFailed
                        : SetupResultCode.RejectedRollbackFailed,
                    cleanupFailed
                        ? "Partial write cleanup failed; manual intervention may be required."
                        : "Partial write rollback failed; manual intervention may be required.");
            }

            return SetupResult.Fail(
                SetupResultCode.RejectedPartialWrite,
                "Bundle write failed and partial output was removed.");
        }
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

    private void EnsureDirectory(string path, List<string> createdDirs)
    {
        if (_fileSystem.DirectoryExists(path))
        {
            if (_fileSystem.IsSymlinkOrReparsePoint(path))
            {
                throw new SetupCoreException(
                    SetupResultCode.RejectedPathUnsafe,
                    "Directory path must not be a symlink or reparse point.");
            }

            return;
        }

        _fileSystem.CreateOwnerOnlyDirectory(path);
        createdDirs.Add(path);
    }

    private byte[] EnsureSealingKey(string sealingKeyPath, List<string> writtenFiles)
    {
        if (_fileSystem.FileExists(sealingKeyPath))
        {
            if (_fileSystem.IsSymlinkOrReparsePoint(sealingKeyPath))
            {
                throw new SetupCoreException(
                    SetupResultCode.RejectedPathUnsafe,
                    "Sealing key path must not be a symlink or reparse point.");
            }

            var existing = _fileSystem.ReadAllBytes(sealingKeyPath);
            if (existing.Length != SetupIntegritySealer.SealingKeyLength)
            {
                throw new SetupCoreException(
                    SetupResultCode.RejectedValidation,
                    "Host sealing key has an unexpected length.");
            }

            return existing;
        }

        var key = SetupIntegritySealer.CreateSealingKey();
        WriteNewBytes(sealingKeyPath, key, writtenFiles);
        return key;
    }

    private void WriteNew(string path, string content, List<string> writtenFiles)
    {
        if (_fileSystem.FileExists(path) || _fileSystem.IsSymlinkOrReparsePoint(path))
        {
            throw new SetupCoreException(
                SetupResultCode.RejectedBundleExists,
                "Refusing to overwrite an existing bundle file.");
        }

        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory) && _fileSystem.IsSymlinkOrReparsePoint(directory))
        {
            throw new SetupCoreException(
                SetupResultCode.RejectedPathUnsafe,
                "Target directory must not be a symlink or reparse point.");
        }

        _fileSystem.WriteProtectedFileCreateNew(path, content);
        writtenFiles.Add(path);
    }

    private void WriteNewBytes(string path, byte[] content, List<string> writtenFiles)
    {
        if (_fileSystem.FileExists(path) || _fileSystem.IsSymlinkOrReparsePoint(path))
        {
            throw new SetupCoreException(
                SetupResultCode.RejectedBundleExists,
                "Refusing to overwrite an existing bundle file.");
        }

        _fileSystem.WriteProtectedFileCreateNew(path, content);
        writtenFiles.Add(path);
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
