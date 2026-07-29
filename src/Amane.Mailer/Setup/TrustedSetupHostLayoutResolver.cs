using System.Reflection;
using System.Security.Cryptography;
using System.Text;
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
    internal static SetupDockerResult TryResolve(
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
                ComposeImageDigestSha256 = document.ComposeImageDigestSha256,
                ComposeRecordedMetadataSha256 = document.ComposeRecordedMetadataSha256,
                ComposeMailpitSha256 = document.ComposeMailpitSha256,
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

        var deployCompose = Path.GetFullPath(
            Path.Combine(rootFull, SetupDockerInventory.DeployComposeRelativePath));
        if (!TryValidateComposeFile(fileSystem, rootFull, deployCompose, out var composeFailure))
        {
            return composeFailure!;
        }

        if (!DigestMatches(fileSystem, deployCompose, inventory.ComposeSha256))
        {
            return SetupDockerResult.Fail(
                SetupDockerResultCode.InvalidBundleInventory,
                "Trusted Compose digest does not match the release inventory.");
        }

        if (!TryGetLauncherVersion(out var launcherVersion)
            || !TryParseVersion(inventory.LauncherVersionMin, out var launcherMin)
            || !TryParseVersion(inventory.LauncherVersionMax, out var launcherMax)
            || launcherVersion < launcherMin
            || launcherVersion > launcherMax)
        {
            return SetupDockerResult.Fail(
                SetupDockerResultCode.InvalidBundleInventory,
                "Launcher version is outside the trusted release inventory range.");
        }

        var topology = SetupComposeTopologySelector.ForMode(mode);
        if (topology == SetupComposeTopology.DeployWithMailpit
            && string.IsNullOrWhiteSpace(inventory.MailpitImageReference))
        {
            return SetupDockerResult.Fail(
                SetupDockerResultCode.InvalidBundleInventory,
                "Mode 1 requires a digest-pinned Mailpit image in the release inventory.");
        }

        var composePaths = new List<string> { deployCompose };
        var digestOverlay = Path.GetFullPath(
            Path.Combine(rootFull, SetupDockerInventory.ImageDigestOverlayRelativePath));
        if (!TryValidateComposeFile(fileSystem, rootFull, digestOverlay, out composeFailure))
        {
            return composeFailure!;
        }

        if (!DigestMatches(fileSystem, digestOverlay, inventory.ComposeImageDigestSha256))
        {
            return SetupDockerResult.Fail(
                SetupDockerResultCode.InvalidBundleInventory,
                "Trusted image-digest Compose overlay digest does not match the release inventory.");
        }

        composePaths.Add(digestOverlay);

        var recordedMetadataOverlay = Path.GetFullPath(
            Path.Combine(rootFull, SetupDockerInventory.RecordedMetadataOverlayRelativePath));
        if (!TryValidateComposeFile(fileSystem, rootFull, recordedMetadataOverlay, out composeFailure))
        {
            return composeFailure!;
        }

        if (!DigestMatches(fileSystem, recordedMetadataOverlay, inventory.ComposeRecordedMetadataSha256))
        {
            return SetupDockerResult.Fail(
                SetupDockerResultCode.InvalidBundleInventory,
                "Trusted recorded-metadata Compose overlay digest does not match the release inventory.");
        }

        composePaths.Add(recordedMetadataOverlay);

        if (topology == SetupComposeTopology.DeployWithMailpit)
        {
            var overlay = Path.GetFullPath(
                Path.Combine(rootFull, SetupDockerInventory.MailpitOverlayRelativePath));
            if (!TryValidateComposeFile(fileSystem, rootFull, overlay, out composeFailure))
            {
                return composeFailure!;
            }

            if (!DigestMatches(fileSystem, overlay, inventory.ComposeMailpitSha256))
            {
                return SetupDockerResult.Fail(
                    SetupDockerResultCode.InvalidBundleInventory,
                    "Trusted Mailpit Compose overlay digest does not match the release inventory.");
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

    public static SetupDockerResult TryResolveInstalled(
        ISetupFileSystem fileSystem,
        SetupMode mode,
        string deploymentIdentity,
        out TrustedSetupHostLayout? layout)
    {
        layout = null;
        ArgumentNullException.ThrowIfNull(fileSystem);

        var candidate = new DirectoryInfo(AppContext.BaseDirectory);
        while (candidate is not null)
        {
            var manifestPath = Path.Combine(candidate.FullName, TrustedReleaseInventory.ManifestFileName);
            if (fileSystem.FileExists(manifestPath))
            {
                return TryResolve(fileSystem, candidate.FullName, mode, deploymentIdentity, out layout);
            }

            candidate = candidate.Parent;
        }

        return SetupDockerResult.Fail(
            SetupDockerResultCode.InvalidBundleInventory,
            "Installed trusted release manifest was not found.");
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
        var rootFull = Path.GetFullPath(scratchRoot);
        Directory.CreateDirectory(rootFull);

        var composeBytes = Encoding.UTF8.GetBytes(deployComposeContents);
        var composeSha256 = "sha256:"
            + Convert.ToHexString(SHA256.HashData(composeBytes)).ToLowerInvariant();
        const string imageDigestOverlayContents =
            """
            services:
              mailer-migrate:
                image: ${MAILER_IMAGE_REFERENCE}
              mailer:
                image: ${MAILER_IMAGE_REFERENCE}
              mailer-acs-admin:
                image: ${MAILER_IMAGE_REFERENCE}
            """;
        const string recordedMetadataOverlayContents =
            """
            services:
              mailer:
                environment:
                  MAILER_SETUP_RECORDED_METADATA_PATH: /run/amane/setup/recorded.json
                volumes:
                  - ${MAILER_SETUP_RECORDED_METADATA_HOST_PATH}:/run/amane/setup/recorded.json:ro
            """;
        var imageDigestOverlaySha256 = ComputeDigest(imageDigestOverlayContents);
        var recordedMetadataOverlaySha256 = ComputeDigest(recordedMetadataOverlayContents);
        var topology = SetupComposeTopologySelector.ForMode(mode);
        string? mailpitOverlaySha256 = null;
        if (topology == SetupComposeTopology.DeployWithMailpit)
        {
            if (string.IsNullOrWhiteSpace(mailpitOverlayContents))
            {
                return SetupDockerResult.Fail(
                    SetupDockerResultCode.InvalidBundleInventory,
                    "Mode 1 test layout requires a Mailpit overlay.");
            }

            mailpitOverlaySha256 = ComputeDigest(mailpitOverlayContents);
        }

        if (!TryGetLauncherVersion(out var launcherVersion))
        {
            return SetupDockerResult.Fail(
                SetupDockerResultCode.InvalidBundleInventory,
                "Current launcher version could not be determined.");
        }

        var launcherVersionText =
            $"{launcherVersion.Major}.{launcherVersion.Minor}.{launcherVersion.Build}";
        var manifestPath = Path.Combine(rootFull, TrustedReleaseInventory.ManifestFileName);
        var manifest = new ReleaseBundleManifestDocument
        {
            SchemaVersion = TrustedReleaseInventory.CurrentSchemaVersion,
            ImageRepository = inventory.AllowedImageRepository,
            ImageDigest = inventory.RequiredImageDigest,
            ImageTag = inventory.AllowedDisplayTag,
            ComposeBundleVersion = inventory.ComposeBundleVersion,
            ComposeSha256 = composeSha256,
            ComposeImageDigestSha256 = imageDigestOverlaySha256,
            ComposeRecordedMetadataSha256 = recordedMetadataOverlaySha256,
            ComposeMailpitSha256 = mailpitOverlaySha256,
            LauncherVersionMin = launcherVersionText,
            LauncherVersionMax = launcherVersionText,
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
        File.WriteAllText(
            Path.Combine(rootFull, SetupDockerInventory.ImageDigestOverlayRelativePath),
            imageDigestOverlayContents);
        File.WriteAllText(
            Path.Combine(rootFull, SetupDockerInventory.RecordedMetadataOverlayRelativePath),
            recordedMetadataOverlayContents);

        if (topology == SetupComposeTopology.DeployWithMailpit)
        {
            File.WriteAllText(
                Path.Combine(rootFull, SetupDockerInventory.MailpitOverlayRelativePath),
                mailpitOverlayContents!);
        }

        return TryResolve(fileSystem, rootFull, mode, deploymentIdentity, out layout);

        static string ComputeDigest(string contents) =>
            "sha256:" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(contents))).ToLowerInvariant();
    }

    private static bool TryGetLauncherVersion(out Version version)
    {
        var assembly = typeof(TrustedSetupHostLayoutResolver).Assembly;
        var informational = assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()
            ?.InformationalVersion;
        if (TryParseVersion(informational, out version))
        {
            return true;
        }

        var assemblyVersion = assembly.GetName().Version;
        if (assemblyVersion is not null)
        {
            version = new Version(
                assemblyVersion.Major,
                assemblyVersion.Minor,
                Math.Max(assemblyVersion.Build, 0));
            return true;
        }

        version = new Version(0, 0, 0);
        return false;
    }

    private static bool TryParseVersion(string? value, out Version version)
    {
        version = new Version(0, 0, 0);
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var core = value.Split(['+', '-'], 2)[0];
        var parts = core.Split('.');
        if (parts.Length != 3
            || !int.TryParse(parts[0], out var major)
            || !int.TryParse(parts[1], out var minor)
            || !int.TryParse(parts[2], out var patch)
            || major < 0
            || minor < 0
            || patch < 0)
        {
            return false;
        }

        version = new Version(major, minor, patch);
        return true;
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

    private static bool DigestMatches(
        ISetupFileSystem fileSystem,
        string path,
        string? expectedDigest)
    {
        if (string.IsNullOrWhiteSpace(expectedDigest))
        {
            return false;
        }

        var actual = "sha256:"
            + Convert.ToHexString(SHA256.HashData(fileSystem.ReadAllBytes(path))).ToLowerInvariant();
        return string.Equals(expectedDigest, actual, StringComparison.Ordinal);
    }
}
