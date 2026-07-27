namespace Amane.Mailer.Operations.AcsTestSend;

/// <summary>
/// Inputs for a standalone ACS test send. Values must never be logged or written to stdout.
/// </summary>
public sealed class AcsTestSendRequest
{
    public required string ConnectionString { get; init; }

    public required string SenderEmail { get; init; }

    public required string RecipientEmail { get; init; }

    public required string Subject { get; init; }

    public required string PlainTextBody { get; init; }

    /// <summary>
    /// Caller-supplied ACS operation id (UUID). Becomes the provider message id used for
    /// Delivery Report correlation in a later verification step (#428).
    /// </summary>
    public required Guid OperationId { get; init; }
}
