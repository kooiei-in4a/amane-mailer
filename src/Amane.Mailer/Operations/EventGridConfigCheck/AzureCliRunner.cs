using System.Diagnostics;
using System.Text;

namespace Amane.Mailer.Operations.EventGridConfigCheck;

/// <summary>
/// Process-based Azure CLI runner. Invokes only allowlisted read-only queries.
/// </summary>
public sealed class AzureCliRunner : IAzureCliRunner
{
    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(45);

    public async Task<AzureCliRunResult> RunAsync(AzureCliQuery query, CancellationToken cancellationToken)
    {
        var arguments = AzureCliArgumentBuilder.Build(query);
        var azPath = TryResolveAzExecutablePath();
        if (azPath is null)
        {
            return new AzureCliRunResult(
                Started: false,
                ExitCode: -1,
                StandardOutput: string.Empty,
                StandardError: string.Empty,
                TimedOut: false);
        }

        ProcessStartInfo startInfo;
        try
        {
            startInfo = CreateStartInfo(azPath, arguments);
        }
        catch
        {
            return new AzureCliRunResult(
                Started: false,
                ExitCode: -1,
                StandardOutput: string.Empty,
                StandardError: string.Empty,
                TimedOut: false);
        }

        Process? process;
        try
        {
            process = Process.Start(startInfo);
        }
        catch
        {
            return new AzureCliRunResult(
                Started: false,
                ExitCode: -1,
                StandardOutput: string.Empty,
                StandardError: string.Empty,
                TimedOut: false);
        }

        if (process is null)
        {
            return new AzureCliRunResult(
                Started: false,
                ExitCode: -1,
                StandardOutput: string.Empty,
                StandardError: string.Empty,
                TimedOut: false);
        }

        using (process)
        {
            var stdoutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
            var stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(DefaultTimeout);

            try
            {
                await process.WaitForExitAsync(timeoutCts.Token);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                TryKill(process);
                return new AzureCliRunResult(
                    Started: true,
                    ExitCode: -1,
                    StandardOutput: string.Empty,
                    StandardError: string.Empty,
                    TimedOut: true);
            }

            var stdout = await stdoutTask;
            var stderr = await stderrTask;
            return new AzureCliRunResult(
                Started: true,
                ExitCode: process.ExitCode,
                StandardOutput: stdout ?? string.Empty,
                StandardError: stderr ?? string.Empty,
                TimedOut: false);
        }
    }

    internal static string? TryResolveAzExecutablePath()
    {
        var fileName = OperatingSystem.IsWindows() ? "az.cmd" : "az";
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

    private static ProcessStartInfo CreateStartInfo(string azPath, string arguments)
    {
        if (!OperatingSystem.IsWindows())
        {
            return new ProcessStartInfo
            {
                FileName = azPath,
                Arguments = arguments,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8,
            };
        }

        // Windows .cmd files cannot be started with UseShellExecute=false via CreateProcess.
        // Route through ComSpec. FileName is the real cmd.exe; the /c command string is built
        // only from a PATH-resolved az.cmd path (validated) plus allowlisted args that already
        // reject shell metacharacters in AzureCliArgumentBuilder. /s + outer quotes keep nested quotes;
        return new ProcessStartInfo
        {
            FileName = ResolveComSpec(),
            Arguments = BuildWindowsCmdArguments(azPath, arguments),
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
    }

    private static string ResolveComSpec()
    {
        var comSpec = Environment.GetEnvironmentVariable("ComSpec");
        if (!string.IsNullOrWhiteSpace(comSpec) && File.Exists(comSpec))
        {
            return comSpec;
        }

        return Path.Combine(Environment.SystemDirectory, "cmd.exe");
    }

    /// <summary>
    /// Builds <c>cmd /d /s /v:off /c</c> arguments.
    /// Uses the classic outer-quote form so nested quotes around az.cmd and allowlisted
    /// argument values (for example subscription display names with spaces) survive cmd parsing.
    /// </summary>
    internal static string BuildWindowsCmdArguments(string azPath, string arguments)
    {
        if (azPath.IndexOfAny(['"', '\r', '\n', '\0', '&', '|', ';', '<', '>', '^', '%', '!']) >= 0)
        {
            throw new ArgumentException("Azure CLI path contains unsupported characters.");
        }

        // Pattern: cmd /d /s /v:off /c ""path\az.cmd" arg1 "value with spaces""
        // The outer quotes are stripped by /s; nested quotes around path and values remain.
        return string.IsNullOrWhiteSpace(arguments)
            ? $"/d /s /v:off /c \"\"{azPath}\"\""
            : $"/d /s /v:off /c \"\"{azPath}\" {arguments}\"";
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
            // Best-effort cleanup after timeout.
        }
    }
}
