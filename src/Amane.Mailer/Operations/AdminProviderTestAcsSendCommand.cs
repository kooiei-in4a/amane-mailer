using Amane.Mailer.Operations.AcsSetup;
using Amane.Mailer.Operations.AcsTestSend;
using Microsoft.Extensions.Configuration;

namespace Amane.Mailer.Operations;

/// <summary>
/// <c>admin provider test-acs-send</c> TTY adapter over <see cref="AcsStagingVerificationOperation"/>.
/// Direct CLI does not apply Assistant session limits (#451 non-goal) and has no Managed tenant
/// context, so the typed sender-match gate compares the operator-entered sender to itself.
/// </summary>
public sealed partial class AdminProviderTestAcsSendCommand
{
    public const string IntentPhrase = AcsStagingVerificationOperation.IntentPhrase;

    /// <summary>
    /// Only this exact, capitalized literal is accepted (same Staging gate as register-acs).
    /// </summary>
    public const string RequiredEnvironmentConfirmation = AcsEnvironmentConfirmation.Staging;

    public const string SyntheticSubject = AcsStagingVerificationOperation.SyntheticSubject;

    public const string SyntheticPlainTextBody = AcsStagingVerificationOperation.SyntheticPlainTextBody;

    public const int SuccessExitCode = 0;
    public const int FailureExitCode = 1;
    public const int RejectedExitCode = 2;

    private const string MessageIdHandoffEnvVar = "MAILER_ACS_TEST_SEND_MESSAGE_ID_FILE";

    private readonly IAdminProviderTestAcsSendConsole _console;
    private readonly IConfiguration _configuration;
    private readonly IAcsTestSendClient _acsClient;
    private readonly Func<Guid> _operationIdFactory;

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

            var environmentConfirmation = _console.ReadVisibleLine(
                "Confirm target environment (exact match): ",
                cancellationToken);
            if (!string.Equals(environmentConfirmation, RequiredEnvironmentConfirmation, StringComparison.Ordinal))
            {
                return Reject(AdminProviderTestAcsSendResultCodes.RejectedEnvironmentMismatch);
            }

            var intent = _console.ReadVisibleLine(
                $"Type {IntentPhrase} to confirm intent: ",
                cancellationToken);
            if (!string.Equals(intent, IntentPhrase, StringComparison.Ordinal))
            {
                return Reject(AdminProviderTestAcsSendResultCodes.RejectedIntentMismatch);
            }

            var connectionString = ResolveConnectionString(cancellationToken);
            var senderEmail = ReadBareEmail(
                "Sender email: ",
                AdminProviderTestAcsSendResultCodes.RejectedInvalidSenderEmail,
                cancellationToken);
            var recipientEmail = ReadBareEmail(
                "Recipient email: ",
                AdminProviderTestAcsSendResultCodes.RejectedInvalidRecipientEmail,
                cancellationToken);
            var handoffPath = ResolveMessageIdHandoffPath(cancellationToken);

            cancellationToken.ThrowIfCancellationRequested();

            var operationId = _operationIdFactory();
            var operation = new AcsStagingVerificationOperation(
                _acsClient,
                sessionLimiter: null,
                operationIdFactory: () => operationId);

            var result = await operation.ExecuteAsync(
                new AcsStagingVerificationRequest
                {
                    EnvironmentConfirmation = environmentConfirmation,
                    IntentConfirmation = intent,
                    ConnectionString = connectionString,
                    SenderEmail = senderEmail,
                    RecipientEmail = recipientEmail,
                    // Direct CLI has no Managed tenant bundle; match the entered sender to itself.
                    ExpectedTenantSenderEmail = senderEmail,
                    AssistantSessionId = null,
                },
                cancellationToken);

            if (cancellationToken.IsCancellationRequested)
            {
                throw new OperationCanceledException(cancellationToken);
            }

            if (!result.IsSuccess)
            {
                return MapFailure(result);
            }

            return ReportSuccess(result, handoffPath, operationId);
        }
        catch (SecretOperationException ex)
        {
            return Reject(ex.CanonicalCode);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
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

    private string ResolveConnectionString(CancellationToken cancellationToken)
    {
        var fromFile = TryReadSecretFile();
        if (!string.IsNullOrEmpty(fromFile))
        {
            if (!AcsConnectionStringRules.LooksLikeAcsConnectionString(fromFile))
            {
                throw new SecretOperationException(
                    AdminProviderTestAcsSendResultCodes.RejectedInvalidConnectionString,
                    "Connection string file does not look like an ACS endpoint/accesskey value.");
            }

            _console.WriteLine("Using ACS connection string from configured secret file.");
            return fromFile;
        }

        return ReadConfirmedConnectionStringFromTty(cancellationToken);
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

    private string ReadConfirmedConnectionStringFromTty(CancellationToken cancellationToken)
    {
        var first = _console.ReadSecret("ACS connection string: ", cancellationToken);
        var second = _console.ReadSecret("Re-enter ACS connection string: ", cancellationToken);
        if (!string.Equals(first, second, StringComparison.Ordinal))
        {
            throw new SecretOperationException(
                AdminProviderTestAcsSendResultCodes.RejectedSecretMismatch,
                "Connection string confirmation did not match.");
        }

        if (!AcsConnectionStringRules.LooksLikeAcsConnectionString(first))
        {
            throw new SecretOperationException(
                AdminProviderTestAcsSendResultCodes.RejectedInvalidConnectionString,
                "Connection string does not look like an ACS endpoint/accesskey value.");
        }

        return first;
    }

    private string ReadBareEmail(string prompt, string invalidCode, CancellationToken cancellationToken)
    {
        var email = _console.ReadHiddenLine(prompt, cancellationToken).Trim();
        if (!System.Net.Mail.MailAddress.TryCreate(email, out var parsed)
            || !string.Equals(parsed.Address, email, StringComparison.Ordinal))
        {
            throw new SecretOperationException(invalidCode, "Email must be a bare email address.");
        }

        return email;
    }

    private string ResolveMessageIdHandoffPath(CancellationToken cancellationToken)
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
            path = _console.ReadVisibleLine(
                "Absolute path for message ID handoff file: ",
                cancellationToken).Trim();
        }

        if (string.IsNullOrWhiteSpace(path)
            || path.IndexOfAny(Path.GetInvalidPathChars()) >= 0
            || !Path.IsPathFullyQualified(path))
        {
            throw new SecretOperationException(
                AdminProviderTestAcsSendResultCodes.RejectedMessageIdHandoffPathInvalid,
                "Message ID handoff path must be a fully qualified absolute path.");
        }

        if (File.Exists(path))
        {
            throw new SecretOperationException(
                AdminProviderTestAcsSendResultCodes.RejectedMessageIdHandoffPathExists,
                "Message ID handoff file already exists; remove it before retrying.");
        }

        return path;
    }

    private int ReportSuccess(AcsStagingVerificationResult result, string handoffPath, Guid expectedOperationId)
    {
        if (result.AuthenticationState == AcsEvaluationState.Succeeded)
        {
            _console.WriteLine("[PASS] ACS authentication");
        }

        if (result.SendRequestAccepted)
        {
            _console.WriteLine("[PASS] Send request accepted");
        }

        if (result.OperationCompleted)
        {
            _console.WriteLine("[PASS] ACS send operation completed");
        }

        string canonicalMessageId;
        try
        {
            canonicalMessageId = RequireCanonicalMessageId(
                result.ProviderMessageIdForHandoff,
                expectedOperationId);
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

    private int MapFailure(AcsStagingVerificationResult result)
    {
        var code = result.Code;
        if (code is AcsStagingVerificationOperation.RejectedProductionEnvironment
            or AdminProviderTestAcsSendResultCodes.RejectedEnvironmentMismatch
            or AdminProviderTestAcsSendResultCodes.RejectedIntentMismatch
            or AdminProviderTestAcsSendResultCodes.RejectedInvalidConnectionString
            or AdminProviderTestAcsSendResultCodes.RejectedInvalidSenderEmail
            or AdminProviderTestAcsSendResultCodes.RejectedInvalidRecipientEmail
            or AdminProviderTestAcsSendResultCodes.RejectedCancelled
            or AcsStagingVerificationOperation.RejectedSenderMismatch
            or AcsStagingVerificationOperation.RejectedSessionLimitExceeded)
        {
            return Reject(code);
        }

        return Fail(code, stageFailLine: MapStageFailLine(result));
    }

    private static string? MapStageFailLine(AcsStagingVerificationResult result)
    {
        if (result.AuthenticationState == AcsEvaluationState.Failed)
        {
            return "[FAIL] ACS authentication";
        }

        if (string.Equals(
                result.Code,
                AdminProviderTestAcsSendResultCodes.FailedAcsNetwork,
                StringComparison.Ordinal))
        {
            return "[FAIL] ACS network reachability";
        }

        if (!result.SendRequestAccepted)
        {
            return "[FAIL] Send request accepted";
        }

        if (!result.OperationCompleted)
        {
            return "[FAIL] ACS send operation completed";
        }

        return null;
    }

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
