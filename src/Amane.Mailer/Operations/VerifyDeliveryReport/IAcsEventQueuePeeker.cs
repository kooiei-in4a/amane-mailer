namespace Amane.Mailer.Operations.VerifyDeliveryReport;

/// <summary>
/// Read-only Storage Queue access for Delivery Report E2E (#428).
/// Implementations must use peek / metadata only — never receive, delete, or change visibility.
/// </summary>
public interface IAcsEventQueuePeeker
{
    /// <summary>
    /// Peeks up to <paramref name="maxMessages"/> messages without altering visibility or deleting.
    /// </summary>
    Task<IReadOnlyList<PeekedQueueMessageBody>> PeekMessagesAsync(
        int maxMessages,
        CancellationToken cancellationToken);

    /// <summary>
    /// Returns the approximate message count when available; <c>null</c> when the service
    /// does not expose a usable count (caller must not treat null as empty).
    /// </summary>
    Task<int?> GetApproximateMessageCountAsync(CancellationToken cancellationToken);
}

/// <summary>
/// Peeked queue body only. Queue message IDs / pop receipts are intentionally omitted so
/// callers cannot accidentally call delete APIs.
/// </summary>
public sealed record PeekedQueueMessageBody(string Body);

/// <summary>
/// Factory used by the CLI so tests can inject a fake peeker without Azure credentials.
/// </summary>
public interface IAcsEventQueuePeekerFactory
{
    IAcsEventQueuePeeker Create(string connectionString, string queueName);
}
