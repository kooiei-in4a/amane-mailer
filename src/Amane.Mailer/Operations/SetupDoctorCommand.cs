using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using Amane.Mailer.Admin;
using Amane.Mailer.Configuration;
using Amane.Mailer.Data.Sqlite;
using Amane.Mailer.Json;
using Microsoft.Extensions.Configuration;

namespace Amane.Mailer.Operations;

/// <summary>
/// Read-only setup diagnostics for local configuration and host prerequisites (#425).
/// Does not mutate configuration, databases, containers, or Azure resources.
/// </summary>
public sealed class SetupDoctorCommand
{
    public const int SuccessExitCode = 0;
    public const int FailureExitCode = 1;
    public const int UsageErrorExitCode = 2;

    private readonly IConfiguration _configuration;
    private readonly SetupDoctorMode _mode;
    private readonly string? _composeFilePath;
    private readonly TextWriter _output;
    private readonly TextWriter _error;
    private readonly SetupDoctorReport _report = new();

    public SetupDoctorCommand(
        IConfiguration configuration,
        SetupDoctorMode mode,
        string? composeFilePath,
        TextWriter output,
        TextWriter error)
    {
        _configuration = configuration;
        _mode = mode;
        _composeFilePath = composeFilePath;
        _output = output;
        _error = error;
    }

    public static bool IsSetupDoctorCommand(IReadOnlyList<string> args) =>
        args.Count >= 2
        && string.Equals(args[0], "setup", StringComparison.Ordinal)
        && string.Equals(args[1], "doctor", StringComparison.Ordinal);

    public static bool TryParseArguments(
        IReadOnlyList<string> args,
        out SetupDoctorMode mode,
        out string? composeFilePath,
        out string? usageError)
    {
        mode = default;
        composeFilePath = null;
        usageError = null;

        var index = 2;
        var modeSpecified = false;

        while (index < args.Count)
        {
            var token = args[index];
            if (string.Equals(token, "--mode", StringComparison.Ordinal))
            {
                index++;
                if (index >= args.Count)
                {
                    usageError = "--mode requires a value.";
                    return false;
                }

                if (!SetupDoctorModeParser.TryParse(args[index], out mode))
                {
                    usageError = $"Unknown --mode value. Expected one of: {SetupDoctorModeParser.UsageHint}.";
                    return false;
                }

                modeSpecified = true;
                index++;
                continue;
            }

            if (string.Equals(token, "--compose-file", StringComparison.Ordinal))
            {
                index++;
                if (index >= args.Count)
                {
                    usageError = "--compose-file requires a path.";
                    return false;
                }

                composeFilePath = args[index];
                index++;
                continue;
            }

            usageError = "Unknown argument.";
            return false;
        }

        if (!modeSpecified)
        {
            usageError = $"--mode is required. Expected one of: {SetupDoctorModeParser.UsageHint}.";
            return false;
        }

        return true;
    }

    public async Task<int> ExecuteAsync(CancellationToken cancellationToken)
    {
        try
        {
            RunTenantPreflight();
            RunOptionsPreflight();
            RunHostDirectoryChecks();
            RunAcsSecretStateCheck();
            await RunDatabaseReadinessCheckAsync(cancellationToken);
            RunDockerAvailabilityCheck();
            RunPortAvailabilityCheck();
            RunComposeGuidanceChecks();
            RunModeSpecificChecks();
            RunPublishedImageGuidance();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            _report.AddFail(
                "doctor_unexpected",
                "Setup doctor encountered an unexpected error while diagnosing configuration.");
            await _error.WriteLineAsync(
                "setup doctor failed: unexpected diagnostic error (details omitted).");
        }

        await WriteReportAsync(cancellationToken);
        return _report.HasFailure ? FailureExitCode : SuccessExitCode;
    }

    private void RunTenantPreflight()
    {
        var tenantsPath = ResolveTenantsPath();
        if (!File.Exists(tenantsPath))
        {
            _report.AddFail("tenant_file", $"Tenant configuration file does not exist at the resolved path.");
            return;
        }

        MailerTenantsFile tenantFile;
        try
        {
            tenantFile = JsonSerializer.Deserialize(
                File.ReadAllText(tenantsPath),
                MailerJsonContext.Default.MailerTenantsFile)
                ?? throw new InvalidOperationException("empty");
            tenantFile.Validate();
        }
        catch (Exception)
        {
            _report.AddFail("tenant_schema", "Tenant configuration file failed schema validation.");
            return;
        }

        _report.AddPass("tenant_file", "Tenant configuration file exists and passes schema validation.");

        var tenantIds = new Dictionary<Guid, int>();
        for (var index = 0; index < tenantFile.Tenants.Count; index++)
        {
            var tenant = tenantFile.Tenants[index];
            try
            {
                tenant.Validate();
            }
            catch (InvalidOperationException)
            {
                _report.AddFail($"tenant_{index}", $"Tenant at index {index} failed validation.");
                continue;
            }

            if (tenantIds.TryGetValue(tenant.TenantId, out var previousIndex))
            {
                _report.AddFail(
                    $"tenant_{index}",
                    $"Duplicate tenant_id (also declared at tenant index {previousIndex}).");
            }
            else
            {
                tenantIds.Add(tenant.TenantId, index);
            }

            ValidateTenantTokenEnv(tenant, index);
            ValidateTenantWebhookSecretEnv(tenant, index);
            ValidateTenantSourceServices(tenant, index);
        }

        ValidateEffectiveProviders(tenantFile.Tenants);
    }

    private void ValidateTenantTokenEnv(MailerTenant tenant, int index)
    {
        var token = _configuration[tenant.TokenEnv]
            ?? Environment.GetEnvironmentVariable(tenant.TokenEnv);

        if (token is null)
        {
            _report.AddFail(
                $"tenant_{index}_token",
                $"Environment variable named by token_env is not set for tenant at index {index}.");
            return;
        }

        if (token.Length == 0)
        {
            _report.AddFail(
                $"tenant_{index}_token",
                $"Environment variable named by token_env is set but empty for tenant at index {index}.");
            return;
        }

        if (ConfigurationPlaceholderDetector.LooksLikePlaceholder(token))
        {
            _report.AddFail(
                $"tenant_{index}_token",
                $"Environment variable named by token_env appears to contain a placeholder value for tenant at index {index}.");
            return;
        }

        _report.AddPass(
            $"tenant_{index}_token",
            $"Token environment variable is set for tenant at index {index}.");
    }

    private void ValidateTenantWebhookSecretEnv(MailerTenant tenant, int index)
    {
        if (tenant.Webhook is null)
        {
            return;
        }

        var secretEnv = tenant.Webhook.SecretEnv;
        var secret = _configuration[secretEnv]
            ?? Environment.GetEnvironmentVariable(secretEnv);

        if (secret is null)
        {
            _report.AddFail(
                $"tenant_{index}_webhook_secret",
                $"Environment variable named by webhook.secret_env is not set for tenant at index {index}.");
            return;
        }

        if (secret.Length == 0)
        {
            _report.AddFail(
                $"tenant_{index}_webhook_secret",
                $"Environment variable named by webhook.secret_env is set but empty for tenant at index {index}.");
            return;
        }

        if (ConfigurationPlaceholderDetector.LooksLikePlaceholder(secret))
        {
            _report.AddFail(
                $"tenant_{index}_webhook_secret",
                $"Environment variable named by webhook.secret_env appears to contain a placeholder value for tenant at index {index}.");
            return;
        }

        _report.AddPass(
            $"tenant_{index}_webhook_secret",
            $"Webhook secret environment variable is set for tenant at index {index}.");
    }

    private void ValidateTenantSourceServices(MailerTenant tenant, int index)
    {
        if (tenant.SourceServices.Count == 0)
        {
            _report.AddFail(
                $"tenant_{index}_source_services",
                $"Tenant at index {index} must list at least one source_service.");
            return;
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var sourceService in tenant.SourceServices)
        {
            if (!seen.Add(sourceService))
            {
                _report.AddFail(
                    $"tenant_{index}_source_services",
                    $"Tenant at index {index} has a duplicate source_service entry.");
            }
        }
    }

    private void ValidateEffectiveProviders(IReadOnlyList<MailerTenant> tenants)
    {
        var mailerOptions = MailerOptions.Load(_configuration);
        try
        {
            mailerOptions.ValidateEffectiveProviders(tenants);
        }
        catch (InvalidOperationException)
        {
            _report.AddFail(
                "provider_effective",
                "Effective provider settings are inconsistent with tenant configuration or ACS secret requirements.");
            return;
        }

        var liveSendingAcsChecked = false;
        foreach (var tenant in tenants)
        {
            var effectiveProvider = mailerOptions.ResolveProvider(tenant);
            if (effectiveProvider.Equals("acs", StringComparison.Ordinal)
                && tenant.LiveSending)
            {
                if (!liveSendingAcsChecked)
                {
                    ValidateAcsSecretForLiveSending();
                    liveSendingAcsChecked = true;
                }
            }
        }

        _report.AddPass("provider_effective", "Effective provider settings are consistent with tenant configuration.");
    }

    private void ValidateAcsSecretForLiveSending()
    {
        var filePath = _configuration["ACS_CONNECTION_STRING_FILE"];
        if (!string.IsNullOrWhiteSpace(filePath))
        {
            if (!File.Exists(filePath))
            {
                _report.AddFail(
                    "acs_secret",
                    "ACS_CONNECTION_STRING_FILE is set but the file does not exist (required for live_sending ACS).");
                return;
            }

            var content = File.ReadAllText(filePath).Trim();
            if (content.Length == 0)
            {
                _report.AddFail(
                    "acs_secret",
                    "ACS_CONNECTION_STRING_FILE is set but the file is empty (required for live_sending ACS).");
                return;
            }

            if (ConfigurationPlaceholderDetector.LooksLikePlaceholder(content))
            {
                _report.AddFail(
                    "acs_secret",
                    "Resolved ACS secret appears to contain a placeholder value (required for live_sending ACS).");
                return;
            }

            _report.AddPass("acs_secret", "ACS secret file is present for live_sending ACS tenant(s).");
            return;
        }

        var envValue = _configuration["ACS_CONNECTION_STRING"];
        if (string.IsNullOrWhiteSpace(envValue))
        {
            _report.AddFail(
                "acs_secret",
                "Neither ACS_CONNECTION_STRING_FILE nor ACS_CONNECTION_STRING provides a value (required for live_sending ACS).");
            return;
        }

        if (ConfigurationPlaceholderDetector.LooksLikePlaceholder(envValue))
        {
            _report.AddFail(
                "acs_secret",
                "Resolved ACS secret appears to contain a placeholder value (required for live_sending ACS).");
            return;
        }

        _report.AddPass("acs_secret", "ACS connection string environment variable is set for live_sending ACS tenant(s).");
    }

    private void RunOptionsPreflight()
    {
        var environmentName = ReadEnvironmentName();
        var adminEnabled = ConfigurationBooleanReader.Read(
            _configuration,
            defaultValue: false,
            "AMANE_ADMIN_ENABLED",
            "MAILER_ADMIN_ENABLED");

        ValidateMetrics(environmentName);
        ValidateAdmin(adminEnabled, environmentName);
        ValidateBounceIngestion();
        ValidateDbOps(adminEnabled);
    }

    private void ValidateMetrics(string environmentName)
    {
        var metrics = MailerMetricsOptions.Load(_configuration);
        try
        {
            metrics.Validate(environmentName);
        }
        catch (InvalidOperationException)
        {
            _report.AddFail(
                "metrics_bearer",
                "Metrics bearer configuration is invalid for the current environment.");
            return;
        }

        if (metrics.Enabled)
        {
            _report.AddPass("metrics_bearer", "Metrics configuration is valid for the current environment.");
        }
        else
        {
            _report.AddPass("metrics_bearer", "Metrics are disabled.");
        }
    }

    private void ValidateAdmin(bool adminEnabled, string environmentName)
    {
        if (!adminEnabled)
        {
            _report.AddPass("admin_config", "Admin UI is disabled.");
            return;
        }

        try
        {
            var adminOptions = MailerAdminOptions.Load(_configuration);
            adminOptions.Validate();
        }
        catch (InvalidOperationException)
        {
            _report.AddFail("admin_config", "Admin configuration is invalid.");
            return;
        }

        var allowHttp = AdminCookieTransportPolicy.IsAllowHttpRequested(_configuration, adminEnabled: true);
        try
        {
            AdminCookieTransportPolicy.Validate(allowHttp, environmentName, adminEnabled: true);
        }
        catch (InvalidOperationException)
        {
            _report.AddFail(
                "admin_https",
                "AMANE_ADMIN_ALLOW_HTTP=true is not permitted when ASPNETCORE_ENVIRONMENT is not Development.");
            return;
        }

        _report.AddPass("admin_config", "Admin configuration is valid for the current environment.");
    }

    private void ValidateBounceIngestion()
    {
        MailerBounceIngestionOptions bounceOptions;
        try
        {
            bounceOptions = MailerBounceIngestionOptions.Load(_configuration);
        }
        catch (InvalidOperationException)
        {
            _report.AddFail("bounce_ingestion", "Bounce ingestion configuration could not be loaded.");
            return;
        }

        if (bounceOptions.Mode == BounceIngestionMode.Queue)
        {
            if (string.IsNullOrWhiteSpace(bounceOptions.QueueConnectionString))
            {
                _report.AddFail(
                    "bounce_queue_secret",
                    "MAILER_BOUNCE_INGESTION=queue requires MAILER_BOUNCE_QUEUE_CONNECTION_STRING "
                    + "(or MAILER_BOUNCE_QUEUE_CONNECTION_STRING_FILE / Mailer:BounceIngestion:Queue:ConnectionString).");
                return;
            }

            if (ConfigurationPlaceholderDetector.LooksLikePlaceholder(bounceOptions.QueueConnectionString))
            {
                _report.AddFail(
                    "bounce_queue_secret",
                    "Resolved bounce Queue connection string appears to contain a placeholder value.");
                return;
            }

            if (string.IsNullOrWhiteSpace(bounceOptions.QueueName))
            {
                _report.AddFail(
                    "bounce_queue_name",
                    "MAILER_BOUNCE_INGESTION=queue requires MAILER_BOUNCE_QUEUE_NAME "
                    + "(or Mailer:BounceIngestion:Queue:Name).");
                return;
            }
        }

        try
        {
            bounceOptions.Validate();
        }
        catch (InvalidOperationException)
        {
            _report.AddFail("bounce_ingestion", "Bounce ingestion configuration is invalid.");
            return;
        }

        if (bounceOptions.Mode == BounceIngestionMode.Queue)
        {
            _report.AddPass("bounce_queue", "Bounce Queue mode configuration includes connection string and queue name.");
        }
        else
        {
            _report.AddPass("bounce_ingestion", "Bounce ingestion mode is off or disabled.");
        }
    }

    private void ValidateDbOps(bool adminEnabled)
    {
        var connectionString = _configuration.GetConnectionString("Mailer")
            ?? _configuration["ConnectionStrings:Mailer"]
            ?? string.Empty;

        MailerAdminDbOpsOptions dbOps;
        try
        {
            dbOps = MailerAdminDbOpsOptions.Load(_configuration, connectionString, adminEnabled);
        }
        catch (InvalidOperationException)
        {
            _report.AddFail("admin_db_ops", "Admin DB operations configuration could not be loaded.");
            return;
        }

        if (!dbOps.Enabled)
        {
            _report.AddPass("admin_db_ops", "Admin DB operations are disabled.");
            return;
        }

        try
        {
            var factory = new SqliteConnectionFactory(_configuration);
            dbOps.Validate(adminEnabled, factory);
        }
        catch (InvalidOperationException)
        {
            _report.AddFail("admin_db_ops", "Admin DB backup configuration is invalid.");
            return;
        }

        _report.AddPass("admin_db_ops", "Admin DB backup configuration is valid.");
    }

    private void RunHostDirectoryChecks()
    {
        CheckDirectoryReadOnly("data_directory", ResolveDataDirectory(), required: true);
        CheckTenantMountPath();
        CheckAcsSecretDirectoryReadOnly();
    }

    private void CheckTenantMountPath()
    {
        var tenantsPath = ResolveTenantsPath();
        var directory = Path.GetDirectoryName(tenantsPath);
        if (string.IsNullOrEmpty(directory))
        {
            return;
        }

        CheckDirectoryReadOnly("tenant_mount_directory", directory, required: false);
    }

    private void CheckAcsSecretDirectoryReadOnly()
    {
        var acsDirectory = _configuration["MAILER_ACS_SECRET_HOST_PATH"]
            ?? Path.GetDirectoryName(_configuration["ACS_CONNECTION_STRING_FILE"] ?? string.Empty)
            ?? string.Empty;

        if (string.IsNullOrWhiteSpace(acsDirectory))
        {
            if (_mode is SetupDoctorMode.StagingNoSend
                or SetupDoctorMode.StagingVerification
                or SetupDoctorMode.ProductionAcs
                or SetupDoctorMode.ProductionQueue)
            {
                _report.AddWarn(
                    "acs_secret_directory",
                    "ACS secret host directory is not configured; verify MAILER_ACS_SECRET_HOST_PATH or ACS_CONNECTION_STRING_FILE.");
            }

            return;
        }

        CheckDirectoryReadOnly("acs_secret_directory", acsDirectory, required: false);
    }

    private void CheckDirectoryReadOnly(string checkId, string directoryPath, bool required)
    {
        if (string.IsNullOrWhiteSpace(directoryPath))
        {
            if (required)
            {
                _report.AddFail(checkId, "Required host directory path is not configured.");
            }

            return;
        }

        if (!Directory.Exists(directoryPath))
        {
            if (required)
            {
                _report.AddFail(checkId, "Required host directory does not exist.");
            }
            else
            {
                _report.AddWarn(checkId, "Configured host directory does not exist yet.");
            }

            return;
        }

        try
        {
            FileSystemSafetyGuard.EnsureDirectoryIsSafe(directoryPath);
            _report.AddPass(checkId, "Host directory exists and passes read-only safety checks.");
        }
        catch (SecretOperationException ex)
        {
            _report.AddFail(checkId, SanitizeDirectoryFailure(ex.CanonicalCode));
        }
    }

    private void RunAcsSecretStateCheck()
    {
        var acsFilePath = ResolveAcsSecretFilePath();
        var senderDirectory = _configuration["MAILER_PLATFORM_SENDER_HOST_PATH"] ?? string.Empty;
        var senderPath = string.IsNullOrWhiteSpace(senderDirectory)
            ? string.Empty
            : Path.Combine(senderDirectory, PlatformSenderFile.CanonicalFileName);

        if (string.IsNullOrWhiteSpace(acsFilePath) && string.IsNullOrWhiteSpace(senderPath))
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(acsFilePath) || string.IsNullOrWhiteSpace(senderPath))
        {
            _report.AddWarn(
                "acs_registration_state",
                "ACS secret or platform sender path is partially configured; verify both mount targets.");
            return;
        }

        var state = RegisteredSecretStateInspector.Inspect(acsFilePath, senderPath);
        switch (state)
        {
            case RegisteredSecretState.Clean:
                _report.AddPass("acs_registration_state", "ACS secret and platform sender files are not registered yet.");
                break;
            case RegisteredSecretState.FullyRegistered:
                _report.AddPass("acs_registration_state", "ACS secret and platform sender files are both registered.");
                break;
            case RegisteredSecretState.PartialOrCorrupt:
                _report.AddFail(
                    "acs_registration_state",
                    "ACS secret and platform sender registration state is partial or corrupt; inspect both paths manually.");
                break;
        }
    }

    private Task RunDatabaseReadinessCheckAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var connectionString = _configuration.GetConnectionString("Mailer")
            ?? _configuration["ConnectionStrings:Mailer"];

        if (string.IsNullOrWhiteSpace(connectionString))
        {
            _report.AddWarn("db_schema", "ConnectionStrings:Mailer is not configured; skipping schema readiness check.");
            return Task.CompletedTask;
        }

        string dataSource;
        try
        {
            dataSource = new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder(connectionString).DataSource;
        }
        catch
        {
            _report.AddFail("db_schema", "ConnectionStrings:Mailer could not be parsed.");
            return Task.CompletedTask;
        }

        if (string.IsNullOrWhiteSpace(dataSource)
            || string.Equals(dataSource, ":memory:", StringComparison.OrdinalIgnoreCase))
        {
            _report.AddWarn("db_schema", "Mailer database is in-memory; skipping on-disk schema readiness check.");
            return Task.CompletedTask;
        }

        var databasePath = Path.GetFullPath(dataSource);
        if (!File.Exists(databasePath))
        {
            _report.AddAction(
                "db_schema",
                "Database file does not exist yet. Run db migrate after first container start or manually.");
            return Task.CompletedTask;
        }

        // Do not open the live SQLite file from setup doctor: even ReadOnly opens can create or
        // update WAL/SHM sidecars. Presence is reported here; schema currency stays a host ACTION.
        _report.AddPass("db_schema", "Database file exists.");
        _report.AddAction(
            "db_schema",
            "Verify schema currency with `Amane.Mailer healthcheck` (or db migrate) without treating setup doctor as a live-DB opener.");
        return Task.CompletedTask;
    }

    private void RunDockerAvailabilityCheck()
    {
        if (LooksLikeContainerRuntime())
        {
            _report.AddWarn(
                "docker",
                "Running inside a container namespace; Docker CLI availability on the host was not verified.");
            _report.AddAction(
                "docker",
                "Run setup doctor on the host (or verify `docker version` / `docker compose version` on the host) before compose-based setup.");
            return;
        }

        if (TryRunProcess("docker", "version --format {{.Server.Version}}", out _))
        {
            _report.AddPass("docker", "Docker CLI is available on PATH.");
            if (TryRunProcess("docker", "compose version --short", out _))
            {
                _report.AddPass("docker_compose", "Docker Compose plugin is available.");
            }
            else
            {
                _report.AddWarn("docker_compose", "Docker Compose plugin was not detected.");
                _report.AddAction(
                    "docker_compose",
                    "Install Docker Compose v2 or verify `docker compose version` succeeds on the host.");
            }

            return;
        }

        _report.AddWarn("docker", "Docker CLI was not detected on PATH.");
        _report.AddAction("docker", "Install Docker Engine and verify `docker version` succeeds before compose-based setup.");
    }

    private void RunPortAvailabilityCheck()
    {
        var httpPortRaw = _configuration["MAILER_HTTP_PORT"];
        if (string.IsNullOrWhiteSpace(httpPortRaw))
        {
            httpPortRaw = "8080";
        }

        if (!int.TryParse(httpPortRaw.Trim(), out var httpPort)
            || httpPort < ConfigurationIntReader.MinPort
            || httpPort > ConfigurationIntReader.MaxPort)
        {
            _report.AddWarn("http_port", "MAILER_HTTP_PORT is not a valid TCP port; verify deploy .env.");
            return;
        }

        if (LooksLikeContainerRuntime())
        {
            _report.AddWarn(
                "http_port",
                "Running inside a container namespace; host port occupancy was not verified.");
            _report.AddAction(
                "http_port",
                "Confirm the published host port is free on the Docker host before binding.");
            return;
        }

        if (IsPortAvailable(httpPort))
        {
            _report.AddPass("http_port", "Configured Mailer HTTP port is not in use on this host.");
        }
        else
        {
            _report.AddFail("http_port", "Configured Mailer HTTP port is already in use on this host.");
        }
    }

    private void RunComposeGuidanceChecks()
    {
        var composePath = _composeFilePath ?? TryFindDefaultComposePath();
        if (composePath is null || !File.Exists(composePath))
        {
            _report.AddAction(
                "compose_validate",
                "Run `docker compose --env-file .env -f compose.yml config --quiet` from the deploy directory to validate compose wiring.");
            return;
        }

        _report.AddPass("compose_file", "Referenced compose file path exists.");

        var composeText = File.ReadAllText(composePath);
        if (!composeText.Contains("MAILER_BOUNCE_INGESTION", StringComparison.Ordinal)
            && !composeText.Contains("MAILER_BOUNCE_QUEUE", StringComparison.Ordinal))
        {
            if (_mode == SetupDoctorMode.ProductionQueue)
            {
                _report.AddFail(
                    "compose_bounce_wiring",
                    "Deploy compose template does not wire bounce Queue settings into the mailer service.");
                _report.AddAction(
                    "compose_bounce_wiring",
                    "Add MAILER_BOUNCE_INGESTION, Queue credentials, and Queue name to compose environment/volumes before mode 5 completion.");
            }
            else
            {
                _report.AddPass(
                    "compose_bounce_wiring",
                    "Bounce Queue env is not wired in the referenced compose file (expected unless mode 5).");
            }
        }

        _report.AddAction(
            "compose_validate",
            "Run `docker compose --env-file .env -f <compose-file> config --quiet` to validate rendered compose on this host.");
    }

    private void RunModeSpecificChecks()
    {
        switch (_mode)
        {
            case SetupDoctorMode.LocalMailpit:
                ValidateModeLocalMailpit();
                break;
            case SetupDoctorMode.StagingNoSend:
                ValidateModeStagingNoSend();
                break;
            case SetupDoctorMode.StagingVerification:
                ValidateModeStagingVerification();
                break;
            case SetupDoctorMode.ProductionAcs:
                ValidateModeProductionAcs();
                break;
            case SetupDoctorMode.ProductionQueue:
                ValidateModeProductionQueue();
                break;
        }
    }

    private void ValidateModeLocalMailpit()
    {
        _report.AddPass("mode_profile", "Diagnosing local Mailpit mode prerequisites.");
        ValidateTenantProviderExpectation(expectedProvider: "mailpit", liveSendingRequired: false);
    }

    private void ValidateModeStagingNoSend()
    {
        _report.AddPass("mode_profile", "Diagnosing staging ACS no-send mode prerequisites.");
        ValidateTenantProviderExpectation(expectedProvider: "acs", liveSendingRequired: false);
    }

    private void ValidateModeStagingVerification()
    {
        _report.AddPass("mode_profile", "Diagnosing staging ACS verification mode prerequisites.");
        ValidateTenantProviderExpectation(expectedProvider: "acs", liveSendingRequired: true);
        _report.AddAction(
            "staging_register_acs",
            "Use admin provider register-acs with Staging confirmation for ACS secret registration (Staging only).");
    }

    private void ValidateModeProductionAcs()
    {
        _report.AddPass("mode_profile", "Diagnosing production ACS mode prerequisites (deploy shape).");
        ValidateTenantProviderExpectation(expectedProvider: "acs", liveSendingRequired: true);
        _report.AddAction(
            "production_register_acs",
            "Use admin provider register-acs with exact Production confirmation for ACS file-secret registration. Do not type Staging for production work.");
    }

    private void ValidateModeProductionQueue()
    {
        _report.AddPass("mode_profile", "Diagnosing production ACS + Queue mode prerequisites (target configuration).");
        ValidateTenantProviderExpectation(expectedProvider: "acs", liveSendingRequired: true);

        var bounceOptions = MailerBounceIngestionOptions.Load(_configuration);
        if (bounceOptions.Mode != BounceIngestionMode.Queue)
        {
            _report.AddFail(
                "mode_bounce_queue",
                "Production Queue mode requires MAILER_BOUNCE_INGESTION=queue.");
        }
        else
        {
            _report.AddPass("mode_bounce_queue", "Bounce ingestion mode is queue.");
        }

        _report.AddFail(
            "production_queue_completion",
            "Production ACS + Queue mode is target-only until deploy compose wires Queue settings into the container.");
        _report.AddAction(
            "production_queue_completion",
            "Wire MAILER_BOUNCE_INGESTION, Queue credentials, and Queue name through compose before treating mode 5 as complete.");
    }

    private void ValidateTenantProviderExpectation(string expectedProvider, bool? liveSendingRequired)
    {
        var tenantsPath = ResolveTenantsPath();
        if (!File.Exists(tenantsPath))
        {
            return;
        }

        MailerTenantsFile tenantFile;
        try
        {
            tenantFile = JsonSerializer.Deserialize(
                File.ReadAllText(tenantsPath),
                MailerJsonContext.Default.MailerTenantsFile)!;
        }
        catch
        {
            return;
        }

        var mailerOptions = MailerOptions.Load(_configuration);
        for (var index = 0; index < tenantFile.Tenants.Count; index++)
        {
            var tenant = tenantFile.Tenants[index];
            var effectiveProvider = mailerOptions.ResolveProvider(tenant);
            if (!effectiveProvider.Equals(expectedProvider, StringComparison.Ordinal))
            {
                _report.AddFail(
                    "mode_tenant_provider",
                    $"Tenant at index {index} effective provider does not match the selected setup mode.");
            }

            if (liveSendingRequired is true && !tenant.LiveSending)
            {
                _report.AddFail(
                    "mode_live_sending",
                    $"Tenant at index {index} must have live_sending=true for this mode.");
            }

            if (liveSendingRequired is false && tenant.LiveSending)
            {
                _report.AddWarn(
                    "mode_live_sending",
                    $"Tenant at index {index} has live_sending=true; no-send mode expects live_sending=false.");
            }
        }
    }

    private void RunPublishedImageGuidance()
    {
        _report.AddWarn(
            "published_v1_1_0_image",
            "Public GitHub release v1.1.0 is not published yet; verify against a published image after release.");
        _report.AddAction(
            "published_v1_1_0_image",
            "After v1.1.0 release / publish / post-promote sync, re-run setup doctor against the published GHCR tag.");
    }

    private async Task WriteReportAsync(CancellationToken cancellationToken)
    {
        foreach (var check in _report.Checks)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var codeLabel = check.Code switch
            {
                SetupDoctorResultCode.Pass => "PASS",
                SetupDoctorResultCode.Fail => "FAIL",
                SetupDoctorResultCode.Warn => "WARN",
                SetupDoctorResultCode.Action => "ACTION",
                _ => check.Code.ToString().ToUpperInvariant(),
            };

            await _output.WriteLineAsync($"[{codeLabel}] {check.CheckId}: {check.Message}");
        }

        await _output.WriteLineAsync(
            $"Summary: PASS={_report.PassCount} FAIL={_report.FailCount} WARN={_report.WarnCount} ACTION={_report.ActionCount}");
    }

    private string ResolveTenantsPath() =>
        _configuration["Mailer:TenantsPath"]
        ?? _configuration["MAILER_TENANTS_PATH"]
        ?? Path.Combine(AppContext.BaseDirectory, "config", "mailer", "tenants.example.json");

    private string ResolveDataDirectory()
    {
        var connectionString = _configuration.GetConnectionString("Mailer")
            ?? _configuration["ConnectionStrings:Mailer"]
            ?? string.Empty;

        if (string.IsNullOrWhiteSpace(connectionString))
        {
            return _configuration["MAILER_DATA_PATH"] ?? string.Empty;
        }

        var dataSource = new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder(connectionString).DataSource;
        if (string.IsNullOrWhiteSpace(dataSource)
            || string.Equals(dataSource, ":memory:", StringComparison.OrdinalIgnoreCase))
        {
            return _configuration["MAILER_DATA_PATH"] ?? string.Empty;
        }

        var databaseDirectory = Path.GetDirectoryName(Path.GetFullPath(dataSource));
        return databaseDirectory ?? (_configuration["MAILER_DATA_PATH"] ?? string.Empty);
    }

    private string ResolveAcsSecretFilePath()
    {
        var configuredFile = _configuration["ACS_CONNECTION_STRING_FILE"];
        if (!string.IsNullOrWhiteSpace(configuredFile))
        {
            return configuredFile;
        }

        var acsDirectory = _configuration["MAILER_ACS_SECRET_HOST_PATH"];
        if (string.IsNullOrWhiteSpace(acsDirectory))
        {
            return string.Empty;
        }

        return Path.Combine(acsDirectory, AcsSecretFileNames.CanonicalFileName);
    }

    private static string ReadEnvironmentName() =>
        Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT")
        ?? "Production";

    private static string? TryFindDefaultComposePath()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var candidate = Path.Combine(directory.FullName, "infra", "deploy", "compose.yml");
            if (File.Exists(candidate))
            {
                return candidate;
            }

            directory = directory.Parent;
        }

        return null;
    }

    private static bool IsPortAvailable(int port)
    {
        try
        {
            using var listener = new TcpListener(IPAddress.Loopback, port);
            listener.Start();
            listener.Stop();
            return true;
        }
        catch (SocketException)
        {
            return false;
        }
    }

    private static bool TryRunProcess(string fileName, string arguments, out int exitCode)
    {
        exitCode = -1;
        try
        {
            using var process = Process.Start(new ProcessStartInfo
            {
                FileName = fileName,
                Arguments = arguments,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            });

            if (process is null)
            {
                return false;
            }

            process.WaitForExit(TimeSpan.FromSeconds(5));
            exitCode = process.ExitCode;
            return exitCode == 0;
        }
        catch
        {
            return false;
        }
    }

    private static string SanitizeDirectoryFailure(string canonicalCode) =>
        canonicalCode switch
        {
            AdminProviderRegisterAcsResultCodes.RejectedDirectoryUnsafe =>
                "Host directory failed safety checks (missing, symlink/reparse point, or overly permissive mode).",
            AdminProviderRegisterAcsResultCodes.RejectedDirectoryNotWritable =>
                "Host directory is not writable (write probe not run in setup doctor; use check-acs-preflight when write access must be verified).",
            _ => "Host directory failed safety checks.",
        };

    private static bool LooksLikeContainerRuntime() =>
        File.Exists("/.dockerenv")
        || string.Equals(
            Environment.GetEnvironmentVariable("DOTNET_RUNNING_IN_CONTAINER"),
            "true",
            StringComparison.OrdinalIgnoreCase);
}
