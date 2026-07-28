using System.Diagnostics;
using System.Text;
using Amane.Mailer.Setup;

namespace Amane.Mailer.Tests.Setup;

public sealed class SetupRound5ReviewTests
{
    [Fact]
    public void Non_dry_run_without_image_tag_is_rejected()
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
                ImageTag = null,
                RuntimeFileOwnership = request.RuntimeFileOwnership,
            };

            var result = new SetupCore().GenerateBundle(request);
            Assert.Equal(SetupResultCode.RejectedValidation, result.Code);
            Assert.Contains("Image tag is required", result.Message, StringComparison.Ordinal);
        }
        finally
        {
            TryDelete(root);
        }
    }

    [Fact]
    public void Non_dry_run_placeholder_image_tag_is_rejected()
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
                ImageTag = SetupImageDefaults.DryRunImageTagPlaceholder,
                RuntimeFileOwnership = request.RuntimeFileOwnership,
            };

            var result = new SetupCore().GenerateBundle(request);
            Assert.Equal(SetupResultCode.RejectedValidation, result.Code);
        }
        finally
        {
            TryDelete(root);
        }
    }

    [Fact]
    public void Image_keys_in_public_env_overrides_are_rejected()
    {
        var root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "amane-r5-imgov-" + Guid.NewGuid().ToString("N")));
        var request = SetupTestFixtures.LocalMailpitRequest(root, dryRun: true);
        request = new SetupRequest
        {
            Mode = request.Mode,
            ManagedRootPath = request.ManagedRootPath,
            DryRun = true,
            Tenants = request.Tenants,
            TokenSecrets = request.TokenSecrets,
            MetricsBearerToken = request.MetricsBearerToken,
            ImageTag = "1.2.0",
            PublicEnvOverrides = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["MAILER_IMAGE_TAG"] = "other-tag",
            },
        };

        var result = new SetupCore().GenerateBundle(request);
        Assert.Equal(SetupResultCode.RejectedValidation, result.Code);
    }

    [Fact]
    public void Mem_limit_below_six_megabytes_is_rejected()
    {
        var root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "amane-r5-mem-" + Guid.NewGuid().ToString("N")));
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
                ["MAILER_MEM_LIMIT"] = "1m",
            },
        };

        var result = new SetupCore().GenerateBundle(request);
        Assert.Equal(SetupResultCode.RejectedValidation, result.Code);
    }

    [Fact]
    public void Log_max_size_terabyte_unit_is_rejected()
    {
        var root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "amane-r5-log-" + Guid.NewGuid().ToString("N")));
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
                ["LOG_MAX_SIZE"] = "1t",
            },
        };

        var result = new SetupCore().GenerateBundle(request);
        Assert.Equal(SetupResultCode.RejectedValidation, result.Code);
    }

    [Fact]
    public void Healthcheck_retries_do_not_use_retention_day_ceiling()
    {
        var root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "amane-r5-hc-" + Guid.NewGuid().ToString("N")));
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
                ["MAILER_HEALTHCHECK_RETRIES"] = "101",
            },
        };

        var result = new SetupCore().GenerateBundle(request);
        Assert.Equal(SetupResultCode.RejectedValidation, result.Code);
        Assert.Contains("HEALTHCHECK_RETRIES", result.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Docker_rejects_memory_limit_below_six_megabytes()
    {
        if (!IsDockerAvailable())
        {
            Assert.Skip("docker is required for memory-limit boundary proof.");
            return;
        }

        var low = RunDocker(["run", "--rm", "--memory=1m", "alpine:3.20", "true"]);
        Assert.True(low.ExitCode != 0, "docker should reject --memory=1m");

        var ok = RunDocker(["run", "--rm", "--memory=6m", "alpine:3.20", "true"]);
        Assert.True(ok.ExitCode == 0, ok.Stderr);
    }

    [Fact]
    public void Concurrent_fresh_root_generation_does_not_drop_sealing_key()
    {
        var root = SetupTestFixtures.CreateManagedRoot();
        try
        {
            SetupResult? first = null;
            SetupResult? second = null;
            Exception? firstEx = null;
            Exception? secondEx = null;

            var barrier = new Barrier(2);
            var t1 = new Thread(() =>
            {
                try
                {
                    barrier.SignalAndWait();
                    first = new SetupCore(bundleIdFactory: static () => "concurrent-a")
                        .GenerateBundle(SetupTestFixtures.LocalMailpitRequest(root));
                }
                catch (Exception ex)
                {
                    firstEx = ex;
                }
            });
            var t2 = new Thread(() =>
            {
                try
                {
                    barrier.SignalAndWait();
                    second = new SetupCore(bundleIdFactory: static () => "concurrent-b")
                        .GenerateBundle(SetupTestFixtures.LocalMailpitRequest(root));
                }
                catch (Exception ex)
                {
                    secondEx = ex;
                }
            });

            t1.Start();
            t2.Start();
            t1.Join();
            t2.Join();

            Assert.Null(firstEx);
            Assert.Null(secondEx);
            Assert.NotNull(first);
            Assert.NotNull(second);

            var codes = new[] { first!.Code, second!.Code };
            Assert.Contains(SetupResultCode.Succeeded, codes);
            Assert.True(
                codes.Count(c => c == SetupResultCode.Succeeded) == 1
                || codes.All(c => c is SetupResultCode.Succeeded or SetupResultCode.RejectedConcurrentExecution
                    or SetupResultCode.RejectedBundleExists),
                $"unexpected concurrent codes: {first.Code}, {second.Code}");

            if (codes.Contains(SetupResultCode.Succeeded))
            {
                Assert.True(File.Exists(SetupBundleLayout.HostSealingKeyPath(root)));
            }
        }
        finally
        {
            TryDelete(root);
        }
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
