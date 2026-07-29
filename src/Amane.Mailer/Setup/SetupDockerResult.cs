namespace Amane.Mailer.Setup;

/// <summary>
/// Public host Docker adapter result. Allowlisted facts only — never raw process output.
/// </summary>
public sealed class SetupDockerResult
{
    public required string Code { get; init; }

    /// <summary>Fixed, non-secret operator-facing message.</summary>
    public string? Message { get; init; }

    /// <summary>Allowlisted engine classification when preflight succeeds.</summary>
    public DockerEngineKind? EngineKind { get; init; }

    /// <summary>Compose plugin major version when known.</summary>
    public int? ComposeMajorVersion { get; init; }

    public SetupInspectEffectiveResult? Inspection { get; init; }

    public bool IsSuccess => Code == SetupDockerResultCode.Succeeded;

    public static SetupDockerResult Ok(
        string? message = null,
        DockerEngineKind? engineKind = null,
        int? composeMajorVersion = null,
        SetupInspectEffectiveResult? inspection = null) =>
        new()
        {
            Code = SetupDockerResultCode.Succeeded,
            Message = message,
            EngineKind = engineKind,
            ComposeMajorVersion = composeMajorVersion,
            Inspection = inspection,
        };

    public static SetupDockerResult Fail(string code, string message) =>
        new()
        {
            Code = code,
            Message = message,
        };
}

public enum DockerEngineKind
{
    Unknown = 0,
    WindowsDockerDesktop = 1,
    LinuxDockerEngine = 2,
}
