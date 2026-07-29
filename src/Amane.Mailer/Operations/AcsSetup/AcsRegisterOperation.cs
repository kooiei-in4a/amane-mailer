using System.Net.Mail;
using System.Text.Json;
using Amane.Mailer.Configuration;
using Amane.Mailer.Json;

namespace Amane.Mailer.Operations.AcsSetup;

/// <summary>
/// Console-independent ACS registration request. Adapters collect inputs; this type never
/// reads stdin or writes stdout.
/// </summary>
public sealed class AcsRegisterRequest
{
    public required string EnvironmentConfirmation { get; init; }
    public required string IntentConfirmation { get; init; }
    public required string ConnectionString { get; init; }
    public required string ConnectionStringConfirmation { get; init; }
    public required string SenderEmail { get; init; }
    public required string SenderDisplayName { get; init; }
    public required string AcsSecretDirectory { get; init; }
    public required string PlatformSenderDirectory { get; init; }
}

/// <summary>
/// Canonical registration result safe for Web / terminal / TTY adapters.
/// Never carries secret plaintext, provider raw errors, or unmasked addresses beyond optional mask.
/// </summary>
public sealed class AcsRegisterResult
{
    public required string Code { get; init; }
    public string? InternalEnvironment { get; init; }
    public string? MaskedSenderEmail { get; init; }

    public bool IsSuccess => Code == AdminProviderRegisterAcsResultCodes.Success;

    public static AcsRegisterResult Ok(string internalEnvironment, string maskedSender) =>
        new()
        {
            Code = AdminProviderRegisterAcsResultCodes.Success,
            InternalEnvironment = internalEnvironment,
            MaskedSenderEmail = maskedSender,
        };

    public static AcsRegisterResult Fail(string code) =>
        new() { Code = code };
}

/// <summary>
/// Typed ACS registration Application Service. Shared by TTY CLI and future Web / terminal adapters.
/// </summary>
public sealed class AcsRegisterOperation
{
    public const string IntentPhrase = "MAILER-ACS-REGISTER";

    public AcsRegisterResult Execute(AcsRegisterRequest request)
    {
        try
        {
            if (!AcsEnvironmentConfirmation.TryMap(request.EnvironmentConfirmation, out var internalEnvironment))
            {
                return AcsRegisterResult.Fail(AdminProviderRegisterAcsResultCodes.RejectedEnvironmentMismatch);
            }

            if (!string.Equals(request.IntentConfirmation, IntentPhrase, StringComparison.Ordinal))
            {
                return AcsRegisterResult.Fail(AdminProviderRegisterAcsResultCodes.RejectedIntentMismatch);
            }

            if (!string.Equals(
                    request.ConnectionString,
                    request.ConnectionStringConfirmation,
                    StringComparison.Ordinal))
            {
                return AcsRegisterResult.Fail(AdminProviderRegisterAcsResultCodes.RejectedSecretMismatch);
            }

            if (!AcsConnectionStringRules.LooksLikeAcsConnectionString(request.ConnectionString))
            {
                return AcsRegisterResult.Fail(AdminProviderRegisterAcsResultCodes.RejectedInvalidConnectionString);
            }

            if (!TryValidateBareEmail(request.SenderEmail))
            {
                return AcsRegisterResult.Fail(AdminProviderRegisterAcsResultCodes.RejectedInvalidSenderEmail);
            }

            if (!TryValidateDisplayName(request.SenderDisplayName))
            {
                return AcsRegisterResult.Fail(AdminProviderRegisterAcsResultCodes.RejectedInvalidDisplayName);
            }

            var acsSecretPath = Path.Combine(request.AcsSecretDirectory, AcsSecretFileNames.CanonicalFileName);
            var senderPath = Path.Combine(request.PlatformSenderDirectory, PlatformSenderFile.CanonicalFileName);

            RunPreflight(acsSecretPath, senderPath);

            using var operationLock = ExclusiveOperationLock.Acquire(request.AcsSecretDirectory);
            RunPreflight(acsSecretPath, senderPath);

            var senderFile = new PlatformSenderFile
            {
                Version = 1,
                Environment = internalEnvironment,
                Sender = new PlatformSenderAddress
                {
                    Email = request.SenderEmail,
                    DisplayName = request.SenderDisplayName,
                },
                Provider = "acs",
                LiveSending = false,
            };
            senderFile.Validate();

            var senderJson = JsonSerializer.Serialize(senderFile, MailerJsonContext.Default.PlatformSenderFile);

            TwoPhaseSecretWriteCoordinator.WriteBoth(
                new SecretFileWriter(acsSecretPath, request.AcsSecretDirectory),
                request.ConnectionString,
                new SecretFileWriter(senderPath, request.PlatformSenderDirectory),
                senderJson);

            return AcsRegisterResult.Ok(
                internalEnvironment,
                AcsAddressMask.MaskEmail(request.SenderEmail));
        }
        catch (SecretOperationException ex)
        {
            return AcsRegisterResult.Fail(ex.CanonicalCode);
        }
        catch (Exception)
        {
            return AcsRegisterResult.Fail(AdminProviderRegisterAcsResultCodes.FailedUnexpected);
        }
    }

    public static AcsRegisterResult RunPreflightOnly(string acsSecretDirectory, string platformSenderDirectory)
    {
        try
        {
            var acsSecretPath = Path.Combine(acsSecretDirectory, AcsSecretFileNames.CanonicalFileName);
            var senderPath = Path.Combine(platformSenderDirectory, PlatformSenderFile.CanonicalFileName);
            RunPreflight(acsSecretPath, senderPath);
            return AcsRegisterResult.Ok(string.Empty, string.Empty);
        }
        catch (SecretOperationException ex)
        {
            return AcsRegisterResult.Fail(ex.CanonicalCode);
        }
        catch (Exception)
        {
            return AcsRegisterResult.Fail(AdminProviderRegisterAcsResultCodes.FailedUnexpected);
        }
    }

    private static void RunPreflight(string acsSecretPath, string senderPath)
    {
        var acsDirectory = Path.GetDirectoryName(acsSecretPath)!;
        var senderDirectory = Path.GetDirectoryName(senderPath)!;

        FileSystemSafetyGuard.EnsureDirectoryIsSafe(acsDirectory);
        FileSystemSafetyGuard.EnsureDirectoryIsWritable(acsDirectory);
        FileSystemSafetyGuard.EnsureDirectoryIsSafe(senderDirectory);
        FileSystemSafetyGuard.EnsureDirectoryIsWritable(senderDirectory);

        var state = RegisteredSecretStateInspector.Inspect(acsSecretPath, senderPath);
        switch (state)
        {
            case RegisteredSecretState.Clean:
                return;
            case RegisteredSecretState.FullyRegistered:
                throw new SecretOperationException(
                    AdminProviderRegisterAcsResultCodes.RejectedAlreadyRegistered,
                    "Both the ACS secret and the platform sender file already hold a value.");
            default:
                throw new SecretOperationException(
                    AdminProviderRegisterAcsResultCodes.RejectedPartialState,
                    "Exactly one of the two files holds a value, or one is unparseable. Manual review is required before retrying.");
        }
    }

    private static bool TryValidateBareEmail(string email) =>
        MailAddress.TryCreate(email, out var parsed)
        && string.Equals(parsed.Address, email, StringComparison.Ordinal);

    private static bool TryValidateDisplayName(string displayName) =>
        !string.IsNullOrEmpty(displayName)
        && displayName.Length <= 200
        && !displayName.Any(char.IsControl);
}
