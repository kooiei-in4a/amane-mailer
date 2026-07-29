namespace Amane.Mailer.Setup;

/// <summary>
/// Immutable Docker connection binding produced by preflight.
/// All subsequent docker invocations must pin this context explicitly.
/// </summary>
public sealed class DockerConnectionBinding
{
    internal DockerConnectionBinding(
        string contextName,
        string endpoint,
        DockerEndpointKind endpointKind,
        DockerEngineKind engineKind,
        string? engineIdentity,
        DateTimeOffset probeTimestamp,
        string dockerExecutablePath,
        int composeMajorVersion)
    {
        ContextName = contextName;
        Endpoint = endpoint;
        EndpointKind = endpointKind;
        EngineKind = engineKind;
        EngineIdentity = engineIdentity;
        ProbeTimestamp = probeTimestamp;
        DockerExecutablePath = dockerExecutablePath;
        ComposeMajorVersion = composeMajorVersion;
    }

    public string ContextName { get; }
    public string Endpoint { get; }
    public DockerEndpointKind EndpointKind { get; }
    public DockerEngineKind EngineKind { get; }
    public string? EngineIdentity { get; }
    public DateTimeOffset ProbeTimestamp { get; }
    public string DockerExecutablePath { get; }
    public int ComposeMajorVersion { get; }
}

public enum DockerEndpointKind
{
    Unknown = 0,
    WindowsNamedPipe = 1,
    UnixSocket = 2,
    RemoteRejected = 3,
}
