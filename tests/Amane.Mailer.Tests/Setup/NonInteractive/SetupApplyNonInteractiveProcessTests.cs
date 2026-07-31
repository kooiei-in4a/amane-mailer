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
        if (!TryResolveMailerEntry(out var entry))
        {
            return;
        }

        var result = RunMailer(entry, ["setup", "apply"]);
        Assert.Equal(SetupApplyNonInteractiveCommand.UsageErrorExitCode, result.ExitCode);
        Assert.True(string.IsNullOrWhiteSpace(result.Stdout), result.Stdout);
        Assert.Contains("Usage:", result.Stderr, StringComparison.Ordinal);
    }

    [Fact]
    public void Malformed_json_emits_one_canonical_json_and_keeps_config_file()
    {
        if (!OperatingSystem.IsLinux() || !TryResolveMailerEntry(out var entry))
        {
            return;
        }

        var configPath = SetupNonInteractiveTestSupport.WriteOwnerOnlyConfigOnHost("{ not-json");
        try
        {
            var result = RunMailer(
                entry,
                ["setup", "apply", "--config", configPath, "--non-interactive"]);
            Assert.Equal(SetupApplyNonInteractiveCommand.FailureExitCode, result.ExitCode);
            Assert.True(File.Exists(configPath));
            AssertSingleJsonLine(result.Stdout, out var parsed);
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
        if (!OperatingSystem.IsLinux() || !TryResolveMailerEntry(out var entry))
        {
            return;
        }

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
            AssertSingleJsonLine(result.Stdout, out var parsed);
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
        if (!OperatingSystem.IsLinux() || !TryResolveMailerEntry(out var entry))
        {
            return;
        }

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
            AssertSingleJsonLine(result.Stdout, out var parsed);
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
        if (!OperatingSystem.IsLinux() || !TryResolveMailerEntry(out var entry))
        {
            return;
        }

        var oversized = "{" + new string('a', SetupNonInteractiveConfigReader.MaxConfigBytes + 64) + "}";
        var configPath = SetupNonInteractiveTestSupport.WriteOwnerOnlyConfigOnHost(oversized);
        try
        {
            var result = RunMailer(
                entry,
                ["setup", "apply", "--config", configPath, "--non-interactive"]);
            Assert.Equal(SetupApplyNonInteractiveCommand.FailureExitCode, result.ExitCode);
            Assert.True(File.Exists(configPath));
            AssertSingleJsonLine(result.Stdout, out var parsed);
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

    private static void AssertSingleJsonLine(string stdout, out JsonElement parsed)
    {
        var lines = stdout
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        Assert.True(lines.Length >= 1, stdout);
        // Canonical contract: recognized invocations emit exactly one JSON object on stdout.
        Assert.Equal(1, lines.Count(static line => line.StartsWith('{') && line.EndsWith('}')));
        using var doc = JsonDocument.Parse(lines.First(static line => line.StartsWith('{')));
        parsed = doc.RootElement.Clone();
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
