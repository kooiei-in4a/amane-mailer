using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Amane.Mailer.Setup;

/// <summary>
/// Composes Managed Docker/Compose environment from ADR 0021 D-02 layers only.
/// Never merges a Manual project-directory <c>.env</c>.
/// </summary>
public sealed class ManagedComposeEnvComposer
{
    private static readonly Regex SafeProjectName = new(
        "^[a-z0-9][a-z0-9_-]{0,62}$",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex SafeImageRepository = new(
        @"^[a-z0-9]+([._-][a-z0-9]+)*(/[a-z0-9]+([._-][a-z0-9]+)*)*$",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex SafeResourceLimit = new(
        @"^\d+(\.\d+)?[bkmgtpe]?$",
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex SafeDuration = new(
        @"^\d+[smh]?$",
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex SafePort = new(
        @"^\d{1,5}$",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private readonly ISetupFileSystem _fileSystem;

    public ManagedComposeEnvComposer(ISetupFileSystem fileSystem)
    {
        _fileSystem = fileSystem ?? throw new ArgumentNullException(nameof(fileSystem));
    }

    public SetupDockerResult TryCompose(
        TrustedSetupHostLayout layout,
        out IReadOnlyDictionary<string, string> environment)
    {
        environment = new Dictionary<string, string>(StringComparer.Ordinal);
        ArgumentNullException.ThrowIfNull(layout);

        var merged = new Dictionary<string, string>(StringComparer.Ordinal);

        // Layer 1: fixed release defaults / trusted inventory.
        merged["COMPOSE_PROJECT_NAME"] = layout.ProjectName;
        merged["MAILER_IMAGE_REPOSITORY"] = layout.ReleaseInventory.AllowedImageRepository;
        merged["MAILER_IMAGE_TAG"] = layout.ReleaseInventory.AllowedDisplayTag;
        merged["MAILER_PULL_POLICY"] = "never";
        merged["MAILER_SETUP_RECORDED_METADATA_PATH"] =
            SetupBundleLayout.ContainerRecordedMetadataPath;

        if (layout.Topology == SetupComposeTopology.DeployWithMailpit)
        {
            merged["MAILPIT_IMAGE"] = layout.ReleaseInventory.MailpitImageReference!;
        }

        // Layer 2: allowlisted external.env
        if (_fileSystem.FileExists(layout.ExternalEnvPath))
        {
            if (!SetupPathGuard.TryEnsurePathSafeUnderRoot(
                    _fileSystem,
                    layout.ManagedRoot,
                    layout.ExternalEnvPath,
                    out _,
                    out _))
            {
                return SetupDockerResult.Fail(
                    SetupDockerResultCode.UnsafePath,
                    "external.env path rejected.");
            }

            if (!TryParseEnvFile(_fileSystem.ReadAllBytes(layout.ExternalEnvPath), out var external, out var parseFail))
            {
                return parseFail!;
            }

            foreach (var pair in external)
            {
                if (!ManagedEnvKeyCatalog.ExternalManualOnlyKeys.Contains(pair.Key))
                {
                    return SetupDockerResult.Fail(
                        SetupDockerResultCode.InvalidBundleInventory,
                        "external.env contains a non-allowlisted key.");
                }

                if (merged.ContainsKey(pair.Key))
                {
                    return SetupDockerResult.Fail(
                        SetupDockerResultCode.InvalidBundleInventory,
                        "Environment key collision across composition layers.");
                }

                merged[pair.Key] = pair.Value;
            }
        }

        if (!merged.ContainsKey("MAILER_DATA_PATH"))
        {
            return SetupDockerResult.Fail(
                SetupDockerResultCode.InvalidBundleInventory,
                "MAILER_DATA_PATH is required in allowlisted external input.");
        }

        // Layers 3-4: ACTIVE compose.env + secrets.env
        if (!TryReadActiveBundleEnv(layout, merged, out var activeFailure))
        {
            return activeFailure!;
        }

        // ACTIVE image must match inventory before trusted keys overwrite project/pull policy.
        if (!merged.TryGetValue("MAILER_IMAGE_REPOSITORY", out var activeRepo)
            || !merged.TryGetValue("MAILER_IMAGE_TAG", out var activeTag)
            || !layout.ReleaseInventory.MatchesActiveImage(activeRepo, activeTag))
        {
            return SetupDockerResult.Fail(
                SetupDockerResultCode.InvalidBundleInventory,
                "ACTIVE image does not match the trusted release inventory.");
        }

        // Enforce trusted project identity and pull policy over ACTIVE freeform values.
        merged["COMPOSE_PROJECT_NAME"] = layout.ProjectName;
        merged["MAILER_IMAGE_REPOSITORY"] = layout.ReleaseInventory.AllowedImageRepository;
        merged["MAILER_IMAGE_TAG"] = layout.ReleaseInventory.AllowedDisplayTag;
        merged["MAILER_PULL_POLICY"] = "never";

        foreach (var pair in merged)
        {
            if (!TryValidateValue(pair.Key, pair.Value, out var valueFailure))
            {
                return valueFailure!;
            }
        }

        environment = merged;
        return SetupDockerResult.Ok();
    }

    private bool TryReadActiveBundleEnv(
        TrustedSetupHostLayout layout,
        Dictionary<string, string> merged,
        out SetupDockerResult? failure)
    {
        failure = null;
        var activePath = layout.ActivePointerPath;
        if (!_fileSystem.FileExists(activePath))
        {
            failure = SetupDockerResult.Fail(
                SetupDockerResultCode.InvalidBundleInventory,
                "ACTIVE pointer is missing.");
            return false;
        }

        if (!SetupPathGuard.TryEnsurePathSafeUnderRoot(
                _fileSystem, layout.ManagedRoot, activePath, out _, out _))
        {
            failure = SetupDockerResult.Fail(
                SetupDockerResultCode.UnsafePath,
                "ACTIVE pointer path rejected.");
            return false;
        }

        if (SetupPathGuard.IsUnsafeLink(_fileSystem.InspectSymlinkOrReparsePoint(activePath)))
        {
            failure = SetupDockerResult.Fail(
                SetupDockerResultCode.UnsafePath,
                "ACTIVE pointer must not be a symlink or reparse point.");
            return false;
        }

        var activeText = Encoding.UTF8.GetString(_fileSystem.ReadAllBytes(activePath)).Trim();
        if (!TryParseActiveBundleId(activeText, out var bundleId))
        {
            failure = SetupDockerResult.Fail(
                SetupDockerResultCode.InvalidBundleInventory,
                "ACTIVE pointer payload is invalid.");
            return false;
        }

        var bundleRoot = Path.GetFullPath(SetupBundleLayout.BundleRoot(layout.ManagedRoot, bundleId));
        if (!SetupPathGuard.TryEnsurePathSafeUnderRoot(
                _fileSystem, layout.ManagedRoot, bundleRoot, out _, out _))
        {
            failure = SetupDockerResult.Fail(
                SetupDockerResultCode.UnsafePath,
                "ACTIVE bundle path rejected.");
            return false;
        }

        var composeEnvPath = Path.GetFullPath(
            Path.Combine(SetupBundleLayout.EnvDir(bundleRoot), SetupBundleLayout.ComposeEnvFileName));
        var secretsEnvPath = Path.GetFullPath(
            Path.Combine(SetupBundleLayout.EnvDir(bundleRoot), SetupBundleLayout.SecretsEnvFileName));

        if (!TryMergeEnvFile(composeEnvPath, isSecretLayer: false, merged, layout, out failure)
            || !TryMergeEnvFile(secretsEnvPath, isSecretLayer: true, merged, layout, out failure))
        {
            return false;
        }

        // ACTIVE image fields must agree with trusted inventory before we overwrite.
        if (merged.TryGetValue("MAILER_IMAGE_REPOSITORY", out var repo)
            && merged.TryGetValue("MAILER_IMAGE_TAG", out var tag)
            && !layout.ReleaseInventory.MatchesActiveImage(repo, tag))
        {
            failure = SetupDockerResult.Fail(
                SetupDockerResultCode.InvalidBundleInventory,
                "ACTIVE image does not match the trusted release inventory.");
            return false;
        }

        return true;
    }

    private bool TryMergeEnvFile(
        string path,
        bool isSecretLayer,
        Dictionary<string, string> merged,
        TrustedSetupHostLayout layout,
        out SetupDockerResult? failure)
    {
        failure = null;
        if (!_fileSystem.FileExists(path))
        {
            failure = SetupDockerResult.Fail(
                SetupDockerResultCode.InvalidBundleInventory,
                "ACTIVE bundle env file is missing.");
            return false;
        }

        if (!SetupPathGuard.TryEnsurePathSafeUnderRoot(
                _fileSystem, layout.ManagedRoot, path, out _, out _))
        {
            failure = SetupDockerResult.Fail(
                SetupDockerResultCode.UnsafePath,
                "ACTIVE bundle env path rejected.");
            return false;
        }

        if (!TryParseEnvFile(_fileSystem.ReadAllBytes(path), out var entries, out failure))
        {
            return false;
        }

        foreach (var pair in entries)
        {
            var expectedClass = isSecretLayer
                ? ManagedEnvKeyCatalog.KeyClass.SecretValuedEnvironment
                : ManagedEnvKeyCatalog.KeyClass.PublicNonSecret;

            if (!ManagedEnvKeyCatalog.TryClassify(pair.Key, out var keyClass)
                || keyClass != expectedClass)
            {
                failure = SetupDockerResult.Fail(
                    SetupDockerResultCode.InvalidBundleInventory,
                    "ACTIVE env contains a key outside its layer classification.");
                return false;
            }

            if (merged.ContainsKey(pair.Key)
                && !string.Equals(merged[pair.Key], pair.Value, StringComparison.Ordinal)
                && pair.Key is not ("COMPOSE_PROJECT_NAME" or "MAILER_IMAGE_REPOSITORY" or "MAILER_IMAGE_TAG"
                    or "MAILER_PULL_POLICY" or "MAILER_SETUP_RECORDED_METADATA_PATH"))
            {
                // Trusted fixed keys may be overwritten by inventory; other collisions fail.
                if (merged.ContainsKey(pair.Key)
                    && keyClass == ManagedEnvKeyCatalog.KeyClass.PublicNonSecret
                    && pair.Key is "COMPOSE_PROJECT_NAME" or "MAILER_IMAGE_REPOSITORY" or "MAILER_IMAGE_TAG"
                        or "MAILER_PULL_POLICY")
                {
                    // Stash ACTIVE values temporarily; inventory overwrite happens after.
                    merged[pair.Key] = pair.Value;
                    continue;
                }
            }

            if (merged.ContainsKey(pair.Key)
                && pair.Key is not ("COMPOSE_PROJECT_NAME" or "MAILER_IMAGE_REPOSITORY" or "MAILER_IMAGE_TAG"
                    or "MAILER_PULL_POLICY" or "MAILER_SETUP_RECORDED_METADATA_PATH" or "MAILPIT_IMAGE"))
            {
                failure = SetupDockerResult.Fail(
                    SetupDockerResultCode.InvalidBundleInventory,
                    "Environment key collision across composition layers.");
                return false;
            }

            merged[pair.Key] = pair.Value;
        }

        return true;
    }

    internal static bool TryParseActiveBundleId(string activeText, out string bundleId)
    {
        bundleId = string.Empty;
        // Minimal ACTIVE payload: either bare bundleId or JSON with bundleId.
        if (activeText.StartsWith('{'))
        {
            // Avoid pulling full ACTIVE DTO; extract with a tight scan.
            const string marker = "\"bundleId\"";
            var idx = activeText.IndexOf(marker, StringComparison.Ordinal);
            if (idx < 0)
            {
                return false;
            }

            var colon = activeText.IndexOf(':', idx + marker.Length);
            var firstQuote = activeText.IndexOf('"', colon + 1);
            var secondQuote = activeText.IndexOf('"', firstQuote + 1);
            if (colon < 0 || firstQuote < 0 || secondQuote < 0)
            {
                return false;
            }

            bundleId = activeText.Substring(firstQuote + 1, secondQuote - firstQuote - 1);
        }
        else
        {
            bundleId = activeText.Trim();
        }

        return bundleId.Length is > 0 and <= 64
            && bundleId.All(static c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_');
    }

    internal static bool TryParseEnvFile(
        byte[] bytes,
        out Dictionary<string, string> entries,
        out SetupDockerResult? failure)
    {
        entries = new Dictionary<string, string>(StringComparer.Ordinal);
        failure = null;
        var text = Encoding.UTF8.GetString(bytes);
        using var reader = new StringReader(text);
        string? line;
        while ((line = reader.ReadLine()) is not null)
        {
            var trimmed = line.Trim();
            if (trimmed.Length == 0 || trimmed.StartsWith('#'))
            {
                continue;
            }

            var eq = trimmed.IndexOf('=');
            if (eq <= 0)
            {
                failure = SetupDockerResult.Fail(
                    SetupDockerResultCode.InvalidBundleInventory,
                    "Environment file contains a malformed line.");
                return false;
            }

            var key = trimmed[..eq].Trim();
            var value = trimmed[(eq + 1)..];
            if (value.Length >= 2
                && ((value[0] == '"' && value[^1] == '"') || (value[0] == '\'' && value[^1] == '\'')))
            {
                value = value[1..^1];
            }

            if (!ManagedEnvKeyCatalog.TryClassify(key, out _)
                && !string.Equals(key, "MAILPIT_IMAGE", StringComparison.Ordinal))
            {
                failure = SetupDockerResult.Fail(
                    SetupDockerResultCode.InvalidBundleInventory,
                    "Environment file contains an unclassified key.");
                return false;
            }

            if (!entries.TryAdd(key, value))
            {
                failure = SetupDockerResult.Fail(
                    SetupDockerResultCode.InvalidBundleInventory,
                    "Environment file contains a duplicate key.");
                return false;
            }
        }

        return true;
    }

    internal static bool TryValidateValue(string key, string value, out SetupDockerResult? failure)
    {
        failure = null;
        if (value.Length > 4096)
        {
            failure = SetupDockerResult.Fail(
                SetupDockerResultCode.InvalidBundleInventory,
                "Environment value exceeds the allowed length.");
            return false;
        }

        if (value.IndexOfAny(['\0', '\n', '\r']) >= 0)
        {
            failure = SetupDockerResult.Fail(
                SetupDockerResultCode.InvalidBundleInventory,
                "Environment value contains forbidden control characters.");
            return false;
        }

        switch (key)
        {
            case "COMPOSE_PROJECT_NAME":
                if (!SafeProjectName.IsMatch(value))
                {
                    failure = RejectValue();
                    return false;
                }

                break;
            case "MAILER_IMAGE_REPOSITORY":
                if (!SafeImageRepository.IsMatch(value))
                {
                    failure = RejectValue();
                    return false;
                }

                break;
            case "MAILER_IMAGE_TAG":
                if (TrustedReleaseInventory.IsForbiddenDisplayTag(value))
                {
                    failure = RejectValue();
                    return false;
                }

                break;
            case "MAILPIT_IMAGE":
                if (!value.Contains("@sha256:", StringComparison.Ordinal))
                {
                    failure = RejectValue();
                    return false;
                }

                break;
            case "MAILER_HTTP_PORT":
            case "MAILPIT_HTTP_PORT":
                if (!SafePort.IsMatch(value)
                    || !int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var port)
                    || port is < 1 or > 65535)
                {
                    failure = RejectValue();
                    return false;
                }

                break;
            case "MAILER_MEM_LIMIT":
            case "MAILER_CPUS":
            case "LOG_MAX_SIZE":
                if (!SafeResourceLimit.IsMatch(value))
                {
                    failure = RejectValue();
                    return false;
                }

                break;
            case "MAILER_STOP_GRACE_PERIOD":
            case "MAILER_HEALTHCHECK_INTERVAL":
            case "MAILER_HEALTHCHECK_TIMEOUT":
            case "MAILER_HEALTHCHECK_START_PERIOD":
                if (!SafeDuration.IsMatch(value))
                {
                    failure = RejectValue();
                    return false;
                }

                break;
            case "MAILER_DATA_PATH":
            case "MAILER_TENANTS_HOST_PATH":
            case "MAILER_ACS_SECRET_HOST_PATH":
            case "MAILER_PLATFORM_SENDER_HOST_PATH":
            case "MAILER_BOUNCE_QUEUE_SECRET_HOST_PATH":
                if (value.Contains("..", StringComparison.Ordinal)
                    || value.IndexOfAny(['\0', '\n', '\r']) >= 0)
                {
                    failure = RejectValue();
                    return false;
                }

                break;
        }

        return true;

        static SetupDockerResult RejectValue() =>
            SetupDockerResult.Fail(
                SetupDockerResultCode.InvalidBundleInventory,
                "Environment value failed allowlisted validation.");
    }
}
