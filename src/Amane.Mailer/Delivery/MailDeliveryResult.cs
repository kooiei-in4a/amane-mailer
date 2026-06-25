namespace Amane.Mailer.Delivery;

public sealed record MailDeliveryResult
{
    public required bool Succeeded { get; init; }

    public string? ProviderMessageId { get; init; }

    public string? ErrorCode { get; init; }

    public string? ErrorMessage { get; init; }

    public bool Retryable { get; init; }

    public static MailDeliveryResult Success(string? providerMessageId = null) =>
        new()
        {
            Succeeded = true,
            ProviderMessageId = providerMessageId,
        };

    public static MailDeliveryResult Failure(
        string errorCode,
        string? errorMessage,
        bool retryable) =>
        new()
        {
            Succeeded = false,
            ErrorCode = errorCode,
            ErrorMessage = errorMessage,
            Retryable = retryable,
        };
}
