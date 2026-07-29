using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace Amane.Mailer.Setup;

/// <summary>
/// Fail-closed local Docker / Compose preflight. Produces an immutable connection binding.
/// </summary>
public sealed class DockerEnvironmentProbe
{
    private static readonly Regex ComposeVersionMajor = new(
        @"^v?(?<major>\d+)\.",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private readonly IHostProcessRunner _runner;
    private readonly Func<string?> _getDockerHost;
    private readonly Func<string?> _getDockerContextEnv;
    private readonly Func<string?> _resolveDockerExecutable;

    public DockerEnvironmentProbe()
        : this(new HostProcessRunner())
    {
    }

    internal DockerEnvironmentProbe(
        IHostProcessRunner runner,
        Func<string?>? getDockerHost = null,
        Func<string?>? getDockerContextEnv = null,
        Func<string?>? resolveDockerExecutable = null)
    {
        _runner = runner ?? throw new ArgumentNullException(nameof(runner));
        _getDockerHost = getDockerHost ?? (() => Environment.GetEnvironmentVariable("DOCKER_HOST"));
        _getDockerContextEnv = getDockerContextEnv
            ?? (() => Environment.GetEnvironmentVariable("DOCKER_CONTEXT"));
        _resolveDockerExecutable = resolveDockerExecutable ?? HostProcessRunner.TryResolveDockerExecutable;
    }

    public async Task<(SetupDockerResult Result, DockerConnectionBinding? Binding)> ProbeAsync(
        CancellationToken cancellationToken)
    {
        var hostResult = ClassifyDockerHost(_getDockerHost());
        if (!hostResult.IsSuccess)
        {
            return (hostResult, null);
        }

        var dockerPath = _resolveDockerExecutable();
        if (string.IsNullOrWhiteSpace(dockerPath))
        {
            return (SetupDockerResult.Fail(
                SetupDockerResultCode.DockerUnavailable,
                "Docker CLI was not found on PATH."), null);
        }

        // Context name from DOCKER_CONTEXT env must itself be a safe identifier before use.
        var contextEnv = _getDockerContextEnv();
        if (!string.IsNullOrWhiteSpace(contextEnv) && !IsSafeContextName(contextEnv))
        {
            return (SetupDockerResult.Fail(
                SetupDockerResultCode.UnsupportedDockerEnvironment,
                "DOCKER_CONTEXT value is not a safe context name."), null);
        }

        var contextShow = await RunDockerAsync(
            dockerPath,
            ["context", "show"],
            cancellationToken);
        if (contextShow.Outcome != HostProcessOutcome.Completed || contextShow.ExitCode != 0)
        {
            return (MapProcessFailure(contextShow, SetupDockerResultCode.UnsupportedDockerEnvironment), null);
        }

        var contextName = (contextShow.StandardOutput ?? string.Empty).Trim();
        if (!IsSafeContextName(contextName))
        {
            return (SetupDockerResult.Fail(
                SetupDockerResultCode.OutputMalformed,
                "Active Docker context name is malformed."), null);
        }

        if (!string.IsNullOrWhiteSpace(contextEnv)
            && !string.Equals(contextEnv.Trim(), contextName, StringComparison.Ordinal))
        {
            return (SetupDockerResult.Fail(
                SetupDockerResultCode.UnsupportedDockerEnvironment,
                "DOCKER_CONTEXT does not match the active Docker context."), null);
        }

        var inspect = await RunDockerAsync(
            dockerPath,
            ["context", "inspect", contextName, "--format", "{{json .}}"],
            cancellationToken);
        if (inspect.Outcome != HostProcessOutcome.Completed || inspect.ExitCode != 0)
        {
            return (MapProcessFailure(inspect, SetupDockerResultCode.UnsupportedDockerEnvironment), null);
        }

        if (!TryParseContextEndpoint(inspect.StandardOutput, out var endpoint, out var parseFailure))
        {
            return (parseFailure!, null);
        }

        var endpointKind = ClassifyEndpoint(endpoint);
        if (endpointKind == DockerEndpointKind.RemoteRejected)
        {
            return (SetupDockerResult.Fail(
                SetupDockerResultCode.RemoteContextRejected,
                "Active Docker context points at a remote endpoint."), null);
        }

        if (endpointKind == DockerEndpointKind.Unknown)
        {
            return (SetupDockerResult.Fail(
                SetupDockerResultCode.UnsupportedDockerEnvironment,
                "Docker endpoint could not be classified as local."), null);
        }

        var version = await RunDockerAsync(
            dockerPath,
            ["version", "--format", "{{.Server.Version}}"],
            cancellationToken);
        if (version.Outcome != HostProcessOutcome.Completed || version.ExitCode != 0)
        {
            return (MapProcessFailure(version, SetupDockerResultCode.DockerUnavailable), null);
        }

        var serverVersion = (version.StandardOutput ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(serverVersion) || serverVersion.Length > 64)
        {
            return (SetupDockerResult.Fail(
                SetupDockerResultCode.DockerVersionUnsupported,
                "Docker Engine version could not be determined."), null);
        }

        var composeVersion = await RunDockerAsync(
            dockerPath,
            ["compose", "version", "--short"],
            cancellationToken);
        if (composeVersion.Outcome != HostProcessOutcome.Completed || composeVersion.ExitCode != 0)
        {
            return (MapProcessFailure(composeVersion, SetupDockerResultCode.ComposeUnavailable), null);
        }

        var composeText = (composeVersion.StandardOutput ?? string.Empty).Trim();
        var majorMatch = ComposeVersionMajor.Match(composeText);
        if (!majorMatch.Success
            || !int.TryParse(majorMatch.Groups["major"].Value, out var composeMajor)
            || composeMajor < 2)
        {
            return (SetupDockerResult.Fail(
                SetupDockerResultCode.ComposeVersionUnsupported,
                "Docker Compose plugin v2 or later is required."), null);
        }

        var engineKind = ClassifyEngineKind(endpointKind, contextName);
        if (engineKind == DockerEngineKind.Unknown)
        {
            return (SetupDockerResult.Fail(
                SetupDockerResultCode.UnsupportedDockerEnvironment,
                "Docker environment is not a supported Desktop or Engine target."), null);
        }

        var binding = new DockerConnectionBinding(
            contextName,
            NormalizeEndpoint(endpoint),
            endpointKind,
            engineKind,
            serverVersion,
            DateTimeOffset.UtcNow,
            dockerPath,
            composeMajor);

        return (SetupDockerResult.Ok(
            "Local Docker preflight succeeded.",
            engineKind,
            composeMajor), binding);
    }

    /// <summary>
    /// Re-check ambient overrides and re-inspect the pinned context endpoint before mutate.
    /// </summary>
    public async Task<SetupDockerResult> RevalidateBindingAsync(
        DockerConnectionBinding binding,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(binding);

        var hostResult = ClassifyDockerHost(_getDockerHost());
        if (!hostResult.IsSuccess)
        {
            return hostResult;
        }

        var contextEnv = _getDockerContextEnv();
        if (!string.IsNullOrWhiteSpace(contextEnv)
            && !string.Equals(contextEnv.Trim(), binding.ContextName, StringComparison.Ordinal))
        {
            return SetupDockerResult.Fail(
                SetupDockerResultCode.UnsupportedDockerEnvironment,
                "Docker connection binding drifted from DOCKER_CONTEXT.");
        }

        var inspect = await RunDockerAsync(
            binding.DockerExecutablePath,
            ["--context", binding.ContextName, "context", "inspect", binding.ContextName, "--format", "{{json .}}"],
            cancellationToken);
        if (inspect.Outcome != HostProcessOutcome.Completed || inspect.ExitCode != 0)
        {
            return MapProcessFailure(inspect, SetupDockerResultCode.UnsupportedDockerEnvironment);
        }

        if (!TryParseContextEndpoint(inspect.StandardOutput, out var endpoint, out var parseFailure))
        {
            return parseFailure!;
        }

        var normalized = NormalizeEndpoint(endpoint);
        var kind = ClassifyEndpoint(normalized);
        if (kind == DockerEndpointKind.RemoteRejected)
        {
            return SetupDockerResult.Fail(
                SetupDockerResultCode.RemoteContextRejected,
                "Docker context endpoint became remote after preflight.");
        }

        if (kind != binding.EndpointKind
            || !string.Equals(normalized, binding.Endpoint, StringComparison.OrdinalIgnoreCase))
        {
            return SetupDockerResult.Fail(
                SetupDockerResultCode.UnsupportedDockerEnvironment,
                "Docker connection binding endpoint drifted after preflight.");
        }

        var version = await RunDockerAsync(
            binding.DockerExecutablePath,
            ["--context", binding.ContextName, "version", "--format", "{{.Server.Version}}"],
            cancellationToken);
        if (version.Outcome != HostProcessOutcome.Completed || version.ExitCode != 0)
        {
            return MapProcessFailure(version, SetupDockerResultCode.DockerUnavailable);
        }

        var serverVersion = (version.StandardOutput ?? string.Empty).Trim();
        if (!string.Equals(serverVersion, binding.EngineIdentity, StringComparison.Ordinal))
        {
            return SetupDockerResult.Fail(
                SetupDockerResultCode.UnsupportedDockerEnvironment,
                "Docker Engine identity drifted after preflight.");
        }

        return SetupDockerResult.Ok();
    }

    internal static SetupDockerResult ClassifyDockerHost(string? dockerHost)
    {
        if (string.IsNullOrWhiteSpace(dockerHost))
        {
            return SetupDockerResult.Ok();
        }

        var value = NormalizeEndpoint(dockerHost);
        if (IsRemoteScheme(value))
        {
            return SetupDockerResult.Fail(
                SetupDockerResultCode.RemoteDockerRejected,
                "DOCKER_HOST points at a remote Docker daemon.");
        }

        if (MatchesAllowedWindowsNamedPipeShape(value) || IsLocalUnixSocket(value))
        {
            return SetupDockerResult.Ok();
        }

        return SetupDockerResult.Fail(
            SetupDockerResultCode.UnsupportedDockerEnvironment,
            "DOCKER_HOST could not be classified as a local Docker endpoint.");
    }

    /// <summary>
    /// Exact allowlist of local Windows Docker named pipes (Desktop Linux/Windows engines + classic).
    /// Remote host names inside npipe URIs are never accepted.
    /// </summary>
    internal static readonly string[] AllowedWindowsNamedPipes =
    [
        "npipe:////./pipe/docker_engine",
        "npipe:////./pipe/dockerDesktopLinuxEngine",
        "npipe:////./pipe/dockerDesktopWindowsEngine",
    ];

    internal static string NormalizeEndpoint(string endpoint)
    {
        var value = endpoint.Trim();
        while (value.EndsWith('/') && !value.EndsWith("://", StringComparison.Ordinal))
        {
            value = value[..^1];
        }

        return value;
    }

    internal static bool IsRemoteScheme(string value) =>
        value.StartsWith("tcp://", StringComparison.OrdinalIgnoreCase)
        || value.StartsWith("ssh://", StringComparison.OrdinalIgnoreCase)
        || value.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
        || value.StartsWith("https://", StringComparison.OrdinalIgnoreCase);

    internal static bool MatchesAllowedWindowsNamedPipeShape(string value)
    {
        if (value.Contains('%', StringComparison.Ordinal))
        {
            return false;
        }

        var normalized = NormalizeEndpoint(value);
        foreach (var allowed in AllowedWindowsNamedPipes)
        {
            if (string.Equals(normalized, allowed, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    internal static bool IsAllowedWindowsNamedPipe(string value) =>
        OperatingSystem.IsWindows() && MatchesAllowedWindowsNamedPipeShape(value);

    internal static bool IsLocalUnixSocket(string value)
    {
        var normalized = NormalizeEndpoint(value);
        return normalized.Equals("unix:///var/run/docker.sock", StringComparison.Ordinal)
            || normalized.Equals("unix:///run/docker.sock", StringComparison.Ordinal)
            || normalized.Equals("/var/run/docker.sock", StringComparison.Ordinal)
            || normalized.Equals("/run/docker.sock", StringComparison.Ordinal);
    }

    internal static DockerEndpointKind ClassifyEndpoint(string endpoint)
    {
        if (string.IsNullOrWhiteSpace(endpoint))
        {
            return DockerEndpointKind.Unknown;
        }

        var value = NormalizeEndpoint(endpoint);

        // Scheme-first: remote schemes are always rejected regardless of path content.
        if (IsRemoteScheme(value))
        {
            return DockerEndpointKind.RemoteRejected;
        }

        if (value.Contains('%', StringComparison.Ordinal))
        {
            return DockerEndpointKind.Unknown;
        }

        if (IsAllowedWindowsNamedPipe(value))
        {
            return DockerEndpointKind.WindowsNamedPipe;
        }

        if (MatchesAllowedWindowsNamedPipeShape(value))
        {
            // Exact local pipe shape observed on a non-Windows host — fail closed.
            return DockerEndpointKind.Unknown;
        }

        // npipe that is not on the exact local allowlist is fail-closed (includes remote hosts).
        if (value.StartsWith("npipe:", StringComparison.OrdinalIgnoreCase))
        {
            return DockerEndpointKind.RemoteRejected;
        }

        if (IsLocalUnixSocket(value))
        {
            return DockerEndpointKind.UnixSocket;
        }

        return DockerEndpointKind.Unknown;
    }

    internal static DockerEndpointKind ClassifyEndpointForTests(string endpoint, bool pretendWindows)
    {
        // Test helper to evaluate Windows named-pipe allowlist without depending on host OS.
        if (string.IsNullOrWhiteSpace(endpoint))
        {
            return DockerEndpointKind.Unknown;
        }

        var value = NormalizeEndpoint(endpoint);
        if (IsRemoteScheme(value))
        {
            return DockerEndpointKind.RemoteRejected;
        }

        if (value.Contains('%', StringComparison.Ordinal))
        {
            return DockerEndpointKind.Unknown;
        }

        if (pretendWindows)
        {
            foreach (var allowed in AllowedWindowsNamedPipes)
            {
                if (string.Equals(value, allowed, StringComparison.OrdinalIgnoreCase))
                {
                    return DockerEndpointKind.WindowsNamedPipe;
                }
            }

            if (value.StartsWith("npipe:", StringComparison.OrdinalIgnoreCase))
            {
                return DockerEndpointKind.RemoteRejected;
            }
        }
        else if (value.StartsWith("npipe:", StringComparison.OrdinalIgnoreCase))
        {
            return DockerEndpointKind.Unknown;
        }

        if (IsLocalUnixSocket(value))
        {
            return DockerEndpointKind.UnixSocket;
        }

        return DockerEndpointKind.Unknown;
    }

    internal static bool IsLocalNamedPipe(string value) => IsAllowedWindowsNamedPipe(value);

    internal static DockerEngineKind ClassifyEngineKind(DockerEndpointKind endpointKind, string contextName)
    {
        if (endpointKind == DockerEndpointKind.WindowsNamedPipe)
        {
            return DockerEngineKind.WindowsDockerDesktop;
        }

        if (endpointKind == DockerEndpointKind.UnixSocket)
        {
            if (contextName.Contains("desktop", StringComparison.OrdinalIgnoreCase)
                && OperatingSystem.IsWindows())
            {
                return DockerEngineKind.WindowsDockerDesktop;
            }

            return DockerEngineKind.LinuxDockerEngine;
        }

        return DockerEngineKind.Unknown;
    }

    internal static bool IsSafeContextName(string name)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Length > 128)
        {
            return false;
        }

        foreach (var c in name)
        {
            if (!(char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.'))
            {
                return false;
            }
        }

        return true;
    }

    private async Task<HostProcessResult> RunDockerAsync(
        string dockerPath,
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken)
    {
        var spec = new HostProcessSpec(
            dockerPath,
            arguments,
            workingDirectory: null,
            environment: HostProcessRunner.CreateMinimalDockerChildEnvironment(clearDockerOverrides: true),
            timeout: TimeSpan.FromSeconds(30));
        return await _runner.RunAsync(spec, cancellationToken);
    }

    private static SetupDockerResult MapProcessFailure(HostProcessResult result, string defaultCode) =>
        result.Outcome switch
        {
            HostProcessOutcome.TimedOut => SetupDockerResult.Fail(
                SetupDockerResultCode.Timeout,
                "Docker preflight timed out."),
            HostProcessOutcome.Cancelled => SetupDockerResult.Fail(
                SetupDockerResultCode.Cancelled,
                "Docker preflight was cancelled."),
            HostProcessOutcome.OutputLimitExceeded => SetupDockerResult.Fail(
                SetupDockerResultCode.OutputLimitExceeded,
                "Docker preflight output exceeded the allowed limit."),
            HostProcessOutcome.FailedToStart => SetupDockerResult.Fail(
                SetupDockerResultCode.DockerUnavailable,
                "Docker CLI could not be started."),
            _ => SetupDockerResult.Fail(defaultCode, "Docker preflight failed."),
        };

    private static bool TryParseContextEndpoint(
        string? json,
        out string endpoint,
        out SetupDockerResult? failure)
    {
        endpoint = string.Empty;
        failure = null;
        if (string.IsNullOrWhiteSpace(json))
        {
            failure = SetupDockerResult.Fail(
                SetupDockerResultCode.OutputMalformed,
                "Docker context inspect returned empty output.");
            return false;
        }

        try
        {
            var trimmed = json.Trim();
            if (trimmed.StartsWith('['))
            {
                var array = JsonSerializer.Deserialize(
                    trimmed,
                    SetupHostDockerJsonContext.Default.DockerContextInspectDocumentArray);
                if (array is null || array.Length == 0)
                {
                    failure = SetupDockerResult.Fail(
                        SetupDockerResultCode.OutputMalformed,
                        "Docker context inspect returned no entries.");
                    return false;
                }

                endpoint = array[0].Endpoints?.Docker?.Host ?? string.Empty;
            }
            else
            {
                var document = JsonSerializer.Deserialize(
                    trimmed,
                    SetupHostDockerJsonContext.Default.DockerContextInspectDocument);
                endpoint = document?.Endpoints?.Docker?.Host ?? string.Empty;
            }

            if (string.IsNullOrWhiteSpace(endpoint))
            {
                failure = SetupDockerResult.Fail(
                    SetupDockerResultCode.OutputMalformed,
                    "Docker context endpoint host is missing.");
                return false;
            }

            return true;
        }
        catch
        {
            failure = SetupDockerResult.Fail(
                SetupDockerResultCode.OutputMalformed,
                "Docker context inspect output was malformed.");
            return false;
        }
    }
}

public sealed class DockerContextInspectDocument
{
    [JsonPropertyName("Endpoints")]
    public DockerContextInspectEndpoints? Endpoints { get; init; }
}

public sealed class DockerContextInspectEndpoints
{
    [JsonPropertyName("docker")]
    public DockerContextInspectEndpoint? Docker { get; init; }
}

public sealed class DockerContextInspectEndpoint
{
    [JsonPropertyName("Host")]
    public string? Host { get; init; }
}
