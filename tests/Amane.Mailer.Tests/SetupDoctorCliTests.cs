using Amane.Mailer.Admin;
using Amane.Mailer.Configuration;
using Amane.Mailer.Operations;
using Amane.Mailer.Tests.TestSupport;
using Microsoft.Extensions.Configuration;

namespace Amane.Mailer.Tests;

public sealed class SetupDoctorCliTests
{
    private const string ValidAcsSecret =
        "Endpoint=https://example.communication.azure.com/;AccessKey=not-a-real-key";

    [Fact]
    public async Task local_mailpit_mode_passes_with_valid_mailpit_configuration()
    {
        using var scratch = new DoctorScratch();
        scratch.WriteTenantFile(CreateMailpitTenantJson());
        Environment.SetEnvironmentVariable("MAIL_SERVICE_TOKEN", "local-mail-service-token");

        try
        {
            var configuration = scratch.BuildConfiguration(new Dictionary<string, string?>
            {
                ["Mailer:Worker:Enabled"] = "false",
                ["Mailer:Metrics:Enabled"] = "false",
                ["MAILER_HTTP_PORT"] = scratch.FreePort.ToString(),
            });

            var (exitCode, output) = await RunDoctorAsync(
                configuration,
                SetupDoctorMode.LocalMailpit,
                scratch.ComposePath);

            Assert.Equal(SetupDoctorCommand.SuccessExitCode, exitCode);
            Assert.Contains("[PASS] tenant_file:", output, StringComparison.Ordinal);
            Assert.Contains("[PASS] mode_profile:", output, StringComparison.Ordinal);
            Assert.Contains("Summary: PASS=", output, StringComparison.Ordinal);
        }
        finally
        {
            Environment.SetEnvironmentVariable("MAIL_SERVICE_TOKEN", null);
        }
    }

    [Fact]
    public async Task staging_no_send_mode_warns_when_live_sending_is_true()
    {
        using var scratch = new DoctorScratch();
        scratch.WriteTenantFile(CreateAcsTenantJson(liveSending: true));
        Environment.SetEnvironmentVariable("MAIL_SERVICE_TOKEN", "local-mail-service-token");

        try
        {
            var configuration = scratch.BuildConfiguration(new Dictionary<string, string?>
            {
                ["Mailer:Worker:Enabled"] = "false",
                ["Mailer:Metrics:Enabled"] = "false",
                ["ACS_CONNECTION_STRING_FILE"] = scratch.WriteAcsSecret(ValidAcsSecret),
                ["MAILER_HTTP_PORT"] = scratch.FreePort.ToString(),
            });

            var (exitCode, output) = await RunDoctorAsync(
                configuration,
                SetupDoctorMode.StagingNoSend,
                scratch.ComposePath);

            Assert.Equal(SetupDoctorCommand.SuccessExitCode, exitCode);
            Assert.Contains("[WARN] mode_live_sending:", output, StringComparison.Ordinal);
        }
        finally
        {
            Environment.SetEnvironmentVariable("MAIL_SERVICE_TOKEN", null);
        }
    }

    [Fact]
    public async Task production_acs_mode_reports_blocked_live_send()
    {
        using var scratch = new DoctorScratch();
        scratch.WriteTenantFile(CreateAcsTenantJson(liveSending: true));
        Environment.SetEnvironmentVariable("MAIL_SERVICE_TOKEN", "local-mail-service-token");

        try
        {
            var configuration = scratch.BuildConfiguration(new Dictionary<string, string?>
            {
                ["Mailer:Worker:Enabled"] = "false",
                ["Mailer:Metrics:Enabled"] = "false",
                ["ACS_CONNECTION_STRING_FILE"] = scratch.WriteAcsSecret(ValidAcsSecret),
                ["MAILER_HTTP_PORT"] = scratch.FreePort.ToString(),
            });

            var (exitCode, output) = await RunDoctorAsync(
                configuration,
                SetupDoctorMode.ProductionAcs,
                scratch.ComposePath);

            Assert.Equal(SetupDoctorCommand.FailureExitCode, exitCode);
            Assert.Contains("[FAIL] production_live_send:", output, StringComparison.Ordinal);
            Assert.Contains("[ACTION] production_live_send:", output, StringComparison.Ordinal);
        }
        finally
        {
            Environment.SetEnvironmentVariable("MAIL_SERVICE_TOKEN", null);
        }
    }

    [Fact]
    public async Task queue_mode_fails_when_connection_string_is_missing()
    {
        using var scratch = new DoctorScratch();
        scratch.WriteTenantFile(CreateAcsTenantJson(liveSending: false));
        Environment.SetEnvironmentVariable("MAIL_SERVICE_TOKEN", "local-mail-service-token");

        try
        {
            var configuration = scratch.BuildConfiguration(new Dictionary<string, string?>
            {
                ["Mailer:Worker:Enabled"] = "false",
                ["Mailer:Metrics:Enabled"] = "false",
                ["MAILER_BOUNCE_INGESTION"] = "queue",
                ["MAILER_BOUNCE_QUEUE_NAME"] = "bounce-events",
                ["MAILER_HTTP_PORT"] = scratch.FreePort.ToString(),
            });

            var (exitCode, output) = await RunDoctorAsync(
                configuration,
                SetupDoctorMode.ProductionQueue,
                scratch.ComposePath);

            Assert.Equal(SetupDoctorCommand.FailureExitCode, exitCode);
            Assert.Contains("[FAIL] bounce_queue_secret:", output, StringComparison.Ordinal);
        }
        finally
        {
            Environment.SetEnvironmentVariable("MAIL_SERVICE_TOKEN", null);
        }
    }

    [Fact]
    public async Task queue_mode_fails_when_queue_name_is_missing()
    {
        using var scratch = new DoctorScratch();
        scratch.WriteTenantFile(CreateAcsTenantJson(liveSending: false));
        Environment.SetEnvironmentVariable("MAIL_SERVICE_TOKEN", "local-mail-service-token");

        try
        {
            var configuration = scratch.BuildConfiguration(new Dictionary<string, string?>
            {
                ["Mailer:Worker:Enabled"] = "false",
                ["Mailer:Metrics:Enabled"] = "false",
                ["MAILER_BOUNCE_INGESTION"] = "queue",
                ["MAILER_BOUNCE_QUEUE_CONNECTION_STRING"] = "DefaultEndpointsProtocol=https;AccountName=example;AccountKey=abc;EndpointSuffix=core.windows.net",
                ["MAILER_HTTP_PORT"] = scratch.FreePort.ToString(),
            });

            var (exitCode, output) = await RunDoctorAsync(
                configuration,
                SetupDoctorMode.ProductionQueue,
                scratch.ComposePath);

            Assert.Equal(SetupDoctorCommand.FailureExitCode, exitCode);
            Assert.Contains("[FAIL] bounce_queue_name:", output, StringComparison.Ordinal);
        }
        finally
        {
            Environment.SetEnvironmentVariable("MAIL_SERVICE_TOKEN", null);
        }
    }

    [Fact]
    public async Task fails_when_token_env_is_missing()
    {
        using var scratch = new DoctorScratch();
        scratch.WriteTenantFile(CreateMailpitTenantJson());
        Environment.SetEnvironmentVariable("MAIL_SERVICE_TOKEN", null);

        var configuration = scratch.BuildConfiguration(new Dictionary<string, string?>
        {
            ["Mailer:Worker:Enabled"] = "false",
            ["Mailer:Metrics:Enabled"] = "false",
            ["MAILER_HTTP_PORT"] = scratch.FreePort.ToString(),
        });

        var (exitCode, output) = await RunDoctorAsync(
            configuration,
            SetupDoctorMode.LocalMailpit,
            scratch.ComposePath);

        Assert.Equal(SetupDoctorCommand.FailureExitCode, exitCode);
        Assert.Contains("[FAIL] tenant_0_token:", output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task fails_when_token_env_looks_like_placeholder()
    {
        using var scratch = new DoctorScratch();
        scratch.WriteTenantFile(CreateMailpitTenantJson());
        Environment.SetEnvironmentVariable("MAIL_SERVICE_TOKEN", "replace-with-your-token");

        try
        {
            var configuration = scratch.BuildConfiguration(new Dictionary<string, string?>
            {
                ["Mailer:Worker:Enabled"] = "false",
                ["Mailer:Metrics:Enabled"] = "false",
                ["MAILER_HTTP_PORT"] = scratch.FreePort.ToString(),
            });

            var (exitCode, output) = await RunDoctorAsync(
                configuration,
                SetupDoctorMode.LocalMailpit,
                scratch.ComposePath);

            Assert.Equal(SetupDoctorCommand.FailureExitCode, exitCode);
            Assert.Contains("[FAIL] tenant_0_token:", output, StringComparison.Ordinal);
        }
        finally
        {
            Environment.SetEnvironmentVariable("MAIL_SERVICE_TOKEN", null);
        }
    }

    [Fact]
    public async Task fails_when_metrics_bearer_is_missing_in_production()
    {
        using var scratch = new DoctorScratch();
        scratch.WriteTenantFile(CreateMailpitTenantJson());
        Environment.SetEnvironmentVariable("MAIL_SERVICE_TOKEN", "local-mail-service-token");
        Environment.SetEnvironmentVariable("ASPNETCORE_ENVIRONMENT", "Production");

        try
        {
            var configuration = scratch.BuildConfiguration(new Dictionary<string, string?>
            {
                ["Mailer:Worker:Enabled"] = "false",
                ["Mailer:Metrics:Enabled"] = "true",
                ["MAILER_HTTP_PORT"] = scratch.FreePort.ToString(),
            });

            var (exitCode, output) = await RunDoctorAsync(
                configuration,
                SetupDoctorMode.LocalMailpit,
                scratch.ComposePath);

            Assert.Equal(SetupDoctorCommand.FailureExitCode, exitCode);
            Assert.Contains("[FAIL] metrics_bearer:", output, StringComparison.Ordinal);
        }
        finally
        {
            Environment.SetEnvironmentVariable("MAIL_SERVICE_TOKEN", null);
            Environment.SetEnvironmentVariable("ASPNETCORE_ENVIRONMENT", null);
        }
    }

    [Fact]
    public async Task fails_when_admin_allow_http_is_true_in_production()
    {
        using var scratch = new DoctorScratch();
        scratch.WriteTenantFile(CreateMailpitTenantJson());
        Environment.SetEnvironmentVariable("MAIL_SERVICE_TOKEN", "local-mail-service-token");
        Environment.SetEnvironmentVariable("ASPNETCORE_ENVIRONMENT", "Production");

        try
        {
            var configuration = scratch.BuildConfiguration(new Dictionary<string, string?>
            {
                ["Mailer:Worker:Enabled"] = "false",
                ["Mailer:Metrics:Enabled"] = "false",
                ["AMANE_ADMIN_ENABLED"] = "true",
                ["AMANE_ADMIN_PASSWORD_HASH"] = AdminPasswordHasher.Hash("password"),
                ["AMANE_ADMIN_ALLOW_HTTP"] = "true",
                ["MAILER_HTTP_PORT"] = scratch.FreePort.ToString(),
            });

            var (exitCode, output) = await RunDoctorAsync(
                configuration,
                SetupDoctorMode.LocalMailpit,
                scratch.ComposePath);

            Assert.Equal(SetupDoctorCommand.FailureExitCode, exitCode);
            Assert.Contains("[FAIL] admin_https:", output, StringComparison.Ordinal);
        }
        finally
        {
            Environment.SetEnvironmentVariable("MAIL_SERVICE_TOKEN", null);
            Environment.SetEnvironmentVariable("ASPNETCORE_ENVIRONMENT", null);
        }
    }

    [Fact]
    public async Task output_does_not_echo_secret_like_values()
    {
        const string secretToken = "super-secret-token-value-12345";
        const string secretAcs = "Endpoint=https://secret.communication.azure.com/;AccessKey=super-secret-key";

        using var scratch = new DoctorScratch();
        scratch.WriteTenantFile(CreateAcsTenantJson(liveSending: true));
        Environment.SetEnvironmentVariable("MAIL_SERVICE_TOKEN", secretToken);

        try
        {
            var configuration = scratch.BuildConfiguration(new Dictionary<string, string?>
            {
                ["Mailer:Worker:Enabled"] = "false",
                ["Mailer:Metrics:Enabled"] = "false",
                ["ACS_CONNECTION_STRING_FILE"] = scratch.WriteAcsSecret(secretAcs),
                ["MAILER_HTTP_PORT"] = scratch.FreePort.ToString(),
            });

            var (_, output) = await RunDoctorAsync(
                configuration,
                SetupDoctorMode.StagingVerification,
                scratch.ComposePath);

            Assert.DoesNotContain(secretToken, output, StringComparison.Ordinal);
            Assert.DoesNotContain("super-secret-key", output, StringComparison.Ordinal);
            Assert.DoesNotContain("secret.communication.azure.com", output, StringComparison.Ordinal);
        }
        finally
        {
            Environment.SetEnvironmentVariable("MAIL_SERVICE_TOKEN", null);
        }
    }

    [Fact]
    public async Task repeated_runs_do_not_modify_tenant_or_secret_files()
    {
        using var scratch = new DoctorScratch();
        scratch.WriteTenantFile(CreateMailpitTenantJson());
        var acsPath = scratch.WriteAcsSecret(ValidAcsSecret);
        Environment.SetEnvironmentVariable("MAIL_SERVICE_TOKEN", "local-mail-service-token");

        try
        {
            var configuration = scratch.BuildConfiguration(new Dictionary<string, string?>
            {
                ["Mailer:Worker:Enabled"] = "false",
                ["Mailer:Metrics:Enabled"] = "false",
                ["ACS_CONNECTION_STRING_FILE"] = acsPath,
                ["MAILER_HTTP_PORT"] = scratch.FreePort.ToString(),
            });

            var tenantBytesBefore = File.ReadAllBytes(scratch.TenantPath);
            var acsBytesBefore = File.ReadAllBytes(acsPath);
            var tenantWriteBefore = File.GetLastWriteTimeUtc(scratch.TenantPath);
            var acsWriteBefore = File.GetLastWriteTimeUtc(acsPath);

            await RunDoctorAsync(configuration, SetupDoctorMode.LocalMailpit, scratch.ComposePath);
            await RunDoctorAsync(configuration, SetupDoctorMode.LocalMailpit, scratch.ComposePath);

            Assert.Equal(tenantBytesBefore, File.ReadAllBytes(scratch.TenantPath));
            Assert.Equal(acsBytesBefore, File.ReadAllBytes(acsPath));
            Assert.Equal(tenantWriteBefore, File.GetLastWriteTimeUtc(scratch.TenantPath));
            Assert.Equal(acsWriteBefore, File.GetLastWriteTimeUtc(acsPath));
        }
        finally
        {
            Environment.SetEnvironmentVariable("MAIL_SERVICE_TOKEN", null);
        }
    }

    [Fact]
    public async Task usage_error_when_mode_is_missing()
    {
        var configuration = new ConfigurationBuilder().Build();
        var output = new StringWriter();
        var error = new StringWriter();

        var exitCode = await MailerCliHost.RunSetupDoctorAsync(
            configuration,
            ["setup", "doctor"],
            output,
            error,
            TestContext.Current.CancellationToken);

        Assert.Equal(SetupDoctorCommand.UsageErrorExitCode, exitCode);
        Assert.Contains("--mode is required", error.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void placeholder_detector_matches_validate_tenant_config_rules()
    {
        Assert.True(ConfigurationPlaceholderDetector.LooksLikePlaceholder("replace-with-token"));
        Assert.True(ConfigurationPlaceholderDetector.LooksLikePlaceholder("<token>"));
        Assert.False(ConfigurationPlaceholderDetector.LooksLikePlaceholder("local-mail-service-token"));
    }

    private static async Task<(int ExitCode, string Output)> RunDoctorAsync(
        IConfiguration configuration,
        SetupDoctorMode mode,
        string composePath)
    {
        var modeArg = mode switch
        {
            SetupDoctorMode.LocalMailpit => "local-mailpit",
            SetupDoctorMode.StagingNoSend => "staging-no-send",
            SetupDoctorMode.StagingVerification => "staging-verification",
            SetupDoctorMode.ProductionAcs => "production-acs",
            SetupDoctorMode.ProductionQueue => "production-queue",
            _ => throw new ArgumentOutOfRangeException(nameof(mode)),
        };

        var output = new StringWriter();
        var error = new StringWriter();
        var command = new SetupDoctorCommand(
            configuration,
            mode,
            composePath,
            output,
            error);

        var exitCode = await command.ExecuteAsync(TestContext.Current.CancellationToken);
        return (exitCode, output.ToString());
    }

    private static string CreateMailpitTenantJson() =>
        """
        {
          "version": 1,
          "environment": "develop",
          "tenants": [
            {
              "tenant_id": "00000000-0000-0000-0000-000000000101",
              "name": "example-develop",
              "source_services": ["example-service"],
              "default_from": {
                "email": "noreply@example.com",
                "display_name": "Example Service"
              },
              "token_env": "MAIL_SERVICE_TOKEN",
              "provider": "mailpit",
              "live_sending": false,
              "metadata_max_bytes": 4096,
              "retry": {
                "max_attempts": 10,
                "initial_delay_seconds": 10,
                "max_delay_seconds": 300
              }
            }
          ]
        }
        """;

    private static string CreateAcsTenantJson(bool liveSending) =>
        $$"""
        {
          "version": 1,
          "environment": "staging",
          "tenants": [
            {
              "tenant_id": "00000000-0000-0000-0000-000000000201",
              "name": "example-staging",
              "source_services": ["example-service"],
              "default_from": {
                "email": "noreply@example.com",
                "display_name": "Example Service"
              },
              "token_env": "MAIL_SERVICE_TOKEN",
              "provider": "acs",
              "live_sending": {{liveSending.ToString().ToLowerInvariant()}},
              "metadata_max_bytes": 4096,
              "retry": {
                "max_attempts": 10,
                "initial_delay_seconds": 10,
                "max_delay_seconds": 300
              }
            }
          ]
        }
        """;

    private sealed class DoctorScratch : IDisposable
    {
        private readonly string _root;

        public DoctorScratch()
        {
            _root = Path.Combine(Path.GetTempPath(), "amane-mailer-setup-doctor", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_root);
            DataDirectory = Path.Combine(_root, "data");
            Directory.CreateDirectory(DataDirectory);
            TenantPath = Path.Combine(_root, "tenants.json");
            ComposePath = Path.Combine(_root, "compose.yml");
            File.WriteAllText(ComposePath, "services:\n  mailer:\n    image: example\n");
            FreePort = FindFreePort();
        }

        public string DataDirectory { get; }

        public string TenantPath { get; }

        public string ComposePath { get; }

        public int FreePort { get; }

        public void WriteTenantFile(string json) => File.WriteAllText(TenantPath, json);

        public string WriteAcsSecret(string content)
        {
            var directory = Path.Combine(_root, "secrets", "acs");
            if (OperatingSystem.IsLinux())
            {
                TestSecretDirectory.CreateSecure(directory);
            }
            else
            {
                Directory.CreateDirectory(directory);
            }

            var path = Path.Combine(directory, AcsSecretFileNames.CanonicalFileName);
            File.WriteAllText(path, content);
            return path;
        }

        public IConfiguration BuildConfiguration(IReadOnlyDictionary<string, string?> extra)
        {
            var values = new Dictionary<string, string?>(extra)
            {
                ["ConnectionStrings:Mailer"] = $"Data Source={Path.Combine(DataDirectory, "mailer.db")}",
                ["MAILER_TENANTS_PATH"] = TenantPath,
            };

            return new ConfigurationBuilder()
                .AddInMemoryCollection(values)
                .Build();
        }

        public void Dispose()
        {
            try
            {
                Directory.Delete(_root, recursive: true);
            }
            catch
            {
                // Best effort cleanup for temp scratch paths.
            }
        }

        private static int FindFreePort()
        {
            using var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
            listener.Start();
            var port = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
            listener.Stop();
            return port;
        }
    }
}
