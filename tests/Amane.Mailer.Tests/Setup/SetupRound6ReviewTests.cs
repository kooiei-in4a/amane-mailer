using System.Diagnostics;
using System.Text;
using Amane.Mailer.Setup;

namespace Amane.Mailer.Tests.Setup;

public sealed class SetupRound6ReviewTests
{
    [Theory]
    [InlineData("placeholder")]
    [InlineData("todo")]
    [InlineData("replace-with-release-tag")]
    public void Non_dry_run_generic_placeholder_image_tags_are_rejected(string tag)
    {
        var root = SetupTestFixtures.CreateManagedRoot();
        try
        {
            var request = SetupTestFixtures.LocalMailpitRequest(root);
            request = new SetupRequest
            {
                Mode = request.Mode,
                ManagedRootPath = request.ManagedRootPath,
                DryRun = false,
                Tenants = request.Tenants,
                TokenSecrets = request.TokenSecrets,
                MetricsBearerToken = request.MetricsBearerToken,
                ImageRepository = SetupImageDefaults.DefaultRepository,
                ImageTag = tag,
                RuntimeFileOwnership = request.RuntimeFileOwnership,
            };

            var result = new SetupCore().GenerateBundle(request);
            Assert.Equal(SetupResultCode.RejectedValidation, result.Code);
            Assert.Contains("placeholders are dry-run only", result.Message, StringComparison.Ordinal);
        }
        finally
        {
            TryDelete(root);
        }
    }

    [Fact]
    public void Mailer_cpus_thousands_separator_is_rejected()
    {
        var root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "amane-r6-cpus-" + Guid.NewGuid().ToString("N")));
        var request = SetupTestFixtures.LocalMailpitRequest(root, dryRun: true);
        request = new SetupRequest
        {
            Mode = request.Mode,
            ManagedRootPath = request.ManagedRootPath,
            DryRun = true,
            Tenants = request.Tenants,
            TokenSecrets = request.TokenSecrets,
            MetricsBearerToken = request.MetricsBearerToken,
            PublicEnvOverrides = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["MAILER_CPUS"] = "1,000",
            },
        };

        var result = new SetupCore().GenerateBundle(request);
        Assert.Equal(SetupResultCode.RejectedValidation, result.Code);
        Assert.Contains("MAILER_CPUS", result.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("registry.example.com:0/team/amane-mailer")]
    [InlineData("registry.example.com:99999/team/amane-mailer")]
    public void Image_repository_port_out_of_range_is_rejected(string repository)
    {
        var root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "amane-r6-port-" + Guid.NewGuid().ToString("N")));
        var request = SetupTestFixtures.LocalMailpitRequest(root, dryRun: true);
        request = new SetupRequest
        {
            Mode = request.Mode,
            ManagedRootPath = request.ManagedRootPath,
            DryRun = true,
            Tenants = request.Tenants,
            TokenSecrets = request.TokenSecrets,
            MetricsBearerToken = request.MetricsBearerToken,
            ImageRepository = repository,
            ImageTag = "1.2.0",
        };

        var result = new SetupCore().GenerateBundle(request);
        Assert.Equal(SetupResultCode.RejectedValidation, result.Code);
        Assert.Contains("port", result.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Mem_limit_overflowing_byte_size_is_rejected()
    {
        var root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "amane-r6-ovf-" + Guid.NewGuid().ToString("N")));
        var request = SetupTestFixtures.LocalMailpitRequest(root, dryRun: true);
        request = new SetupRequest
        {
            Mode = request.Mode,
            ManagedRootPath = request.ManagedRootPath,
            DryRun = true,
            Tenants = request.Tenants,
            TokenSecrets = request.TokenSecrets,
            MetricsBearerToken = request.MetricsBearerToken,
            PublicEnvOverrides = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["MAILER_MEM_LIMIT"] = "9007199254740993g",
            },
        };

        var result = new SetupCore().GenerateBundle(request);
        Assert.Equal(SetupResultCode.RejectedValidation, result.Code);
    }

    [Fact]
    public void Manual_conflict_does_not_create_generation_lock_file()
    {
        var root = SetupTestFixtures.CreateManagedRoot();
        try
        {
            File.WriteAllText(Path.Combine(root, ".env"), "MAILER_HTTP_PORT=8080\n", Encoding.UTF8);
            var result = new SetupCore().GenerateBundle(SetupTestFixtures.LocalMailpitRequest(root));
            Assert.Equal(SetupResultCode.RejectedConflictManual, result.Code);
            Assert.False(File.Exists(Path.Combine(root, SetupGenerationLock.LockFileName)));
        }
        finally
        {
            TryDelete(root);
        }
    }

    [Fact]
    public void Generation_lock_symlink_is_rejected_as_path_unsafe()
    {
        if (OperatingSystem.IsWindows())
        {
            Assert.Skip("symlink lock probe is exercised on Unix CI.");
            return;
        }

        var root = SetupTestFixtures.CreateManagedRoot();
        var outside = Path.Combine(Path.GetTempPath(), "amane-r6-lock-out-" + Guid.NewGuid().ToString("N"));
        try
        {
            File.WriteAllText(outside, "outside\n", Encoding.UTF8);
            var lockPath = Path.Combine(root, SetupGenerationLock.LockFileName);
            File.CreateSymbolicLink(lockPath, outside);

            var result = new SetupCore().GenerateBundle(SetupTestFixtures.LocalMailpitRequest(root));
            Assert.Equal(SetupResultCode.RejectedPathUnsafe, result.Code);
        }
        finally
        {
            TryDelete(root);
            try { File.Delete(outside); } catch { }
        }
    }

    [Fact]
    public void Docker_rejects_cpus_with_thousands_separator()
    {
        if (!IsDockerAvailable())
        {
            Assert.Skip("docker is required for cpus boundary proof.");
            return;
        }

        var bad = RunDocker(["run", "--rm", "--cpus=1,000", "alpine:3.20", "true"]);
        Assert.True(bad.ExitCode != 0, "docker should reject --cpus=1,000");

        var ok = RunDocker(["run", "--rm", "--cpus=1.0", "alpine:3.20", "true"]);
        Assert.True(ok.ExitCode == 0, ok.Stderr);
    }

    private static (int ExitCode, string Stderr) RunDocker(string[] args)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "docker",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var arg in args)
        {
            psi.ArgumentList.Add(arg);
        }

        using var process = Process.Start(psi) ?? throw new InvalidOperationException("docker failed to start");
        _ = process.StandardOutput.ReadToEnd();
        var stderr = process.StandardError.ReadToEnd();
        process.WaitForExit(180_000);
        return (process.ExitCode, stderr);
    }

    private static bool IsDockerAvailable()
    {
        try
        {
            var result = RunDocker(["version"]);
            return result.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }

    private static void TryDelete(string root)
    {
        try
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
        catch
        {
        }
    }
}
