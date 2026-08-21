using System.Diagnostics;
using System.Runtime.ExceptionServices;
using System.Text;
using Amane.Mailer.Configuration;
using Amane.Mailer.Setup;

namespace Amane.Mailer.Tests.Setup;

public sealed class SetupRound4ReviewTests
{
    [Fact]
    public void Token_and_webhook_secret_env_collision_is_rejected()
    {
        var root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "amane-r4-col-" + Guid.NewGuid().ToString("N")));
        var tenants = SetupTestFixtures.LocalMailpitTenants();
        tenants = new MailerTenantsFile
        {
            Version = tenants.Version,
            Environment = tenants.Environment,
            Tenants =
            [
                tenants.Tenants[0] with
                {
                    Webhook = new MailerWebhookConfig
                    {
                        Url = "https://hooks.example.com/mail",
                        SecretEnv = "MAIL_SERVICE_TOKEN",
                    },
                },
            ],
        };

        var request = new SetupRequest
        {
            Mode = SetupMode.LocalMailpit,
            ManagedRootPath = root,
            DryRun = true,
            Tenants = tenants,
            TokenSecrets = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["MAIL_SERVICE_TOKEN"] = "synthetic-mail-token-not-real",
            },
            MetricsBearerToken = "synthetic-metrics-token-not-real",
            WebhookSecrets = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["MAIL_SERVICE_TOKEN"] = "synthetic-webhook-secret-not-real",
            },
        };

        var result = new SetupCore().GenerateBundle(request);
        Assert.Equal(SetupResultCode.RejectedValidation, result.Code);
    }

    [Fact]
    public void Extra_token_secret_keys_are_rejected()
    {
        var root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "amane-r4-extra-" + Guid.NewGuid().ToString("N")));
        var request = SetupTestFixtures.LocalMailpitRequest(root, dryRun: true);
        request = new SetupRequest
        {
            Mode = request.Mode,
            ManagedRootPath = request.ManagedRootPath,
            DryRun = true,
            Tenants = request.Tenants,
            TokenSecrets = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["MAIL_SERVICE_TOKEN"] = "synthetic-mail-token-not-real",
                ["MAIL_SERVICE_TOKEN_DEVELOP"] = "extra-not-used",
            },
            MetricsBearerToken = request.MetricsBearerToken,
        };

        var result = new SetupCore().GenerateBundle(request);
        Assert.Equal(SetupResultCode.RejectedValidation, result.Code);
    }

    [Fact]
    public void Invalid_metrics_enabled_override_is_rejected()
    {
        var root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "amane-r4-met-" + Guid.NewGuid().ToString("N")));
        var request = SetupTestFixtures.LocalMailpitRequest(root, dryRun: true);
        request = new SetupRequest
        {
            Mode = request.Mode,
            ManagedRootPath = request.ManagedRootPath,
            DryRun = true,
            Tenants = request.Tenants,
            TokenSecrets = request.TokenSecrets,
            MetricsBearerToken = null,
            PublicEnvOverrides = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["MAILER_METRICS_ENABLED"] = "invalid",
            },
        };

        var result = new SetupCore().GenerateBundle(request);
        Assert.Equal(SetupResultCode.RejectedValidation, result.Code);
    }

    [Fact]
    public void Duplicate_tenant_id_is_rejected()
    {
        var root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "amane-r4-dup-" + Guid.NewGuid().ToString("N")));
        var tenants = SetupTestFixtures.LocalMailpitTenants();
        var first = tenants.Tenants[0];
        tenants = new MailerTenantsFile
        {
            Version = tenants.Version,
            Environment = tenants.Environment,
            Tenants =
            [
                first,
                first with { Name = "duplicate-name", TokenEnv = "MAIL_SERVICE_TOKEN_DEVELOP" },
            ],
        };

        var request = new SetupRequest
        {
            Mode = SetupMode.LocalMailpit,
            ManagedRootPath = root,
            DryRun = true,
            Tenants = tenants,
            TokenSecrets = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["MAIL_SERVICE_TOKEN"] = "synthetic-mail-token-not-real",
                ["MAIL_SERVICE_TOKEN_DEVELOP"] = "synthetic-develop-token-not-real",
            },
            MetricsBearerToken = "synthetic-metrics-token-not-real",
        };

        var result = new SetupCore().GenerateBundle(request);
        Assert.Equal(SetupResultCode.RejectedValidation, result.Code);
        Assert.Contains("Duplicate tenant_id", result.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Env_file_literal_round_trip_through_docker_compose_run()
    {
        if (!IsDockerComposeAvailable())
        {
            Assert.Skip("docker compose is required for env-file round-trip proof.");
            return;
        }

        var work = Path.Combine(Path.GetTempPath(), "amane-compose-cfg-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(work);
        Exception? testFailure = null;
        try
        {
            const string secret = "tok$ENV${HOME}a\\b\"c #frag";
            var envText = SetupConfigurationMaterializer.FormatEnvFile(new Dictionary<string, string>
            {
                ["MAIL_SERVICE_TOKEN"] = secret,
            });
            File.WriteAllText(
                Path.Combine(work, "secrets.env"),
                envText,
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            File.WriteAllText(
                Path.Combine(work, "compose.yaml"),
                "services:\n  probe:\n    image: alpine:3.20\n    env_file:\n      - secrets.env\n    command: [\"printenv\", \"MAIL_SERVICE_TOKEN\"]\n",
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

            // `docker compose config` may still show $$ escapes; runtime interpolation is what
            // containers observe, so prove round-trip via compose run.
            var psi = new ProcessStartInfo
            {
                FileName = "docker",
                ArgumentList =
                {
                    "compose",
                    "--project-directory",
                    work,
                    "run",
                    "--rm",
                    "--no-deps",
                    "probe",
                },
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            using var process = Process.Start(psi);
            Assert.NotNull(process);
            var stdout = process!.StandardOutput.ReadToEnd().TrimEnd('\r', '\n');
            _ = process.StandardError.ReadToEnd();
            process.WaitForExit(180_000);
            Assert.True(process.ExitCode == 0, "Docker Compose run did not complete successfully.");
            Assert.True(
                string.Equals(secret, stdout, StringComparison.Ordinal),
                "Compose env-file round-trip did not preserve the literal value.");
        }
        catch (Exception ex)
        {
            testFailure = ex;
        }
        finally
        {
            Exception? cleanupFailure = null;
            try
            {
                TearDownComposeProject(work, Path.GetFileName(work));
            }
            catch (Exception ex)
            {
                cleanupFailure = ex;
            }

            try
            {
                Directory.Delete(work, recursive: true);
            }
            catch (Exception ex)
            {
                cleanupFailure ??= ex;
            }

            if (testFailure is not null && cleanupFailure is not null)
            {
                throw new AggregateException(
                    "Docker Compose test failed and its project cleanup also failed.",
                    testFailure,
                    cleanupFailure);
            }

            if (testFailure is not null)
            {
                ExceptionDispatchInfo.Capture(testFailure).Throw();
            }

            if (cleanupFailure is not null)
            {
                throw cleanupFailure;
            }
        }
    }

    private static void TearDownComposeProject(string work, string projectName)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "docker",
            ArgumentList =
            {
                "compose",
                "--project-directory",
                work,
                "--project-name",
                projectName,
                "down",
                "--remove-orphans",
            },
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        using var process = Process.Start(psi)
            ?? throw new InvalidOperationException("Docker Compose cleanup process could not be started.");
        var stdoutTask = process.StandardOutput.ReadToEndAsync();
        var stderrTask = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(180_000))
        {
            try { process.Kill(entireProcessTree: true); } catch { }
            throw new TimeoutException("Docker Compose project cleanup timed out.");
        }

        Task.WaitAll(stdoutTask, stderrTask);
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"Docker Compose project cleanup failed with exit code {process.ExitCode}.");
        }
    }


    [Fact]
    public void Compose_project_name_with_uppercase_or_dot_is_rejected()
    {
        var root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "amane-r4-proj-" + Guid.NewGuid().ToString("N")));
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
                ["COMPOSE_PROJECT_NAME"] = "Example.Project",
            },
        };

        var result = new SetupCore().GenerateBundle(request);
        Assert.Equal(SetupResultCode.RejectedValidation, result.Code);
    }

    [Fact]
    public void Retention_days_above_runtime_max_are_rejected()
    {
        var root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "amane-r4-ret-" + Guid.NewGuid().ToString("N")));
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
                ["MAILER_RETENTION_DAYS"] = "3651",
            },
        };

        var result = new SetupCore().GenerateBundle(request);
        Assert.Equal(SetupResultCode.RejectedValidation, result.Code);
    }

    [Fact]
    public void Empty_image_repository_is_rejected()
    {
        var root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "amane-r4-img-" + Guid.NewGuid().ToString("N")));
        var request = SetupTestFixtures.LocalMailpitRequest(root, dryRun: true);
        request = new SetupRequest
        {
            Mode = request.Mode,
            ManagedRootPath = request.ManagedRootPath,
            DryRun = true,
            Tenants = request.Tenants,
            TokenSecrets = request.TokenSecrets,
            MetricsBearerToken = request.MetricsBearerToken,
            ImageRepository = "",
        };

        var result = new SetupCore().GenerateBundle(request);
        Assert.Equal(SetupResultCode.RejectedValidation, result.Code);
    }

    [Fact]
    public void Private_registry_repository_with_port_is_accepted()
    {
        var root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "amane-r4-reg-" + Guid.NewGuid().ToString("N")));
        var request = SetupTestFixtures.LocalMailpitRequest(root, dryRun: true);
        request = new SetupRequest
        {
            Mode = request.Mode,
            ManagedRootPath = request.ManagedRootPath,
            DryRun = true,
            Tenants = request.Tenants,
            TokenSecrets = request.TokenSecrets,
            MetricsBearerToken = request.MetricsBearerToken,
            ImageRepository = "registry.example.com:5000/team/amane-mailer",
            ImageTag = "1.2.0",
        };

        var result = new SetupCore().GenerateBundle(request);
        Assert.Equal(SetupResultCode.DryRunPlan, result.Code);
    }

    private static bool IsDockerComposeAvailable()
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "docker",
                ArgumentList = { "compose", "version" },
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            using var process = Process.Start(psi);
            if (process is null)
            {
                return false;
            }

            process.WaitForExit(30_000);
            return process.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }
}
