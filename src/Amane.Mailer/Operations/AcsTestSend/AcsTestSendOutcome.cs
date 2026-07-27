namespace Amane.Mailer.Operations.AcsTestSend;

/// <summary>
/// Three-state evaluation for a verification stage. <see cref="NotEvaluated"/> means the stage
/// was not reached or could not be judged (for example network failure before auth).
/// </summary>
public enum AcsEvaluationState
{
    NotEvaluated = 0,
    Succeeded = 1,
    Failed = 2,
}

/// <summary>
/// Result of a standalone ACS test send. Safe for in-process handoff to later verification
/// (#428). Do not serialize this type to operator-facing logs without redaction.
/// </summary>
public sealed class AcsTestSendOutcome
{
    public required AcsEvaluationState AuthenticationState { get; init; }

    public required bool SendRequestAccepted { get; init; }

    public required bool OperationCompleted { get; init; }

    /// <summary>
    /// ACS operation / provider message id when the send request was accepted. Never print.
    /// </summary>
    public string? ProviderMessageId { get; init; }

    public string? CanonicalFailureCode { get; init; }

    public static AcsTestSendOutcome Succeeded(string providerMessageId) =>
        new()
        {
            AuthenticationState = AcsEvaluationState.Succeeded,
            SendRequestAccepted = true,
            OperationCompleted = true,
            ProviderMessageId = providerMessageId,
        };

    public static AcsTestSendOutcome Failed(
        string canonicalFailureCode,
        AcsEvaluationState authenticationState = AcsEvaluationState.NotEvaluated,
        bool sendRequestAccepted = false,
        string? providerMessageId = null) =>
        new()
        {
            AuthenticationState = authenticationState,
            SendRequestAccepted = sendRequestAccepted,
            OperationCompleted = false,
            ProviderMessageId = providerMessageId,
            CanonicalFailureCode = canonicalFailureCode,
        };
}
