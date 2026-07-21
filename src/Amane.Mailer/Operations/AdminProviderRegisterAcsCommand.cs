using System.Net.Mail;
using System.Text.Json;
using System.Text.RegularExpressions;
using Amane.Mailer.Configuration;
using Amane.Mailer.Json;

namespace Amane.Mailer.Operations;

/// <summary>
/// <c>admin provider register-acs</c>: interactively registers the ACS connection string
/// (deploy-time secret file, never tenant JSON, never the DB) and the platform-owned sender
/// identity (a new, tenant-independent config file — not an existing tenant's <c>default_from</c>,
/// and no tenant is created or faked here) used by System Admin platform-owned mail.
/// <para>
/// Registering these two values does not, by itself, wire System Admin mail sending. Consuming
/// the platform sender file in a send decision belongs to the platform-owned mail request
/// contract (tracked separately) — this command only writes safely-collected data to disk.
/// </para>
/// </summary>
public sealed partial class AdminProviderRegisterAcsCommand(
    IAdminProviderRegisterAcsConsole console,
    string acsSecretDirectory,
    string platformSenderDirectory)
{
    public const string IntentPhrase = "MAILER-ACS-REGISTER";

    /// <summary>
    /// Only this exact, capitalized literal is accepted. Amane Mailer's own tenant schema uses a
    /// lowercase "staging" environment enum; this command deliberately does not fold case or
    /// accept that spelling directly from the operator, to avoid silently accepting a typo or an
    /// unrelated environment name as confirmation. The only permitted conversion from operator
    /// input to the internal schema value is the fixed one-way mapping below.
    /// </summary>
    public const string RequiredEnvironmentConfirmation = "Staging";

    private const string InternalEnvironmentValue = "staging";

    private const int RegexMatchTimeoutMilliseconds = 250;

    [GeneratedRegex(
        @"^(?:endpoint=https://.+;accesskey=.+)$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
        RegexMatchTimeoutMilliseconds)]
    private static partial Regex AcsConnectionStringRegex();

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
    /// Non-interactive, side-effect-free health check for the two mounted directories: existence,
    /// symlink/reparse-point rejection, non-permissive mode (Linux), an actual write probe, and
    /// current registration state. Safe to run repeatedly (including from CI / deploy validation
    /// scripts) because it never prompts and never writes a secret.
    /// </summary>
    public int RunPreflightOnly()
    {
        var (acsSecretPath, senderPath) = ResolveTargetPaths();
        try
        {
            RunPreflight(acsSecretPath, senderPath);
            console.WriteLine($"success: operation=check_acs_preflight result={AdminProviderRegisterAcsResultCodes.Success}");
            return 0;
        }
        catch (SecretOperationException ex)
        {
            return Reject("check_acs_preflight", ex.CanonicalCode);
        }
        catch (Exception)
        {
            return Reject("check_acs_preflight", AdminProviderRegisterAcsResultCodes.FailedUnexpected);
        }
    }

    public int Run()
    {
        var (acsSecretPath, senderPath) = ResolveTargetPaths();
        try
        {
            RunPreflight(acsSecretPath, senderPath);

            using var operationLock = ExclusiveOperationLock.Acquire(acsSecretDirectory);

            // Re-run the same non-interactive checks now that the lock is held. Nothing else
            // writes to these paths without first holding this lock, so state cannot legitimately
            // have changed since the first preflight; this re-check makes that invariant explicit
            // instead of assumed, at negligible cost.
            RunPreflight(acsSecretPath, senderPath);

            var environmentConfirmation = console.ReadLine("Confirm target environment (exact match): ");
            if (!string.Equals(environmentConfirmation, RequiredEnvironmentConfirmation, StringComparison.Ordinal))
            {
                throw new SecretOperationException(
                    AdminProviderRegisterAcsResultCodes.RejectedEnvironmentMismatch,
                    "Environment confirmation did not match.");
            }

            var intent = console.ReadLine($"Type {IntentPhrase} to confirm intent: ");
            if (!string.Equals(intent, IntentPhrase, StringComparison.Ordinal))
            {
                throw new SecretOperationException(
                    AdminProviderRegisterAcsResultCodes.RejectedIntentMismatch,
                    "Intent confirmation did not match.");
            }

            var connectionString = ReadConfirmedConnectionString();
            var senderEmail = ReadSenderEmail();
            var senderDisplayName = ReadSenderDisplayName();

            var senderFile = new PlatformSenderFile
            {
                Version = 1,
                Environment = InternalEnvironmentValue,
                Sender = new PlatformSenderAddress { Email = senderEmail, DisplayName = senderDisplayName },
                Provider = "acs",
                LiveSending = false,
            };
            senderFile.Validate();

            var senderJson = JsonSerializer.Serialize(senderFile, MailerJsonContext.Default.PlatformSenderFile);

            TwoPhaseSecretWriteCoordinator.WriteBoth(
                new SecretFileWriter(acsSecretPath),
                connectionString,
                new SecretFileWriter(senderPath),
                senderJson);

            console.WriteLine($"success: operation=register_acs result={AdminProviderRegisterAcsResultCodes.Success}");
            return 0;
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

    private (string AcsSecretPath, string SenderPath) ResolveTargetPaths()
    {
        var acsSecretPath = Path.Combine(acsSecretDirectory, AcsSecretFileNames.CanonicalFileName);
        var senderPath = Path.Combine(platformSenderDirectory, PlatformSenderFile.CanonicalFileName);
        return (acsSecretPath, senderPath);
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

    private string ReadConfirmedConnectionString()
    {
        var first = console.ReadSecret("ACS connection string: ");
        var second = console.ReadSecret("Re-enter ACS connection string: ");
        if (!string.Equals(first, second, StringComparison.Ordinal))
        {
            throw new SecretOperationException(
                AdminProviderRegisterAcsResultCodes.RejectedSecretMismatch,
                "Connection string confirmation did not match.");
        }

        if (!AcsConnectionStringRegex().IsMatch(first))
        {
            throw new SecretOperationException(
                AdminProviderRegisterAcsResultCodes.RejectedInvalidConnectionString,
                "Connection string does not look like an ACS endpoint/accesskey value.");
        }

        return first;
    }

    private string ReadSenderEmail()
    {
        var email = console.ReadLine("Sender email: ").Trim();
        if (!MailAddress.TryCreate(email, out var parsed) || !string.Equals(parsed.Address, email, StringComparison.Ordinal))
        {
            throw new SecretOperationException(
                AdminProviderRegisterAcsResultCodes.RejectedInvalidSenderEmail,
                "Sender email must be a bare email address.");
        }

        return email;
    }

    private string ReadSenderDisplayName()
    {
        var displayName = console.ReadLine("Sender display name: ");
        if (string.IsNullOrEmpty(displayName) || displayName.Length > 200 || displayName.Any(char.IsControl))
        {
            throw new SecretOperationException(
                AdminProviderRegisterAcsResultCodes.RejectedInvalidDisplayName,
                "Sender display name must be 1-200 characters with no control characters.");
        }

        return displayName;
    }

    private int Reject(string operationCode, string canonicalCode)
    {
        console.WriteError($"rejected: operation={operationCode} result={canonicalCode}");
        return 2;
    }
}
