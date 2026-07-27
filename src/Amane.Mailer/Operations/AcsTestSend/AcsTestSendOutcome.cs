namespace Amane.Mailer.Operations.AcsTestSend;

/// <summary>
/// Result of a standalone ACS test send. Safe for in-process handoff to later verification
/// (#428). Do not serialize this type to operator-facing logs without redaction.
/// </summary>
public sealed class AcsTestSendOutcome
{
    public required bool AuthenticationSucceeded { get; init; }

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
            AuthenticationSucceeded = true,
            SendRequestAccepted = true,
            OperationCompleted = true,
            ProviderMessageId = providerMessageId,
        };

    public static AcsTestSendOutcome Failed(
        string canonicalFailureCode,
        bool authenticationSucceeded = false,
        bool sendRequestAccepted = false,
        string? providerMessageId = null) =>
        new()
        {
            AuthenticationSucceeded = authenticationSucceeded,
            SendRequestAccepted = sendRequestAccepted,
            OperationCompleted = false,
            ProviderMessageId = providerMessageId,
            CanonicalFailureCode = canonicalFailureCode,
        };
}
