namespace Amane.Mailer.Setup;

/// <summary>
/// Fixed Compose inventory for Easy Setup host Docker operations.
/// Separates services that may appear in files from services the adapter may operate.
/// </summary>
public static class SetupDockerInventory
{
    public const string DeployComposeRelativePath = "compose.yml";
    public const string MailpitOverlayRelativePath = "compose.mailpit.yml";
    public const string ReleaseManifestRelativePath = TrustedReleaseInventory.ManifestFileName;

    public const string ServiceMailer = "mailer";
    public const string ServiceMailerMigrate = "mailer-migrate";
    public const string ServiceMailerAcsAdmin = "mailer-acs-admin";

    public const string ProfileOps = "ops";
    public const string ProfileAcsAdmin = "acs-admin";

    public const string NetworkInternal = "internal";
    public const string NetworkMailer = "mailer";

    public const string ContainerVerifierMountPath = "/run/amane/setup/mount-verifier.json";
    public const string ContainerVerifierEnvKey = "MAILER_SETUP_MOUNT_VERIFIER_PATH";

    /// <summary>Services that may legally appear in trusted compose files.</summary>
    public static IReadOnlySet<string> KnownServices { get; } = new HashSet<string>(StringComparer.Ordinal)
    {
        ServiceMailer,
        ServiceMailerMigrate,
        ServiceMailerAcsAdmin,
    };

    /// <summary>Services this adapter is allowed to start/stop/run.</summary>
    public static IReadOnlySet<string> OperableServices { get; } = new HashSet<string>(StringComparer.Ordinal)
    {
        ServiceMailer,
        ServiceMailerMigrate,
    };

    public static IReadOnlySet<string> ForbiddenProfiles { get; } = new HashSet<string>(StringComparer.Ordinal)
    {
        ProfileAcsAdmin,
    };

    public static IReadOnlyList<string> ForbiddenArgumentTokens { get; } =
    [
        "down",
        "prune",
        "system",
        "volume",
        "rmi",
        "tag",
        "build",
        "-v",
        "--volumes",
        "latest",
    ];

    public static bool IsOperableService(string serviceName) =>
        OperableServices.Contains(serviceName);

    public static bool IsAcsAdminService(string serviceName) =>
        string.Equals(serviceName, ServiceMailerAcsAdmin, StringComparison.Ordinal);

    /// <summary>
    /// Builds a deterministic Compose project name from the trusted prefix and deployment identity.
    /// Never accepts an operator-typed project name.
    /// </summary>
    public static string BuildProjectName(string projectNamePrefix, string deploymentIdentity)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectNamePrefix);
        ArgumentException.ThrowIfNullOrWhiteSpace(deploymentIdentity);

        var normalized = new string(deploymentIdentity
            .Where(static c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_')
            .Take(24)
            .ToArray())
            .ToLowerInvariant();
        if (normalized.Length == 0)
        {
            normalized = "dep";
        }

        return $"{projectNamePrefix}-{normalized}";
    }
}

public enum SetupComposeTopology
{
    DeployOnly = 0,
    DeployWithMailpit = 1,
}

public static class SetupComposeTopologySelector
{
    public static SetupComposeTopology ForMode(SetupMode mode) => mode switch
    {
        SetupMode.LocalMailpit => SetupComposeTopology.DeployWithMailpit,
        _ => SetupComposeTopology.DeployOnly,
    };
}
