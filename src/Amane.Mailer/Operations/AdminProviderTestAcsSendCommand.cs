using System.Net.Mail;
using System.Text.RegularExpressions;
using Amane.Mailer.Operations.AcsTestSend;
using Microsoft.Extensions.Configuration;

namespace Amane.Mailer.Operations;

/// <summary>
/// <c>admin provider test-acs-send</c>: one-shot Staging-only ACS live-send verification that
/// bypasses Mailer API, Worker, Event Grid, Storage Queue, bounce processing, DB, and tenant JSON.
/// Prefers an existing ACS secret file; falls back to interactive TTY secret entry. Never accepts
/// connection string, access key, sender, or recipient as command-line arguments.
/// </summary>
public sealed partial class AdminProviderTestAcsSendCommand
{
    public const string IntentPhrase = "MAILER-ACS-TEST-SEND";

    /// <summary>
    /// Only this exact, capitalized literal is accepted (same Staging gate as register-acs).
    /// </summary>
    public const string RequiredEnvironmentConfirmation = "Staging";

    public const string SyntheticSubject = "Amane Mailer ACS test-send verification";

    public const string SyntheticPlainTextBody =
        "This is a fixed synthetic message from Amane Mailer admin provider test-acs-send. Do not reply.";

    public const int SuccessExitCode = 0;
    public const int FailureExitCode = 1;
    public const int RejectedExitCode = 2;

    private const string MessageIdHandoffEnvVar = "MAILER_ACS_TEST_SEND_MESSAGE_ID_FILE";
    private const int RegexMatchTimeoutMilliseconds = 250;

    private readonly IAdminProviderRegisterAcsConsole _console;
    private readonly IConfiguration _configuration;
    private readonly IAcsTestSendClient _acsClient;
    private readonly Func<Guid> _operationIdFactory;

    [GeneratedRegex(
        @"^(?:endpoint=https://.+;accesskey=.+)$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
        RegexMatchTimeoutMilliseconds)]
    private static partial Regex AcsConnectionStringRegex();

    public AdminProviderTestAcsSendCommand(
        IAdminProviderRegisterAcsConsole console,
        IConfiguration configuration,
        IAcsTestSendClient? acsClient = null,
        Func<Guid>? operationIdFactory = null)
    {
        _console = console;
        _configuration = configuration;
        _acsClient = acsClient ?? new AzureAcsTestSendClient();
        _operationIdFactory = operationIdFactory ?? (() => Guid.NewGuid());
    }

    public static bool IsTestAcsSendCommand(IReadOnlyList<string> args) =>
        args.Count == 3
        && string.Equals(args[0], "admin", StringComparison.Ordinal)
        && string.Equals(args[1], "provider", StringComparison.Ordinal)
        && string.Equals(args[2], "test-acs-send", StringComparison.Ordinal);

    public async Task<int> RunAsync(CancellationToken cancellationToken)
    {
        try
        {
            cancellationToken.ThrowIfCancellationRequested();

            var environmentConfirmation = _console.ReadLine("Confirm target environment (exact match): ");
            if (!string.Equals(environmentConfirmation, RequiredEnvironmentConfirmation, StringComparison.Ordinal))
            {
                return Reject(AdminProviderTestAcsSendResultCodes.RejectedEnvironmentMismatch);
            }

            var intent = _console.ReadLine($"Type {IntentPhrase} to confirm intent: ");
            if (!string.Equals(intent, IntentPhrase, StringComparison.Ordinal))
            {
                return Reject(AdminProviderTestAcsSendResultCodes.RejectedIntentMismatch);
            }

            var connectionString = ResolveConnectionString();
            var senderEmail = ReadBareEmail(
                "Sender email: ",
                AdminProviderTestAcsSendResultCodes.RejectedInvalidSenderEmail);
            var recipientEmail = ReadBareEmail(
                "Recipient email: ",
                AdminProviderTestAcsSendResultCodes.RejectedInvalidRecipientEmail);
            var displayName = ReadOptionalDisplayName();
            var handoffPath = ResolveMessageIdHandoffPath();

            cancellationToken.ThrowIfCancellationRequested();

            var operationId = _operationIdFactory();
            var outcome = await _acsClient.SendAsync(
                new AcsTestSendRequest
                {
                    ConnectionString = connectionString,
                    SenderEmail = senderEmail,
                    RecipientEmail = recipientEmail,
                    SenderDisplayName = displayName,
                    Subject = SyntheticSubject,
                    PlainTextBody = SyntheticPlainTextBody,
                    OperationId = operationId,
                },
                cancellationToken);

            return ReportOutcome(outcome, handoffPath);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (SecretOperationException ex)
        {
            return Reject(MapSharedSecretCode(ex.CanonicalCode));
        }
        catch (OperationCanceledException)
        {
            return Reject(AdminProviderTestAcsSendResultCodes.RejectedCancelled);
        }
        catch (Exception)
        {
            return Fail(AdminProviderTestAcsSendResultCodes.FailedUnexpected);
        }
    }

    private string ResolveConnectionString()
    {
        var fromFile = TryReadSecretFile();
        if (!string.IsNullOrEmpty(fromFile))
        {
            if (!AcsConnectionStringRegex().IsMatch(fromFile))
            {
                throw new SecretOperationException(
                    AdminProviderTestAcsSendResultCodes.RejectedInvalidConnectionString,
                    "Connection string file does not look like an ACS endpoint/accesskey value.");
            }

            _console.WriteLine("Using ACS connection string from configured secret file.");
            return fromFile;
        }

        return ReadConfirmedConnectionStringFromTty();
    }

    private string? TryReadSecretFile()
    {
        var configuredFile = _configuration["ACS_CONNECTION_STRING_FILE"];
        if (!string.IsNullOrWhiteSpace(configuredFile) && File.Exists(configuredFile))
        {
            var value = File.ReadAllText(configuredFile).Trim();
            if (!string.IsNullOrEmpty(value))
            {
                return value;
            }
        }

        var acsDirectory = _configuration["MAILER_ACS_SECRET_DIRECTORY"];
        if (!string.IsNullOrWhiteSpace(acsDirectory))
        {
            var path = Path.Combine(acsDirectory, AcsSecretFileNames.CanonicalFileName);
            if (File.Exists(path))
            {
                var value = File.ReadAllText(path).Trim();
                if (!string.IsNullOrEmpty(value))
                {
                    return value;
                }
            }
        }

        return null;
    }

    private string ReadConfirmedConnectionStringFromTty()
    {
        var first = _console.ReadSecret("ACS connection string: ");
        var second = _console.ReadSecret("Re-enter ACS connection string: ");
        if (!string.Equals(first, second, StringComparison.Ordinal))
        {
            throw new SecretOperationException(
                AdminProviderTestAcsSendResultCodes.RejectedSecretMismatch,
                "Connection string confirmation did not match.");
        }

        if (!AcsConnectionStringRegex().IsMatch(first))
        {
            throw new SecretOperationException(
                AdminProviderTestAcsSendResultCodes.RejectedInvalidConnectionString,
                "Connection string does not look like an ACS endpoint/accesskey value.");
        }

        return first;
    }

    private string ReadBareEmail(string prompt, string invalidCode)
    {
        var email = _console.ReadLine(prompt).Trim();
        if (!MailAddress.TryCreate(email, out var parsed)
            || !string.Equals(parsed.Address, email, StringComparison.Ordinal))
        {
            throw new SecretOperationException(invalidCode, "Email must be a bare email address.");
        }

        return email;
    }

    private string? ReadOptionalDisplayName()
    {
        var displayName = _console.ReadLine("Sender display name (optional, empty to skip): ");
        if (string.IsNullOrEmpty(displayName))
        {
            return null;
        }

        if (displayName.Length > 200 || displayName.Any(char.IsControl))
        {
            throw new SecretOperationException(
                AdminProviderTestAcsSendResultCodes.RejectedInvalidDisplayName,
                "Sender display name must be at most 200 characters with no control characters.");
        }

        return displayName;
    }

    private string ResolveMessageIdHandoffPath()
    {
        var configured = _configuration[MessageIdHandoffEnvVar];
        string path;
        if (!string.IsNullOrWhiteSpace(configured))
        {
            path = configured.Trim();
            _console.WriteLine("Using message ID handoff path from environment.");
        }
        else
        {
            path = _console.ReadLine("Absolute path for message ID handoff file: ").Trim();
        }

        if (string.IsNullOrWhiteSpace(path)
            || path.IndexOfAny(Path.GetInvalidPathChars()) >= 0
            || !Path.IsPathRooted(path))
        {
            throw new SecretOperationException(
                AdminProviderTestAcsSendResultCodes.RejectedMessageIdHandoffPathInvalid,
                "Message ID handoff path must be an absolute path.");
        }

        return path;
    }

    private int ReportOutcome(AcsTestSendOutcome outcome, string handoffPath)
    {
        if (outcome.AuthenticationSucceeded)
        {
            _console.WriteLine("[PASS] ACS authentication");
        }
        else
        {
            return Fail(
                outcome.CanonicalFailureCode ?? AdminProviderTestAcsSendResultCodes.FailedAcsAuthentication,
                authenticationLine: "[FAIL] ACS authentication");
        }

        if (outcome.SendRequestAccepted)
        {
            _console.WriteLine("[PASS] Send request accepted");
        }
        else
        {
            return Fail(
                outcome.CanonicalFailureCode ?? AdminProviderTestAcsSendResultCodes.FailedAcsSendRequest,
                authenticationLine: null,
                extraFailLine: "[FAIL] Send request accepted");
        }

        if (!outcome.OperationCompleted || string.IsNullOrWhiteSpace(outcome.ProviderMessageId))
        {
            return Fail(
                outcome.CanonicalFailureCode ?? AdminProviderTestAcsSendResultCodes.FailedAcsOperation,
                authenticationLine: null,
                extraFailLine: "[FAIL] ACS send operation completed");
        }

        _console.WriteLine("[PASS] ACS send operation completed");

        try
        {
            WriteMessageIdHandoff(handoffPath, outcome.ProviderMessageId);
        }
        catch (SecretOperationException ex)
        {
            return Fail(ex.CanonicalCode, extraFailLine: "[FAIL] Message ID handoff file written");
        }
        catch (Exception)
        {
            return Fail(
                AdminProviderTestAcsSendResultCodes.RejectedMessageIdHandoffWriteFailed,
                extraFailLine: "[FAIL] Message ID handoff file written");
        }

        _console.WriteLine("[PASS] Message ID handoff file written");
        _console.WriteLine("[ACTION] Confirm receipt in the test mailbox");
        _console.WriteLine(
            $"success: operation=test_acs_send result={AdminProviderTestAcsSendResultCodes.Success}");
        return SuccessExitCode;
    }

    private static void WriteMessageIdHandoff(string path, string providerMessageId)
    {
        var directory = Path.GetDirectoryName(path);
        if (string.IsNullOrEmpty(directory))
        {
            throw new SecretOperationException(
                AdminProviderTestAcsSendResultCodes.RejectedMessageIdHandoffPathInvalid,
                "Message ID handoff path must include a directory.");
        }

        Directory.CreateDirectory(directory);

        // Write UUID only. Never include emails, subject, body, or secrets.
        var tempPath = path + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            File.WriteAllText(tempPath, providerMessageId.Trim() + Environment.NewLine);
            if (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS())
            {
                File.SetUnixFileMode(
                    tempPath,
                    UnixFileMode.UserRead | UnixFileMode.UserWrite);
            }

            File.Move(tempPath, path, overwrite: true);
            if (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS())
            {
                File.SetUnixFileMode(
                    path,
                    UnixFileMode.UserRead | UnixFileMode.UserWrite);
            }
        }
        catch
        {
            try
            {
                if (File.Exists(tempPath))
                {
                    File.Delete(tempPath);
                }
            }
            catch
            {
                // Best-effort cleanup of temp handoff content.
            }

            throw new SecretOperationException(
                AdminProviderTestAcsSendResultCodes.RejectedMessageIdHandoffWriteFailed,
                "Failed to write message ID handoff file.");
        }
    }

    private int Reject(string canonicalCode)
    {
        _console.WriteError($"rejected: operation=test_acs_send result={canonicalCode}");
        return RejectedExitCode;
    }

    private int Fail(
        string canonicalCode,
        string? authenticationLine = null,
        string? extraFailLine = null)
    {
        if (authenticationLine is not null)
        {
            _console.WriteLine(authenticationLine);
        }

        if (extraFailLine is not null)
        {
            _console.WriteLine(extraFailLine);
        }

        _console.WriteError($"failed: operation=test_acs_send result={canonicalCode}");
        return FailureExitCode;
    }

    private static string MapSharedSecretCode(string code) =>
        code switch
        {
            AdminProviderRegisterAcsResultCodes.RejectedInputRedirected =>
                AdminProviderTestAcsSendResultCodes.RejectedInputRedirected,
            AdminProviderRegisterAcsResultCodes.RejectedCancelled =>
                AdminProviderTestAcsSendResultCodes.RejectedCancelled,
            _ => code,
        };
}
