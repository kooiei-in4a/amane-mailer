using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Amane.Mailer.Setup;

/// <summary>
/// Composes Managed Docker/Compose environment from ADR 0021 D-02 layers only.
/// Never merges a Manual project-directory <c>.env</c>.
/// </summary>
/// <remarks>
/// Issue #450 splits composition into two pins so one apply session cannot silently observe two
/// different inputs: the ACTIVE-independent external layer is pinned once, and the ACTIVE-dependent
/// bundle layers are composed against an explicit <see cref="SetupActivePointer"/> generation.
/// </remarks>
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

    private static readonly string[] TrustedFixedKeys =
    [
        "COMPOSE_PROJECT_NAME",
        "MAILER_IMAGE_REPOSITORY",
        "MAILER_IMAGE_TAG",
        "MAILER_IMAGE_REFERENCE",
        "MAILER_PULL_POLICY",
        "MAILER_SETUP_RECORDED_METADATA_PATH",
        "MAILPIT_IMAGE",
    ];

    private readonly ISetupFileSystem _fileSystem;
    private readonly TimeProvider _timeProvider;

    public ManagedComposeEnvComposer(ISetupFileSystem fileSystem)
        : this(fileSystem, timeProvider: null)
    {
    }

    internal ManagedComposeEnvComposer(ISetupFileSystem fileSystem, TimeProvider? timeProvider)
    {
        _fileSystem = fileSystem ?? throw new ArgumentNullException(nameof(fileSystem));
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    /// <summary>
    /// Reads the strict ACTIVE pointer document. Bare bundle-id payloads are rejected (ADR 0021 D-03).
    /// </summary>
    public bool TryReadActivePointer(
        TrustedSetupHostLayout layout,
        out SetupActivePointer? pointer,
        out SetupDockerResult result)
    {
        ArgumentNullException.ThrowIfNull(layout);
        pointer = null;

        var activePath = layout.ActivePointerPath;
        if (!_fileSystem.FileExists(activePath))
        {
            result = SetupDockerResult.Fail(
                SetupDockerResultCode.InvalidBundleInventory,
                "ACTIVE pointer is missing.");
            return false;
        }

        if (!SetupPathGuard.TryEnsurePathSafeUnderRoot(
                _fileSystem, layout.ManagedRoot, activePath, out _, out _))
        {
            result = SetupDockerResult.Fail(
                SetupDockerResultCode.UnsafePath,
                "ACTIVE pointer path rejected.");
            return false;
        }

        if (SetupPathGuard.IsUnsafeLink(_fileSystem.InspectSymlinkOrReparsePoint(activePath)))
        {
            result = SetupDockerResult.Fail(
                SetupDockerResultCode.UnsafePath,
                "ACTIVE pointer must not be a symlink or reparse point.");
            return false;
        }

        var activeText = Encoding.UTF8.GetString(_fileSystem.ReadAllBytes(activePath));
        if (!SetupActivePointer.TryParse(activeText, out var parsed) || parsed is null)
        {
            result = SetupDockerResult.Fail(
                SetupDockerResultCode.InvalidBundleInventory,
                "ACTIVE pointer payload is invalid.");
            return false;
        }

        pointer = parsed;
        result = SetupDockerResult.Ok();
        return true;
    }

    /// <summary>
    /// Pins the ACTIVE-independent external layer (<c>managed/external.env</c>) for one session.
    /// Produces a canonical digest plus the owner-only runtime-identity binding MAC. Values stay in
    /// process memory; public surfaces only see the digest.
    /// </summary>
    public bool TryPinExternalLayer(
        TrustedSetupHostLayout layout,
        ReadOnlySpan<byte> sealingKey,
        out SetupExternalInputSnapshot? snapshot,
        out SetupDockerResult result)
    {
        ArgumentNullException.ThrowIfNull(layout);
        snapshot = null;

        if (sealingKey.Length != SetupIntegritySealer.SealingKeyLength)
        {
            result = SetupDockerResult.Fail(
                SetupDockerResultCode.InvalidBundleInventory,
                "Host sealing key is invalid.");
            return false;
        }

        var external = new Dictionary<string, string>(StringComparer.Ordinal);
        if (_fileSystem.FileExists(layout.ExternalEnvPath))
        {
            if (!SetupPathGuard.TryEnsurePathSafeUnderRoot(
                    _fileSystem,
                    layout.ManagedRoot,
                    layout.ExternalEnvPath,
                    out _,
                    out _))
            {
                result = SetupDockerResult.Fail(
                    SetupDockerResultCode.UnsafePath,
                    "external.env path rejected.");
                return false;
            }

            if (SetupPathGuard.IsUnsafeLink(
                    _fileSystem.InspectSymlinkOrReparsePoint(layout.ExternalEnvPath)))
            {
                result = SetupDockerResult.Fail(
                    SetupDockerResultCode.UnsafePath,
                    "external.env must not be a symlink or reparse point.");
                return false;
            }

            if (!TryParseEnvFile(_fileSystem.ReadAllBytes(layout.ExternalEnvPath), out var parsed, out var parseFail))
            {
                result = parseFail!;
                return false;
            }

            foreach (var pair in parsed)
            {
                if (!ManagedEnvKeyCatalog.ExternalManualOnlyKeys.Contains(pair.Key))
                {
                    result = SetupDockerResult.Fail(
                        SetupDockerResultCode.InvalidBundleInventory,
                        "external.env contains a non-allowlisted key.");
                    return false;
                }

                if (!TryValidateValue(pair.Key, pair.Value, out var valueFailure))
                {
                    result = valueFailure!;
                    return false;
                }

                external[pair.Key] = pair.Value;
            }
        }

        if (!external.TryGetValue("MAILER_DATA_PATH", out var dataPath)
            || string.IsNullOrWhiteSpace(dataPath))
        {
            result = SetupDockerResult.Fail(
                SetupDockerResultCode.InvalidBundleInventory,
                "MAILER_DATA_PATH is required in allowlisted external input.");
            return false;
        }

        var canonical = BuildCanonicalExternalBytes(external);
        string digest;
        try
        {
            digest = SetupExternalInputDigests.Sha256Hex(canonical);
        }
        finally
        {
            System.Security.Cryptography.CryptographicOperations.ZeroMemory(canonical);
        }

        var normalizedDataPath = NormalizeHostPathValue(dataPath);
        external.TryGetValue("MAILER_CONNECTION_STRING", out var connectionString);
        var normalizedConnectionString = string.IsNullOrWhiteSpace(connectionString)
            ? null
            : connectionString.Trim();

        var bindingMac = SetupRuntimeIdentityBindingStamp.ComputeBindingMac(
            sealingKey,
            normalizedDataPath,
            normalizedConnectionString);

        snapshot = new SetupExternalInputSnapshot(
            digest,
            normalizedDataPath,
            normalizedConnectionString,
            bindingMac,
            external,
            _timeProvider.GetUtcNow());
        result = SetupDockerResult.Ok();
        return true;
    }

    /// <summary>
    /// Composes the full Managed environment for an explicit activation generation. The bundle id
    /// comes from <paramref name="expected"/>, never from a fresh ACTIVE read, so a concurrent
    /// pointer flip cannot retarget an in-flight operation.
    /// </summary>
    public bool TryComposeWithActivePointer(
        TrustedSetupHostLayout layout,
        SetupExternalInputSnapshot externalSnapshot,
        SetupActivePointer expected,
        out SetupComposeInputSnapshot? snapshot,
        out SetupDockerResult result)
    {
        ArgumentNullException.ThrowIfNull(layout);
        ArgumentNullException.ThrowIfNull(externalSnapshot);
        ArgumentNullException.ThrowIfNull(expected);
        snapshot = null;

        if (!SetupActivePointer.IsSafeBundleId(expected.BundleId)
            || expected.ActivationGeneration < 1
            || expected.SchemaVersion != SetupActivePointer.CurrentSchemaVersion)
        {
            result = SetupDockerResult.Fail(
                SetupDockerResultCode.InvalidBundleInventory,
                "Expected ACTIVE pointer is invalid.");
            return false;
        }

        var merged = new Dictionary<string, string>(StringComparer.Ordinal);

        // Layer 1: fixed release defaults / trusted inventory.
        merged["COMPOSE_PROJECT_NAME"] = layout.ProjectName;
        merged["MAILER_IMAGE_REPOSITORY"] = layout.ReleaseInventory.AllowedImageRepository;
        merged["MAILER_IMAGE_TAG"] = layout.ReleaseInventory.AllowedDisplayTag;
        merged["MAILER_IMAGE_REFERENCE"] = layout.ReleaseInventory.PinnedMailerImageReference;
        merged["MAILER_PULL_POLICY"] = "never";
        merged["MAILER_SETUP_RECORDED_METADATA_PATH"] =
            SetupBundleLayout.ContainerRecordedMetadataPath;

        if (layout.Topology == SetupComposeTopology.DeployWithMailpit)
        {
            merged["MAILPIT_IMAGE"] = layout.ReleaseInventory.MailpitImageReference!;
        }

        // Layer 2: pinned allowlisted external input (never re-read from disk here).
        foreach (var pair in externalSnapshot.ExternalEnvironmentValues)
        {
            if (!ManagedEnvKeyCatalog.ExternalManualOnlyKeys.Contains(pair.Key))
            {
                result = SetupDockerResult.Fail(
                    SetupDockerResultCode.InvalidBundleInventory,
                    "Pinned external input contains a non-allowlisted key.");
                return false;
            }

            if (merged.ContainsKey(pair.Key))
            {
                result = SetupDockerResult.Fail(
                    SetupDockerResultCode.InvalidBundleInventory,
                    "Environment key collision across composition layers.");
                return false;
            }

            merged[pair.Key] = pair.Value;
        }

        if (!merged.ContainsKey("MAILER_DATA_PATH"))
        {
            result = SetupDockerResult.Fail(
                SetupDockerResultCode.InvalidBundleInventory,
                "MAILER_DATA_PATH is required in allowlisted external input.");
            return false;
        }

        // Layers 3-4: expected bundle compose.env + secrets.env
        if (!TryReadBundleEnv(
                layout,
                expected.BundleId,
                merged,
                out var recordedMetadataHostPath,
                out var bundleFailure))
        {
            result = bundleFailure!;
            return false;
        }

        // Bundle image fields must match inventory before trusted keys overwrite them.
        if (!merged.TryGetValue("MAILER_IMAGE_REPOSITORY", out var activeRepo)
            || !merged.TryGetValue("MAILER_IMAGE_TAG", out var activeTag)
            || !layout.ReleaseInventory.MatchesActiveImage(activeRepo, activeTag))
        {
            result = SetupDockerResult.Fail(
                SetupDockerResultCode.InvalidBundleInventory,
                "ACTIVE image does not match the trusted release inventory.");
            return false;
        }

        // Enforce trusted project identity and pull policy over bundle freeform values.
        merged["COMPOSE_PROJECT_NAME"] = layout.ProjectName;
        merged["MAILER_IMAGE_REPOSITORY"] = layout.ReleaseInventory.AllowedImageRepository;
        merged["MAILER_IMAGE_TAG"] = layout.ReleaseInventory.AllowedDisplayTag;
        merged["MAILER_IMAGE_REFERENCE"] = layout.ReleaseInventory.PinnedMailerImageReference;
        merged["MAILER_PULL_POLICY"] = "never";
        merged["MAILER_SETUP_RECORDED_METADATA_PATH"] =
            SetupBundleLayout.ContainerRecordedMetadataPath;

        foreach (var pair in merged)
        {
            if (!TryValidateValue(pair.Key, pair.Value, out var valueFailure))
            {
                result = valueFailure!;
                return false;
            }
        }

        snapshot = new SetupComposeInputSnapshot(
            externalSnapshot,
            expected.BundleId,
            expected.ActivationGeneration,
            merged,
            recordedMetadataHostPath,
            _timeProvider.GetUtcNow());
        result = SetupDockerResult.Ok();
        return true;
    }

    /// <summary>
    /// Convenience path that pins the external layer, reads strict ACTIVE, and composes in one call.
    /// Apply/rollback callers must use the explicit pin APIs instead so every operation in a session
    /// observes one generation.
    /// </summary>
    public SetupDockerResult TryCompose(
        TrustedSetupHostLayout layout,
        ReadOnlySpan<byte> sealingKey,
        out IReadOnlyDictionary<string, string> environment,
        out string? recordedMetadataHostPath)
    {
        environment = new Dictionary<string, string>(StringComparer.Ordinal);
        recordedMetadataHostPath = null;
        ArgumentNullException.ThrowIfNull(layout);

        SetupExternalInputSnapshot? external = null;
        SetupComposeInputSnapshot? snapshot = null;
        try
        {
            if (!TryPinExternalLayer(layout, sealingKey, out external, out var pinResult) || external is null)
            {
                return pinResult;
            }

            if (!TryReadActivePointer(layout, out var active, out var activeResult) || active is null)
            {
                return activeResult;
            }

            if (!TryComposeWithActivePointer(layout, external, active, out snapshot, out var composeResult)
                || snapshot is null)
            {
                return composeResult;
            }

            environment = new Dictionary<string, string>(snapshot.ComposedEnvironment, StringComparer.Ordinal);
            recordedMetadataHostPath = snapshot.RecordedMetadataHostPath;
            return SetupDockerResult.Ok();
        }
        finally
        {
            snapshot?.Dispose();
            external?.Dispose();
        }
    }

    internal static byte[] BuildCanonicalExternalBytes(IReadOnlyDictionary<string, string> external)
    {
        var builder = new StringBuilder();
        foreach (var key in external.Keys.OrderBy(static k => k, StringComparer.Ordinal))
        {
            builder.Append(key).Append('=').Append(external[key]).Append('\n');
        }

        return Encoding.UTF8.GetBytes(builder.ToString());
    }

    internal static string NormalizeHostPathValue(string value) =>
        value.Trim().TrimEnd('/', '\\');

    private bool TryReadBundleEnv(
        TrustedSetupHostLayout layout,
        string bundleId,
        Dictionary<string, string> merged,
        out string? recordedMetadataHostPath,
        out SetupDockerResult? failure)
    {
        recordedMetadataHostPath = null;
        failure = null;

        var bundleRoot = Path.GetFullPath(SetupBundleLayout.BundleRoot(layout.ManagedRoot, bundleId));
        if (!SetupPathGuard.TryEnsurePathSafeUnderRoot(
                _fileSystem, layout.ManagedRoot, bundleRoot, out _, out _))
        {
            failure = SetupDockerResult.Fail(
                SetupDockerResultCode.UnsafePath,
                "ACTIVE bundle path rejected.");
            return false;
        }

        var finalizedPath = Path.GetFullPath(
            Path.Combine(bundleRoot, SetupBundleLayout.FinalizedMarkerFileName));
        var tenantsPath = Path.GetFullPath(
            Path.Combine(SetupBundleLayout.ConfigDir(bundleRoot), SetupBundleLayout.TenantsFileName));
        var secretsPath = Path.GetFullPath(SetupBundleLayout.SecretsDir(bundleRoot));
        var configPath = Path.GetFullPath(SetupBundleLayout.ConfigDir(bundleRoot));
        var recordedPath = Path.GetFullPath(
            Path.Combine(SetupBundleLayout.MetadataDir(bundleRoot), SetupBundleLayout.RecordedMetadataFileName));

        if (!TryValidateActivePath(layout, finalizedPath, expectDirectory: false, out failure)
            || !TryValidateActivePath(layout, tenantsPath, expectDirectory: false, out failure)
            || !TryValidateActivePath(layout, secretsPath, expectDirectory: true, out failure)
            || !TryValidateActivePath(layout, configPath, expectDirectory: true, out failure)
            || !TryValidateActivePath(layout, recordedPath, expectDirectory: false, out failure))
        {
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

        merged["MAILER_TENANTS_HOST_PATH"] = tenantsPath;
        merged["MAILER_ACS_SECRET_HOST_PATH"] = secretsPath;
        merged["MAILER_PLATFORM_SENDER_HOST_PATH"] = configPath;
        merged["MAILER_SETUP_RECORDED_METADATA_HOST_PATH"] = recordedPath;
        if (merged.ContainsKey("MAILER_BOUNCE_QUEUE_SECRET_HOST_PATH"))
        {
            var bounceSecretsPath = Path.GetFullPath(Path.Combine(secretsPath, "bounce-queue"));
            if (!TryValidateActivePath(layout, bounceSecretsPath, expectDirectory: true, out failure))
            {
                return false;
            }

            merged["MAILER_BOUNCE_QUEUE_SECRET_HOST_PATH"] = bounceSecretsPath;
        }

        recordedMetadataHostPath = recordedPath;
        return true;
    }

    private bool TryValidateActivePath(
        TrustedSetupHostLayout layout,
        string path,
        bool expectDirectory,
        out SetupDockerResult? failure)
    {
        failure = null;
        if (!SetupPathGuard.TryEnsurePathSafeUnderRoot(
                _fileSystem, layout.ManagedRoot, path, out _, out _))
        {
            failure = SetupDockerResult.Fail(
                SetupDockerResultCode.UnsafePath,
                "ACTIVE bundle path rejected.");
            return false;
        }

        var exists = expectDirectory
            ? _fileSystem.DirectoryExists(path)
            : _fileSystem.FileExists(path);
        if (!exists)
        {
            failure = SetupDockerResult.Fail(
                SetupDockerResultCode.InvalidBundleInventory,
                "ACTIVE bundle is incomplete.");
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

            // Trusted fixed keys are re-asserted from the inventory after merging; any other
            // collision across layers is a hard failure.
            if (merged.ContainsKey(pair.Key)
                && !TrustedFixedKeys.Contains(pair.Key, StringComparer.Ordinal))
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
            case "MAILER_IMAGE_REFERENCE":
                if (!value.Contains("@sha256:", StringComparison.Ordinal))
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
