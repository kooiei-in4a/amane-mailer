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

        if (fileSystem.IsSymlinkOrReparsePoint(managedRootFull))
        {
            failureCode = SetupResultCode.RejectedPathUnsafe;
            message = "Managed root must not be a symlink or reparse point.";
            return false;
        }

        // Manual markers at managed root (tenants.json / .env) without a bundles/ tree => refuse adopt.
        var bundlesDir = Path.Combine(managedRootFull, SetupBundleLayout.BundlesDirectoryName);
        var hasBundlesDir = fileSystem.DirectoryExists(bundlesDir);
        var hasManualTenants = fileSystem.FileExists(Path.Combine(managedRootFull, SetupBundleLayout.TenantsFileName));
        var hasManualEnv = fileSystem.FileExists(Path.Combine(managedRootFull, ".env"));

        if ((hasManualTenants || hasManualEnv) && !hasBundlesDir)
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
}
