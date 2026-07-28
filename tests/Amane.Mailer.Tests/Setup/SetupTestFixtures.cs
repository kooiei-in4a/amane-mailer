using Amane.Mailer.Configuration;
using Amane.Mailer.Setup;
using Amane.Mailer.Tests.TestSupport;

namespace Amane.Mailer.Tests.Setup;

internal static class SetupTestFixtures
{
    public static SetupRuntimeFileOwnership? LinuxRuntimeOwnershipOrNull()
    {
        if (!OperatingSystem.IsLinux())
        {
            return null;
        }

        // Use the current non-root euid when available so chown is a no-op identity assign.
        // Tests that need a distinct container UID should set ownership explicitly.
        var euid = new HostSetupFileSystem().GetEffectiveUnixUserId() ?? 1654u;
        if (euid == 0)
        {
            euid = 1654;
        }

        return new SetupRuntimeFileOwnership { UnixUserId = euid, UnixGroupId = euid };
    }

    public static MailerTenantsFile LocalMailpitTenants() => new()
    {
        Version = 1,
        Environment = "develop",
        Tenants =
        [
            new MailerTenant
            {
                TenantId = Guid.Parse("00000000-0000-0000-0000-000000000101"),
                Name = "example-develop",
                SourceServices = ["example-service"],
                DefaultFrom = new MailerAddress
                {
                    Email = "noreply@example.com",
                    DisplayName = "Example Service",
                },
                TokenEnv = "MAIL_SERVICE_TOKEN",
                Provider = "mailpit",
                LiveSending = false,
                Retry = new MailerRetryOptions
                {
                    MaxAttempts = 10,
                    InitialDelaySeconds = 10,
                    MaxDelaySeconds = 300,
                },
            },
        ],
    };

    public static MailerTenantsFile AcsStagingTenants() => new()
    {
        Version = 1,
        Environment = "staging",
        Tenants =
        [
            new MailerTenant
            {
                TenantId = Guid.Parse("00000000-0000-0000-0000-000000000201"),
                Name = "example-staging",
                SourceServices = ["example-service"],
                DefaultFrom = new MailerAddress
                {
                    Email = "noreply@example.com",
                    DisplayName = "Example Service",
                },
                TokenEnv = "MAIL_SERVICE_TOKEN_STAGING",
                Provider = "acs",
                LiveSending = false,
                Retry = new MailerRetryOptions
                {
                    MaxAttempts = 10,
                    InitialDelaySeconds = 10,
                    MaxDelaySeconds = 300,
                },
            },
        ],
    };

    public static SetupRequest LocalMailpitRequest(string managedRoot, bool dryRun = false) => new()
    {
        Mode = SetupMode.LocalMailpit,
        ManagedRootPath = managedRoot,
        DryRun = dryRun,
        Tenants = LocalMailpitTenants(),
        TokenSecrets = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["MAIL_SERVICE_TOKEN"] = "synthetic-mail-token-not-real",
        },
        MetricsBearerToken = "synthetic-metrics-token-not-real",
        RuntimeFileOwnership = dryRun ? null : LinuxRuntimeOwnershipOrNull(),
    };

    public static SetupRequest StagingAcsRequest(string managedRoot, bool dryRun = false) => new()
    {
        Mode = SetupMode.StagingNoSend,
        ManagedRootPath = managedRoot,
        DryRun = dryRun,
        Tenants = AcsStagingTenants(),
        TokenSecrets = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["MAIL_SERVICE_TOKEN_STAGING"] = "synthetic-staging-token-not-real",
        },
        MetricsBearerToken = "synthetic-metrics-token-not-real",
        AcsConnectionString =
            "endpoint=https://synthetic.example.communication.azure.com/;accesskey=SYNTHETICACCESSKEY000000000000000000000000000000=",
        PlatformSender = new SetupPlatformSenderInput
        {
            Environment = "staging",
            Email = "platform@example.com",
            DisplayName = "Platform Sender",
        },
        RuntimeFileOwnership = dryRun ? null : LinuxRuntimeOwnershipOrNull(),
    };

    public static string CreateManagedRoot()
    {
        var path = Path.Combine(Path.GetTempPath(), "amane-setup-core-" + Guid.NewGuid().ToString("N"));
        TestSecretDirectory.CreateSecure(path);
        return path;
    }
}
