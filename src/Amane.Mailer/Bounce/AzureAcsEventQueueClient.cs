using Azure.Storage.Queues;
using Amane.Mailer.Configuration;

namespace Amane.Mailer.Bounce;

/// <summary>
/// Azure.Storage.Queues adapter for ACS Event Grid Storage Queue subscriptions (#305 / #399).
/// </summary>
public sealed class AzureAcsEventQueueClient : IAcsEventQueueClient
{
    private readonly QueueClient _client;

    public AzureAcsEventQueueClient(MailerBounceIngestionOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        // MessageEncoding.None: Event Grid → Storage Queue payloads are raw UTF-8 JSON (ADR 0020 F-6).
        // Base64 wrapping is handled in AcsQueueMessageBodyDecoder when present.
        _client = new QueueClient(
            options.QueueConnectionString,
            options.QueueName,
            new QueueClientOptions
            {
                MessageEncoding = QueueMessageEncoding.None,
            });
    }

    internal AzureAcsEventQueueClient(QueueClient client)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
    }

    public async Task<IReadOnlyList<AcsQueueReceivedMessage>> ReceiveMessagesAsync(
        int maxMessages,
        TimeSpan visibilityTimeout,
        CancellationToken cancellationToken)
    {
        var response = await _client.ReceiveMessagesAsync(
            maxMessages,
            visibilityTimeout,
            cancellationToken);

        if (response.Value is null || response.Value.Length == 0)
        {
            return [];
        }

        var messages = new List<AcsQueueReceivedMessage>(response.Value.Length);
        foreach (var message in response.Value)
        {
            messages.Add(new AcsQueueReceivedMessage(
                message.MessageId,
                message.PopReceipt,
                message.Body.ToString(),
                message.DequeueCount));
        }

        return messages;
    }

    public Task DeleteMessageAsync(
        string messageId,
        string popReceipt,
        CancellationToken cancellationToken) =>
        _client.DeleteMessageAsync(messageId, popReceipt, cancellationToken);
}
