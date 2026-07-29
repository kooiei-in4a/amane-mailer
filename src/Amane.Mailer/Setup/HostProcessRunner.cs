using System.Diagnostics;
using System.Text;

namespace Amane.Mailer.Setup;

/// <summary>
/// ArgumentList-only process runner with concurrent stream drain, byte caps, and kill+await.
/// </summary>
internal sealed class HostProcessRunner : IHostProcessRunner
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

            var limitHit = 0;
            void OnLimitExceeded()
            {
                if (Interlocked.Exchange(ref limitHit, 1) == 0)
                {
                    TryKill(process);
                }
            }

            var stdoutTask = ReadStreamAsync(
                process.StandardOutput.BaseStream,
                spec.MaxStdoutBytes,
                OnLimitExceeded,
                timeoutCts.Token);
            var stderrTask = ReadStreamAsync(
                process.StandardError.BaseStream,
                spec.MaxStderrBytes,
                OnLimitExceeded,
                timeoutCts.Token);

            try
            {
                await process.WaitForExitAsync(timeoutCts.Token);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                TryKill(process);
                await WaitForExitIgnoreCancelAsync(process);
                await DrainIgnoreErrorsAsync(stdoutTask, stderrTask);
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
                await DrainIgnoreErrorsAsync(stdoutTask, stderrTask);
                return new HostProcessResult
                {
                    Outcome = HostProcessOutcome.TimedOut,
                    ExitCode = process.HasExited ? process.ExitCode : -1,
                };
            }

            // Always await both streams after exit/kill so ownership is complete.
            var stdout = await SafeAwaitStreamAsync(stdoutTask);
            var stderr = await SafeAwaitStreamAsync(stderrTask);

            if (limitHit != 0 || stdout.LimitExceeded || stderr.LimitExceeded)
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

    internal static string? TryResolveDockerExecutable()
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

    internal static Dictionary<string, string?> CreateMinimalDockerChildEnvironment(
        bool clearDockerOverrides,
        IReadOnlyDictionary<string, string?>? extra = null)
    {
        _ = clearDockerOverrides;
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
        Action onLimitExceeded,
        CancellationToken cancellationToken)
    {
        var buffer = new byte[8192];
        using var memory = new MemoryStream();
        var limitExceeded = false;
        while (true)
        {
            int read;
            try
            {
                read = await stream.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken);
            }
            catch (OperationCanceledException) when (limitExceeded)
            {
                // Process was killed due to limit; treat as drained.
                break;
            }

            if (read == 0)
            {
                break;
            }

            if (!limitExceeded && memory.Length + read > maxBytes)
            {
                limitExceeded = true;
                onLimitExceeded();
                // Discard remaining bytes until EOF so the child is not blocked on a full pipe.
                continue;
            }

            if (!limitExceeded)
            {
                await memory.WriteAsync(buffer.AsMemory(0, read), CancellationToken.None);
            }
        }

        return limitExceeded
            ? new StreamReadResult(string.Empty, LimitExceeded: true)
            : new StreamReadResult(Encoding.UTF8.GetString(memory.ToArray()), LimitExceeded: false);
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
            // Best-effort kill; caller still awaits exit and streams.
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

    private static async Task DrainIgnoreErrorsAsync(
        Task<StreamReadResult> stdoutTask,
        Task<StreamReadResult> stderrTask)
    {
        await SafeAwaitStreamAsync(stdoutTask);
        await SafeAwaitStreamAsync(stderrTask);
    }

    private static async Task<StreamReadResult> SafeAwaitStreamAsync(Task<StreamReadResult> task)
    {
        try
        {
            return await task;
        }
        catch
        {
            return new StreamReadResult(string.Empty, LimitExceeded: false);
        }
    }

    private readonly record struct StreamReadResult(string Text, bool LimitExceeded);
}
