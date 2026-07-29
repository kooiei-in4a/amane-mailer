using System.Net.Mail;
using Amane.Mailer.Operations.AcsTestSend;

namespace Amane.Mailer.Operations.AcsSetup;

/// <summary>
/// Console-independent Staging verification request. Subject, body, and headers are fixed by the
/// operation and cannot be supplied by adapters.
/// </summary>
public sealed record AcsStagingVerificationRequest
{
    public required string EnvironmentConfirmation { get; init; }
    public required string IntentConfirmation { get; init; }
    public required string ConnectionString { get; init; }
    public required string SenderEmail { get; init; }
    public required string RecipientEmail { get; init; }

    /// <summary>
    /// Sender email from the generated tenant configuration (default_from). Must match
    /// <see cref="SenderEmail"/> exactly (ordinal).
    /// </summary>
    public required string ExpectedTenantSenderEmail { get; init; }

    /// <summary>
    /// When set, Assistant session rate limiting applies. Omit for direct CLI (non-goal for limits).
    /// </summary>
    public string? AssistantSessionId { get; init; }
}

/// <summary>
/// Public Staging verification result. Distinguishes provider accepted / completed / mailbox ACTION.
/// Never carries secret, unmasked addresses beyond optional masks, or provider raw errors.
/// </summary>
public sealed class AcsStagingVerificationResult
{
    public required string Code { get; init; }
    public AcsEvaluationState AuthenticationState { get; init; } = AcsEvaluationState.NotEvaluated;
    public bool SendRequestAccepted { get; init; }
    public bool OperationCompleted { get; init; }

    /// <summary>
    /// Mailbox arrival is never auto-PASS. Always an ACTION for the operator when send completed.
    /// </summary>
    public string MailboxCheckStatus { get; init; } = MailboxCheckActionRequired;

    public string? MaskedSenderEmail { get; init; }
    public string? MaskedRecipientEmail { get; init; }

    /// <summary>
    /// Provider message id for in-process / TTY handoff only. Adapters must not persist this into
    /// setup metadata, browser storage, logs, or verification records.
    /// </summary>
    public string? ProviderMessageIdForHandoff { get; init; }

    public const string MailboxCheckActionRequired = "ACTION";
    public const string MailboxCheckNotEvaluated = "not-evaluated";

    public bool IsSuccess => Code == AdminProviderTestAcsSendResultCodes.Success;

    public static AcsStagingVerificationResult Reject(string code) =>
        new()
        {
            Code = code,
            MailboxCheckStatus = MailboxCheckNotEvaluated,
        };

    public static AcsStagingVerificationResult FromOutcome(
        AcsTestSendOutcome outcome,
        string maskedSender,
        string maskedRecipient)
    {
        if (outcome.CanonicalFailureCode is { } failure)
        {
            return new AcsStagingVerificationResult
            {
                Code = failure,
                AuthenticationState = outcome.AuthenticationState,
                SendRequestAccepted = outcome.SendRequestAccepted,
                OperationCompleted = outcome.OperationCompleted,
                MailboxCheckStatus = MailboxCheckNotEvaluated,
                MaskedSenderEmail = maskedSender,
                MaskedRecipientEmail = maskedRecipient,
                ProviderMessageIdForHandoff = outcome.ProviderMessageId,
            };
        }

        return new AcsStagingVerificationResult
        {
            Code = AdminProviderTestAcsSendResultCodes.Success,
            AuthenticationState = outcome.AuthenticationState,
            SendRequestAccepted = outcome.SendRequestAccepted,
            OperationCompleted = outcome.OperationCompleted,
            MailboxCheckStatus = MailboxCheckActionRequired,
            MaskedSenderEmail = maskedSender,
            MaskedRecipientEmail = maskedRecipient,
            ProviderMessageIdForHandoff = outcome.ProviderMessageId,
        };
    }
}

/// <summary>
/// Staging-only ACS synthetic test send Application Service. Production is always rejected.
/// </summary>
public sealed class AcsStagingVerificationOperation
{
    public const string IntentPhrase = "MAILER-ACS-TEST-SEND";
    public const string SyntheticSubject = "Amane Mailer ACS test-send verification";
    public const string SyntheticPlainTextBody =
        "This is a fixed synthetic message from Amane Mailer admin provider test-acs-send. Do not reply.";

    public const string RejectedProductionEnvironment = "REJECTED_PRODUCTION_ENVIRONMENT";
    public const string RejectedSenderMismatch = "REJECTED_SENDER_MISMATCH";
    public const string RejectedSessionLimitExceeded = "REJECTED_SESSION_LIMIT_EXCEEDED";
    public const string RejectedArbitraryContent = "REJECTED_ARBITRARY_CONTENT";

    private readonly IAcsTestSendClient _acsClient;
    private readonly AcsSessionTestSendLimiter _sessionLimiter;
    private readonly Func<Guid> _operationIdFactory;

    public AcsStagingVerificationOperation(
        IAcsTestSendClient? acsClient = null,
        AcsSessionTestSendLimiter? sessionLimiter = null,
        Func<Guid>? operationIdFactory = null)
    {
        _acsClient = acsClient ?? new AzureAcsTestSendClient();
        _sessionLimiter = sessionLimiter ?? new AcsSessionTestSendLimiter();
        _operationIdFactory = operationIdFactory ?? (() => Guid.NewGuid());
    }

    public async Task<AcsStagingVerificationResult> ExecuteAsync(
        AcsStagingVerificationRequest request,
        CancellationToken cancellationToken)
    {
        try
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (AcsEnvironmentConfirmation.IsExactProduction(request.EnvironmentConfirmation))
            {
                return AcsStagingVerificationResult.Reject(RejectedProductionEnvironment);
            }

            if (!AcsEnvironmentConfirmation.IsExactStaging(request.EnvironmentConfirmation))
            {
                return AcsStagingVerificationResult.Reject(
                    AdminProviderTestAcsSendResultCodes.RejectedEnvironmentMismatch);
            }

            if (!string.Equals(request.IntentConfirmation, IntentPhrase, StringComparison.Ordinal))
            {
                return AcsStagingVerificationResult.Reject(
                    AdminProviderTestAcsSendResultCodes.RejectedIntentMismatch);
            }

            if (!AcsConnectionStringRules.LooksLikeAcsConnectionString(request.ConnectionString))
            {
                return AcsStagingVerificationResult.Reject(
                    AdminProviderTestAcsSendResultCodes.RejectedInvalidConnectionString);
            }

            if (!TryValidateBareEmail(request.SenderEmail))
            {
                return AcsStagingVerificationResult.Reject(
                    AdminProviderTestAcsSendResultCodes.RejectedInvalidSenderEmail);
            }

            if (!TryValidateBareEmail(request.RecipientEmail))
            {
                return AcsStagingVerificationResult.Reject(
                    AdminProviderTestAcsSendResultCodes.RejectedInvalidRecipientEmail);
            }

            if (!string.Equals(
                    request.SenderEmail,
                    request.ExpectedTenantSenderEmail,
                    StringComparison.Ordinal))
            {
                return AcsStagingVerificationResult.Reject(RejectedSenderMismatch);
            }

            if (!string.IsNullOrEmpty(request.AssistantSessionId)
                && !_sessionLimiter.TryAcquire(request.AssistantSessionId))
            {
                return AcsStagingVerificationResult.Reject(RejectedSessionLimitExceeded);
            }

            var maskedSender = AcsAddressMask.MaskEmail(request.SenderEmail);
            var maskedRecipient = AcsAddressMask.MaskEmail(request.RecipientEmail);

            var outcome = await _acsClient.SendAsync(
                new AcsTestSendRequest
                {
                    ConnectionString = request.ConnectionString,
                    SenderEmail = request.SenderEmail,
                    RecipientEmail = request.RecipientEmail,
                    Subject = SyntheticSubject,
                    PlainTextBody = SyntheticPlainTextBody,
                    OperationId = _operationIdFactory(),
                },
                cancellationToken);

            return AcsStagingVerificationResult.FromOutcome(outcome, maskedSender, maskedRecipient);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return AcsStagingVerificationResult.Reject(AdminProviderTestAcsSendResultCodes.RejectedCancelled);
        }
        catch (SecretOperationException ex)
        {
            return AcsStagingVerificationResult.Reject(ex.CanonicalCode);
        }
        catch (Exception)
        {
            return AcsStagingVerificationResult.Reject(AdminProviderTestAcsSendResultCodes.FailedUnexpected);
        }
    }

    private static bool TryValidateBareEmail(string email) =>
        MailAddress.TryCreate(email, out var parsed)
        && string.Equals(parsed.Address, email, StringComparison.Ordinal);
}
