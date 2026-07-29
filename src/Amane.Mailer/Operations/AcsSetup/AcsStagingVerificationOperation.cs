using System.Net.Mail;
using Amane.Mailer.Operations.AcsTestSend;
using Amane.Mailer.Setup;

namespace Amane.Mailer.Operations.AcsSetup;

/// <summary>
/// Adapter-facing Staging verification request. The sender is intentionally absent: Managed
/// verification derives it from the opaque configuration-applied proof and selected tenant.
/// </summary>
public sealed record AcsStagingVerificationRequest
{
    public required string EnvironmentConfirmation { get; init; }
    public required string IntentConfirmation { get; init; }
    public required Guid TenantId { get; init; }
    public required string RecipientEmail { get; init; }
    public string? AssistantSessionId { get; init; }
}

public sealed class AcsStagingVerificationResult
{
    public required string Code { get; init; }
    public AcsEvaluationState AuthenticationState { get; init; } = AcsEvaluationState.NotEvaluated;
    public bool SendRequestAccepted { get; init; }
    public bool OperationCompleted { get; init; }
    public string MailboxCheckStatus { get; init; } = MailboxCheckActionRequired;
    public string? MaskedSenderEmail { get; init; }
    public string? MaskedRecipientEmail { get; init; }

    /// <summary>In-process TTY handoff only; adapters must never persist this value.</summary>
    public string? ProviderMessageIdForHandoff { get; init; }

    public const string MailboxCheckActionRequired = "ACTION";
    public const string MailboxCheckNotEvaluated = "not-evaluated";

    public bool IsSuccess => Code == AdminProviderTestAcsSendResultCodes.Success;

    public static AcsStagingVerificationResult Reject(string code) =>
        new() { Code = code, MailboxCheckStatus = MailboxCheckNotEvaluated };

    internal static AcsStagingVerificationResult FromOutcome(
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

public sealed class AcsStagingVerificationOperation
{
    public const string IntentPhrase = "MAILER-ACS-TEST-SEND";
    public const string SyntheticSubject = "Amane Mailer ACS test-send verification";
    public const string SyntheticPlainTextBody =
        "This is a fixed synthetic message from Amane Mailer admin provider test-acs-send. Do not reply.";

    public const string RejectedProductionEnvironment = "REJECTED_PRODUCTION_ENVIRONMENT";
    public const string RejectedSenderMismatch = "REJECTED_SENDER_MISMATCH";
    public const string RejectedTenantNotFound = "REJECTED_TENANT_NOT_FOUND";
    public const string RejectedSessionLimitExceeded = "REJECTED_SESSION_LIMIT_EXCEEDED";

    private readonly IAcsTestSendClient _acsClient;
    private readonly AcsSessionTestSendLimiter _sessionLimiter;
    private readonly Func<Guid> _operationIdFactory;

    public AcsStagingVerificationOperation(
        IAcsTestSendClient? acsClient = null,
        AcsSessionTestSendLimiter? sessionLimiter = null,
        Func<Guid>? operationIdFactory = null)
    {
        _acsClient = acsClient ?? new AzureAcsTestSendClient();
        _sessionLimiter = sessionLimiter ?? AcsSessionTestSendLimiter.Shared;
        _operationIdFactory = operationIdFactory ?? Guid.NewGuid;
    }

    public Task<AcsStagingVerificationResult> ExecuteAsync(
        AcsStagingVerificationRequest request,
        AcsConfigurationAppliedProof appliedProof,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(appliedProof);

        if (appliedProof.Mode is not (
                SetupMode.StagingNoSend
                or SetupMode.StagingVerification))
        {
            return Task.FromResult(
                AcsStagingVerificationResult.Reject(RejectedProductionEnvironment));
        }

        var tenant = appliedProof.AppliedRequest.Tenants.Tenants.SingleOrDefault(
            tenant => tenant.TenantId == request.TenantId);
        if (tenant is null)
        {
            return Task.FromResult(
                AcsStagingVerificationResult.Reject(RejectedTenantNotFound));
        }

        if (!string.Equals(tenant.Provider, "acs", StringComparison.Ordinal)
            || tenant.LiveSending)
        {
            return Task.FromResult(
                AcsStagingVerificationResult.Reject(RejectedSenderMismatch));
        }

        var connectionString = appliedProof.AppliedRequest.AcsConnectionString;
        if (connectionString is null)
        {
            return Task.FromResult(
                AcsStagingVerificationResult.Reject(
                    AdminProviderTestAcsSendResultCodes.RejectedInvalidConnectionString));
        }

        return ExecuteCoreAsync(
            request.EnvironmentConfirmation,
            request.IntentConfirmation,
            connectionString,
            tenant.DefaultFrom.Email,
            request.RecipientEmail,
            request.AssistantSessionId,
            cancellationToken);
    }

    /// <summary>
    /// Existing direct CLI path has no Managed tenant context. It still uses the same typed
    /// validation/send core but intentionally has no Assistant session limit.
    /// </summary>
    internal Task<AcsStagingVerificationResult> ExecuteDirectCliAsync(
        string environmentConfirmation,
        string intentConfirmation,
        string connectionString,
        string senderEmail,
        string recipientEmail,
        CancellationToken cancellationToken) =>
        ExecuteCoreAsync(
            environmentConfirmation,
            intentConfirmation,
            connectionString,
            senderEmail,
            recipientEmail,
            assistantSessionId: null,
            cancellationToken);

    private async Task<AcsStagingVerificationResult> ExecuteCoreAsync(
        string environmentConfirmation,
        string intentConfirmation,
        string connectionString,
        string senderEmail,
        string recipientEmail,
        string? assistantSessionId,
        CancellationToken cancellationToken)
    {
        try
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (AcsEnvironmentConfirmation.IsExactProduction(environmentConfirmation))
            {
                return AcsStagingVerificationResult.Reject(RejectedProductionEnvironment);
            }

            if (!AcsEnvironmentConfirmation.IsExactStaging(environmentConfirmation))
            {
                return AcsStagingVerificationResult.Reject(
                    AdminProviderTestAcsSendResultCodes.RejectedEnvironmentMismatch);
            }

            if (AcsConfigurationValidator.ValidateIntent(intentConfirmation, IntentPhrase) is { } intentError)
            {
                return AcsStagingVerificationResult.Reject(intentError);
            }

            if (AcsConfigurationValidator.ValidateConnectionStrings(
                    connectionString,
                    connectionString) is { } connectionError)
            {
                return AcsStagingVerificationResult.Reject(connectionError);
            }

            if (!IsBareEmail(senderEmail))
            {
                return AcsStagingVerificationResult.Reject(
                    AdminProviderTestAcsSendResultCodes.RejectedInvalidSenderEmail);
            }

            if (!IsBareEmail(recipientEmail))
            {
                return AcsStagingVerificationResult.Reject(
                    AdminProviderTestAcsSendResultCodes.RejectedInvalidRecipientEmail);
            }

            if (!string.IsNullOrEmpty(assistantSessionId)
                && !_sessionLimiter.TryAcquire(assistantSessionId))
            {
                return AcsStagingVerificationResult.Reject(RejectedSessionLimitExceeded);
            }

            var outcome = await _acsClient.SendAsync(
                new AcsTestSendRequest
                {
                    ConnectionString = connectionString,
                    SenderEmail = senderEmail,
                    RecipientEmail = recipientEmail,
                    Subject = SyntheticSubject,
                    PlainTextBody = SyntheticPlainTextBody,
                    OperationId = _operationIdFactory(),
                },
                cancellationToken);

            return AcsStagingVerificationResult.FromOutcome(
                outcome,
                AcsAddressMask.MaskEmail(senderEmail),
                AcsAddressMask.MaskEmail(recipientEmail));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return AcsStagingVerificationResult.Reject(
                AdminProviderTestAcsSendResultCodes.RejectedCancelled);
        }
        catch (SecretOperationException ex)
        {
            return AcsStagingVerificationResult.Reject(ex.CanonicalCode);
        }
        catch (Exception)
        {
            return AcsStagingVerificationResult.Reject(
                AdminProviderTestAcsSendResultCodes.FailedUnexpected);
        }
    }

    private static bool IsBareEmail(string email) =>
        MailAddress.TryCreate(email, out var parsed)
        && string.Equals(parsed.Address, email, StringComparison.Ordinal);
}
