using System.Text.RegularExpressions;

namespace Amane.Mailer.Setup;

/// <summary>
/// Physical Managed bundle layout from ADR 0021 D-03 (not the illustrative layout in issue #448).
/// </summary>
public static partial class SetupBundleLayout
{
    public const int MinimumSupportedRecordedSchemaVersion = 1;
    public const int RecordedSchemaVersion = 2;
    public const string BundlesDirectoryName = "bundles";
    public const string StateDirectoryName = "state";
    public const string SealingDirectoryName = "sealing";
    public const string ConfigDirectoryName = "config";
    public const string SecretsDirectoryName = "secrets";
    public const string EnvDirectoryName = "env";
    public const string MetadataDirectoryName = "metadata";
    public const string VerificationDirectoryName = "verification";
    public const string VerifierTempDirectoryName = "tmp";
    public const string ComposeEnvFileName = "compose.env";
    public const string SecretsEnvFileName = "secrets.env";
    public const string RecordedMetadataFileName = "recorded.json";
    public const string IntegritySealFileName = "integrity.seal";
    public const string FinalizedMarkerFileName = "FINALIZED";
    public const string ActivePointerFileName = "ACTIVE";
    public const string PreviousPointerFileName = "PREVIOUS";
    public const string TransactionStampFileName = "TX.stamp";
    public const string LastRecordFileName = "last-record.json";
    public const string RuntimeIdentityBindFileName = "runtime-identity.bind";
    public const string AdminBootstrapCurrentFileName = "admin-bootstrap-current.json";
    public const string AdminBootstrapPendingFileName = "admin-bootstrap-pending.json";
    public const string MountVerifierFileNamePrefix = "mount-verifier-";
    public const string MountVerifierFileNameSuffix = ".json";
    public const string ApplyLockFileName = SetupApplyLock.LockFileName;
    public const string ExternalEnvFileName = TrustedSetupHostLayoutResolver.ExternalEnvFileName;
    public const string HostSealingKeyFileName = "host-sealing-key";
    public const string TenantsFileName = "tenants.json";
    public const string ContainerRecordedMetadataPath = "/run/amane/setup/recorded.json";
    public const string ContainerTenantsPath = "/app/config/mailer/tenants.json";
    public const string ContainerAcsSecretsPath = "/run/secrets/acs";

    public static string BundleRoot(string managedRoot, string bundleId) =>
        Path.Combine(managedRoot, BundlesDirectoryName, bundleId);

    public static string ConfigDir(string bundleRoot) => Path.Combine(bundleRoot, ConfigDirectoryName);

    public static string SecretsDir(string bundleRoot) => Path.Combine(bundleRoot, SecretsDirectoryName);

    public static string EnvDir(string bundleRoot) => Path.Combine(bundleRoot, EnvDirectoryName);

    public static string MetadataDir(string bundleRoot) => Path.Combine(bundleRoot, MetadataDirectoryName);

    public static string SealingDir(string managedRoot) => Path.Combine(managedRoot, SealingDirectoryName);

    public static string HostSealingKeyPath(string managedRoot) =>
        Path.Combine(SealingDir(managedRoot), HostSealingKeyFileName);

    public static string StateDir(string managedRoot) => Path.Combine(managedRoot, StateDirectoryName);

    public static string ActivePointerPath(string managedRoot) =>
        Path.Combine(StateDir(managedRoot), ActivePointerFileName);

    public static string PreviousPointerPath(string managedRoot) =>
        Path.Combine(StateDir(managedRoot), PreviousPointerFileName);

    public static string TransactionStampPath(string managedRoot) =>
        Path.Combine(StateDir(managedRoot), TransactionStampFileName);

    public static string VerificationDir(string managedRoot) =>
        Path.Combine(managedRoot, VerificationDirectoryName);

    public static string LastRecordPath(string managedRoot) =>
        Path.Combine(VerificationDir(managedRoot), LastRecordFileName);

    public static string RuntimeIdentityBindPath(string managedRoot) =>
        Path.Combine(VerificationDir(managedRoot), RuntimeIdentityBindFileName);

    public static string AdminBootstrapCurrentPath(string managedRoot) =>
        Path.Combine(StateDir(managedRoot), AdminBootstrapCurrentFileName);

    public static string AdminBootstrapPendingPath(string managedRoot) =>
        Path.Combine(StateDir(managedRoot), AdminBootstrapPendingFileName);

    public static bool IsSupportedRecordedSchemaVersion(int schemaVersion) =>
        schemaVersion is >= MinimumSupportedRecordedSchemaVersion and <= RecordedSchemaVersion;

    public static string VerifierTempDir(string managedRoot) =>
        Path.Combine(managedRoot, VerifierTempDirectoryName);

    /// <summary>
    /// Only <c>mount-verifier-&lt;32 lowercase hex&gt;.json</c> may live under <c>managed/tmp</c>.
    /// Anything else is treated as unsafe residue by the apply/recovery paths.
    /// </summary>
    [GeneratedRegex(
        "^mount-verifier-[0-9a-f]{32}\\.json$",
        RegexOptions.CultureInvariant)]
    public static partial Regex MountVerifierFileNamePattern();

    public static bool IsMountVerifierFileName(string fileName) =>
        !string.IsNullOrEmpty(fileName) && MountVerifierFileNamePattern().IsMatch(fileName);

    public static string BuildMountVerifierFileName(string lowercaseHexToken) =>
        MountVerifierFileNamePrefix + lowercaseHexToken + MountVerifierFileNameSuffix;

    public static string MountVerifierPath(string managedRoot, string lowercaseHexToken) =>
        Path.Combine(VerifierTempDir(managedRoot), BuildMountVerifierFileName(lowercaseHexToken));
}
