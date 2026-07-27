using Amane.Mailer.Admin;
using Amane.Mailer.Configuration;
using Amane.Mailer.Data.Sqlite;
using Amane.Mailer.Operations;
using Amane.Mailer.Tests.TestSupport;
using Microsoft.Data.Sqlite;
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

            var (exitCode, output, _) = await RunDoctorAsync(
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

            var (exitCode, output, _) = await RunDoctorAsync(
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
    public async Task local_mailpit_mode_fails_when_effective_provider_is_acs()
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
                ["MAILER_HTTP_PORT"] = scratch.FreePort.ToString(),
            });

            var (exitCode, output, _) = await RunDoctorAsync(
                configuration,
                SetupDoctorMode.LocalMailpit,
                scratch.ComposePath);

            Assert.Equal(SetupDoctorCommand.FailureExitCode, exitCode);
            Assert.Contains("[FAIL] mode_tenant_provider:", output, StringComparison.Ordinal);
        }
        finally
        {
            Environment.SetEnvironmentVariable("MAIL_SERVICE_TOKEN", null);
        }
    }

    [Fact]
    public async Task production_acs_mode_requires_live_sending_true()
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
                ["MAILER_HTTP_PORT"] = scratch.FreePort.ToString(),
            });

            var (exitCode, output, _) = await RunDoctorAsync(
                configuration,
                SetupDoctorMode.ProductionAcs,
                scratch.ComposePath);

            Assert.Equal(SetupDoctorCommand.FailureExitCode, exitCode);
            Assert.Contains("[FAIL] mode_live_sending:", output, StringComparison.Ordinal);
            Assert.Contains("[FAIL] production_live_send:", output, StringComparison.Ordinal);
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

            var (exitCode, output, _) = await RunDoctorAsync(
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
    public async Task queue_mode_passes_when_secret_and_name_are_present()
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
                ["MAILER_BOUNCE_INGESTION"] = "queue",
                ["MAILER_BOUNCE_QUEUE_CONNECTION_STRING"] =
                    "DefaultEndpointsProtocol=https;AccountName=example;AccountKey=abc;EndpointSuffix=core.windows.net",
                ["MAILER_BOUNCE_QUEUE_NAME"] = "bounce-events",
                ["MAILER_HTTP_PORT"] = scratch.FreePort.ToString(),
            });

            var (exitCode, output, _) = await RunDoctorAsync(
                configuration,
                SetupDoctorMode.ProductionQueue,
                scratch.ComposePath);

            Assert.Equal(SetupDoctorCommand.FailureExitCode, exitCode);
            Assert.Contains("[PASS] bounce_queue:", output, StringComparison.Ordinal);
            Assert.Contains("[FAIL] production_queue_completion:", output, StringComparison.Ordinal);
            Assert.DoesNotContain("AccountKey=abc", output, StringComparison.Ordinal);
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
        scratch.WriteTenantFile(CreateAcsTenantJson(liveSending: true));
        Environment.SetEnvironmentVariable("MAIL_SERVICE_TOKEN", "local-mail-service-token");

        try
        {
            var configuration = scratch.BuildConfiguration(new Dictionary<string, string?>
            {
                ["Mailer:Worker:Enabled"] = "false",
                ["Mailer:Metrics:Enabled"] = "false",
                ["ACS_CONNECTION_STRING_FILE"] = scratch.WriteAcsSecret(ValidAcsSecret),
                ["MAILER_BOUNCE_INGESTION"] = "queue",
                ["MAILER_BOUNCE_QUEUE_NAME"] = "bounce-events",
                ["MAILER_HTTP_PORT"] = scratch.FreePort.ToString(),
            });

            var (exitCode, output, _) = await RunDoctorAsync(
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
        scratch.WriteTenantFile(CreateAcsTenantJson(liveSending: true));
        Environment.SetEnvironmentVariable("MAIL_SERVICE_TOKEN", "local-mail-service-token");

        try
        {
            var configuration = scratch.BuildConfiguration(new Dictionary<string, string?>
            {
                ["Mailer:Worker:Enabled"] = "false",
                ["Mailer:Metrics:Enabled"] = "false",
                ["ACS_CONNECTION_STRING_FILE"] = scratch.WriteAcsSecret(ValidAcsSecret),
                ["MAILER_BOUNCE_INGESTION"] = "queue",
                ["MAILER_BOUNCE_QUEUE_CONNECTION_STRING"] =
                    "DefaultEndpointsProtocol=https;AccountName=example;AccountKey=abc;EndpointSuffix=core.windows.net",
                ["MAILER_HTTP_PORT"] = scratch.FreePort.ToString(),
            });

            var (exitCode, output, _) = await RunDoctorAsync(
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

        var (exitCode, output, _) = await RunDoctorAsync(
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

            var (exitCode, output, _) = await RunDoctorAsync(
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
    public async Task fails_when_webhook_secret_env_is_missing()
    {
        using var scratch = new DoctorScratch();
        scratch.WriteTenantFile(CreateMailpitTenantWithWebhookJson());
        Environment.SetEnvironmentVariable("MAIL_SERVICE_TOKEN", "local-mail-service-token");
        Environment.SetEnvironmentVariable("WEBHOOK_SIGNING_SECRET", null);

        try
        {
            var configuration = scratch.BuildConfiguration(new Dictionary<string, string?>
            {
                ["Mailer:Worker:Enabled"] = "false",
                ["Mailer:Metrics:Enabled"] = "false",
                ["MAILER_HTTP_PORT"] = scratch.FreePort.ToString(),
            });

            var (exitCode, output, _) = await RunDoctorAsync(
                configuration,
                SetupDoctorMode.LocalMailpit,
                scratch.ComposePath);

            Assert.Equal(SetupDoctorCommand.FailureExitCode, exitCode);
            Assert.Contains("[FAIL] tenant_0_webhook_secret:", output, StringComparison.Ordinal);
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

            var (exitCode, output, _) = await RunDoctorAsync(
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

            var (exitCode, output, _) = await RunDoctorAsync(
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
    public async Task fails_when_http_port_is_already_in_use()
    {
        using var scratch = new DoctorScratch();
        scratch.WriteTenantFile(CreateMailpitTenantJson());
        Environment.SetEnvironmentVariable("MAIL_SERVICE_TOKEN", "local-mail-service-token");

        using var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        listener.Start();
        var busyPort = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;

        try
        {
            var configuration = scratch.BuildConfiguration(new Dictionary<string, string?>
            {
                ["Mailer:Worker:Enabled"] = "false",
                ["Mailer:Metrics:Enabled"] = "false",
                ["MAILER_HTTP_PORT"] = busyPort.ToString(),
            });

            var (exitCode, output, _) = await RunDoctorAsync(
                configuration,
                SetupDoctorMode.LocalMailpit,
                scratch.ComposePath);

            Assert.Equal(SetupDoctorCommand.FailureExitCode, exitCode);
            Assert.Contains("[FAIL] http_port:", output, StringComparison.Ordinal);
        }
        finally
        {
            listener.Stop();
            Environment.SetEnvironmentVariable("MAIL_SERVICE_TOKEN", null);
        }
    }

    [Fact]
    public async Task output_does_not_echo_secret_like_or_tenant_values_on_stdout_or_stderr()
    {
        const string secretToken = "super-secret-token-value-12345";
        const string secretAcs = "Endpoint=https://secret.communication.azure.com/;AccessKey=super-secret-key";
        const string tenantName = "pii-tenant-name-canary";

        using var scratch = new DoctorScratch();
        scratch.WriteTenantFile(CreateAcsTenantJson(liveSending: true, name: tenantName));
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

            var (_, output, error) = await RunDoctorAsync(
                configuration,
                SetupDoctorMode.StagingVerification,
                scratch.ComposePath);

            Assert.DoesNotContain(secretToken, output, StringComparison.Ordinal);
            Assert.DoesNotContain(secretToken, error, StringComparison.Ordinal);
            Assert.DoesNotContain("super-secret-key", output, StringComparison.Ordinal);
            Assert.DoesNotContain("super-secret-key", error, StringComparison.Ordinal);
            Assert.DoesNotContain("secret.communication.azure.com", output, StringComparison.Ordinal);
            Assert.DoesNotContain(tenantName, output, StringComparison.Ordinal);
            Assert.DoesNotContain(tenantName, error, StringComparison.Ordinal);
            Assert.DoesNotContain("noreply@example.com", output, StringComparison.Ordinal);
            Assert.DoesNotContain(scratch.Root, output, StringComparison.Ordinal);
        }
        finally
        {
            Environment.SetEnvironmentVariable("MAIL_SERVICE_TOKEN", null);
        }
    }

    [Fact]
    public async Task repeated_runs_do_not_modify_tenant_secret_or_database_files()
    {
        using var scratch = new DoctorScratch();
        scratch.WriteTenantFile(CreateMailpitTenantJson());
        var acsPath = scratch.WriteAcsSecret(ValidAcsSecret);
        Environment.SetEnvironmentVariable("MAIL_SERVICE_TOKEN", "local-mail-service-token");

        var configuration = scratch.BuildConfiguration(new Dictionary<string, string?>
        {
            ["Mailer:Worker:Enabled"] = "false",
            ["Mailer:Metrics:Enabled"] = "false",
            ["ACS_CONNECTION_STRING_FILE"] = acsPath,
            ["MAILER_HTTP_PORT"] = scratch.FreePort.ToString(),
        });

        var factory = new SqliteConnectionFactory(configuration);
        var runner = new SqlMigrationRunner(factory);
        await runner.ApplyPendingAsync(TestContext.Current.CancellationToken);
        SqliteConnection.ClearAllPools();

        try
        {
            var before = CaptureDbArtifacts(scratch.DatabasePath);
            var tenantBytesBefore = File.ReadAllBytes(scratch.TenantPath);
            var acsBytesBefore = File.ReadAllBytes(acsPath);

            var (exitCode, output, _) = await RunDoctorAsync(
                configuration,
                SetupDoctorMode.LocalMailpit,
                scratch.ComposePath);
            await RunDoctorAsync(configuration, SetupDoctorMode.LocalMailpit, scratch.ComposePath);

            Assert.Equal(SetupDoctorCommand.SuccessExitCode, exitCode);
            Assert.Contains("[PASS] db_schema:", output, StringComparison.Ordinal);
            Assert.Contains("[ACTION] db_schema:", output, StringComparison.Ordinal);
            Assert.Equal(tenantBytesBefore, File.ReadAllBytes(scratch.TenantPath));
            Assert.Equal(acsBytesBefore, File.ReadAllBytes(acsPath));
            AssertEqualArtifacts(before, CaptureDbArtifacts(scratch.DatabasePath));
        }
        finally
        {
            Environment.SetEnvironmentVariable("MAIL_SERVICE_TOKEN", null);
            SqliteConnection.ClearAllPools();
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
    public async Task usage_error_for_unknown_argument_does_not_echo_secret_like_value()
    {
        const string secretArg = "Endpoint=https://secret.example/;AccessKey=leaked-key-value";
        var configuration = new ConfigurationBuilder().Build();
        var output = new StringWriter();
        var error = new StringWriter();

        var exitCode = await MailerCliHost.RunSetupDoctorAsync(
            configuration,
            ["setup", "doctor", "--mode", "local-mailpit", secretArg],
            output,
            error,
            TestContext.Current.CancellationToken);

        var combined = output.ToString() + error.ToString();
        Assert.Equal(SetupDoctorCommand.UsageErrorExitCode, exitCode);
        Assert.Contains("Unknown argument.", error.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(secretArg, combined, StringComparison.Ordinal);
        Assert.DoesNotContain("leaked-key-value", combined, StringComparison.Ordinal);
        Assert.DoesNotContain("secret.example", combined, StringComparison.Ordinal);
    }

    [Fact]
    public void placeholder_detector_matches_validate_tenant_config_rules()
    {
        Assert.True(ConfigurationPlaceholderDetector.LooksLikePlaceholder("replace-with-token"));
        Assert.True(ConfigurationPlaceholderDetector.LooksLikePlaceholder("<token>"));
        Assert.False(ConfigurationPlaceholderDetector.LooksLikePlaceholder("local-mail-service-token"));
    }

    [Fact]
    public async Task schema_probe_for_healthcheck_uses_readonly_open_mode()
    {
        using var scratch = new DoctorScratch();
        var configuration = scratch.BuildConfiguration(new Dictionary<string, string?>());
        var factory = new SqliteConnectionFactory(configuration);
        await using (var bootstrap = new SqliteConnection($"Data Source={scratch.DatabasePath}"))
        {
            await bootstrap.OpenAsync(TestContext.Current.CancellationToken);
        }

        SqliteConnection? opened = null;
        factory.ConnectionCreatedForTests = connection => opened = connection;

        await using var connection = await factory.OpenSchemaProbeConnectionAsync(
            TestContext.Current.CancellationToken);

        Assert.NotNull(opened);
        var builder = new SqliteConnectionStringBuilder(opened.ConnectionString);
        Assert.Equal(SqliteOpenMode.ReadOnly, builder.Mode);
    }

    private static async Task<(int ExitCode, string Output, string Error)> RunDoctorAsync(
        IConfiguration configuration,
        SetupDoctorMode mode,
        string composePath)
    {
        var output = new StringWriter();
        var error = new StringWriter();
        var command = new SetupDoctorCommand(
            configuration,
            mode,
            composePath,
            output,
            error);

        var exitCode = await command.ExecuteAsync(TestContext.Current.CancellationToken);
        return (exitCode, output.ToString(), error.ToString());
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

    private static string CreateMailpitTenantWithWebhookJson() =>
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
              },
              "webhook": {
                "url": "https://example.com/hooks/mailer",
                "secret_env": "WEBHOOK_SIGNING_SECRET"
              }
            }
          ]
        }
        """;

    private static string CreateAcsTenantJson(bool liveSending, string name = "example-staging") =>
        $$"""
        {
          "version": 1,
          "environment": "staging",
          "tenants": [
            {
              "tenant_id": "00000000-0000-0000-0000-000000000201",
              "name": "{{name}}",
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

    private static Dictionary<string, (bool Exists, byte[]? Bytes, DateTime? WriteUtc)> CaptureDbArtifacts(
        string databasePath)
    {
        string[] paths =
        [
            databasePath,
            databasePath + "-wal",
            databasePath + "-shm",
            databasePath + "-journal",
        ];

        var result = new Dictionary<string, (bool Exists, byte[]? Bytes, DateTime? WriteUtc)>(StringComparer.Ordinal);
        foreach (var path in paths)
        {
            if (!File.Exists(path))
            {
                result[path] = (false, null, null);
                continue;
            }

            result[path] = (true, File.ReadAllBytes(path), File.GetLastWriteTimeUtc(path));
        }

        return result;
    }

    private static void AssertEqualArtifacts(
        Dictionary<string, (bool Exists, byte[]? Bytes, DateTime? WriteUtc)> before,
        Dictionary<string, (bool Exists, byte[]? Bytes, DateTime? WriteUtc)> after)
    {
        Assert.Equal(before.Count, after.Count);
        foreach (var (path, beforeState) in before)
        {
            Assert.True(after.TryGetValue(path, out var afterState));
            Assert.Equal(beforeState.Exists, afterState.Exists);
            Assert.Equal(beforeState.Bytes, afterState.Bytes);
            Assert.Equal(beforeState.WriteUtc, afterState.WriteUtc);
        }
    }

    private sealed class DoctorScratch : IDisposable
    {
        private readonly string _root;

        public DoctorScratch()
        {
            _root = Path.Combine(Path.GetTempPath(), "amane-mailer-setup-doctor", Guid.NewGuid().ToString("N"));
            TestSecretDirectory.CreateSecure(_root);
            DataDirectory = Path.Combine(_root, "data");
            TestSecretDirectory.CreateSecure(DataDirectory);
            TenantPath = Path.Combine(_root, "tenants.json");
            DatabasePath = Path.Combine(DataDirectory, "mailer.db");
            ComposePath = Path.Combine(_root, "compose.yml");
            File.WriteAllText(ComposePath, "services:\n  mailer:\n    image: example\n");
            FreePort = FindFreePort();
        }

        public string Root => _root;

        public string DataDirectory { get; }

        public string TenantPath { get; }

        public string DatabasePath { get; }

        public string ComposePath { get; }

        public int FreePort { get; }

        public void WriteTenantFile(string json) => File.WriteAllText(TenantPath, json);

        public string WriteAcsSecret(string content)
        {
            var directory = Path.Combine(_root, "secrets", "acs");
            TestSecretDirectory.CreateSecure(directory);
            var path = Path.Combine(directory, AcsSecretFileNames.CanonicalFileName);
            File.WriteAllText(path, content);
            return path;
        }

        public IConfiguration BuildConfiguration(IReadOnlyDictionary<string, string?> extra)
        {
            var values = new Dictionary<string, string?>(extra)
            {
                ["ConnectionStrings:Mailer"] = $"Data Source={DatabasePath}",
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
