using System.Globalization;
using Amane.Mailer.Configuration;
using System.Text.RegularExpressions;

namespace Amane.Mailer.Setup;

/// <summary>
/// Key-specific value schema checks for <see cref="SetupRequest.PublicEnvOverrides"/> and
/// typed image fields that flow into compose.env.
/// </summary>
public static partial class SetupPublicEnvOverrideValidator
{
    private const int RegexMatchTimeoutMilliseconds = 250;

    public static bool TryValidate(
        IReadOnlyDictionary<string, string> overrides,
        string? imageRepository,
        string? imageTag,
        out string message)
    {
        message = string.Empty;

        foreach (var pair in overrides)
        {
            if (!IsEnvFileSafeValue(pair.Value))
            {
                message = "Public env override values must not contain unsupported control characters.";
                return false;
            }

            if (!TryValidateKey(pair.Key, pair.Value, out message))
            {
                return false;
            }
        }

        if (imageRepository is not null)
        {
            if (string.IsNullOrWhiteSpace(imageRepository)
                || !IsEnvFileSafeValue(imageRepository)
                || !ImageRepositoryRegex().IsMatch(imageRepository))
            {
                message = "Image repository is not a valid compose image repository value.";
                return false;
            }
        }

        if (imageTag is not null)
        {
            if (string.IsNullOrWhiteSpace(imageTag)
                || !IsEnvFileSafeValue(imageTag)
                || !ImageTagRegex().IsMatch(imageTag))
            {
                message = "Image tag is not a valid compose image tag value.";
                return false;
            }
        }

        return true;
    }

    private static bool TryValidateKey(string key, string value, out string message)
    {
        message = string.Empty;
        switch (key)
        {
            case "MAILER_METRICS_ENABLED":
            case "ASPNETCORE_FORWARDEDHEADERS_ENABLED":
                if (value is not ("true" or "false"))
                {
                    message = $"{key} must be exactly 'true' or 'false'.";
                    return false;
                }

                return true;

            case "MAILER_PULL_POLICY":
                if (value is not ("always" or "never" or "missing" or "build"))
                {
                    message = "MAILER_PULL_POLICY must be one of: always, never, missing, build.";
                    return false;
                }

                return true;

            case "MAILER_HTTP_PORT":
                if (!int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var port)
                    || port is < 1 or > 65535)
                {
                    message = "MAILER_HTTP_PORT must be an integer from 1 to 65535.";
                    return false;
                }

                return true;

            case "MAILER_HEALTHCHECK_RETRIES":
            case "LOG_MAX_FILE":
            case "MAILER_RETENTION_DAYS":
                if (!int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var days)
                    || days < MailerRetentionOptions.MinRetentionDays
                    || days > MailerRetentionOptions.MaxRetentionDays)
                {
                    message =
                        $"MAILER_RETENTION_DAYS must be an integer between {MailerRetentionOptions.MinRetentionDays} and {MailerRetentionOptions.MaxRetentionDays} (inclusive).";
                    return false;
                }

                return true;

            case "MAILER_RETENTION_SWEEP_INTERVAL_HOURS":
                if (!int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var hours)
                    || hours < MailerRetentionOptions.MinSweepIntervalHours
                    || hours > MailerRetentionOptions.MaxSweepIntervalHours)
                {
                    message =
                        $"MAILER_RETENTION_SWEEP_INTERVAL_HOURS must be an integer between {MailerRetentionOptions.MinSweepIntervalHours} and {MailerRetentionOptions.MaxSweepIntervalHours} (inclusive).";
                    return false;
                }

                return true;

            case "MAILER_CPUS":
                if (!decimal.TryParse(value, NumberStyles.Number, CultureInfo.InvariantCulture, out var cpus)
                    || cpus <= 0)
                {
                    message = "MAILER_CPUS must be a positive decimal.";
                    return false;
                }

                return true;

            case "MAILER_MEM_LIMIT":
            case "LOG_MAX_SIZE":
                if (!ByteSizeRegex().IsMatch(value))
                {
                    message = $"{key} must look like a Docker byte size (e.g. 512m, 1g).";
                    return false;
                }

                return true;

            case "MAILER_STOP_GRACE_PERIOD":
            case "MAILER_HEALTHCHECK_INTERVAL":
            case "MAILER_HEALTHCHECK_TIMEOUT":
            case "MAILER_HEALTHCHECK_START_PERIOD":
                if (!DurationRegex().IsMatch(value))
                {
                    message = $"{key} must look like a Docker duration (e.g. 30s, 2m).";
                    return false;
                }

                return true;

            case "COMPOSE_PROJECT_NAME":
                if (!ComposeProjectNameRegex().IsMatch(value))
                {
                    message = "COMPOSE_PROJECT_NAME must be lowercase alphanumeric, '-', or '_', starting with a letter or digit.";
                    return false;
                }

                return true;

            case "MAILER_NETWORK_NAME":
            case "MAILER_NETWORK_ALIAS":
                if (!DnsLabelRegex().IsMatch(value))
                {
                    message = $"{key} must be a DNS-safe label.";
                    return false;
                }

                return true;

            case "MAILER_IMAGE_REPOSITORY":
                if (!ImageRepositoryRegex().IsMatch(value))
                {
                    message = "MAILER_IMAGE_REPOSITORY is not a valid compose image repository value.";
                    return false;
                }

                return true;

            case "MAILER_IMAGE_TAG":
                if (!ImageTagRegex().IsMatch(value))
                {
                    message = "MAILER_IMAGE_TAG is not a valid compose image tag value.";
                    return false;
                }

                return true;

            default:
                message = "Public env override key is not allowlisted for Setup Core.";
                return false;
        }
    }

    public static bool IsEnvFileSafeValue(string value)
    {
        foreach (var ch in value)
        {
            if (ch is '\0' or '\n' or '\r')
            {
                return false;
            }

            if (ch < 0x20 && ch is not '\t')
            {
                return false;
            }
        }

        return true;
    }

    [GeneratedRegex(@"^[1-9]\d*[bBkKmMgGtT]?$", RegexOptions.CultureInvariant, RegexMatchTimeoutMilliseconds)]
    private static partial Regex ByteSizeRegex();

    [GeneratedRegex(@"^[1-9]\d*[smh]$", RegexOptions.CultureInvariant, RegexMatchTimeoutMilliseconds)]
    private static partial Regex DurationRegex();

    [GeneratedRegex(@"^[a-zA-Z0-9][a-zA-Z0-9_.-]*$", RegexOptions.CultureInvariant, RegexMatchTimeoutMilliseconds)]
    private static partial Regex DnsLabelRegex();

    [GeneratedRegex(@"^[a-z0-9][a-z0-9_-]*$", RegexOptions.CultureInvariant, RegexMatchTimeoutMilliseconds)]
    private static partial Regex ComposeProjectNameRegex();

    // Docker image reference name without tag: [HOST[:PORT]/]PATH (path components lowercase).
    [GeneratedRegex(
        @"^(?:(?:[a-zA-Z0-9](?:[a-zA-Z0-9-]{0,61}[a-zA-Z0-9])?)(?:\.(?:[a-zA-Z0-9](?:[a-zA-Z0-9-]{0,61}[a-zA-Z0-9])?))*(?::[0-9]{1,5})?/)?[a-z0-9]+(?:[._-][a-z0-9]+)*(?:/[a-z0-9]+(?:[._-][a-z0-9]+)*)*$",
        RegexOptions.CultureInvariant,
        RegexMatchTimeoutMilliseconds)]
    private static partial Regex ImageRepositoryRegex();

    [GeneratedRegex(@"^[A-Za-z0-9_][A-Za-z0-9_.-]{0,127}$", RegexOptions.CultureInvariant, RegexMatchTimeoutMilliseconds)]
    private static partial Regex ImageTagRegex();
}
