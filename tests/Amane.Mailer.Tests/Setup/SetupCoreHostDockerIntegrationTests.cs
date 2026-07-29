using Amane.Mailer.Configuration;
using Amane.Mailer.Operations;
using Amane.Mailer.Setup;

namespace Amane.Mailer.Tests.Setup;

public sealed class SetupCoreHostDockerIntegrationTests
{
    private const string TestDigest =
        "sha256:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";

    [Theory]
    [InlineData(SetupMode.LocalMailpit)]
    [InlineData(SetupMode.StagingNoSend)]
    [InlineData(SetupMode.StagingVerification)]
    [InlineData(SetupMode.ProductionAcs)]
    public async Task Generated_bundle_validates_through_host_adapter(SetupMode mode)
    {
        var root = Path.Combine(Path.GetTempPath(), "amane-core-host-" + Guid.NewGuid().ToString("N"));
        try
        {
            var fileSystem = new HostSetupFileSystem();
            var inventory = CreateInventory(mode == SetupMode.LocalMailpit);
            var layoutResult = TrustedSetupHostLayoutResolver.CreateLayoutForTests(
                fileSystem,
                root,
                mode,
                inventory,
                "integration",
                MinimalCompose,
                mode == SetupMode.LocalMailpit
                    ? "services:\n  mailpit:\n    image: ${MAILPIT_IMAGE}\n"
                    : null,
                out var layout);
            Assert.True(layoutResult.IsSuccess);
            Assert.NotNull(layout);

            const string bundleId = "bundle-integration";
            var request = CreateRequest(layout!.ManagedRoot, mode);
            var generated = new SetupCore(bundleIdFactory: static () => bundleId).GenerateBundle(request);
            Assert.Equal(SetupResultCode.Succeeded, generated.Code);

            Directory.CreateDirectory(layout.StatePath);
            File.WriteAllText(
                layout.ActivePointerPath,
                $"{{\"bundleId\":\"{bundleId}\",\"activationGeneration\":1,\"schemaVersion\":1}}\n");
            var dataPath = Path.Combine(layout.ManagedRoot, "data");
            Directory.CreateDirectory(dataPath);
            File.WriteAllText(layout.ExternalEnvPath, $"MAILER_DATA_PATH={dataPath}\n");

            var runner = new DockerFixtureRunner();
            var probe = new DockerEnvironmentProbe(
                runner,
                getDockerHost: static () => null,
                getDockerContextEnv: static () => null,
                resolveDockerExecutable: static () => "docker");
            var adapter = new SetupHostDockerAdapter(fileSystem, runner, probe);
            var (probeResult, binding) = await adapter.CheckDockerAsync(CancellationToken.None);
            Assert.True(probeResult.IsSuccess);
            Assert.NotNull(binding);

            var (sessionResult, session) = await adapter.AcquireSessionAsync(
                layout,
                binding!,
                CancellationToken.None);
            Assert.True(sessionResult.IsSuccess);
            Assert.NotNull(session);
            await using (session!)
            {
                var validate = await adapter.ValidateComposeAsync(session, CancellationToken.None);
                Assert.True(validate.IsSuccess);
            }

            var bundleRoot = Path.GetFullPath(SetupBundleLayout.BundleRoot(layout.ManagedRoot, bundleId));
            Assert.True(Directory.Exists(Path.Combine(bundleRoot, "secrets")));
            Assert.True(Directory.Exists(Path.Combine(bundleRoot, "secrets", "bounce-queue")));
            Assert.Equal(inventory.PinnedMailerImageReference, runner.LastEnvironment["MAILER_IMAGE_REFERENCE"]);
            Assert.Equal(
                Path.Combine(bundleRoot, "secrets"),
                runner.LastEnvironment["MAILER_ACS_SECRET_HOST_PATH"]);
            Assert.Equal(
                Path.Combine(bundleRoot, "secrets", "bounce-queue"),
                runner.LastEnvironment["MAILER_BOUNCE_QUEUE_SECRET_HOST_PATH"]);
            Assert.Equal(
                Path.Combine(bundleRoot, "metadata", SetupBundleLayout.RecordedMetadataFileName),
                runner.LastEnvironment["MAILER_SETUP_RECORDED_METADATA_HOST_PATH"]);
            if (mode == SetupMode.LocalMailpit)
            {
                Assert.False(
                    File.Exists(
                        Path.Combine(bundleRoot, "secrets", AcsSecretFileNames.CanonicalFileName)));
            }
            else
            {
                Assert.True(
                    File.Exists(
                        Path.Combine(bundleRoot, "secrets", AcsSecretFileNames.CanonicalFileName)));
            }
        }
        finally
        {
            try
            {
                Directory.Delete(root, recursive: true);
            }
            catch
            {
                // Best-effort temp cleanup.
            }
        }
    }

    private static SetupRequest CreateRequest(string managedRoot, SetupMode mode)
    {
        if (mode == SetupMode.LocalMailpit)
        {
            return SetupTestFixtures.LocalMailpitRequest(managedRoot);
        }

        var environment = SetupRequestValidator.ExpectedEnvironment(mode);
        var staging = SetupTestFixtures.StagingAcsRequest(managedRoot);
        var stagingTenant = staging.Tenants.Tenants[0];
        return new SetupRequest
        {
            Mode = mode,
            ManagedRootPath = managedRoot,
            DryRun = false,
            Tenants = new MailerTenantsFile
            {
                Version = staging.Tenants.Version,
                Environment = environment,
                Tenants =
                [
                    stagingTenant with
                    {
                        Name = environment == "production" ? "example-production" : stagingTenant.Name,
                    },
                ],
            },
            TokenSecrets = staging.TokenSecrets,
            MetricsBearerToken = staging.MetricsBearerToken,
            AcsConnectionString = staging.AcsConnectionString,
            PlatformSender = new SetupPlatformSenderInput
            {
                Environment = environment,
                Email = staging.PlatformSender!.Email,
                DisplayName = staging.PlatformSender.DisplayName,
            },
            ImageRepository = staging.ImageRepository,
            ImageTag = staging.ImageTag,
            RuntimeFileOwnership = staging.RuntimeFileOwnership,
        };
    }

    private static TrustedReleaseInventory CreateInventory(bool includeMailpit) =>
        new()
        {
            AllowedImageRepository = SetupImageDefaults.DefaultRepository,
            RequiredImageDigest = TestDigest,
            AllowedDisplayTag = "test-synthetic-image-tag",
            ComposeBundleVersion = "1",
            LauncherVersionMin = "1.0.0",
            LauncherVersionMax = "1.0.0",
            ProjectNamePrefix = "amane",
            MailpitImageReference = includeMailpit ? "axllent/mailpit@" + TestDigest : null,
        };

    private const string MinimalCompose =
        """
        services:
          mailer-migrate:
            image: ${MAILER_IMAGE_REPOSITORY}:${MAILER_IMAGE_TAG}
            profiles: [ops]
          mailer:
            image: ${MAILER_IMAGE_REPOSITORY}:${MAILER_IMAGE_TAG}
          mailer-acs-admin:
            image: ${MAILER_IMAGE_REPOSITORY}:${MAILER_IMAGE_TAG}
            profiles: [acs-admin]
        """;

    private sealed class DockerFixtureRunner : IHostProcessRunner
    {
        public Dictionary<string, string?> LastEnvironment { get; private set; } =
            new(StringComparer.Ordinal);

        public Task<HostProcessResult> RunAsync(
            HostProcessSpec spec,
            CancellationToken cancellationToken)
        {
            LastEnvironment = new Dictionary<string, string?>(spec.Environment, StringComparer.Ordinal);
            var joined = string.Join(' ', spec.ArgumentList);
            var stdout = joined.Contains("context show", StringComparison.Ordinal)
                ? "default\n"
                : joined.Contains("context inspect", StringComparison.Ordinal)
                    ? OperatingSystem.IsWindows()
                        ? "{\"Endpoints\":{\"docker\":{\"Host\":\"npipe:////./pipe/docker_engine\"}}}"
                        : "{\"Endpoints\":{\"docker\":{\"Host\":\"unix:///var/run/docker.sock\"}}}"
                    : joined.Contains("version --format", StringComparison.Ordinal)
                        ? "27.0.0\n"
                        : joined.Contains("compose version", StringComparison.Ordinal)
                            ? "v2.29.0\n"
                            : string.Empty;
            return Task.FromResult(new HostProcessResult
            {
                Outcome = HostProcessOutcome.Completed,
                ExitCode = 0,
                StandardOutput = stdout,
                StandardError = string.Empty,
            });
        }
    }
}
