using Amane.Mailer.Operations.AcsSetup;

namespace Amane.Mailer.Operations;

/// <summary>
/// <c>admin provider register-acs</c> TTY adapter over <see cref="AcsRegisterOperation"/>.
/// </summary>
public sealed partial class AdminProviderRegisterAcsCommand(
    IAdminProviderRegisterAcsConsole console,
    string acsSecretDirectory,
    string platformSenderDirectory)
{
    public const string IntentPhrase = AcsRegisterOperation.IntentPhrase;

    /// <summary>
    /// Exact Staging confirmation phrase. Tenant schema uses lowercase <c>staging</c>; this
    /// command deliberately does not fold case or accept that spelling from the operator.
    /// </summary>
    public const string StagingEnvironmentConfirmation = AcsEnvironmentConfirmation.Staging;

    /// <summary>
    /// Exact Production confirmation phrase. Do not ask production operators to type
    /// <see cref="StagingEnvironmentConfirmation"/>; that destroys the environment safety check.
    /// </summary>
    public const string ProductionEnvironmentConfirmation = AcsEnvironmentConfirmation.Production;

    /// <summary>
    /// Retained for callers/tests that still refer to the Staging-only era constant.
    /// Prefer <see cref="StagingEnvironmentConfirmation"/> or
    /// <see cref="ProductionEnvironmentConfirmation"/>.
    /// </summary>
    public const string RequiredEnvironmentConfirmation = StagingEnvironmentConfirmation;

    private readonly AcsRegisterOperation _operation = new();

    public static bool IsRegisterAcsCommand(IReadOnlyList<string> args) =>
        args.Count == 3
        && string.Equals(args[0], "admin", StringComparison.Ordinal)
        && string.Equals(args[1], "provider", StringComparison.Ordinal)
        && string.Equals(args[2], "register-acs", StringComparison.Ordinal);

    public static bool IsCheckAcsPreflightCommand(IReadOnlyList<string> args) =>
        args.Count == 3
        && string.Equals(args[0], "admin", StringComparison.Ordinal)
        && string.Equals(args[1], "provider", StringComparison.Ordinal)
        && string.Equals(args[2], "check-acs-preflight", StringComparison.Ordinal);

    /// <summary>
    /// Non-interactive health check for the two mounted directories.
    /// </summary>
    public int RunPreflightOnly()
    {
        var result = AcsRegisterOperation.RunPreflightOnly(acsSecretDirectory, platformSenderDirectory);
        if (result.IsSuccess)
        {
            console.WriteLine($"success: operation=check_acs_preflight result={AdminProviderRegisterAcsResultCodes.Success}");
            return 0;
        }

        return Reject("check_acs_preflight", result.Code);
    }

    public int Run()
    {
        try
        {
            var preflight = AcsRegisterOperation.RunPreflightOnly(acsSecretDirectory, platformSenderDirectory);
            if (!preflight.IsSuccess)
            {
                return Reject("register_acs", preflight.Code);
            }

            var environmentConfirmation = console.ReadLine("Confirm target environment (exact match): ");
            if (!AcsEnvironmentConfirmation.TryMap(environmentConfirmation, out _))
            {
                return Reject("register_acs", AdminProviderRegisterAcsResultCodes.RejectedEnvironmentMismatch);
            }

            var intent = console.ReadLine($"Type {IntentPhrase} to confirm intent: ");
            if (!string.Equals(intent, IntentPhrase, StringComparison.Ordinal))
            {
                return Reject("register_acs", AdminProviderRegisterAcsResultCodes.RejectedIntentMismatch);
            }

            var connectionString = console.ReadSecret("ACS connection string: ");
            var connectionStringConfirmation = console.ReadSecret("Re-enter ACS connection string: ");
            if (!string.Equals(connectionString, connectionStringConfirmation, StringComparison.Ordinal))
            {
                return Reject("register_acs", AdminProviderRegisterAcsResultCodes.RejectedSecretMismatch);
            }

            if (!AcsConnectionStringRules.LooksLikeAcsConnectionString(connectionString))
            {
                return Reject("register_acs", AdminProviderRegisterAcsResultCodes.RejectedInvalidConnectionString);
            }

            var senderEmail = console.ReadLine("Sender email: ").Trim();
            if (!System.Net.Mail.MailAddress.TryCreate(senderEmail, out var parsed)
                || !string.Equals(parsed.Address, senderEmail, StringComparison.Ordinal))
            {
                return Reject("register_acs", AdminProviderRegisterAcsResultCodes.RejectedInvalidSenderEmail);
            }

            var senderDisplayName = console.ReadLine("Sender display name: ");
            if (string.IsNullOrEmpty(senderDisplayName)
                || senderDisplayName.Length > 200
                || senderDisplayName.Any(char.IsControl))
            {
                return Reject("register_acs", AdminProviderRegisterAcsResultCodes.RejectedInvalidDisplayName);
            }

            var result = _operation.Execute(new AcsRegisterRequest
            {
                EnvironmentConfirmation = environmentConfirmation,
                IntentConfirmation = intent,
                ConnectionString = connectionString,
                ConnectionStringConfirmation = connectionStringConfirmation,
                SenderEmail = senderEmail,
                SenderDisplayName = senderDisplayName,
                AcsSecretDirectory = acsSecretDirectory,
                PlatformSenderDirectory = platformSenderDirectory,
            });

            if (result.IsSuccess)
            {
                console.WriteLine($"success: operation=register_acs result={AdminProviderRegisterAcsResultCodes.Success}");
                return 0;
            }

            return Reject("register_acs", result.Code);
        }
        catch (SecretOperationException ex)
        {
            return Reject("register_acs", ex.CanonicalCode);
        }
        catch (Exception)
        {
            return Reject("register_acs", AdminProviderRegisterAcsResultCodes.FailedUnexpected);
        }
    }

    /// <summary>
    /// Fixed one-way map from exact operator confirmation to the platform-sender schema value.
    /// </summary>
    internal static bool TryMapEnvironmentConfirmation(string? confirmation, out string internalEnvironment) =>
        AcsEnvironmentConfirmation.TryMap(confirmation, out internalEnvironment);

    private int Reject(string operationCode, string canonicalCode)
    {
        console.WriteError($"rejected: operation={operationCode} result={canonicalCode}");
        return 2;
    }
}
