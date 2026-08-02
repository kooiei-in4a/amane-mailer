using System.Net.Mail;
using Amane.Mailer.Setup;

namespace Amane.Mailer.Operations.AcsSetup;

/// <summary>
/// Storage- and console-independent ACS input validation shared by Manual registration,
/// Managed bundle generation, and TTY adapters.
/// </summary>
public static class AcsConfigurationValidator
{
    public static string? ValidateEnvironment(string? confirmation, SetupMode? expectedMode = null)
    {
        if (!AcsEnvironmentConfirmation.TryMap(confirmation, out var environment))
        {
            return AdminProviderRegisterAcsResultCodes.RejectedEnvironmentMismatch;
        }

        var expected = expectedMode switch
        {
            SetupMode.StagingNoSend or SetupMode.StagingVerification =>
                AcsEnvironmentConfirmation.InternalStaging,
            SetupMode.ProductionAcs => AcsEnvironmentConfirmation.InternalProduction,
            null => environment,
            _ => string.Empty,
        };

        return string.Equals(environment, expected, StringComparison.Ordinal)
            ? null
            : AdminProviderRegisterAcsResultCodes.RejectedEnvironmentMismatch;
    }

    public static string? ValidateIntent(string? intent, string expectedPhrase) =>
        string.Equals(intent, expectedPhrase, StringComparison.Ordinal)
            ? null
            : AdminProviderRegisterAcsResultCodes.RejectedIntentMismatch;

    public static string? ValidateConnectionStrings(string? value, string? confirmation)
    {
        if (!string.Equals(value, confirmation, StringComparison.Ordinal))
        {
            return AdminProviderRegisterAcsResultCodes.RejectedSecretMismatch;
        }

        return AcsConnectionStringRules.LooksLikeAcsConnectionString(value)
            ? null
            : AdminProviderRegisterAcsResultCodes.RejectedInvalidConnectionString;
    }

    public static string? ValidateSenderEmail(string? email)
    {
        if (email is null
            || !MailAddress.TryCreate(email, out var parsed)
            || !string.Equals(parsed.Address, email, StringComparison.Ordinal))
        {
            return AdminProviderRegisterAcsResultCodes.RejectedInvalidSenderEmail;
        }

        return null;
    }

    public static string? ValidateDisplayName(string? displayName) =>
        string.IsNullOrEmpty(displayName)
            || displayName.Length > 200
            || displayName.Any(char.IsControl)
                ? AdminProviderRegisterAcsResultCodes.RejectedInvalidDisplayName
                : null;

    public static string? ValidateSender(string? email, string? displayName) =>
        ValidateSenderEmail(email) ?? ValidateDisplayName(displayName);

    public static string? ValidateManagedRequest(
        SetupRequest request,
        string? environmentConfirmation,
        string? intentConfirmation,
        string? connectionStringConfirmation)
    {
        var error = ValidateEnvironment(environmentConfirmation, request.Mode)
            ?? ValidateIntent(intentConfirmation, AcsRegisterOperation.IntentPhrase)
            ?? ValidateConnectionStrings(request.AcsConnectionString, connectionStringConfirmation);
        if (error is not null)
        {
            return error;
        }

        if (request.PlatformSender is null)
        {
            return AdminProviderRegisterAcsResultCodes.RejectedInvalidSenderEmail;
        }

        return ValidateSender(request.PlatformSender.Email, request.PlatformSender.DisplayName);
    }
}
