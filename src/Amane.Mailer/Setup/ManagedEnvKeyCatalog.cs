namespace Amane.Mailer.Setup;

/// <summary>
/// ADR 0021 D-02 env key classification against infra/deploy/.env.example and compose.
/// Classification sets are mutually exclusive.
/// </summary>
public static class ManagedEnvKeyCatalog
{
    public enum KeyClass
    {
        PublicNonSecret,
        SecretValuedEnvironment,
        FileSecret,
        ExternalManualOnly,
    }

    public static IReadOnlySet<string> PublicNonSecretKeys { get; } = new HashSet<string>(StringComparer.Ordinal)
    {
        "COMPOSE_PROJECT_NAME",
        "MAILER_IMAGE_REPOSITORY",
        "MAILER_IMAGE_TAG",
        "MAILER_IMAGE_REFERENCE",
        "MAILER_PULL_POLICY",
        "MAILER_MEM_LIMIT",
        "MAILER_CPUS",
        "MAILER_STOP_GRACE_PERIOD",
        "MAILER_HTTP_PORT",
        "ASPNETCORE_FORWARDEDHEADERS_ENABLED",
        "MAILER_NETWORK_NAME",
        "MAILER_NETWORK_ALIAS",
        "MAILER_HEALTHCHECK_INTERVAL",
        "MAILER_HEALTHCHECK_TIMEOUT",
        "MAILER_HEALTHCHECK_RETRIES",
        "MAILER_HEALTHCHECK_START_PERIOD",
        "LOG_MAX_SIZE",
        "LOG_MAX_FILE",
        "MAILER_PROVIDER",
        "MAILER_TENANTS_HOST_PATH",
        "MAILER_TENANTS_CONTAINER_PATH",
        "MAILER_ACS_SECRET_HOST_PATH",
        "MAILER_PLATFORM_SENDER_HOST_PATH",
        "MAILER_BOUNCE_INGESTION",
        "MAILER_BOUNCE_QUEUE_NAME",
        "MAILER_BOUNCE_QUEUE_SECRET_HOST_PATH",
        "MAILER_RETENTION_DAYS",
        "MAILER_RETENTION_SWEEP_INTERVAL_HOURS",
        "MAILER_METRICS_ENABLED",
        "AMANE_ADMIN_ENABLED",
        "AMANE_ADMIN_USERNAME",
        "AMANE_ADMIN_ALLOWED_LOCAL_ADDRESS",
        "AMANE_ADMIN_ALLOW_HTTP",
        "AMANE_ADMIN_PII_LIST_MODE",
        "MAILER_SETUP_RECORDED_METADATA_PATH",
        "MAILER_SETUP_RECORDED_METADATA_HOST_PATH",
    };

    /// <summary>
    /// Keys callers may supply via <see cref="SetupRequest.PublicEnvOverrides"/>.
    /// Workflow-owned Admin/bounce/provider/path-binding/image keys are excluded and must use typed
    /// inputs or Core-fixed values instead.
    /// </summary>
    public static IReadOnlySet<string> PublicEnvOverrideAllowlist { get; } = new HashSet<string>(StringComparer.Ordinal)
    {
        "COMPOSE_PROJECT_NAME",
        "MAILER_PULL_POLICY",
        "MAILER_MEM_LIMIT",
        "MAILER_CPUS",
        "MAILER_STOP_GRACE_PERIOD",
        "MAILER_HTTP_PORT",
        "ASPNETCORE_FORWARDEDHEADERS_ENABLED",
        "MAILER_NETWORK_NAME",
        "MAILER_NETWORK_ALIAS",
        "MAILER_HEALTHCHECK_INTERVAL",
        "MAILER_HEALTHCHECK_TIMEOUT",
        "MAILER_HEALTHCHECK_RETRIES",
        "MAILER_HEALTHCHECK_START_PERIOD",
        "LOG_MAX_SIZE",
        "LOG_MAX_FILE",
        "MAILER_RETENTION_DAYS",
        "MAILER_RETENTION_SWEEP_INTERVAL_HOURS",
        "MAILER_METRICS_ENABLED",
    };

    public static IReadOnlySet<string> SecretValuedEnvironmentKeys { get; } = new HashSet<string>(StringComparer.Ordinal)
    {
        "MAIL_SERVICE_TOKEN",
        "MAIL_SERVICE_TOKEN_DEVELOP",
        "MAIL_SERVICE_TOKEN_STAGING",
        "MAIL_SERVICE_TOKEN_PRODUCTION",
        "MAILER_METRICS_BEARER_TOKEN",
        "AMANE_ADMIN_PASSWORD_HASH",
    };

    public static IReadOnlySet<string> FileSecretNames { get; } = new HashSet<string>(StringComparer.Ordinal)
    {
        "acs_connection_string",
        "queue_connection_string",
    };

    public static IReadOnlySet<string> ExternalManualOnlyKeys { get; } = new HashSet<string>(StringComparer.Ordinal)
    {
        "MAILER_DATA_PATH",
        "MAILER_CONNECTION_STRING",
        "MAILER_BACKUP_ENCRYPTION_PUBLIC_KEY",
        "MAILER_BACKUP_RCLONE_REMOTE",
        "MAILER_BACKUP_RCLONE_CONFIG_PATH",
        "MAILER_BACKUP_REQUIRE_OFFSITE",
        "MAILER_BACKUP_PING_URL",
    };

    public static bool TryClassify(string key, out KeyClass keyClass)
    {
        if (PublicNonSecretKeys.Contains(key))
        {
            keyClass = KeyClass.PublicNonSecret;
            return true;
        }

        if (SecretValuedEnvironmentKeys.Contains(key))
        {
            keyClass = KeyClass.SecretValuedEnvironment;
            return true;
        }

        if (ExternalManualOnlyKeys.Contains(key))
        {
            keyClass = KeyClass.ExternalManualOnly;
            return true;
        }

        keyClass = default;
        return false;
    }

    public static bool IsFileSecretName(string fileName) => FileSecretNames.Contains(fileName);
}
