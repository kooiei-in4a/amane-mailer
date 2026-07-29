using System.Text.Json;

namespace Amane.Mailer.Setup;

/// <summary>
/// Resolves a trusted host layout from a release bundle directory marker layout.
/// Operator-typed absolute paths are never accepted on the public surface.
/// </summary>
public static class TrustedSetupHostLayoutResolver
{
    public const string ManagedRootDirectoryName = "managed";
    public const string ExternalEnvFileName = "external.env";

    /// <summary>
    /// Resolves layout by locating <see cref="TrustedReleaseInventory.ManifestFileName"/> under
    /// <paramref name="releaseBundleRoot"/> (already product-owned, not operator UI input).
    /// </summary>
    public static SetupDockerResult TryResolve(
        ISetupFileSystem fileSystem,
        string releaseBundleRoot,
        SetupMode mode,
        string deploymentIdentity,
        out TrustedSetupHostLayout? layout)
    {
        layout = null;
        ArgumentNullException.ThrowIfNull(fileSystem);

        if (string.IsNullOrWhiteSpace(releaseBundleRoot)
            || string.IsNullOrWhiteSpace(deploymentIdentity))
        {
            return SetupDockerResult.Fail(
                SetupDockerResultCode.UnsafePath,
                "Release bundle root and deployment identity are required.");
        }

        string rootFull;
        try
        {
            rootFull = Path.GetFullPath(releaseBundleRoot);
        }
        catch
        {
            return SetupDockerResult.Fail(
                SetupDockerResultCode.UnsafePath,
                "Release bundle root path could not be resolved.");
        }

        if (!SetupPathGuard.TryEnsureManagedRootSafe(fileSystem, rootFull, out _, out _))
        {
            return SetupDockerResult.Fail(
                SetupDockerResultCode.UnsafePath,
                "Release bundle root path rejected.");
        }

        var manifestPath = Path.GetFullPath(
            Path.Combine(rootFull, SetupDockerInventory.ReleaseManifestRelativePath));
        if (!SetupPathGuard.TryEnsurePathSafeUnderRoot(fileSystem, rootFull, manifestPath, out _, out _))
        {
            return SetupDockerResult.Fail(
                SetupDockerResultCode.UnsafePath,
                "Release manifest path rejected.");
        }

        if (!fileSystem.FileExists(manifestPath))
        {
            return SetupDockerResult.Fail(
                SetupDockerResultCode.InvalidBundleInventory,
                "Trusted release manifest is missing.");
        }

        TrustedReleaseInventory inventory;
        try
        {
            var bytes = fileSystem.ReadAllBytes(manifestPath);
            var document = JsonSerializer.Deserialize(
                bytes,
                SetupHostDockerJsonContext.Default.ReleaseBundleManifestDocument);
            if (document is null || document.SchemaVersion != TrustedReleaseInventory.CurrentSchemaVersion)
            {
                return SetupDockerResult.Fail(
                    SetupDockerResultCode.InvalidBundleInventory,
                    "Trusted release manifest schema is unsupported.");
            }

            inventory = new TrustedReleaseInventory
            {
                AllowedImageRepository = document.ImageRepository ?? string.Empty,
                RequiredImageDigest = document.ImageDigest ?? string.Empty,
                AllowedDisplayTag = document.ImageTag ?? string.Empty,
                ComposeBundleVersion = document.ComposeBundleVersion ?? string.Empty,
                ComposeSha256 = document.ComposeSha256,
                LauncherVersionMin = document.LauncherVersionMin ?? string.Empty,
                LauncherVersionMax = document.LauncherVersionMax ?? string.Empty,
                ProjectNamePrefix = document.ProjectNamePrefix ?? "amane",
                MailpitImageReference = document.MailpitImageReference,
            };
        }
        catch
        {
            return SetupDockerResult.Fail(
                SetupDockerResultCode.InvalidBundleInventory,
                "Trusted release manifest could not be parsed.");
        }

        var shapeFailure = inventory.ValidateShape();
        if (shapeFailure is not null)
        {
            return shapeFailure;
        }

        var topology = SetupComposeTopologySelector.ForMode(mode);
        if (topology == SetupComposeTopology.DeployWithMailpit
            && string.IsNullOrWhiteSpace(inventory.MailpitImageReference))
        {
            return SetupDockerResult.Fail(
                SetupDockerResultCode.InvalidBundleInventory,
                "Mode 1 requires a digest-pinned Mailpit image in the release inventory.");
        }

        var composePaths = new List<string>();
        var deployCompose = Path.GetFullPath(
            Path.Combine(rootFull, SetupDockerInventory.DeployComposeRelativePath));
        if (!TryValidateComposeFile(fileSystem, rootFull, deployCompose, out var composeFailure))
        {
            return composeFailure!;
        }

        composePaths.Add(deployCompose);

        if (topology == SetupComposeTopology.DeployWithMailpit)
        {
            var overlay = Path.GetFullPath(
                Path.Combine(rootFull, SetupDockerInventory.MailpitOverlayRelativePath));
            if (!TryValidateComposeFile(fileSystem, rootFull, overlay, out composeFailure))
            {
                return composeFailure!;
            }

            composePaths.Add(overlay);
        }

        var managedRoot = Path.GetFullPath(Path.Combine(rootFull, ManagedRootDirectoryName));
        if (!SetupPathGuard.TryEnsurePathSafeUnderRoot(fileSystem, rootFull, managedRoot, out _, out _)
            && managedRoot != rootFull)
        {
            // managed root may not exist yet; ensure parent is safe and candidate is under root.
            if (!SetupPathGuard.IsUnderRoot(rootFull, managedRoot)
                || SetupPathGuard.HasSymlinkOrReparseInAncestry(fileSystem, rootFull))
            {
                return SetupDockerResult.Fail(
                    SetupDockerResultCode.UnsafePath,
                    "Managed root path rejected.");
            }
        }

        if (SetupPathGuard.HasSymlinkOrReparseInAncestry(fileSystem, managedRoot)
            && fileSystem.DirectoryExists(managedRoot))
        {
            return SetupDockerResult.Fail(
                SetupDockerResultCode.UnsafePath,
                "Managed root path rejected.");
        }

        var statePath = Path.GetFullPath(
            Path.Combine(managedRoot, SetupBundleLayout.StateDirectoryName));
        var externalEnvPath = Path.GetFullPath(Path.Combine(managedRoot, ExternalEnvFileName));

        layout = new TrustedSetupHostLayout(
            rootFull,
            managedRoot,
            statePath,
            externalEnvPath,
            composePaths,
            topology,
            inventory,
            deploymentIdentity);
        return SetupDockerResult.Ok("Trusted host layout resolved.");
    }

    /// <summary>
    /// Test-only factory that materializes a release-bundle-shaped directory tree.
    /// Not callable from Web/terminal operator input paths.
    /// </summary>
    internal static SetupDockerResult CreateLayoutForTests(
        ISetupFileSystem fileSystem,
        string scratchRoot,
        SetupMode mode,
        TrustedReleaseInventory inventory,
        string deploymentIdentity,
        string deployComposeContents,
        string? mailpitOverlayContents,
        out TrustedSetupHostLayout? layout)
    {
        layout = null;
        var shapeFailure = inventory.ValidateShape();
        if (shapeFailure is not null)
        {
            return shapeFailure;
        }

        var rootFull = Path.GetFullPath(scratchRoot);
        Directory.CreateDirectory(rootFull);

        var manifestPath = Path.Combine(rootFull, TrustedReleaseInventory.ManifestFileName);
        var manifest = new ReleaseBundleManifestDocument
        {
            SchemaVersion = TrustedReleaseInventory.CurrentSchemaVersion,
            ImageRepository = inventory.AllowedImageRepository,
            ImageDigest = inventory.RequiredImageDigest,
            ImageTag = inventory.AllowedDisplayTag,
            ComposeBundleVersion = inventory.ComposeBundleVersion,
            ComposeSha256 = inventory.ComposeSha256,
            LauncherVersionMin = inventory.LauncherVersionMin,
            LauncherVersionMax = inventory.LauncherVersionMax,
            ProjectNamePrefix = inventory.ProjectNamePrefix,
            MailpitImageReference = inventory.MailpitImageReference,
        };
        var manifestJson = JsonSerializer.Serialize(
            manifest,
            SetupHostDockerJsonContext.Default.ReleaseBundleManifestDocument);
        File.WriteAllText(manifestPath, manifestJson);

        File.WriteAllText(
            Path.Combine(rootFull, SetupDockerInventory.DeployComposeRelativePath),
            deployComposeContents);

        var topology = SetupComposeTopologySelector.ForMode(mode);
        if (topology == SetupComposeTopology.DeployWithMailpit)
        {
            if (string.IsNullOrWhiteSpace(mailpitOverlayContents))
            {
                return SetupDockerResult.Fail(
                    SetupDockerResultCode.InvalidBundleInventory,
                    "Mode 1 test layout requires a Mailpit overlay.");
            }

            File.WriteAllText(
                Path.Combine(rootFull, SetupDockerInventory.MailpitOverlayRelativePath),
                mailpitOverlayContents);
        }

        return TryResolve(fileSystem, rootFull, mode, deploymentIdentity, out layout);
    }

    private static bool TryValidateComposeFile(
        ISetupFileSystem fileSystem,
        string rootFull,
        string composeFull,
        out SetupDockerResult? failure)
    {
        failure = null;
        if (!SetupPathGuard.TryEnsurePathSafeUnderRoot(fileSystem, rootFull, composeFull, out _, out _))
        {
            failure = SetupDockerResult.Fail(
                SetupDockerResultCode.UnsafePath,
                "Compose file path rejected.");
            return false;
        }

        if (!fileSystem.FileExists(composeFull))
        {
            failure = SetupDockerResult.Fail(
                SetupDockerResultCode.InvalidBundleInventory,
                "Trusted Compose file is missing.");
            return false;
        }

        return true;
    }
}
