using System.Diagnostics;
using System.Globalization;

namespace Amane.Mailer.Setup;

internal enum SetupProcessRunnerFixtureMode
{
    StdoutFlood,
    StderrFlood,
    BothFlood,
    Hang,
    SpawnChildHang,
}

internal static class SetupProcessRunnerFixtureCommand
{
    private const int SuccessExitCode = 0;
    private const int UsageErrorExitCode = 2;
    private const int FloodBytes = HostProcessRunner.DefaultMaxStreamBytes + 64 * 1024;

    public static bool IsFixtureCommand(IReadOnlyList<string> args) =>
        args.Count >= 2
        && string.Equals(args[0], "setup", StringComparison.Ordinal)
        && string.Equals(args[1], "process-runner-fixture", StringComparison.Ordinal);

    public static async Task<int> ExecuteAsync(
        IReadOnlyList<string> args,
        TextWriter output,
        TextWriter error,
        CancellationToken cancellationToken)
    {
        if (args.Count != 3 || !TryParseMode(args[2], out var mode))
        {
            await error.WriteLineAsync(
                "Usage: setup process-runner-fixture <stdout-flood|stderr-flood|both-flood|hang|spawn-child-hang>");
            return UsageErrorExitCode;
        }

        switch (mode)
        {
            case SetupProcessRunnerFixtureMode.StdoutFlood:
                await WriteFloodAsync(output);
                return SuccessExitCode;
            case SetupProcessRunnerFixtureMode.StderrFlood:
                await WriteFloodAsync(error);
                return SuccessExitCode;
            case SetupProcessRunnerFixtureMode.BothFlood:
                await Task.WhenAll(WriteFloodAsync(output), WriteFloodAsync(error));
                return SuccessExitCode;
            case SetupProcessRunnerFixtureMode.Hang:
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                return SuccessExitCode;
            case SetupProcessRunnerFixtureMode.SpawnChildHang:
                using (var child = StartHangChild())
                {
                    var pidPath = Environment.GetEnvironmentVariable("AMANE_FIXTURE_CHILD_PID_PATH");
                    if (!string.IsNullOrWhiteSpace(pidPath))
                    {
                        await File.WriteAllTextAsync(pidPath, child.Id.ToString(CultureInfo.InvariantCulture), cancellationToken);
                    }

                    await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                }

                return SuccessExitCode;
            default:
                return UsageErrorExitCode;
        }
    }

    private static async Task WriteFloodAsync(TextWriter writer)
    {
        var block = new string('x', 8192);
        for (var written = 0; written < FloodBytes; written += block.Length)
        {
            await writer.WriteAsync(block);
        }

        await writer.FlushAsync();
    }

    private static Process StartHangChild()
    {
        var processPath = Environment.ProcessPath
            ?? throw new InvalidOperationException("Current process path is unavailable.");
        var startInfo = new ProcessStartInfo
        {
            FileName = processPath,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        if (Path.GetFileNameWithoutExtension(processPath).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
        {
            startInfo.ArgumentList.Add("exec");
            startInfo.ArgumentList.Add(Environment.GetCommandLineArgs()[0]);
        }

        startInfo.ArgumentList.Add("setup");
        startInfo.ArgumentList.Add("process-runner-fixture");
        startInfo.ArgumentList.Add("hang");
        return Process.Start(startInfo)
            ?? throw new InvalidOperationException("Fixture child process could not be started.");
    }

    private static bool TryParseMode(string value, out SetupProcessRunnerFixtureMode mode)
    {
        mode = value switch
        {
            "stdout-flood" => SetupProcessRunnerFixtureMode.StdoutFlood,
            "stderr-flood" => SetupProcessRunnerFixtureMode.StderrFlood,
            "both-flood" => SetupProcessRunnerFixtureMode.BothFlood,
            "hang" => SetupProcessRunnerFixtureMode.Hang,
            "spawn-child-hang" => SetupProcessRunnerFixtureMode.SpawnChildHang,
            _ => default,
        };
        return value is "stdout-flood" or "stderr-flood" or "both-flood" or "hang" or "spawn-child-hang";
    }
}
