namespace Amane.Mailer.Setup;

/// <summary>
/// Physical Managed bundle layout from ADR 0021 D-03 (not the illustrative layout in issue #448).
/// </summary>
public static class SetupBundleLayout
{
    public const int RecordedSchemaVersion = 1;
    public const string BundlesDirectoryName = "bundles";
    public const string StateDirectoryName = "state";
    public const string SealingDirectoryName = "sealing";
    public const string ConfigDirectoryName = "config";
    public const string SecretsDirectoryName = "secrets";
    public const string EnvDirectoryName = "env";
    public const string MetadataDirectoryName = "metadata";
    public const string ComposeEnvFileName = "compose.env";
    public const string SecretsEnvFileName = "secrets.env";
    public const string RecordedMetadataFileName = "recorded.json";
    public const string IntegritySealFileName = "integrity.seal";
    public const string FinalizedMarkerFileName = "FINALIZED";
    public const string ActivePointerFileName = "ACTIVE";
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
}
