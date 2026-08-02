using System.Text.Json;
using Amane.Mailer.ReleaseBundle;

if (args.Length == 0 || IsHelp(args[0]))
{
    PrintUsage();
    return args.Length == 0 ? 2 : 0;
}

return args[0] switch
{
    "stage" => RunStage(args.AsSpan(1)),
    "validate-oci" => RunValidateOci(args.AsSpan(1)),
    "assert-binary-version" => RunAssertBinaryVersion(args.AsSpan(1)),
    "assert-image-identity" => RunAssertImageIdentity(args.AsSpan(1)),
    "write-image-identity" => RunWriteImageIdentity(args.AsSpan(1)),
    "write-provenance" => RunWriteProvenance(args.AsSpan(1)),
    _ => Unknown(args[0]),
};

static int Unknown(string command)
{
    Console.Error.WriteLine("Unknown command: " + command);
    PrintUsage();
    return 2;
}

static bool IsHelp(string value) =>
    value is "-h" or "--help" or "help";

static void PrintUsage()
{
    Console.Out.WriteLine(
        """
        Amane.Mailer.ReleaseBundle — build-only Easy Setup candidate packaging (#455)

        Commands:
          stage --output <dir> --staging-parent <dir> --rid <rid> ...
          validate-oci --layout <dir> --image-digest <sha256:...> [--require-platforms linux/amd64,linux/arm64] [--metadata-file <buildx.json>]
          assert-binary-version --binary <path> --expected-core <major.minor.patch>
          assert-image-identity --identity <file> --source-sha <sha> --mailer-version <ver>
          write-image-identity --output <file> --repository <repo> --tag <tag> --digest <sha256:...> --source-sha <sha> --mailer-version <ver> --platforms <csv>
          write-provenance --output <file> --handoff <file> --sums <file> ...

        Product Amane.Mailer does not expose stage-release-bundle.
        """);
}

static int RunStage(ReadOnlySpan<string> args)
{
    if (!TryParseKv(args, out var values, out var error))
    {
        Console.Error.WriteLine(error);
        return 2;
    }

    string[] required =
    [
        "--output",
        "--staging-parent",
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
        "--license",
        "--mailpit-image",
    ];

    foreach (var flag in required)
    {
        if (!values.ContainsKey(flag))
        {
            Console.Error.WriteLine("Missing required argument: " + flag);
            return 2;
        }
    }

    var rid = values["--rid"];
    if (!ReleaseBundlePackaging.IsSupportedHostRid(rid))
    {
        Console.Error.WriteLine("Unsupported --rid. Use win-x64, linux-x64, or linux-arm64.");
        return 2;
    }

    if (!ReleaseBundlePackaging.IsValidReleaseVersion(values["--mailer-version"]))
    {
        Console.Error.WriteLine("release_version / --mailer-version must be major.minor.patch only.");
        return 2;
    }

    if (!ReleaseBundlePackaging.IsValidMailpitImageReference(values["--mailpit-image"]))
    {
        Console.Error.WriteLine("--mailpit-image must be repo@sha256:<64 lowercase hex>.");
        return 2;
    }

    values.TryGetValue("--project-name-prefix", out var prefix);
    var assertVersion = !values.ContainsKey("--skip-binary-version-assert");

    var request = new ReleaseBundlePackaging.StageRequest
    {
        OutputDirectory = values["--output"],
        StagingParentDirectory = values["--staging-parent"],
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
        LicensePath = values["--license"],
        MailpitImageReference = values["--mailpit-image"],
        ProjectNamePrefix = string.IsNullOrWhiteSpace(prefix) ? "amane" : prefix,
        AssertHostBinaryVersion = assertVersion,
    };

    var result = ReleaseBundlePackaging.Stage(request);
    if (!result.Success)
    {
        Console.Error.WriteLine(
            "stage failed: " + (result.ReasonCode ?? "unknown") + " — " + (result.Message ?? "staging failed."));
        return 1;
    }

    Console.Out.WriteLine("stage: ok");
    Console.Out.WriteLine("output=" + result.OutputDirectory);
    Console.Out.WriteLine("manifest=" + result.ManifestPath);
    Console.Out.WriteLine("payloadTreeSha256=" + result.PayloadTreeSha256);
    Console.Out.WriteLine("ociIndexDigest=" + result.Manifest?.OciIndexDigest);
    return 0;
}

static int RunValidateOci(ReadOnlySpan<string> args)
{
    if (!TryParseKv(args, out var values, out var error))
    {
        Console.Error.WriteLine(error);
        return 2;
    }

    if (!values.ContainsKey("--layout") || !values.ContainsKey("--image-digest"))
    {
        Console.Error.WriteLine("validate-oci requires --layout and --image-digest.");
        return 2;
    }

    string[] platforms = ReleaseBundlePackaging.RequiredOciPlatforms;
    if (values.TryGetValue("--require-platforms", out var csv) && !string.IsNullOrWhiteSpace(csv))
    {
        platforms = csv.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }

    OciDescriptor? expectedDescriptor = null;
    if (values.TryGetValue("--metadata-file", out var metadataPath) && !string.IsNullOrWhiteSpace(metadataPath))
    {
        if (!File.Exists(metadataPath))
        {
            Console.Error.WriteLine("validate-oci --metadata-file not found: " + metadataPath);
            return 1;
        }

        var parsed = ReleaseBundlePackaging.TryParseBuildxMetadata(
            File.ReadAllText(metadataPath),
            out var metaDigest,
            out expectedDescriptor);
        if (!parsed.Success)
        {
            Console.Error.WriteLine(
                "validate-oci metadata parse failed: "
                + (parsed.ReasonCode ?? "unknown")
                + " — "
                + (parsed.Message ?? "invalid."));
            return 1;
        }

        var digestGate = ReleaseBundlePackaging.AssertImageDigestMatchesMetadata(
            values["--image-digest"],
            metaDigest);
        if (!digestGate.Success)
        {
            Console.Error.WriteLine(
                "validate-oci failed: "
                + (digestGate.ReasonCode ?? "buildx_image_digest_mismatch")
                + " — "
                + (digestGate.Message ?? "--image-digest does not match Buildx metadata digest."));
            return 1;
        }
    }

    var result = ReleaseBundlePackaging.ValidateOciLayoutDirectory(
        values["--layout"],
        values["--image-digest"],
        platforms,
        expectedDescriptor);
    if (!result.Success)
    {
        Console.Error.WriteLine(
            "validate-oci failed: " + (result.ReasonCode ?? "unknown") + " — " + (result.Message ?? "invalid."));
        return 1;
    }

    Console.Out.WriteLine("validate-oci: ok");
    return 0;
}

static int RunAssertBinaryVersion(ReadOnlySpan<string> args)
{
    if (!TryParseKv(args, out var values, out var error))
    {
        Console.Error.WriteLine(error);
        return 2;
    }

    if (!values.ContainsKey("--binary") || !values.ContainsKey("--expected-core"))
    {
        Console.Error.WriteLine("assert-binary-version requires --binary and --expected-core.");
        return 2;
    }

    var result = ReleaseBundlePackaging.AssertBinaryVersionCore(
        values["--binary"],
        values["--expected-core"]);
    if (!result.Success)
    {
        Console.Error.WriteLine(
            "assert-binary-version failed: "
            + (result.ReasonCode ?? "unknown")
            + " — "
            + (result.Message ?? "mismatch."));
        return 1;
    }

    Console.Out.WriteLine("assert-binary-version: ok");
    return 0;
}

static int RunAssertImageIdentity(ReadOnlySpan<string> args)
{
    if (!TryParseKv(args, out var values, out var error))
    {
        Console.Error.WriteLine(error);
        return 2;
    }

    if (!values.ContainsKey("--identity")
        || !values.ContainsKey("--source-sha")
        || !values.ContainsKey("--mailer-version"))
    {
        Console.Error.WriteLine(
            "assert-image-identity requires --identity, --source-sha, and --mailer-version.");
        return 2;
    }

    if (!File.Exists(values["--identity"]))
    {
        Console.Error.WriteLine("assert-image-identity identity file missing.");
        return 1;
    }

    ImageIdentityDocument? identity;
    try
    {
        identity = JsonSerializer.Deserialize(
            File.ReadAllText(values["--identity"]),
            ReleaseBundleJsonContext.Default.ImageIdentityDocument);
    }
    catch
    {
        Console.Error.WriteLine("assert-image-identity could not parse identity JSON.");
        return 1;
    }

    if (identity is null)
    {
        Console.Error.WriteLine("assert-image-identity identity document was empty.");
        return 1;
    }

    var result = ReleaseBundlePackaging.AssertImageIdentityForHostPackaging(
        identity,
        values["--source-sha"],
        values["--mailer-version"]);
    if (!result.Success)
    {
        Console.Error.WriteLine(
            "assert-image-identity failed: "
            + (result.ReasonCode ?? "unknown")
            + " — "
            + (result.Message ?? "mismatch."));
        return 1;
    }

    Console.Out.WriteLine("assert-image-identity: ok");
    return 0;
}

static int RunWriteImageIdentity(ReadOnlySpan<string> args)
{
    if (!TryParseKv(args, out var values, out var error))
    {
        Console.Error.WriteLine(error);
        return 2;
    }

    string[] required =
    [
        "--output",
        "--repository",
        "--tag",
        "--digest",
        "--source-sha",
        "--mailer-version",
        "--platforms",
    ];
    foreach (var flag in required)
    {
        if (!values.ContainsKey(flag))
        {
            Console.Error.WriteLine("Missing required argument: " + flag);
            return 2;
        }
    }

    if (!ReleaseBundlePackaging.IsValidDigest(values["--digest"]))
    {
        Console.Error.WriteLine("--digest must be sha256:<64 lowercase hex>.");
        return 1;
    }

    if (!ReleaseBundlePackaging.IsValidReleaseVersion(values["--mailer-version"]))
    {
        Console.Error.WriteLine("--mailer-version must be major.minor.patch.");
        return 1;
    }

    var platforms = values["--platforms"]
        .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    var doc = new ImageIdentityDocument
    {
        ImageRepository = values["--repository"],
        ImageTag = values["--tag"],
        ImageDigest = values["--digest"].ToLowerInvariant(),
        SourceCommitSha = values["--source-sha"].ToLowerInvariant(),
        MailerVersion = values["--mailer-version"],
        Platforms = platforms,
    };

    var assert = ReleaseBundlePackaging.AssertImageIdentityForHostPackaging(
        doc,
        values["--source-sha"],
        values["--mailer-version"]);
    if (!assert.Success)
    {
        Console.Error.WriteLine(
            "write-image-identity failed: "
            + (assert.ReasonCode ?? "unknown")
            + " — "
            + (assert.Message ?? "invalid identity."));
        return 1;
    }

    var json = JsonSerializer.Serialize(doc, ReleaseBundleJsonContext.Default.ImageIdentityDocument);
    Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(values["--output"]))!);
    File.WriteAllText(values["--output"], json + "\n");
    Console.Out.WriteLine("write-image-identity: ok");
    return 0;
}

static int RunWriteProvenance(ReadOnlySpan<string> args)
{
    if (!TryParseKv(args, out var values, out var error))
    {
        Console.Error.WriteLine(error);
        return 2;
    }

    string[] required =
    [
        "--output",
        "--handoff",
        "--sums",
        "--source-sha",
        "--release-version",
        "--image-repository",
        "--image-tag",
        "--oci-index-digest",
        "--mailpit-image",
        "--platforms",
        "--archives-json",
    ];
    foreach (var flag in required)
    {
        if (!values.ContainsKey(flag))
        {
            Console.Error.WriteLine("Missing required argument: " + flag);
            return 2;
        }
    }

    CandidateArchiveProvenance[] archives;
    try
    {
        archives = JsonSerializer.Deserialize(
            File.ReadAllText(values["--archives-json"]),
            ReleaseBundleJsonContext.Default.CandidateArchiveProvenanceArray)
            ?? [];
    }
    catch
    {
        // Fallback: accept a JSON array via the generated List context if needed.
        Console.Error.WriteLine("Could not parse --archives-json.");
        return 1;
    }

    values.TryGetValue("--workflow-run-id", out var runId);
    values.TryGetValue("--workflow-run-attempt", out var runAttempt);
    values.TryGetValue("--workflow-ref", out var workflowRef);
    values.TryGetValue("--dotnet-sdk-version", out var sdkVersion);

    var provenance = new CandidateProvenanceDocument
    {
        SchemaVersion = 1,
        SourceCommitSha = values["--source-sha"].ToLowerInvariant(),
        ReleaseVersion = values["--release-version"],
        WorkflowRunId = runId,
        WorkflowRunAttempt = runAttempt,
        WorkflowRef = workflowRef,
        ImageRepository = values["--image-repository"],
        ImageTag = values["--image-tag"],
        OciIndexDigest = values["--oci-index-digest"].ToLowerInvariant(),
        OciPlatforms = values["--platforms"]
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
        MailpitImageReference = values["--mailpit-image"],
        DotnetSdkVersion = sdkVersion,
        Archives = archives,
        Notes =
            "#458 promotes qualified archive bytes; a rebuild produces a new candidate. "
            + "This package is not a GitHub Release and was not pushed to GHCR.",
    };

    var json = JsonSerializer.Serialize(provenance, ReleaseBundleJsonContext.Default.CandidateProvenanceDocument);
    Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(values["--output"]))!);
    File.WriteAllText(values["--output"], json + "\n");

    var handoff = BuildHandoffMarkdown(provenance, values["--sums"]);
    File.WriteAllText(values["--handoff"], handoff);
    Console.Out.WriteLine("write-provenance: ok");
    return 0;
}

static string BuildHandoffMarkdown(CandidateProvenanceDocument provenance, string sumsPath)
{
    var lines = new List<string>
    {
        "# Easy Setup release-candidate handoff (#455 → #456 / #458)",
        string.Empty,
        $"- Generated at (UTC): {DateTime.UtcNow:yyyy-MM-ddTHH:mm:ssZ}",
        $"- Source commit SHA: `{provenance.SourceCommitSha}`",
        $"- Release version: `{provenance.ReleaseVersion}`",
        $"- Workflow run id / attempt: `{provenance.WorkflowRunId}` / `{provenance.WorkflowRunAttempt}`",
        $"- Workflow ref: `{provenance.WorkflowRef}`",
        $"- OCI workflow artifact name: `setup-release-candidate-oci`",
        $"- OCI layout contents: `oci/` + `image-identity.json` + `buildx-metadata.json` + `oci-index.digest`",
        $"- OCI index digest (local layout; **not** pushed to GHCR): `{provenance.OciIndexDigest}`",
        $"- OCI platforms: `{string.Join(", ", provenance.OciPlatforms ?? [])}`",
        $"- Mailpit image: `{provenance.MailpitImageReference}`",
        $"- .NET SDK / toolchain: `{provenance.DotnetSdkVersion}`",
        $"- Archive checksums file: `{Path.GetFileName(sumsPath)}`",
        string.Empty,
        "## Archives",
        string.Empty,
        "| Artifact | Archive | RID | archiveSha256 | payloadTreeSha256 | smoke |",
        "|----------|---------|-----|---------------|-------------------|-------|",
    };

    foreach (var archive in provenance.Archives ?? [])
    {
        lines.Add(
            $"| `{archive.ArtifactName}` | `{archive.ArchiveFileName}` | `{archive.TargetRid}` | `{archive.ArchiveSha256}` | `{archive.PayloadTreeSha256}` | `{archive.SmokeResult}` |");
    }

    lines.Add(string.Empty);
    lines.Add("## Ownership");
    lines.Add(string.Empty);
    lines.Add("| Issue | Owns |");
    lines.Add("|-------|------|");
    lines.Add("| #455 (this packaging) | Reproducible candidate generation, secret scan, artifact smoke, checksums |");
    lines.Add("| #456 | Qualification / go-no-go on these candidates |");
    lines.Add("| #458 | Tag, GHCR publish, GitHub Release, public checksum recording; promotes qualified archive bytes (rebuild = new candidate) |");
    lines.Add(string.Empty);
    lines.Add("## #456 OCI import notes (Windows Docker Desktop / Linux Engine)");
    lines.Add(string.Empty);
    lines.Add("Workflow artifact: **`setup-release-candidate-oci`** (multi-platform OCI layout for `linux/amd64` + `linux/arm64`).");
    lines.Add(string.Empty);
    lines.Add("Classic `docker load` only accepts a single-platform image tarball and **cannot** load a multi-platform OCI layout directory. Prefer one of:");
    lines.Add(string.Empty);
    lines.Add("1. **Recommended (Linux Engine or Docker Desktop with containerd image store):** enable the containerd image store, then import with `skopeo copy oci:./oci containers-storage:<repo>@<digest>` or `ctr images import` / `nerdctl image load` against an OCI archive produced from the layout.");
    lines.Add("2. **Platform-specific import without containerd store:** use `skopeo copy --override-os linux --override-arch amd64 oci:./oci docker-daemon:<repo>:<tag>` (or `arm64`) so the daemon receives one platform only; repeat per arch under test.");
    lines.Add("3. **crane / buildx:** `crane push ./oci <repo>@<digest>` to a local registry, or `docker buildx imagetools create` from a registry mirror — never rebuild the candidate image during qualification.");
    lines.Add(string.Empty);
    lines.Add("Host archives already pin `image-identity.json` (repo / tag / digest). Qualification should import the **same** OCI graph bytes from `setup-release-candidate-oci`, not rebuild.");
    lines.Add(string.Empty);
    lines.Add("## #458 promote notes");
    lines.Add(string.Empty);
    lines.Add("Promote the **qualified** OCI graph and host archive bytes without rebuild when possible.");
    lines.Add("If attestations (provenance / SBOM) are re-added at publish time, the public image index digest **may change** even when platform image layers are unchanged — record the promoted digest explicitly.");
    lines.Add("A rebuild always produces a **new** candidate (`archiveSha256` / provenance).");
    lines.Add(string.Empty);
    lines.Add("## Explicit non-goals completed as non-goals");
    lines.Add(string.Empty);
    lines.Add("- No Git tag created");
    lines.Add("- No GHCR push");
    lines.Add("- No GitHub Release");
    lines.Add("- No MSI / deb / rpm");
    lines.Add("- No auto-updater");
    lines.Add(string.Empty);
    return string.Join("\n", lines) + "\n";
}

static bool TryParseKv(
    ReadOnlySpan<string> args,
    out Dictionary<string, string> values,
    out string? error)
{
    values = new Dictionary<string, string>(StringComparer.Ordinal);
    error = null;
    for (var i = 0; i < args.Length; i++)
    {
        var arg = args[i];
        if (!arg.StartsWith("--", StringComparison.Ordinal))
        {
            error = "Unexpected positional argument: " + arg;
            return false;
        }

        if (arg is "--skip-binary-version-assert")
        {
            values[arg] = "1";
            continue;
        }

        if (i + 1 >= args.Length || args[i + 1].StartsWith("--", StringComparison.Ordinal))
        {
            error = "Missing value for " + arg + ".";
            return false;
        }

        if (values.ContainsKey(arg))
        {
            error = "Duplicate option: " + arg;
            return false;
        }

        values[arg] = args[++i];
    }

    return true;
}
