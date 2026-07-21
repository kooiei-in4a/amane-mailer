using System.Text.Json.Serialization;

namespace Amane.Mailer.Contracts.MailRequests;

/// <summary>
/// PII-free delivery result event pushed to tenant-configured webhooks.
/// Shared with the future mail request status query API (#216).
/// </summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record MailDeliveryEventPayload
{
    [JsonPropertyName("event_id")]
    public required Guid EventId { get; init; }

    [JsonPropertyName("event_type")]
    public required string EventType { get; init; }

    [JsonPropertyName("occurred_at")]
    public required DateTimeOffset OccurredAt { get; init; }

    [JsonPropertyName("tenant_id")]
    public required Guid TenantId { get; init; }

    [JsonPropertyName("source_service")]
    public required string SourceService { get; init; }

    [JsonPropertyName("mail_request_id")]
    public required Guid MailRequestId { get; init; }

    [JsonPropertyName("status")]
    public required string Status { get; init; }

    [JsonPropertyName("attempt_count")]
    public required int AttemptCount { get; init; }

    [JsonPropertyName("last_error_code")]
    public string? LastErrorCode { get; init; }
}
