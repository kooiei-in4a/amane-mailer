using System.Diagnostics;
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
            var stderr = process.StandardError.ReadToEnd();
            process.WaitForExit(180_000);
            Assert.True(process.ExitCode == 0, stderr);
            Assert.Equal(secret, stdout);
        }
        finally
        {
            try { Directory.Delete(work, recursive: true); } catch { }
        }
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
