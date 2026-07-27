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

    private readonly IAdminProviderTestAcsSendConsole _console;
    private readonly IConfiguration _configuration;
    private readonly IAcsTestSendClient _acsClient;
    private readonly Func<Guid> _operationIdFactory;

    [GeneratedRegex(
        @"^(?:endpoint=https://.+;accesskey=.+)$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
        RegexMatchTimeoutMilliseconds)]
    private static partial Regex AcsConnectionStringRegex();

    public AdminProviderTestAcsSendCommand(
        IAdminProviderTestAcsSendConsole console,
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

            var environmentConfirmation = _console.ReadVisibleLine("Confirm target environment (exact match): ");
            if (!string.Equals(environmentConfirmation, RequiredEnvironmentConfirmation, StringComparison.Ordinal))
            {
                return Reject(AdminProviderTestAcsSendResultCodes.RejectedEnvironmentMismatch);
            }

            var intent = _console.ReadVisibleLine($"Type {IntentPhrase} to confirm intent: ");
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
            var handoffPath = ResolveMessageIdHandoffPath();

            cancellationToken.ThrowIfCancellationRequested();

            var operationId = _operationIdFactory();
            var outcome = await _acsClient.SendAsync(
                new AcsTestSendRequest
                {
                    ConnectionString = connectionString,
                    SenderEmail = senderEmail,
                    RecipientEmail = recipientEmail,
                    Subject = SyntheticSubject,
                    PlainTextBody = SyntheticPlainTextBody,
                    OperationId = operationId,
                },
                cancellationToken);

            return ReportOutcome(outcome, handoffPath, operationId);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (SecretOperationException ex)
        {
            return Reject(ex.CanonicalCode);
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
        // PII: non-echo input so the address does not remain in the PTY transcript.
        var email = _console.ReadHiddenLine(prompt).Trim();
        if (!MailAddress.TryCreate(email, out var parsed)
            || !string.Equals(parsed.Address, email, StringComparison.Ordinal))
        {
            throw new SecretOperationException(invalidCode, "Email must be a bare email address.");
        }

        return email;
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
            path = _console.ReadVisibleLine("Absolute path for message ID handoff file: ").Trim();
        }

        if (string.IsNullOrWhiteSpace(path)
            || path.IndexOfAny(Path.GetInvalidPathChars()) >= 0
            || !Path.IsPathFullyQualified(path))
        {
            throw new SecretOperationException(
                AdminProviderTestAcsSendResultCodes.RejectedMessageIdHandoffPathInvalid,
                "Message ID handoff path must be a fully qualified absolute path.");
        }

        // Fail closed before any ACS call so a previous UUID cannot be mistaken for this run.
        if (File.Exists(path))
        {
            throw new SecretOperationException(
                AdminProviderTestAcsSendResultCodes.RejectedMessageIdHandoffPathExists,
                "Message ID handoff file already exists; remove it before retrying.");
        }

        return path;
    }

    private int ReportOutcome(AcsTestSendOutcome outcome, string handoffPath, Guid expectedOperationId)
    {
        switch (outcome.AuthenticationState)
        {
            case AcsEvaluationState.Succeeded:
                _console.WriteLine("[PASS] ACS authentication");
                break;
            case AcsEvaluationState.Failed:
                return Fail(
                    outcome.CanonicalFailureCode ?? AdminProviderTestAcsSendResultCodes.FailedAcsAuthentication,
                    stageFailLine: "[FAIL] ACS authentication");
            case AcsEvaluationState.NotEvaluated:
                if (string.Equals(
                        outcome.CanonicalFailureCode,
                        AdminProviderTestAcsSendResultCodes.FailedAcsNetwork,
                        StringComparison.Ordinal))
                {
                    return Fail(
                        AdminProviderTestAcsSendResultCodes.FailedAcsNetwork,
                        stageFailLine: "[FAIL] ACS network reachability");
                }

                // Auth was not judged; do not claim PASS or FAIL for authentication.
                break;
        }

        if (outcome.SendRequestAccepted)
        {
            _console.WriteLine("[PASS] Send request accepted");
        }
        else
        {
            return Fail(
                outcome.CanonicalFailureCode ?? AdminProviderTestAcsSendResultCodes.FailedAcsSendRequest,
                stageFailLine: "[FAIL] Send request accepted");
        }

        if (!outcome.OperationCompleted)
        {
            return Fail(
                outcome.CanonicalFailureCode ?? AdminProviderTestAcsSendResultCodes.FailedAcsOperation,
                stageFailLine: "[FAIL] ACS send operation completed");
        }

        _console.WriteLine("[PASS] ACS send operation completed");

        string canonicalMessageId;
        try
        {
            canonicalMessageId = RequireCanonicalMessageId(outcome.ProviderMessageId, expectedOperationId);
            WriteMessageIdHandoff(handoffPath, canonicalMessageId);
        }
        catch (SecretOperationException ex)
        {
            return Fail(ex.CanonicalCode, stageFailLine: "[FAIL] Message ID handoff file written");
        }
        catch (Exception)
        {
            return Fail(
                AdminProviderTestAcsSendResultCodes.RejectedMessageIdHandoffWriteFailed,
                stageFailLine: "[FAIL] Message ID handoff file written");
        }

        _console.WriteLine("[PASS] Message ID handoff file written");
        _console.WriteLine("[ACTION] Confirm receipt in the test mailbox");
        _console.WriteLine(
            $"success: operation=test_acs_send result={AdminProviderTestAcsSendResultCodes.Success}");
        return SuccessExitCode;
    }

    /// <summary>
    /// Ensures the handoff value is exactly one canonical UUID matching the caller-supplied
    /// ACS operation id. Rejects <c>NOT_SET</c>, blank, multi-line, and mismatched values
    /// without writing a file.
    /// </summary>
    internal static string RequireCanonicalMessageId(string? providerMessageId, Guid expectedOperationId)
    {
        if (string.IsNullOrWhiteSpace(providerMessageId))
        {
            throw new SecretOperationException(
                AdminProviderTestAcsSendResultCodes.FailedAcsMessageIdInvalid,
                "Provider message id was missing.");
        }

        var trimmed = providerMessageId.Trim();
        if (trimmed.Contains('\n')
            || trimmed.Contains('\r')
            || trimmed.Any(char.IsControl)
            || string.Equals(trimmed, "NOT_SET", StringComparison.OrdinalIgnoreCase))
        {
            throw new SecretOperationException(
                AdminProviderTestAcsSendResultCodes.FailedAcsMessageIdInvalid,
                "Provider message id was not a usable UUID.");
        }

        if (!Guid.TryParseExact(trimmed, "D", out var parsed)
            && !Guid.TryParse(trimmed, out parsed))
        {
            throw new SecretOperationException(
                AdminProviderTestAcsSendResultCodes.FailedAcsMessageIdInvalid,
                "Provider message id was not a UUID.");
        }

        if (parsed != expectedOperationId)
        {
            throw new SecretOperationException(
                AdminProviderTestAcsSendResultCodes.FailedAcsMessageIdInvalid,
                "Provider message id did not match the requested operation id.");
        }

        return parsed.ToString("D");
    }

    private static void WriteMessageIdHandoff(string path, string canonicalMessageId)
    {
        var directory = Path.GetDirectoryName(path);
        if (string.IsNullOrEmpty(directory))
        {
            throw new SecretOperationException(
                AdminProviderTestAcsSendResultCodes.RejectedMessageIdHandoffPathInvalid,
                "Message ID handoff path must include a directory.");
        }

        if (File.Exists(path))
        {
            throw new SecretOperationException(
                AdminProviderTestAcsSendResultCodes.RejectedMessageIdHandoffPathExists,
                "Message ID handoff file already exists; remove it before retrying.");
        }

        Directory.CreateDirectory(directory);

        // Write UUID only. Never include emails, subject, body, or secrets.
        var tempPath = path + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            File.WriteAllText(tempPath, canonicalMessageId + Environment.NewLine);
            if (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS())
            {
                File.SetUnixFileMode(
                    tempPath,
                    UnixFileMode.UserRead | UnixFileMode.UserWrite);
            }

            // Never overwrite: freshness of the handoff file is part of the safety contract.
            File.Move(tempPath, path, overwrite: false);
            if (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS())
            {
                File.SetUnixFileMode(
                    path,
                    UnixFileMode.UserRead | UnixFileMode.UserWrite);
            }
        }
        catch (SecretOperationException)
        {
            throw;
        }
        catch (IOException) when (File.Exists(path))
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
                AdminProviderTestAcsSendResultCodes.RejectedMessageIdHandoffPathExists,
                "Message ID handoff file already exists; remove it before retrying.");
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

    private int Fail(string canonicalCode, string? stageFailLine = null)
    {
        if (stageFailLine is not null)
        {
            _console.WriteLine(stageFailLine);
        }

        _console.WriteError($"failed: operation=test_acs_send result={canonicalCode}");
        return FailureExitCode;
    }
}
