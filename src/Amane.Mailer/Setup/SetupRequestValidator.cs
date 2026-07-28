using System.Text.RegularExpressions;
using Amane.Mailer.Configuration;

namespace Amane.Mailer.Setup;

public static partial class SetupRequestValidator
{
    private const int RegexMatchTimeoutMilliseconds = 250;

    public static bool TryValidate(SetupRequest request, out string failureCode, out string message)
    {
        failureCode = SetupResultCode.RejectedValidation;
        message = "Request validation failed.";

        if (request.Mode is < SetupMode.LocalMailpit or > SetupMode.ProductionAcs)
        {
            failureCode = SetupResultCode.RejectedModeUnsupported;
            message = "Setup mode is not supported by Easy Setup Core.";
            return false;
        }

        if (string.IsNullOrWhiteSpace(request.ManagedRootPath))
        {
            failureCode = SetupResultCode.RejectedPathUnsafe;
            message = "Managed root path is required.";
            return false;
        }

        if (!Path.IsPathRooted(request.ManagedRootPath))
        {
            failureCode = SetupResultCode.RejectedPathUnsafe;
            message = "Managed root path must be absolute.";
            return false;
        }

        try
        {
            request.Tenants.Validate();
            foreach (var tenant in request.Tenants.Tenants)
            {
                tenant.Validate();
            }
        }
        catch (InvalidOperationException)
        {
            message = "Tenant configuration failed validation.";
            return false;
        }

        var seenTenantIds = new HashSet<Guid>();
        foreach (var tenant in request.Tenants.Tenants)
        {
            if (!seenTenantIds.Add(tenant.TenantId))
            {
                message = "Duplicate tenant_id is not allowed.";
                return false;
            }
        }

        var expectedEnvironment = ExpectedEnvironment(request.Mode);
        if (!request.Tenants.Environment.Equals(expectedEnvironment, StringComparison.Ordinal))
        {
            message = "Tenant configuration environment must match the Setup mode.";
            return false;
        }

        foreach (var tenant in request.Tenants.Tenants)
        {
            // #451 owns live_sending=true promotion after exact environment confirmation.
            if (tenant.LiveSending)
            {
                message = "live_sending=true is not accepted by Setup Core; use the ACS approval workflow.";
                return false;
            }
        }

        foreach (var key in request.PublicEnvOverrides.Keys)
        {
            if (!ManagedEnvKeyCatalog.PublicEnvOverrideAllowlist.Contains(key))
            {
                message = "Public env override key is not allowlisted for Setup Core.";
                return false;
            }
        }

        if (!SetupPublicEnvOverrideValidator.TryValidate(
                request.PublicEnvOverrides,
                request.ImageRepository,
                request.ImageTag,
                out message))
        {
            return false;
        }

        foreach (var key in request.TokenSecrets.Keys)
        {
            // Admin password/hash bootstrap is owned by #459. Silent ignore is forbidden (ADR D-10).
            // Check before exact token_env inventory so the dedicated message is preserved.
            if (key.Equals("AMANE_ADMIN_PASSWORD_HASH", StringComparison.Ordinal))
            {
                message = "Admin password hash is not accepted by Setup Core; use the Admin bootstrap workflow.";
                return false;
            }
        }

        var requiredTokenEnvs = new HashSet<string>(StringComparer.Ordinal);
        foreach (var tenant in request.Tenants.Tenants)
        {
            requiredTokenEnvs.Add(tenant.TokenEnv);
        }

        if (request.TokenSecrets.Count != requiredTokenEnvs.Count
            || requiredTokenEnvs.Any(name => !request.TokenSecrets.ContainsKey(name))
            || request.TokenSecrets.Keys.Any(name => !requiredTokenEnvs.Contains(name)))
        {
            message = "Token secrets must exactly match tenant token_env names.";
            return false;
        }

        foreach (var key in request.TokenSecrets.Keys)
        {
            if (!ManagedEnvKeyCatalog.SecretValuedEnvironmentKeys.Contains(key))
            {
                message = "Token secret key is not an allowlisted secret-valued environment key.";
                return false;
            }

            if (key.Equals("MAILER_METRICS_BEARER_TOKEN", StringComparison.Ordinal)
                || key.Equals("AMANE_ADMIN_PASSWORD_HASH", StringComparison.Ordinal))
            {
                message = "Token secret keys must not use metrics or Admin reserved environment names.";
                return false;
            }

            if (ManagedEnvKeyCatalog.PublicNonSecretKeys.Contains(key)
                || ManagedEnvKeyCatalog.ExternalManualOnlyKeys.Contains(key))
            {
                message = "Token secret keys must not collide with public or external environment names.";
                return false;
            }

            if (!TryValidateSecretValue(request.TokenSecrets[key], "Token secret", out message))
            {
                return false;
            }
        }

        var requiredWebhookSecretEnvs = new HashSet<string>(StringComparer.Ordinal);
        foreach (var tenant in request.Tenants.Tenants)
        {
            if (tenant.Webhook is null)
            {
                continue;
            }

            requiredWebhookSecretEnvs.Add(tenant.Webhook.SecretEnv);
        }

        if (requiredWebhookSecretEnvs.Count != request.WebhookSecrets.Count
            || requiredWebhookSecretEnvs.Any(name => !request.WebhookSecrets.ContainsKey(name))
            || request.WebhookSecrets.Keys.Any(name => !requiredWebhookSecretEnvs.Contains(name)))
        {
            message = "Webhook secrets must exactly match tenant webhook.secret_env names.";
            return false;
        }

        foreach (var pair in request.WebhookSecrets)
        {
            if (!TryValidateSecretValue(pair.Value, "Webhook secret", out message))
            {
                return false;
            }

            if (!TryEnsureSecretKeyIsExclusive(
                    pair.Key,
                    requiredTokenEnvs,
                    out message))
            {
                return false;
            }
        }

        if (requiredTokenEnvs.Overlaps(requiredWebhookSecretEnvs))
        {
            message = "token_env and webhook.secret_env names must be mutually exclusive.";
            return false;
        }

        var metricsEnabled = IsMetricsEnabled(request);
        if (metricsEnabled)
        {
            if (!TryValidateSecretValue(request.MetricsBearerToken, "Metrics bearer token", out message))
            {
                message = "Metrics bearer token is required when MAILER_METRICS_ENABLED=true.";
                return false;
            }
        }
        else if (!string.IsNullOrEmpty(request.MetricsBearerToken)
                 && string.IsNullOrWhiteSpace(request.MetricsBearerToken))
        {
            message = "Metrics bearer token must not be whitespace.";
            return false;
        }
        else if (!string.IsNullOrEmpty(request.MetricsBearerToken)
                 && !IsEnvFileSafeSecret(request.MetricsBearerToken))
        {
            message = "Metrics bearer token contains unsupported control characters.";
            return false;
        }

        var requiresAcs = request.Mode is SetupMode.StagingNoSend
            or SetupMode.StagingVerification
            or SetupMode.ProductionAcs;

        if (requiresAcs)
        {
            if (string.IsNullOrWhiteSpace(request.AcsConnectionString)
                || !AcsConnectionStringRegex().IsMatch(request.AcsConnectionString))
            {
                message = "ACS connection string is required and must look like an ACS endpoint/accesskey value.";
                return false;
            }

            if (!IsEnvFileSafeSecret(request.AcsConnectionString))
            {
                message = "ACS connection string contains unsupported control characters.";
                return false;
            }

            if (request.PlatformSender is null)
            {
                message = "Platform sender representation is required for ACS modes.";
                return false;
            }

            if (!request.PlatformSender.Environment.Equals(expectedEnvironment, StringComparison.Ordinal))
            {
                message = "Platform sender environment must match the Setup mode.";
                return false;
            }

            try
            {
                BuildPlatformSender(request.PlatformSender).Validate();
            }
            catch (InvalidOperationException)
            {
                message = "Platform sender representation failed validation.";
                return false;
            }

            foreach (var tenant in request.Tenants.Tenants)
            {
                if (!tenant.Provider.Equals("acs", StringComparison.Ordinal))
                {
                    message = "ACS setup modes require tenant provider=acs.";
                    return false;
                }
            }
        }
        else
        {
            if (!string.IsNullOrEmpty(request.AcsConnectionString) || request.PlatformSender is not null)
            {
                message = "Local Mailpit mode must not include ACS secrets or platform sender.";
                return false;
            }

            foreach (var tenant in request.Tenants.Tenants)
            {
                if (!tenant.Provider.Equals("mailpit", StringComparison.Ordinal))
                {
                    message = "Local Mailpit mode requires tenant provider=mailpit.";
                    return false;
                }
            }
        }

        if (request.Admin is not null)
        {
            // Admin enablement / bootstrap is owned by #459. Core must fail closed instead of
            // silently writing AMANE_ADMIN_ENABLED=true into compose.env.
            if (request.Admin.Enabled || request.Admin.AllowHttp)
            {
                message = "Admin enablement is not performed by Setup Core; use the Admin bootstrap workflow.";
                return false;
            }

            if (string.IsNullOrWhiteSpace(request.Admin.Username))
            {
                message = "Admin username must be non-empty when Admin representation is provided.";
                return false;
            }

            if (string.IsNullOrWhiteSpace(request.Admin.AllowedLocalAddress))
            {
                message = "Admin allowed local address must be non-empty when Admin representation is provided.";
                return false;
            }
        }

        if (!request.DryRun && OperatingSystem.IsLinux() && request.RuntimeFileOwnership is null)
        {
            failureCode = SetupResultCode.RejectedOwnershipRequired;
            message = "Linux bundle generation requires runtime file ownership (container UID/GID).";
            return false;
        }

        if (request.RuntimeFileOwnership is not null
            && (request.RuntimeFileOwnership.UnixUserId == 0 || request.RuntimeFileOwnership.UnixGroupId == 0))
        {
            // Refuse root ownership as the Mailer runtime identity; deploy images use non-root APP_UID.
            failureCode = SetupResultCode.RejectedOwnershipRequired;
            message = "Runtime file ownership must use a non-root container UID/GID.";
            return false;
        }

        failureCode = string.Empty;
        message = string.Empty;
        return true;
    }

    public static PlatformSenderFile BuildPlatformSender(SetupPlatformSenderInput input) =>
        new()
        {
            Version = 1,
            Environment = input.Environment,
            Sender = new PlatformSenderAddress
            {
                Email = input.Email,
                DisplayName = input.DisplayName,
            },
            Provider = "acs",
            LiveSending = false,
        };

    public static string ExpectedEnvironment(SetupMode mode) =>
        mode switch
        {
            SetupMode.LocalMailpit => "develop",
            SetupMode.StagingNoSend or SetupMode.StagingVerification => "staging",
            SetupMode.ProductionAcs => "production",
            _ => "develop",
        };

    public static bool IsMetricsEnabled(SetupRequest request)
    {
        if (request.PublicEnvOverrides.TryGetValue("MAILER_METRICS_ENABLED", out var raw))
        {
            // Public override schema requires exact true/false before this runs.
            return string.Equals(raw, "true", StringComparison.Ordinal);
        }

        // Matches SetupConfigurationMaterializer default.
        return true;
    }

    private static bool TryEnsureSecretKeyIsExclusive(
        string key,
        IReadOnlySet<string> requiredTokenEnvs,
        out string message)
    {
        message = string.Empty;
        if (requiredTokenEnvs.Contains(key)
            || key.Equals("MAILER_METRICS_BEARER_TOKEN", StringComparison.Ordinal)
            || key.Equals("AMANE_ADMIN_PASSWORD_HASH", StringComparison.Ordinal)
            || ManagedEnvKeyCatalog.PublicNonSecretKeys.Contains(key)
            || ManagedEnvKeyCatalog.ExternalManualOnlyKeys.Contains(key)
            || ManagedEnvKeyCatalog.SecretValuedEnvironmentKeys.Contains(key))
        {
            message = "Webhook secret env names must not collide with token, metrics, Admin, public, or external keys.";
            return false;
        }

        return true;
    }

    private static bool TryValidateSecretValue(string? value, string label, out string message)
    {
        message = string.Empty;
        if (string.IsNullOrWhiteSpace(value))
        {
            message = $"{label} must be non-empty.";
            return false;
        }

        if (!IsEnvFileSafeSecret(value))
        {
            message = $"{label} contains unsupported control characters.";
            return false;
        }

        return true;
    }

    /// <summary>
    /// Secrets must round-trip through Compose env-file serialization without CR/LF/NUL.
    /// </summary>
    public static bool IsEnvFileSafeSecret(string value) =>
        SetupPublicEnvOverrideValidator.IsEnvFileSafeValue(value);

    [GeneratedRegex(
        @"^(?:endpoint=https://.+;accesskey=.+)$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
        RegexMatchTimeoutMilliseconds)]
    private static partial Regex AcsConnectionStringRegex();
}
