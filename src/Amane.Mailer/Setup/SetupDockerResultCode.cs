namespace Amane.Mailer.Setup;

/// <summary>
/// Canonical host Docker adapter results (Issue #449 / ADR 0021).
/// Public results must never carry raw stdout, stderr, exception text, private paths, or secrets.
/// </summary>
public static class SetupDockerResultCode
{
    public const string Succeeded = "setup.docker.succeeded";

    public const string DockerUnavailable = "setup.docker.docker_unavailable";
    public const string DockerVersionUnsupported = "setup.docker.docker_version_unsupported";
    public const string ComposeUnavailable = "setup.docker.compose_unavailable";
    public const string ComposeVersionUnsupported = "setup.docker.compose_version_unsupported";
    public const string RemoteDockerRejected = "setup.docker.remote_docker_rejected";
    public const string RemoteContextRejected = "setup.docker.remote_context_rejected";
    public const string UnsupportedDockerEnvironment = "setup.docker.unsupported_docker_environment";

    public const string InvalidBundleInventory = "setup.docker.invalid_bundle_inventory";
    public const string UnsafePath = "setup.docker.unsafe_path";
    public const string ConcurrentSetupRejected = "setup.docker.concurrent_setup_rejected";

    /// <summary>An ACTIVE-dependent Docker operation ran without a pinned compose snapshot.</summary>
    public const string ComposeInputNotPinned = "setup.docker.compose_input_not_pinned";

    /// <summary>An operation needed pinned external inputs before composing or comparing.</summary>
    public const string ExternalInputNotPinned = "setup.docker.external_input_not_pinned";

    /// <summary>Allowlisted external input changed underneath a pinned apply session.</summary>
    public const string ExternalInputChanged = "setup.docker.external_input_changed";

    /// <summary>On-disk ACTIVE no longer matches the generation the caller pinned.</summary>
    public const string ActiveGenerationMismatch = "setup.docker.active_generation_mismatch";

    public const string Timeout = "setup.docker.timeout";
    public const string Cancelled = "setup.docker.cancelled";
    public const string ProcessFailed = "setup.docker.process_failed";
    public const string OutputLimitExceeded = "setup.docker.output_limit_exceeded";
    public const string OutputMalformed = "setup.docker.output_malformed";

    public const string OperationNotAvailable = "setup.docker.operation_not_available";
    public const string FailedUnexpected = "setup.docker.failed_unexpected";
}
