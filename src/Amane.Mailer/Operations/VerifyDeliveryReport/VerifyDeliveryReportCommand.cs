using System.Net.Mail;
using System.Text.RegularExpressions;
using Amane.Mailer.Configuration;
using Amane.Mailer.Operations.AcsTestSend;
using Microsoft.Extensions.Configuration;

namespace Amane.Mailer.Operations.VerifyDeliveryReport;

/// <summary>
/// <c>setup verify-delivery-report</c>: Staging-only E2E that reuses #426 ACS test send, then
/// read-only peeks Storage Queue for a correlating <c>EmailDeliveryReportReceived</c> event (#428).
/// Never receives, deletes, or changes queue message visibility. Never prints secrets, emails,
/// message IDs, raw event JSON, or provider raw errors.
/// </summary>
public sealed partial class VerifyDeliveryReportCommand
{
    public const string IntentPhrase = "MAILER-VERIFY-DELIVERY-REPORT";

    /// <summary>
    /// Only this exact, capitalized literal is accepted (same Staging gate as test-acs-send).
    /// </summary>
    public const string RequiredEnvironmentConfirmation = "Staging";

    public const int SuccessExitCode = 0;
    public const int FailureExitCode = 1;
    public const int RejectedExitCode = 2;

    public const int DefaultTimeoutSeconds = 180;
    public const int MinTimeoutSeconds = 30;
    public const int MaxTimeoutSeconds = 600;
    public const int DefaultPollIntervalSeconds = 5;
    public const int MinPollIntervalSeconds = 1;
    public const int MaxPollIntervalSeconds = 30;

    private const string TimeoutEnvVar = "MAILER_VERIFY_DELIVERY_REPORT_TIMEOUT_SECONDS";
    private const string PollIntervalEnvVar = "MAILER_VERIFY_DELIVERY_REPORT_POLL_INTERVAL_SECONDS";
    private const int RegexMatchTimeoutMilliseconds = 250;

    private readonly IAdminProviderTestAcsSendConsole _console;
    private readonly IConfiguration _configuration;
    private readonly IAcsTestSendClient _acsClient;
    private readonly IAcsEventQueuePeekerFactory _peekerFactory;
    private readonly Func<Guid> _operationIdFactory;
    private readonly Func<TimeSpan, CancellationToken, Task> _delayAsync;
    private readonly Func<DateTimeOffset> _utcNow;

    [GeneratedRegex(
        @"^(?:endpoint=https://.+;accesskey=.+)$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
        RegexMatchTimeoutMilliseconds)]
    private static partial Regex AcsConnectionStringRegex();

    public VerifyDeliveryReportCommand(
        IAdminProviderTestAcsSendConsole console,
        IConfiguration configuration,
        IAcsTestSendClient? acsClient = null,
        IAcsEventQueuePeekerFactory? peekerFactory = null,
        Func<Guid>? operationIdFactory = null,
        Func<TimeSpan, CancellationToken, Task>? delayAsync = null,
        Func<DateTimeOffset>? utcNow = null)
    {
        _console = console;
        _configuration = configuration;
        _acsClient = acsClient ?? new AzureAcsTestSendClient();
        _peekerFactory = peekerFactory ?? new AzureAcsEventQueuePeekerFactory();
        _operationIdFactory = operationIdFactory ?? (() => Guid.NewGuid());
        _delayAsync = delayAsync ?? ((delay, ct) => Task.Delay(delay, ct));
        _utcNow = utcNow ?? (() => DateTimeOffset.UtcNow);
    }

    public static bool IsVerifyDeliveryReportCommand(IReadOnlyList<string> args) =>
        args.Count == 2
        && string.Equals(args[0], "setup", StringComparison.Ordinal)
        && string.Equals(args[1], "verify-delivery-report", StringComparison.Ordinal);

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
                return Reject(VerifyDeliveryReportResultCodes.RejectedEnvironmentMismatch);
            }

            var intent = _console.ReadVisibleLine(
                $"Type {IntentPhrase} to confirm intent: ",
                cancellationToken);
            if (!string.Equals(intent, IntentPhrase, StringComparison.Ordinal))
            {
                return Reject(VerifyDeliveryReportResultCodes.RejectedIntentMismatch);
            }

            var timeout = ResolveTimeout();
            var pollInterval = ResolvePollInterval();
            if (pollInterval >= timeout)
            {
                return Reject(VerifyDeliveryReportResultCodes.RejectedInvalidPollInterval);
            }

            var acsConnectionString = ResolveAcsConnectionString(cancellationToken);
            var senderEmail = ReadBareEmail(
                "Sender email: ",
                VerifyDeliveryReportResultCodes.RejectedInvalidSenderEmail,
                cancellationToken);
            var recipientEmail = ReadBareEmail(
                "Recipient email: ",
                VerifyDeliveryReportResultCodes.RejectedInvalidRecipientEmail,
                cancellationToken);
            var queueConnectionString = ResolveQueueConnectionString(cancellationToken);
            var queueName = ResolveQueueName(cancellationToken);

            cancellationToken.ThrowIfCancellationRequested();

            var operationId = _operationIdFactory();
            var sendOutcome = await _acsClient.SendAsync(
                new AcsTestSendRequest
                {
                    ConnectionString = acsConnectionString,
                    SenderEmail = senderEmail,
                    RecipientEmail = recipientEmail,
                    Subject = AdminProviderTestAcsSendCommand.SyntheticSubject,
                    PlainTextBody = AdminProviderTestAcsSendCommand.SyntheticPlainTextBody,
                    OperationId = operationId,
                },
                cancellationToken);

            var sendExit = ReportAcsSendOutcome(sendOutcome, operationId, out var canonicalMessageId);
            if (sendExit != SuccessExitCode || canonicalMessageId is null)
            {
                return sendExit;
            }

            _console.WriteLine("Polling Storage Queue with read-only peek (no delete / no visibility change)...");

            IAcsEventQueuePeeker peeker;
            try
            {
                peeker = _peekerFactory.Create(queueConnectionString, queueName);
            }
            catch (Exception)
            {
                return Fail(
                    VerifyDeliveryReportResultCodes.RejectedInvalidQueueConnectionString,
                    stageFailLine: "[FAIL] Delivery Report observed in Storage Queue");
            }

            var poller = new DeliveryReportQueuePoller(peeker, _delayAsync, _utcNow);
            DeliveryReportPollResult pollResult;
            try
            {
                pollResult = await poller.PollAsync(
                    canonicalMessageId,
                    timeout,
                    pollInterval,
                    cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }

            return ReportPollResult(pollResult);
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
            return Reject(VerifyDeliveryReportResultCodes.RejectedCancelled);
        }
        catch (Exception)
        {
            return Fail(VerifyDeliveryReportResultCodes.FailedUnexpected);
        }
    }

    private int ReportAcsSendOutcome(
        AcsTestSendOutcome outcome,
        Guid expectedOperationId,
        out string? canonicalMessageId)
    {
        canonicalMessageId = null;

        switch (outcome.AuthenticationState)
        {
            case AcsEvaluationState.Succeeded:
                _console.WriteLine("[PASS] ACS authentication");
                break;
            case AcsEvaluationState.Failed:
                return Fail(
                    outcome.CanonicalFailureCode ?? VerifyDeliveryReportResultCodes.FailedAcsAuthentication,
                    stageFailLine: "[FAIL] ACS authentication");
            case AcsEvaluationState.NotEvaluated:
                if (string.Equals(
                        outcome.CanonicalFailureCode,
                        VerifyDeliveryReportResultCodes.FailedAcsNetwork,
                        StringComparison.Ordinal))
                {
                    return Fail(
                        VerifyDeliveryReportResultCodes.FailedAcsNetwork,
                        stageFailLine: "[FAIL] ACS network reachability");
                }

                break;
        }

        if (outcome.SendRequestAccepted)
        {
            _console.WriteLine("[PASS] Send request accepted");
        }
        else
        {
            return Fail(
                outcome.CanonicalFailureCode ?? VerifyDeliveryReportResultCodes.FailedAcsSendRequest,
                stageFailLine: "[FAIL] Send request accepted");
        }

        if (!outcome.OperationCompleted)
        {
            return Fail(
                outcome.CanonicalFailureCode ?? VerifyDeliveryReportResultCodes.FailedAcsOperation,
                stageFailLine: "[FAIL] ACS send operation completed");
        }

        try
        {
            canonicalMessageId = AdminProviderTestAcsSendCommand.RequireCanonicalMessageId(
                outcome.ProviderMessageId,
                expectedOperationId);
        }
        catch (SecretOperationException ex)
        {
            return Fail(ex.CanonicalCode, stageFailLine: "[FAIL] ACS send operation completed");
        }

        _console.WriteLine("[PASS] ACS send operation completed");
        return SuccessExitCode;
    }

    private int ReportPollResult(DeliveryReportPollResult pollResult)
    {
        switch (pollResult.Outcome)
        {
            case DeliveryReportPollOutcome.QueueAccessFailed:
                return Fail(
                    pollResult.CanonicalFailureCode ?? VerifyDeliveryReportResultCodes.FailedUnexpected,
                    stageFailLine: "[FAIL] Delivery Report observed in Storage Queue");

            case DeliveryReportPollOutcome.TimedOut:
                // ACS send already reported PASS above; distinguish Event Grid / Queue arrival failure.
                _console.WriteLine("[FAIL] Delivery Report observed in Storage Queue");
                _console.WriteLine("[FAIL] Event correlated to the test send");
                if (pollResult.BacklogPreventsConfirmation)
                {
                    _console.WriteLine(
                        "[WARN] Queue backlog exceeds read-only peek window; target event cannot be confirmed");
                    _console.WriteLine(
                        "[ACTION] Use a dedicated empty Staging queue, pause the Mailer bounce poller, then retry");
                    return Fail(VerifyDeliveryReportResultCodes.FailedDeliveryReportBacklog);
                }

                if (pollResult.SawOtherDeliveryReport)
                {
                    _console.WriteLine(
                        "[WARN] Other Delivery Report events were visible but none matched this send");
                }

                if (pollResult.SawMalformed)
                {
                    _console.WriteLine("[WARN] One or more queue messages could not be parsed as Delivery Reports");
                }

                _console.WriteLine(
                    "[ACTION] Confirm setup check-event-grid passed for Staging, then retry or inspect Azure Portal");
                return Fail(VerifyDeliveryReportResultCodes.FailedDeliveryReportTimeout);

            case DeliveryReportPollOutcome.Correlated:
                _console.WriteLine("[PASS] Delivery Report observed in Storage Queue");
                _console.WriteLine("[PASS] Event correlated to the test send");

                var statusClass = DeliveryStatusClassifier.Classify(pollResult.DeliveryStatus);
                switch (statusClass)
                {
                    case DeliveryStatusClass.Delivered:
                        _console.WriteLine("[PASS] Delivery status classified");
                        break;
                    case DeliveryStatusClass.Failed:
                        _console.WriteLine("[FAIL] Delivery status classified");
                        break;
                    default:
                        _console.WriteLine("[WARN] Delivery status classified");
                        break;
                }

                _console.WriteLine("[ACTION] Confirm receipt in the test mailbox");
                _console.WriteLine(
                    $"success: operation=verify_delivery_report result={VerifyDeliveryReportResultCodes.Success}");
                // Wiring confirmed: exit 0 even when delivery status is non-Delivered.
                return SuccessExitCode;

            default:
                return Fail(VerifyDeliveryReportResultCodes.FailedUnexpected);
        }
    }

    private TimeSpan ResolveTimeout()
    {
        var raw = _configuration[TimeoutEnvVar];
        if (string.IsNullOrWhiteSpace(raw))
        {
            return TimeSpan.FromSeconds(DefaultTimeoutSeconds);
        }

        if (!int.TryParse(raw, out var seconds)
            || seconds < MinTimeoutSeconds
            || seconds > MaxTimeoutSeconds)
        {
            throw new SecretOperationException(
                VerifyDeliveryReportResultCodes.RejectedInvalidTimeout,
                "Timeout is outside the allowed range.");
        }

        return TimeSpan.FromSeconds(seconds);
    }

    private TimeSpan ResolvePollInterval()
    {
        var raw = _configuration[PollIntervalEnvVar];
        if (string.IsNullOrWhiteSpace(raw))
        {
            return TimeSpan.FromSeconds(DefaultPollIntervalSeconds);
        }

        if (!int.TryParse(raw, out var seconds)
            || seconds < MinPollIntervalSeconds
            || seconds > MaxPollIntervalSeconds)
        {
            throw new SecretOperationException(
                VerifyDeliveryReportResultCodes.RejectedInvalidPollInterval,
                "Poll interval is outside the allowed range.");
        }

        return TimeSpan.FromSeconds(seconds);
    }

    private string ResolveAcsConnectionString(CancellationToken cancellationToken)
    {
        var fromFile = TryReadAcsSecretFile();
        if (!string.IsNullOrEmpty(fromFile))
        {
            if (!AcsConnectionStringRegex().IsMatch(fromFile))
            {
                throw new SecretOperationException(
                    VerifyDeliveryReportResultCodes.RejectedInvalidConnectionString,
                    "Connection string file does not look like an ACS endpoint/accesskey value.");
            }

            _console.WriteLine("Using ACS connection string from configured secret file.");
            return fromFile;
        }

        return ReadConfirmedAcsConnectionStringFromTty(cancellationToken);
    }

    private string? TryReadAcsSecretFile()
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

    private string ReadConfirmedAcsConnectionStringFromTty(CancellationToken cancellationToken)
    {
        var first = _console.ReadSecret("ACS connection string: ", cancellationToken);
        var second = _console.ReadSecret("Re-enter ACS connection string: ", cancellationToken);
        if (!string.Equals(first, second, StringComparison.Ordinal))
        {
            throw new SecretOperationException(
                VerifyDeliveryReportResultCodes.RejectedSecretMismatch,
                "Connection string confirmation did not match.");
        }

        if (!AcsConnectionStringRegex().IsMatch(first))
        {
            throw new SecretOperationException(
                VerifyDeliveryReportResultCodes.RejectedInvalidConnectionString,
                "Connection string does not look like an ACS endpoint/accesskey value.");
        }

        return first;
    }

    private string ResolveQueueConnectionString(CancellationToken cancellationToken)
    {
        var fromConfig = TryReadQueueConnectionString();
        if (!string.IsNullOrEmpty(fromConfig))
        {
            if (!LooksLikeStorageConnectionString(fromConfig))
            {
                throw new SecretOperationException(
                    VerifyDeliveryReportResultCodes.RejectedInvalidQueueConnectionString,
                    "Queue connection string file does not look like a Storage connection string.");
            }

            _console.WriteLine("Using Storage Queue connection string from configured secret file or environment.");
            return fromConfig;
        }

        var first = _console.ReadSecret("Storage Queue connection string: ", cancellationToken);
        var second = _console.ReadSecret("Re-enter Storage Queue connection string: ", cancellationToken);
        if (!string.Equals(first, second, StringComparison.Ordinal))
        {
            throw new SecretOperationException(
                VerifyDeliveryReportResultCodes.RejectedSecretMismatch,
                "Queue connection string confirmation did not match.");
        }

        if (!LooksLikeStorageConnectionString(first))
        {
            throw new SecretOperationException(
                VerifyDeliveryReportResultCodes.RejectedInvalidQueueConnectionString,
                "Queue connection string does not look like a Storage connection string.");
        }

        return first;
    }

    private string? TryReadQueueConnectionString()
    {
        var filePath = _configuration[MailerBounceIngestionOptions.QueueConnectionStringFileKey];
        if (!string.IsNullOrWhiteSpace(filePath) && File.Exists(filePath))
        {
            var value = File.ReadAllText(filePath).Trim();
            if (!string.IsNullOrEmpty(value))
            {
                return value;
            }
        }

        var envValue = _configuration[MailerBounceIngestionOptions.QueueConnectionStringEnvironmentKey];
        if (!string.IsNullOrWhiteSpace(envValue))
        {
            return envValue.Trim();
        }

        var configValue = _configuration[MailerBounceIngestionOptions.QueueConnectionStringKey];
        if (!string.IsNullOrWhiteSpace(configValue))
        {
            return configValue.Trim();
        }

        return null;
    }

    private string ResolveQueueName(CancellationToken cancellationToken)
    {
        var configured = _configuration[MailerBounceIngestionOptions.QueueNameEnvironmentKey]
            ?? _configuration[MailerBounceIngestionOptions.QueueNameKey];
        string queueName;
        if (!string.IsNullOrWhiteSpace(configured))
        {
            queueName = configured.Trim();
            _console.WriteLine("Using Storage Queue name from configuration.");
        }
        else
        {
            queueName = _console.ReadVisibleLine("Storage Queue name: ", cancellationToken).Trim();
        }

        if (string.IsNullOrWhiteSpace(queueName)
            || queueName.Length > 63
            || queueName.Contains(' ', StringComparison.Ordinal)
            || queueName.Contains('\n', StringComparison.Ordinal)
            || queueName.Contains('\r', StringComparison.Ordinal))
        {
            throw new SecretOperationException(
                VerifyDeliveryReportResultCodes.RejectedInvalidQueueName,
                "Queue name is invalid.");
        }

        // Soft production guard: reject obvious production queue names after Staging confirmation.
        if (LooksLikeProductionQueueName(queueName))
        {
            throw new SecretOperationException(
                VerifyDeliveryReportResultCodes.RejectedInvalidQueueName,
                "Production-looking queue names are out of scope for this Staging command.");
        }

        return queueName;
    }

    internal static bool LooksLikeProductionQueueName(string queueName)
    {
        var normalized = queueName.Trim().ToLowerInvariant();
        return normalized is "production" or "prod"
            || normalized.StartsWith("production-", StringComparison.Ordinal)
            || normalized.StartsWith("prod-", StringComparison.Ordinal)
            || normalized.EndsWith("-production", StringComparison.Ordinal)
            || normalized.EndsWith("-prod", StringComparison.Ordinal)
            || normalized.Contains("-production-", StringComparison.Ordinal)
            || normalized.Contains("-prod-", StringComparison.Ordinal);
    }

    internal static bool LooksLikeStorageConnectionString(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var trimmed = value.Trim();
        if (trimmed.Contains('\n') || trimmed.Contains('\r'))
        {
            return false;
        }

        return trimmed.Contains("AccountName=", StringComparison.OrdinalIgnoreCase)
            || trimmed.Contains("QueueEndpoint=", StringComparison.OrdinalIgnoreCase)
            || trimmed.Contains("UseDevelopmentStorage=true", StringComparison.OrdinalIgnoreCase)
            || trimmed.Contains("DefaultEndpointsProtocol=", StringComparison.OrdinalIgnoreCase);
    }

    private string ReadBareEmail(string prompt, string invalidCode, CancellationToken cancellationToken)
    {
        var email = _console.ReadHiddenLine(prompt, cancellationToken).Trim();
        if (!MailAddress.TryCreate(email, out var parsed)
            || !string.Equals(parsed.Address, email, StringComparison.Ordinal))
        {
            throw new SecretOperationException(invalidCode, "Email must be a bare email address.");
        }

        return email;
    }

    private int Reject(string canonicalCode)
    {
        _console.WriteError($"rejected: operation=verify_delivery_report result={canonicalCode}");
        return RejectedExitCode;
    }

    private int Fail(string canonicalCode, string? stageFailLine = null)
    {
        if (stageFailLine is not null)
        {
            _console.WriteLine(stageFailLine);
        }

        _console.WriteError($"failed: operation=verify_delivery_report result={canonicalCode}");
        return FailureExitCode;
    }
}
