using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using Amane.Mailer.Setup.NonInteractive;

namespace Amane.Mailer.Tests.Setup.NonInteractive;

/// <summary>
/// Real-process Linux CLI evidence for #453 non-interactive setup apply (usage / JSON / permissions).
/// Does not require Docker or ACS; rejection paths only.
/// </summary>
public sealed class SetupApplyNonInteractiveProcessTests
{
    [Fact]
    public void Incomplete_setup_apply_is_usage_error_with_empty_stdout()
    {
        var entry = RequireMailerEntry();
        var result = RunMailer(entry, ["setup", "apply"]);
        Assert.Equal(SetupApplyNonInteractiveCommand.UsageErrorExitCode, result.ExitCode);
        Assert.Equal(string.Empty, result.Stdout);
        Assert.Contains("Usage:", result.Stderr, StringComparison.Ordinal);
    }

    [Fact]
    public void Malformed_json_emits_one_canonical_json_and_keeps_config_file()
    {
        Assert.SkipWhen(!OperatingSystem.IsLinux(), "Linux filesystem and process CLI evidence.");
        var entry = RequireMailerEntry();

        var configPath = SetupNonInteractiveTestSupport.WriteOwnerOnlyConfigOnHost("{ not-json");
        try
        {
            var result = RunMailer(
                entry,
                ["setup", "apply", "--config", configPath, "--non-interactive"]);
            Assert.Equal(SetupApplyNonInteractiveCommand.FailureExitCode, result.ExitCode);
            Assert.True(File.Exists(configPath));
            AssertExactlyOneCanonicalJsonLine(result.Stdout, out var parsed);
            Assert.False(parsed.GetProperty("ok").GetBoolean());
            Assert.Equal(
                SetupNonInteractiveResultCode.InvalidJson,
                parsed.GetProperty("code").GetString());
        }
        finally
        {
            TryDeleteTree(configPath);
        }
    }

    [Fact]
    public void Group_or_other_readable_config_is_rejected_and_retained()
    {
        Assert.SkipWhen(!OperatingSystem.IsLinux(), "Linux filesystem permission evidence.");
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        var entry = RequireMailerEntry();

        var configPath = SetupNonInteractiveTestSupport.WriteOwnerOnlyConfigOnHost(
            SetupNonInteractiveTestSupport.BuildLocalMailpitJson());
        try
        {
            File.SetUnixFileMode(
                configPath,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead);
            var result = RunMailer(
                entry,
                ["setup", "apply", "--config", configPath, "--non-interactive"]);
            Assert.Equal(SetupApplyNonInteractiveCommand.FailureExitCode, result.ExitCode);
            Assert.True(File.Exists(configPath));
            AssertExactlyOneCanonicalJsonLine(result.Stdout, out var parsed);
            Assert.False(parsed.GetProperty("ok").GetBoolean());
            Assert.Equal(
                SetupNonInteractiveResultCode.ConfigPermissionsRejected,
                parsed.GetProperty("code").GetString());
        }
        finally
        {
            TryDeleteTree(configPath);
        }
    }

    [Fact]
    public void Final_component_symlink_is_rejected_and_target_retained()
    {
        Assert.SkipWhen(!OperatingSystem.IsLinux(), "Linux symlink rejection evidence.");
        var entry = RequireMailerEntry();

        var targetPath = SetupNonInteractiveTestSupport.WriteOwnerOnlyConfigOnHost(
            SetupNonInteractiveTestSupport.BuildLocalMailpitJson());
        var linkDir = Path.Combine(Path.GetTempPath(), "amane-ni-link-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(linkDir);
        var linkPath = Path.Combine(linkDir, "config.json");
        try
        {
            File.CreateSymbolicLink(linkPath, targetPath);
            var result = RunMailer(
                entry,
                ["setup", "apply", "--config", linkPath, "--non-interactive"]);
            Assert.Equal(SetupApplyNonInteractiveCommand.FailureExitCode, result.ExitCode);
            Assert.True(File.Exists(targetPath));
            Assert.True(File.Exists(linkPath) || File.Exists(targetPath));
            AssertExactlyOneCanonicalJsonLine(result.Stdout, out var parsed);
            Assert.False(parsed.GetProperty("ok").GetBoolean());
            var code = parsed.GetProperty("code").GetString();
            Assert.True(
                code is SetupNonInteractiveResultCode.ConfigPathUnsafe
                    or SetupNonInteractiveResultCode.ConfigPathRejected
                    or SetupNonInteractiveResultCode.ConfigPermissionsRejected,
                code);
        }
        finally
        {
            TryDeleteTree(linkPath);
            TryDeleteTree(targetPath);
        }
    }

    [Fact]
    public void Oversize_config_is_rejected_and_retained()
    {
        Assert.SkipWhen(!OperatingSystem.IsLinux(), "Linux oversize config evidence.");
        var entry = RequireMailerEntry();

        var oversized = "{" + new string('a', SetupNonInteractiveConfigReader.MaxConfigBytes + 64) + "}";
        var configPath = SetupNonInteractiveTestSupport.WriteOwnerOnlyConfigOnHost(oversized);
        try
        {
            var result = RunMailer(
                entry,
                ["setup", "apply", "--config", configPath, "--non-interactive"]);
            Assert.Equal(SetupApplyNonInteractiveCommand.FailureExitCode, result.ExitCode);
            Assert.True(File.Exists(configPath));
            AssertExactlyOneCanonicalJsonLine(result.Stdout, out var parsed);
            Assert.False(parsed.GetProperty("ok").GetBoolean());
            Assert.Equal(
                SetupNonInteractiveResultCode.ConfigTooLarge,
                parsed.GetProperty("code").GetString());
        }
        finally
        {
            TryDeleteTree(configPath);
        }
    }

    /// <summary>
    /// Recognized invocations must emit exactly one JSON object on stdout, terminated by a single
    /// trailing newline and nothing else (no diagnostic prefix/suffix lines).
    /// </summary>
    private static void AssertExactlyOneCanonicalJsonLine(string stdout, out JsonElement parsed)
    {
        Assert.False(string.IsNullOrEmpty(stdout), "stdout must contain canonical JSON.");
        Assert.EndsWith("\n", stdout);
        Assert.False(stdout.EndsWith("\n\n", StringComparison.Ordinal), stdout);

        // Drop the required final newline, then require exactly one remaining line.
        var lines = stdout[..^1].Split('\n');
        var line = Assert.Single(lines);
        Assert.DoesNotContain('\r', line);
        using var doc = JsonDocument.Parse(line);
        parsed = doc.RootElement.Clone();
        Assert.Equal(JsonValueKind.Object, parsed.ValueKind);
    }

    private static string RequireMailerEntry()
    {
        Assert.True(
            TryResolveMailerEntry(out var entry),
            "Amane.Mailer.dll (or native Amane.Mailer binary) must be present next to the test output; silent skip is not allowed.");
        return entry;
    }

    private static bool TryResolveMailerEntry(out string entry)
    {
        var dll = Path.Combine(AppContext.BaseDirectory, "Amane.Mailer.dll");
        if (File.Exists(dll))
        {
            entry = dll;
            return true;
        }

        var exe = Path.Combine(
            AppContext.BaseDirectory,
            RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "Amane.Mailer.exe" : "Amane.Mailer");
        if (File.Exists(exe))
        {
            entry = exe;
            return true;
        }

        entry = string.Empty;
        return false;
    }

    private static (int ExitCode, string Stdout, string Stderr) RunMailer(
        string entry,
        IReadOnlyList<string> args)
    {
        var psi = new ProcessStartInfo
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };

        if (entry.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
        {
            psi.FileName = "dotnet";
            psi.ArgumentList.Add(entry);
        }
        else
        {
            psi.FileName = entry;
        }

        foreach (var arg in args)
        {
            psi.ArgumentList.Add(arg);
        }

        using var process = Process.Start(psi)
            ?? throw new InvalidOperationException("Failed to start Amane.Mailer process.");
        var stdoutTask = process.StandardOutput.ReadToEndAsync();
        var stderrTask = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(60_000))
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch
            {
                // best-effort
            }

            throw new TimeoutException("Amane.Mailer process did not exit within 60s.");
        }

        return (process.ExitCode, stdoutTask.GetAwaiter().GetResult(), stderrTask.GetAwaiter().GetResult());
    }

    private static void TryDeleteTree(string filePath)
    {
        try
        {
            var dir = Path.GetDirectoryName(filePath);
            if (!string.IsNullOrEmpty(dir) && Directory.Exists(dir))
            {
                Directory.Delete(dir, recursive: true);
            }
        }
        catch
        {
            // best-effort cleanup for temp fixtures
        }
    }
}
