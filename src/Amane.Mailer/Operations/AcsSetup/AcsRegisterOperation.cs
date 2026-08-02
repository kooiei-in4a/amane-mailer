using System.Text.Json;
using Amane.Mailer.Configuration;
using Amane.Mailer.Json;

namespace Amane.Mailer.Operations.AcsSetup;

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

    public static AcsRegisterResult Fail(string code) => new() { Code = code };
}

/// <summary>
/// Console-independent Manual ACS registration operation. Validation is shared with Managed
/// bundle generation; storage uses the existing #448 secure writer/coordinator.
/// </summary>
public sealed class AcsRegisterOperation
{
    public const string IntentPhrase = "MAILER-ACS-REGISTER";

    public AcsRegisterResult Execute(AcsRegisterRequest request)
    {
        try
        {
            var validationError =
                AcsConfigurationValidator.ValidateEnvironment(request.EnvironmentConfirmation)
                ?? AcsConfigurationValidator.ValidateIntent(request.IntentConfirmation, IntentPhrase)
                ?? AcsConfigurationValidator.ValidateConnectionStrings(
                    request.ConnectionString,
                    request.ConnectionStringConfirmation)
                ?? AcsConfigurationValidator.ValidateSender(
                    request.SenderEmail,
                    request.SenderDisplayName);
            if (validationError is not null)
            {
                return AcsRegisterResult.Fail(validationError);
            }

            AcsEnvironmentConfirmation.TryMap(
                request.EnvironmentConfirmation,
                out var internalEnvironment);

            var acsSecretPath = Path.Combine(
                request.AcsSecretDirectory,
                AcsSecretFileNames.CanonicalFileName);
            var senderPath = Path.Combine(
                request.PlatformSenderDirectory,
                PlatformSenderFile.CanonicalFileName);

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

            var senderJson = JsonSerializer.Serialize(
                senderFile,
                MailerJsonContext.Default.PlatformSenderFile);

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
            return AcsRegisterResult.Fail(
                AdminProviderRegisterAcsResultCodes.FailedUnexpected);
        }
    }

    public static AcsRegisterResult RunPreflightOnly(
        string acsSecretDirectory,
        string platformSenderDirectory)
    {
        try
        {
            RunPreflight(
                Path.Combine(acsSecretDirectory, AcsSecretFileNames.CanonicalFileName),
                Path.Combine(platformSenderDirectory, PlatformSenderFile.CanonicalFileName));
            return AcsRegisterResult.Ok(string.Empty, string.Empty);
        }
        catch (SecretOperationException ex)
        {
            return AcsRegisterResult.Fail(ex.CanonicalCode);
        }
        catch (Exception)
        {
            return AcsRegisterResult.Fail(
                AdminProviderRegisterAcsResultCodes.FailedUnexpected);
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

        switch (RegisteredSecretStateInspector.Inspect(acsSecretPath, senderPath))
        {
            case RegisteredSecretState.Clean:
                return;
            case RegisteredSecretState.FullyRegistered:
                throw new SecretOperationException(
                    AdminProviderRegisterAcsResultCodes.RejectedAlreadyRegistered,
                    "Both registration files already hold a value.");
            default:
                throw new SecretOperationException(
                    AdminProviderRegisterAcsResultCodes.RejectedPartialState,
                    "Registration state requires manual review.");
        }
    }
}
