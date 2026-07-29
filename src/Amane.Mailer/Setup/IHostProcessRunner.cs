namespace Amane.Mailer.Setup;

/// <summary>
/// Spec for a host process invocation. Callers never supply shell strings — only argv arrays.
/// Internal-only: public surface is typed SetupHostDockerAdapter operations.
/// </summary>
internal sealed class HostProcessSpec
{
    public HostProcessSpec(
        string fileName,
        IReadOnlyList<string> argumentList,
        string? workingDirectory,
        IReadOnlyDictionary<string, string?> environment,
        TimeSpan timeout,
        int maxStdoutBytes = HostProcessRunner.DefaultMaxStreamBytes,
        int maxStderrBytes = HostProcessRunner.DefaultMaxStreamBytes)
    {
        FileName = fileName;
        ArgumentList = argumentList;
        WorkingDirectory = workingDirectory;
        Environment = environment;
        Timeout = timeout;
        MaxStdoutBytes = maxStdoutBytes;
        MaxStderrBytes = maxStderrBytes;
    }

    public string FileName { get; }
    public IReadOnlyList<string> ArgumentList { get; }
    public string? WorkingDirectory { get; }
    public IReadOnlyDictionary<string, string?> Environment { get; }
    public TimeSpan Timeout { get; }
    public int MaxStdoutBytes { get; }
    public int MaxStderrBytes { get; }
}

internal enum HostProcessOutcome
{
    Completed = 0,
    FailedToStart = 1,
    TimedOut = 2,
    Cancelled = 3,
    OutputLimitExceeded = 4,
}

/// <summary>
/// Internal process result. Must not be exposed on public SetupDockerResult.
/// </summary>
internal sealed class HostProcessResult
{
    public required HostProcessOutcome Outcome { get; init; }
    public int ExitCode { get; init; } = -1;
    public string? StandardOutput { get; init; }
    public string? StandardError { get; init; }
}

internal interface IHostProcessRunner
{
    Task<HostProcessResult> RunAsync(HostProcessSpec spec, CancellationToken cancellationToken);
}
