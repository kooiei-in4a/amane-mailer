using System.Text.RegularExpressions;
using Amane.Mailer.Configuration;
using Amane.Mailer.Operations;

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

        foreach (var key in request.PublicEnvOverrides.Keys)
        {
            if (!ManagedEnvKeyCatalog.TryClassify(key, out var keyClass))
            {
                message = "Public env override contains an unknown key.";
                return false;
            }

            if (keyClass != ManagedEnvKeyCatalog.KeyClass.PublicNonSecret)
            {
                message = "Public env override must only contain public/non-secret keys.";
                return false;
            }

            if (ManagedEnvKeyCatalog.ExternalManualOnlyKeys.Contains(key)
                || ManagedEnvKeyCatalog.SecretValuedEnvironmentKeys.Contains(key))
            {
                message = "Public env override must not include secret or external keys.";
                return false;
            }
        }

        foreach (var key in request.TokenSecrets.Keys)
        {
            if (!ManagedEnvKeyCatalog.SecretValuedEnvironmentKeys.Contains(key))
            {
                message = "Token secret key is not an allowlisted secret-valued environment key.";
                return false;
            }

            if (string.IsNullOrWhiteSpace(request.TokenSecrets[key]))
            {
                message = "Token secret values must be non-empty.";
                return false;
            }
        }

        foreach (var tenant in request.Tenants.Tenants)
        {
            if (!request.TokenSecrets.ContainsKey(tenant.TokenEnv))
            {
                message = "Each tenant token_env must have a corresponding token secret.";
                return false;
            }
        }

        if (!string.IsNullOrEmpty(request.MetricsBearerToken)
            && string.IsNullOrWhiteSpace(request.MetricsBearerToken))
        {
            message = "Metrics bearer token must not be whitespace.";
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

            if (request.PlatformSender is null)
            {
                message = "Platform sender representation is required for ACS modes.";
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

    [GeneratedRegex(
        @"^(?:endpoint=https://.+;accesskey=.+)$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
        RegexMatchTimeoutMilliseconds)]
    private static partial Regex AcsConnectionStringRegex();
}
