using System.Diagnostics;
using System.Text;

namespace Amane.Mailer.Setup;

/// <summary>
/// ArgumentList-only process runner with concurrent stream drain, byte caps, and kill+await.
/// </summary>
public sealed class HostProcessRunner : IHostProcessRunner
{
    public const int DefaultMaxStreamBytes = 256 * 1024;

    public async Task<HostProcessResult> RunAsync(HostProcessSpec spec, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(spec);

        ProcessStartInfo startInfo;
        try
        {
            startInfo = CreateStartInfo(spec);
        }
        catch
        {
            return new HostProcessResult { Outcome = HostProcessOutcome.FailedToStart };
        }

        Process? process;
        try
        {
            process = Process.Start(startInfo);
        }
        catch
        {
            return new HostProcessResult { Outcome = HostProcessOutcome.FailedToStart };
        }

        if (process is null)
        {
            return new HostProcessResult { Outcome = HostProcessOutcome.FailedToStart };
        }

        using (process)
        {
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(spec.Timeout);

            var stdoutTask = ReadStreamAsync(process.StandardOutput.BaseStream, spec.MaxStdoutBytes, timeoutCts.Token);
            var stderrTask = ReadStreamAsync(process.StandardError.BaseStream, spec.MaxStderrBytes, timeoutCts.Token);

            try
            {
                await process.WaitForExitAsync(timeoutCts.Token);
                var stdout = await stdoutTask;
                var stderr = await stderrTask;

                if (stdout.LimitExceeded || stderr.LimitExceeded)
                {
                    TryKill(process);
                    await WaitForExitIgnoreCancelAsync(process);
                    return new HostProcessResult
                    {
                        Outcome = HostProcessOutcome.OutputLimitExceeded,
                        ExitCode = process.HasExited ? process.ExitCode : -1,
                    };
                }

                return new HostProcessResult
                {
                    Outcome = HostProcessOutcome.Completed,
                    ExitCode = process.ExitCode,
                    StandardOutput = stdout.Text,
                    StandardError = stderr.Text,
                };
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                TryKill(process);
                await WaitForExitIgnoreCancelAsync(process);
                return new HostProcessResult
                {
                    Outcome = HostProcessOutcome.Cancelled,
                    ExitCode = process.HasExited ? process.ExitCode : -1,
                };
            }
            catch (OperationCanceledException)
            {
                TryKill(process);
                await WaitForExitIgnoreCancelAsync(process);
                return new HostProcessResult
                {
                    Outcome = HostProcessOutcome.TimedOut,
                    ExitCode = process.HasExited ? process.ExitCode : -1,
                };
            }
        }
    }

    internal static ProcessStartInfo CreateStartInfo(HostProcessSpec spec)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = spec.FileName,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };

        if (!string.IsNullOrWhiteSpace(spec.WorkingDirectory))
        {
            startInfo.WorkingDirectory = spec.WorkingDirectory;
        }

        foreach (var arg in spec.ArgumentList)
        {
            startInfo.ArgumentList.Add(arg);
        }

        // Start from a cleared environment and apply only allowlisted entries.
        startInfo.Environment.Clear();
        foreach (var pair in spec.Environment)
        {
            if (pair.Value is null)
            {
                continue;
            }

            startInfo.Environment[pair.Key] = pair.Value;
        }

        return startInfo;
    }

    public static string? TryResolveDockerExecutable()
    {
        var fileName = OperatingSystem.IsWindows() ? "docker.exe" : "docker";
        var pathEnv = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        foreach (var segment in pathEnv.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            var directory = segment.Trim().Trim('"');
            if (directory.Length == 0)
            {
                continue;
            }

            var candidate = Path.Combine(directory, fileName);
            if (File.Exists(candidate))
            {
                return Path.GetFullPath(candidate);
            }
        }

        return null;
    }

    /// <summary>
    /// Minimal environment for Docker CLI discovery. Clears DOCKER_HOST / DOCKER_CONTEXT /
    /// COMPOSE_* so the pinned --context binding is authoritative.
    /// </summary>
    public static Dictionary<string, string?> CreateMinimalDockerChildEnvironment(
        bool clearDockerOverrides,
        IReadOnlyDictionary<string, string?>? extra = null)
    {
        var env = new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            ["PATH"] = Environment.GetEnvironmentVariable("PATH"),
            ["SystemRoot"] = Environment.GetEnvironmentVariable("SystemRoot"),
            ["WINDIR"] = Environment.GetEnvironmentVariable("WINDIR"),
            ["HOME"] = Environment.GetEnvironmentVariable("HOME"),
            ["USERPROFILE"] = Environment.GetEnvironmentVariable("USERPROFILE"),
            ["TMP"] = Environment.GetEnvironmentVariable("TMP"),
            ["TEMP"] = Environment.GetEnvironmentVariable("TEMP"),
            ["TMPDIR"] = Environment.GetEnvironmentVariable("TMPDIR"),
            ["COMPOSE_DISABLE_ENV_FILE"] = "1",
        };

        if (clearDockerOverrides)
        {
            // Intentionally omit DOCKER_HOST, DOCKER_CONTEXT, COMPOSE_ENV_FILES, COMPOSE_FILE,
            // and COMPOSE_PROFILES so the pinned --context binding is authoritative.
        }

        if (extra is not null)
        {
            foreach (var pair in extra)
            {
                if (pair.Value is not null)
                {
                    env[pair.Key] = pair.Value;
                }
            }
        }

        // Drop null placeholders from discovery keys.
        foreach (var key in env.Keys.ToArray())
        {
            if (env[key] is null)
            {
                env.Remove(key);
            }
        }

        return env;
    }

    private static async Task<StreamReadResult> ReadStreamAsync(
        Stream stream,
        int maxBytes,
        CancellationToken cancellationToken)
    {
        var buffer = new byte[8192];
        using var memory = new MemoryStream();
        while (true)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken);
            if (read == 0)
            {
                break;
            }

            if (memory.Length + read > maxBytes)
            {
                return new StreamReadResult(string.Empty, LimitExceeded: true);
            }

            await memory.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
        }

        return new StreamReadResult(Encoding.UTF8.GetString(memory.ToArray()), LimitExceeded: false);
    }

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch
        {
            // Best-effort kill; caller still awaits exit.
        }
    }

    private static async Task WaitForExitIgnoreCancelAsync(Process process)
    {
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await process.WaitForExitAsync(cts.Token);
        }
        catch
        {
            // Ignore — process may already be gone.
        }
    }

    private readonly record struct StreamReadResult(string Text, bool LimitExceeded);
}
