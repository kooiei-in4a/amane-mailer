namespace Amane.Mailer.Setup;

/// <summary>
/// Fail-closed Manual vs Managed conflict detection for bundle generation.
/// Does not adopt Manual deployments into Managed Setup.
/// </summary>
public static class SetupConflictDetector
{
    public static bool TryDetectConflicts(
        ISetupFileSystem fileSystem,
        string managedRootFull,
        string bundleId,
        out string failureCode,
        out string message)
    {
        failureCode = string.Empty;
        message = string.Empty;

        if (!fileSystem.DirectoryExists(managedRootFull))
        {
            return true;
        }

        if (SetupPathGuard.IsUnsafeLink(fileSystem.InspectSymlinkOrReparsePoint(managedRootFull)))
        {
            failureCode = SetupResultCode.RejectedPathUnsafe;
            message = "Managed root must not be a symlink or reparse point.";
            return false;
        }

        // Any Manual marker at managed root is fail-closed, even if bundles/ already exists.
        // Easy Setup must not adopt or silently coexist with Manual Deployment overlays.
        var hasManualTenants = fileSystem.FileExists(Path.Combine(managedRootFull, SetupBundleLayout.TenantsFileName));
        var hasManualEnv = fileSystem.FileExists(Path.Combine(managedRootFull, ".env"));
        if (hasManualTenants || hasManualEnv)
        {
            failureCode = SetupResultCode.RejectedConflictManual;
            message = "Managed root looks like a Manual Deployment; Easy Setup will not adopt it.";
            return false;
        }

        var bundleRoot = SetupBundleLayout.BundleRoot(managedRootFull, bundleId);
        if (fileSystem.DirectoryExists(bundleRoot) || fileSystem.FileExists(bundleRoot))
        {
            failureCode = SetupResultCode.RejectedBundleExists;
            message = "A bundle with the same id already exists; bundles are immutable.";
            return false;
        }

        return true;
    }

    public static bool HasExistingFinalizedBundles(ISetupFileSystem fileSystem, string managedRootFull)
    {
        var bundlesDir = Path.Combine(managedRootFull, SetupBundleLayout.BundlesDirectoryName);
        if (!fileSystem.DirectoryExists(bundlesDir))
        {
            return false;
        }

        foreach (var entry in fileSystem.EnumerateFileSystemEntries(bundlesDir))
        {
            var finalized = Path.Combine(entry, SetupBundleLayout.FinalizedMarkerFileName);
            if (fileSystem.DirectoryExists(entry) && fileSystem.FileExists(finalized))
            {
                return true;
            }

            // Non-empty incomplete bundle directories also imply a non-fresh Managed root.
            if (fileSystem.DirectoryExists(entry))
            {
                return true;
            }
        }

        return false;
    }
}
