namespace Amane.Mailer.Setup;

/// <summary>
/// AOT construction self-check for the host Docker adapter.
/// Proves type activation, argv construction, result mapping, and serializer paths with a fake
/// process runner. Does not prove live Process.Start, stream drain, kill tree, or real Docker.
/// </summary>
public static class SetupHostDockerSelfCheckCommand
{
    public const int SuccessExitCode = 0;
    public const int FailureExitCode = 1;
    public const int UsageErrorExitCode = 2;

    public static bool IsSelfCheckCommand(IReadOnlyList<string> args) =>
        args.Count == 2
        && string.Equals(args[0], "setup", StringComparison.Ordinal)
        && string.Equals(args[1], "host-docker-self-check", StringComparison.Ordinal);

    public static async Task<int> ExecuteAsync(TextWriter output, TextWriter error)
    {
        try
        {
            var remote = DockerEnvironmentProbe.ClassifyDockerHost("tcp://127.0.0.1:2375");
            if (remote.Code != SetupDockerResultCode.RemoteDockerRejected)
            {
                error.WriteLine("setup host-docker-self-check failed: remote DOCKER_HOST was not rejected.");
                return FailureExitCode;
            }

            var unknown = DockerEnvironmentProbe.ClassifyDockerHost("fd://unexpected");
            if (unknown.Code != SetupDockerResultCode.UnsupportedDockerEnvironment)
            {
                error.WriteLine("setup host-docker-self-check failed: unknown DOCKER_HOST was not fail-closed.");
                return FailureExitCode;
            }

            var inventory = CreateSelfCheckInventory();
            var shape = inventory.ValidateShape();
            if (shape is not null)
            {
                error.WriteLine("setup host-docker-self-check failed: inventory shape invalid.");
                return FailureExitCode;
            }

            var fileSystem = new HostSetupFileSystem();
            var scratch = Path.GetFullPath(
                Path.Combine(Path.GetTempPath(), "amane-host-docker-self-check-" + Guid.NewGuid().ToString("N")));
            Directory.CreateDirectory(scratch);
            try
            {
                var layoutResult = TrustedSetupHostLayoutResolver.CreateLayoutForTests(
                    fileSystem,
                    scratch,
                    SetupMode.StagingNoSend,
                    inventory,
                    "selfcheck01",
                    MinimalDeployCompose,
                    mailpitOverlayContents: null,
                    out var layout);
                if (!layoutResult.IsSuccess || layout is null)
                {
                    error.WriteLine("setup host-docker-self-check failed: layout resolution failed.");
                    return FailureExitCode;
                }

                var fake = new SelfCheckProcessRunner();
                var probe = new DockerEnvironmentProbe(
                    fake,
                    getDockerHost: static () => null,
                    getDockerContextEnv: static () => null,
                    resolveDockerExecutable: () => Path.Combine(scratch, "docker-fake"));
                var adapter = new SetupHostDockerAdapter(fileSystem, fake, probe);
                var (probeResult, binding) = await adapter.CheckDockerAsync(CancellationToken.None);
                if (!probeResult.IsSuccess || binding is null)
                {
                    error.WriteLine("setup host-docker-self-check failed: fake preflight failed.");
                    return FailureExitCode;
                }

                // Prepare managed state + ACTIVE so compose env composition can succeed.
                Directory.CreateDirectory(layout.ManagedRoot);
                Directory.CreateDirectory(layout.StatePath);
                File.WriteAllText(
                    layout.ExternalEnvPath,
                    "MAILER_DATA_PATH=/tmp/amane-self-check-data\n");
                SeedActiveBundle(layout);

                var (sessionResult, session) = await adapter.AcquireSessionAsync(
                    layout,
                    binding,
                    CancellationToken.None);
                if (!sessionResult.IsSuccess || session is null)
                {
                    error.WriteLine("setup host-docker-self-check failed: session acquire failed.");
                    return FailureExitCode;
                }

                await using (session)
                {
                    var prefix = SetupHostDockerAdapter.BuildComposeArgPrefix(session);
                    if (prefix.Contains("down", StringComparer.Ordinal)
                        || prefix.Contains("--volumes", StringComparer.Ordinal))
                    {
                        error.WriteLine("setup host-docker-self-check failed: destructive tokens in compose prefix.");
                        return FailureExitCode;
                    }

                    var validate = await adapter.ValidateComposeAsync(session, CancellationToken.None);
                    if (!validate.IsSuccess)
                    {
                        error.WriteLine("setup host-docker-self-check failed: validate compose failed.");
                        return FailureExitCode;
                    }

                    var staging = await adapter.ExecuteStagingVerificationAsync(
                        session,
                        CancellationToken.None);
                    if (staging.Code != SetupDockerResultCode.OperationNotAvailable)
                    {
                        error.WriteLine("setup host-docker-self-check failed: staging boundary incorrect.");
                        return FailureExitCode;
                    }

                    var pullArgs = SetupHostDockerAdapter.PrefixedDockerArgs(
                        session,
                        ["pull", layout.ReleaseInventory.PinnedMailerImageReference]);
                    if (!pullArgs[0].Equals("--context", StringComparison.Ordinal)
                        || pullArgs.Any(static a => a.Equals("latest", StringComparison.OrdinalIgnoreCase)))
                    {
                        error.WriteLine("setup host-docker-self-check failed: pull argv construction invalid.");
                        return FailureExitCode;
                    }
                }

                // Serializer path for release manifest.
                _ = System.Text.Json.JsonSerializer.Serialize(
                    new ReleaseBundleManifestDocument
                    {
                        SchemaVersion = TrustedReleaseInventory.CurrentSchemaVersion,
                        ImageRepository = inventory.AllowedImageRepository,
                        ImageDigest = inventory.RequiredImageDigest,
                        ImageTag = inventory.AllowedDisplayTag,
                        ComposeBundleVersion = inventory.ComposeBundleVersion,
                        LauncherVersionMin = inventory.LauncherVersionMin,
                        LauncherVersionMax = inventory.LauncherVersionMax,
                        ProjectNamePrefix = inventory.ProjectNamePrefix,
                    },
                    SetupHostDockerJsonContext.Default.ReleaseBundleManifestDocument);
            }
            finally
            {
                try
                {
                    Directory.Delete(scratch, recursive: true);
                }
                catch
                {
                    // Best-effort temp cleanup.
                }
            }

            output.WriteLine("setup host-docker-self-check: ok");
            return SuccessExitCode;
        }
        catch (Exception)
        {
            error.WriteLine("setup host-docker-self-check failed: unexpected error.");
            return FailureExitCode;
        }
    }

    private static TrustedReleaseInventory CreateSelfCheckInventory() =>
        new()
        {
            AllowedImageRepository = "ghcr.io/kooiei-in4a/amane-mailer",
            RequiredImageDigest =
                "sha256:0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef",
            AllowedDisplayTag = "sha-selfcheck",
            ComposeBundleVersion = "1",
            LauncherVersionMin = "1.2.0",
            LauncherVersionMax = "1.2.0",
            ProjectNamePrefix = "amane",
        };

    private const string MinimalDeployCompose =
        """
        name: amane-self-check
        services:
          mailer-migrate:
            image: ${MAILER_IMAGE_REPOSITORY}:${MAILER_IMAGE_TAG}
            profiles: [ops]
            command: ["db", "migrate"]
          mailer:
            image: ${MAILER_IMAGE_REPOSITORY}:${MAILER_IMAGE_TAG}
          mailer-acs-admin:
            image: ${MAILER_IMAGE_REPOSITORY}:${MAILER_IMAGE_TAG}
            profiles: [acs-admin]
        """;

    private static void SeedActiveBundle(TrustedSetupHostLayout layout)
    {
        const string bundleId = "bundle-selfcheck01";
        var bundleRoot = SetupBundleLayout.BundleRoot(layout.ManagedRoot, bundleId);
        Directory.CreateDirectory(SetupBundleLayout.EnvDir(bundleRoot));
        File.WriteAllText(
            Path.Combine(SetupBundleLayout.EnvDir(bundleRoot), SetupBundleLayout.ComposeEnvFileName),
            $"MAILER_IMAGE_REPOSITORY={layout.ReleaseInventory.AllowedImageRepository}\n"
            + $"MAILER_IMAGE_TAG={layout.ReleaseInventory.AllowedDisplayTag}\n"
            + "MAILER_PROVIDER=mailpit\n");
        File.WriteAllText(
            Path.Combine(SetupBundleLayout.EnvDir(bundleRoot), SetupBundleLayout.SecretsEnvFileName),
            "MAIL_SERVICE_TOKEN=synthetic-self-check-token-not-real\n");
        File.WriteAllText(layout.ActivePointerPath, $"{{\"bundleId\":\"{bundleId}\",\"activationGeneration\":1,\"schemaVersion\":1}}\n");
    }

    private sealed class SelfCheckProcessRunner : IHostProcessRunner
    {
        public Task<HostProcessResult> RunAsync(HostProcessSpec spec, CancellationToken cancellationToken)
        {
            var args = string.Join(' ', spec.ArgumentList);
            if (args.Contains("context show", StringComparison.Ordinal))
            {
                return Task.FromResult(new HostProcessResult
                {
                    Outcome = HostProcessOutcome.Completed,
                    ExitCode = 0,
                    StandardOutput = "default\n",
                });
            }

            if (args.Contains("context inspect", StringComparison.Ordinal))
            {
                var endpoint = OperatingSystem.IsWindows()
                    ? "npipe:////./pipe/docker_engine"
                    : "unix:///var/run/docker.sock";
                return Task.FromResult(new HostProcessResult
                {
                    Outcome = HostProcessOutcome.Completed,
                    ExitCode = 0,
                    StandardOutput =
                        $"{{\"Endpoints\":{{\"docker\":{{\"Host\":\"{endpoint}\"}}}}}}",
                });
            }

            if (args.Contains("version --format", StringComparison.Ordinal))
            {
                return Task.FromResult(new HostProcessResult
                {
                    Outcome = HostProcessOutcome.Completed,
                    ExitCode = 0,
                    StandardOutput = "27.0.0\n",
                });
            }

            if (args.Contains("compose version", StringComparison.Ordinal))
            {
                return Task.FromResult(new HostProcessResult
                {
                    Outcome = HostProcessOutcome.Completed,
                    ExitCode = 0,
                    StandardOutput = "v2.29.0\n",
                });
            }

            // compose config / other mutating fakes
            return Task.FromResult(new HostProcessResult
            {
                Outcome = HostProcessOutcome.Completed,
                ExitCode = 0,
                StandardOutput = string.Empty,
            });
        }
    }
}
