using System.Diagnostics;
using Amane.Mailer.Setup;

namespace Amane.Mailer.Tests.Setup;

public sealed class HostProcessRunnerLiveTests
{
    [Theory]
    [InlineData("stdout-flood")]
    [InlineData("stderr-flood")]
    [InlineData("both-flood")]
    public async Task Flood_fixture_exceeds_output_limit(string mode)
    {
        var result = await new HostProcessRunner().RunAsync(
            CreateSpec(mode, TimeSpan.FromSeconds(10)),
            CancellationToken.None);

        Assert.Equal(HostProcessOutcome.OutputLimitExceeded, result.Outcome);
    }

    [Fact]
    public async Task Hang_fixture_times_out()
    {
        var result = await new HostProcessRunner().RunAsync(
            CreateSpec("hang", TimeSpan.FromSeconds(1)),
            CancellationToken.None);

        Assert.Equal(HostProcessOutcome.TimedOut, result.Outcome);
    }

    [Fact]
    public async Task Hang_fixture_honors_cancellation()
    {
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(1));
        var result = await new HostProcessRunner().RunAsync(
            CreateSpec("hang", TimeSpan.FromSeconds(10)),
            cancellation.Token);

        Assert.Equal(HostProcessOutcome.Cancelled, result.Outcome);
    }

    [Fact]
    public async Task Spawn_child_hang_fixture_terminates_tree_without_orphan()
    {
        var pidPath = Path.Combine(Path.GetTempPath(), "amane-fixture-pid-" + Guid.NewGuid().ToString("N"));
        try
        {
            var result = await new HostProcessRunner().RunAsync(
                CreateSpec(
                    "spawn-child-hang",
                    TimeSpan.FromSeconds(2),
                    new Dictionary<string, string>(StringComparer.Ordinal)
                    {
                        ["AMANE_FIXTURE_CHILD_PID_PATH"] = pidPath,
                    }),
                CancellationToken.None);

            Assert.True(
                result.Outcome is HostProcessOutcome.OutputLimitExceeded or HostProcessOutcome.TimedOut);
            Assert.True(File.Exists(pidPath));
            var childPid = int.Parse(await File.ReadAllTextAsync(pidPath, TestContext.Current.CancellationToken));

            // Allow the OS a brief window to reap the killed process tree.
            var deadline = DateTime.UtcNow.AddSeconds(3);
            while (DateTime.UtcNow < deadline && IsProcessAlive(childPid))
            {
                await Task.Delay(50, TestContext.Current.CancellationToken);
            }

            Assert.False(IsProcessAlive(childPid));
        }
        finally
        {
            try
            {
                File.Delete(pidPath);
            }
            catch
            {
                // Best-effort temp cleanup.
            }
        }
    }

    private static bool IsProcessAlive(int pid)
    {
        try
        {
            var process = Process.GetProcessById(pid);
            return !process.HasExited;
        }
        catch (ArgumentException)
        {
            return false;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    private static HostProcessSpec CreateSpec(
        string mode,
        TimeSpan timeout,
        IReadOnlyDictionary<string, string>? extraEnvironment = null)
    {
        var assemblyPath = typeof(SetupCore).Assembly.Location;
        var dotnetHost = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH");
        if (string.IsNullOrWhiteSpace(dotnetHost))
        {
            dotnetHost = OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet";
        }

        var environment = new Dictionary<string, string?>(StringComparer.Ordinal);
        foreach (var pair in HostProcessRunner.CreateMinimalDockerChildEnvironment(clearDockerOverrides: true))
        {
            environment[pair.Key] = pair.Value;
        }

        if (extraEnvironment is not null)
        {
            foreach (var pair in extraEnvironment)
            {
                environment[pair.Key] = pair.Value;
            }
        }

        return new HostProcessSpec(
            dotnetHost,
            ["exec", assemblyPath, "setup", "process-runner-fixture", mode],
            Path.GetDirectoryName(assemblyPath),
            environment,
            timeout);
    }
}
