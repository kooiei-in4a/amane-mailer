using Azure;
using Azure.Storage.Queues;

namespace Amane.Mailer.Operations.VerifyDeliveryReport;

/// <summary>
/// Peek-only Azure Storage Queue adapter for Staging Delivery Report E2E (#428).
/// Does not call Receive, Delete, or UpdateMessage.
/// </summary>
public sealed class AzureAcsEventQueuePeeker : IAcsEventQueuePeeker
{
    /// <summary>
    /// Azure Storage Queue peek maximum per request.
    /// </summary>
    public const int MaxPeekMessages = 32;

    private readonly QueueClient _client;

    public AzureAcsEventQueuePeeker(string connectionString, string queueName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        ArgumentException.ThrowIfNullOrWhiteSpace(queueName);

        // MessageEncoding.None: Event Grid -> Storage Queue payloads are raw UTF-8 JSON (ADR 0020 F-6).
        _client = new QueueClient(
            connectionString,
            queueName,
            new QueueClientOptions
            {
                MessageEncoding = QueueMessageEncoding.None,
            });
    }

    internal AzureAcsEventQueuePeeker(QueueClient client)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
    }

    public async Task<IReadOnlyList<PeekedQueueMessageBody>> PeekMessagesAsync(
        int maxMessages,
        CancellationToken cancellationToken)
    {
        if (maxMessages < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(maxMessages));
        }

        var capped = Math.Min(maxMessages, MaxPeekMessages);
        var response = await _client.PeekMessagesAsync(capped, cancellationToken);

        if (response.Value is null || response.Value.Length == 0)
        {
            return [];
        }

        var messages = new List<PeekedQueueMessageBody>(response.Value.Length);
        foreach (var message in response.Value)
        {
            messages.Add(new PeekedQueueMessageBody(message.Body.ToString()));
        }

        return messages;
    }

    public async Task<int?> GetApproximateMessageCountAsync(CancellationToken cancellationToken)
    {
        var properties = await _client.GetPropertiesAsync(cancellationToken);
        return properties.Value.ApproximateMessagesCount;
    }
}

/// <summary>
/// Default factory that constructs <see cref="AzureAcsEventQueuePeeker"/>.
/// </summary>
public sealed class AzureAcsEventQueuePeekerFactory : IAcsEventQueuePeekerFactory
{
    public IAcsEventQueuePeeker Create(string connectionString, string queueName) =>
        new AzureAcsEventQueuePeeker(connectionString, queueName);
}

/// <summary>
/// Maps Azure Storage Queue exceptions to canonical verify-delivery-report failure codes
/// without exposing raw provider messages.
/// </summary>
public static class QueuePeekFailureMapper
{
    public static string Map(Exception exception)
    {
        if (exception is RequestFailedException requestFailed)
        {
            if (requestFailed.Status is 401 or 403)
            {
                return VerifyDeliveryReportResultCodes.FailedQueueAuthentication;
            }

            if (requestFailed.Status == 404
                || string.Equals(requestFailed.ErrorCode, "QueueNotFound", StringComparison.OrdinalIgnoreCase)
                || string.Equals(requestFailed.ErrorCode, "ContainerNotFound", StringComparison.OrdinalIgnoreCase))
            {
                return VerifyDeliveryReportResultCodes.FailedQueueNotFound;
            }

            if (requestFailed.Status is >= 500 or 408 or 429)
            {
                return VerifyDeliveryReportResultCodes.FailedQueueNetwork;
            }
        }

        if (exception is TimeoutException
            or OperationCanceledException
            or HttpRequestException
            or IOException)
        {
            return VerifyDeliveryReportResultCodes.FailedQueueNetwork;
        }

        return VerifyDeliveryReportResultCodes.FailedUnexpected;
    }
}
