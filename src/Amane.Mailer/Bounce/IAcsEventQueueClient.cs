namespace Amane.Mailer.Bounce;

/// <summary>
/// Abstraction over Azure Storage Queue receive/delete for bounce Pull transport (#305).
/// </summary>
public interface IAcsEventQueueClient
{
    Task<IReadOnlyList<AcsQueueReceivedMessage>> ReceiveMessagesAsync(
        int maxMessages,
        TimeSpan visibilityTimeout,
        CancellationToken cancellationToken);

    Task DeleteMessageAsync(
        string messageId,
        string popReceipt,
        CancellationToken cancellationToken);
}

public sealed record AcsQueueReceivedMessage(
    string MessageId,
    string PopReceipt,
    string Body);
