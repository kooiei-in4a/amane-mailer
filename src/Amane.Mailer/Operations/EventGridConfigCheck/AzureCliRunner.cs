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
        var fileName = ResolveAzFileName();

        ProcessStartInfo startInfo;
        try
        {
            startInfo = new ProcessStartInfo
            {
                FileName = fileName,
                Arguments = arguments,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8,
            };
        }
        catch
        {
            return new AzureCliRunResult(Started: false, ExitCode: -1, StandardOutput: string.Empty, StandardError: string.Empty, TimedOut: false);
        }

        Process? process;
        try
        {
            process = Process.Start(startInfo);
        }
        catch
        {
            return new AzureCliRunResult(Started: false, ExitCode: -1, StandardOutput: string.Empty, StandardError: string.Empty, TimedOut: false);
        }

        if (process is null)
        {
            return new AzureCliRunResult(Started: false, ExitCode: -1, StandardOutput: string.Empty, StandardError: string.Empty, TimedOut: false);
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

    private static string ResolveAzFileName() =>
        OperatingSystem.IsWindows() ? "az.cmd" : "az";

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
