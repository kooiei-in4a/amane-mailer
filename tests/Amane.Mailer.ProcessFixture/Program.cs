using System.Diagnostics;
using System.Globalization;

// Test-only process fixture for HostProcessRunner live tests.
// Must not be referenced from the product CLI inventory.

const int SuccessExitCode = 0;
const int UsageErrorExitCode = 2;
// Keep above Amane.Mailer.Setup.HostProcessRunner.DefaultMaxStreamBytes (256 KiB).
const int FloodBytes = (256 * 1024) + (64 * 1024);

if (args.Length != 1 || !TryParseMode(args[0], out var mode))
{
    await Console.Error.WriteLineAsync(
        "Usage: Amane.Mailer.ProcessFixture <stdout-flood|stderr-flood|both-flood|hang|spawn-child-hang>");
    return UsageErrorExitCode;
}

using var cancellation = new CancellationTokenSource();
Console.CancelKeyPress += (_, eventArgs) =>
{
    eventArgs.Cancel = true;
    cancellation.Cancel();
};

try
{
    switch (mode)
    {
        case FixtureMode.StdoutFlood:
            await WriteFloodAsync(Console.Out);
            return SuccessExitCode;
        case FixtureMode.StderrFlood:
            await WriteFloodAsync(Console.Error);
            return SuccessExitCode;
        case FixtureMode.BothFlood:
            await Task.WhenAll(WriteFloodAsync(Console.Out), WriteFloodAsync(Console.Error));
            return SuccessExitCode;
        case FixtureMode.Hang:
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellation.Token);
            return SuccessExitCode;
        case FixtureMode.SpawnChildHang:
            using (var child = StartHangChild())
            {
                var pidPath = Environment.GetEnvironmentVariable("AMANE_FIXTURE_CHILD_PID_PATH");
                if (!string.IsNullOrWhiteSpace(pidPath))
                {
                    await File.WriteAllTextAsync(
                        pidPath,
                        child.Id.ToString(CultureInfo.InvariantCulture),
                        cancellation.Token);
                }

                await Task.Delay(Timeout.InfiniteTimeSpan, cancellation.Token);
            }

            return SuccessExitCode;
        default:
            return UsageErrorExitCode;
    }
}
catch (OperationCanceledException)
{
    return SuccessExitCode;
}

static async Task WriteFloodAsync(TextWriter writer)
{
    var block = new string('x', 8192);
    for (var written = 0; written < FloodBytes; written += block.Length)
    {
        await writer.WriteAsync(block);
    }

    await writer.FlushAsync();
}

static Process StartHangChild()
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

    startInfo.ArgumentList.Add("hang");
    return Process.Start(startInfo)
        ?? throw new InvalidOperationException("Fixture child process could not be started.");
}

static bool TryParseMode(string value, out FixtureMode mode)
{
    mode = value switch
    {
        "stdout-flood" => FixtureMode.StdoutFlood,
        "stderr-flood" => FixtureMode.StderrFlood,
        "both-flood" => FixtureMode.BothFlood,
        "hang" => FixtureMode.Hang,
        "spawn-child-hang" => FixtureMode.SpawnChildHang,
        _ => default,
    };
    return value is "stdout-flood" or "stderr-flood" or "both-flood" or "hang" or "spawn-child-hang";
}

enum FixtureMode
{
    StdoutFlood,
    StderrFlood,
    BothFlood,
    Hang,
    SpawnChildHang,
}
