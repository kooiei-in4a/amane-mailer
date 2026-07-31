namespace Amane.Mailer.Setup;

/// <summary>
/// Maintainer/CI command that stages one Easy Setup release-candidate host tree (#455).
/// Does not tag, publish GHCR, or create a GitHub Release.
/// </summary>
public static class SetupStageReleaseBundleCommand
{
    public const int SuccessExitCode = 0;
    public const int FailureExitCode = 1;
    public const int UsageErrorExitCode = 2;

    public const string UsageLine =
        "setup stage-release-bundle --output <dir> --rid <win-x64|linux-x64|linux-arm64> "
        + "--host-binary <path> --source-sha <sha> --mailer-version <version> "
        + "--launcher-version <version> --image-repository <repo> --image-tag <tag> "
        + "--oci-index-digest <sha256:...> --deploy-compose <path> "
        + "--image-digest-overlay <path> --recorded-metadata-overlay <path> "
        + "--mailpit-overlay <path> --env-example <path> --tenants-example <path> "
        + "--tenants-schema <path> --tenants-local-acs-example <path> "
        + "[--mailpit-image <repo@sha256:...>] [--oci-layout <dir>] "
        + "[--project-name-prefix <prefix>]";

    public static bool IsStageReleaseBundleCommand(IReadOnlyList<string> args) =>
        args.Count >= 2
        && string.Equals(args[0], "setup", StringComparison.Ordinal)
        && string.Equals(args[1], "stage-release-bundle", StringComparison.Ordinal);

    public static bool TryParseArguments(
        IReadOnlyList<string> args,
        out ReleaseBundlePackaging.StageRequest? request,
        out string? usageError)
    {
        request = null;
        usageError = null;

        if (!IsStageReleaseBundleCommand(args))
        {
            usageError = "Not a setup stage-release-bundle command.";
            return false;
        }

        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var i = 2; i < args.Count; i++)
        {
            var arg = args[i];
            if (!arg.StartsWith("--", StringComparison.Ordinal))
            {
                usageError = "Unexpected positional argument for setup stage-release-bundle.";
                return false;
            }

            if (i + 1 >= args.Count || args[i + 1].StartsWith("--", StringComparison.Ordinal))
            {
                usageError = "Missing value for " + arg + ".";
                return false;
            }

            if (values.ContainsKey(arg))
            {
                usageError = "Duplicate option: " + arg + ".";
                return false;
            }

            if (!IsKnownFlag(arg))
            {
                usageError = "Unknown argument: " + arg;
                return false;
            }

            values[arg] = args[++i];
        }

        string[] required =
        [
            "--output",
            "--rid",
            "--host-binary",
            "--source-sha",
            "--mailer-version",
            "--launcher-version",
            "--image-repository",
            "--image-tag",
            "--oci-index-digest",
            "--deploy-compose",
            "--image-digest-overlay",
            "--recorded-metadata-overlay",
            "--mailpit-overlay",
            "--env-example",
            "--tenants-example",
            "--tenants-schema",
            "--tenants-local-acs-example",
        ];

        foreach (var flag in required)
        {
            if (!values.ContainsKey(flag))
            {
                usageError = "Missing required stage-release-bundle arguments.";
                return false;
            }
        }

        var rid = values["--rid"];
        if (!ReleaseBundlePackaging.IsSupportedHostRid(rid))
        {
            usageError = "Unsupported --rid. Use win-x64, linux-x64, or linux-arm64.";
            return false;
        }

        values.TryGetValue("--mailpit-image", out var mailpitImage);
        values.TryGetValue("--oci-layout", out var ociLayout);
        values.TryGetValue("--project-name-prefix", out var projectNamePrefix);

        request = new ReleaseBundlePackaging.StageRequest
        {
            OutputDirectory = values["--output"],
            HostRid = rid,
            HostBinaryPath = values["--host-binary"],
            SourceCommitSha = values["--source-sha"],
            MailerVersion = values["--mailer-version"],
            LauncherVersion = values["--launcher-version"],
            ImageRepository = values["--image-repository"],
            ImageDisplayTag = values["--image-tag"],
            OciIndexDigest = values["--oci-index-digest"],
            DeployComposePath = values["--deploy-compose"],
            ImageDigestOverlayPath = values["--image-digest-overlay"],
            RecordedMetadataOverlayPath = values["--recorded-metadata-overlay"],
            MailpitOverlayPath = values["--mailpit-overlay"],
            EnvExamplePath = values["--env-example"],
            TenantsExamplePath = values["--tenants-example"],
            TenantsSchemaPath = values["--tenants-schema"],
            TenantsLocalAcsExamplePath = values["--tenants-local-acs-example"],
            MailpitImageReference = mailpitImage,
            OciLayoutSourceDirectory = ociLayout,
            ProjectNamePrefix = string.IsNullOrWhiteSpace(projectNamePrefix) ? "amane" : projectNamePrefix,
        };
        return true;
    }

    public static Task<int> ExecuteAsync(
        ReleaseBundlePackaging.StageRequest request,
        TextWriter output,
        TextWriter error)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(error);

        var result = ReleaseBundlePackaging.Stage(request);
        if (!result.Success)
        {
            error.WriteLine(
                "setup stage-release-bundle failed: "
                + (result.ReasonCode ?? "unknown")
                + " — "
                + (result.Message ?? "staging failed."));
            return Task.FromResult(FailureExitCode);
        }

        output.WriteLine("setup stage-release-bundle: ok");
        output.WriteLine("output=" + result.OutputDirectory);
        output.WriteLine("manifest=" + result.ManifestPath);
        output.WriteLine("artifactSha256=" + result.ArtifactSha256);
        output.WriteLine("ociIndexDigest=" + result.Manifest?.OciIndexDigest);
        return Task.FromResult(SuccessExitCode);
    }

    private static bool IsKnownFlag(string arg) =>
        arg is "--output"
            or "--rid"
            or "--host-binary"
            or "--source-sha"
            or "--mailer-version"
            or "--launcher-version"
            or "--image-repository"
            or "--image-tag"
            or "--oci-index-digest"
            or "--deploy-compose"
            or "--image-digest-overlay"
            or "--recorded-metadata-overlay"
            or "--mailpit-overlay"
            or "--env-example"
            or "--tenants-example"
            or "--tenants-schema"
            or "--tenants-local-acs-example"
            or "--mailpit-image"
            or "--oci-layout"
            or "--project-name-prefix";
}
