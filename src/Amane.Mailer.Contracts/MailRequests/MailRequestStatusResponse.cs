using System.Text.Json.Serialization;

namespace Amane.Mailer.Contracts.MailRequests;

public sealed record MailRequestStatusResponse
{
    [JsonPropertyName("mail_request_id")]
    public required Guid MailRequestId { get; init; }

    /// <summary>
    /// Worker delivery status. Use values from <see cref="MailRequestStatus"/>, not API acceptance status.
    /// </summary>
    [JsonPropertyName("status")]
    public required string Status { get; init; }

    [JsonPropertyName("attempt_count")]
    public required int AttemptCount { get; init; }

    [JsonPropertyName("max_attempts")]
    public required int MaxAttempts { get; init; }

    [JsonPropertyName("next_attempt_at")]
    public DateTimeOffset? NextAttemptAt { get; init; }

    [JsonPropertyName("accepted_at")]
    public required DateTimeOffset AcceptedAt { get; init; }

    [JsonPropertyName("delivered_at")]
    public DateTimeOffset? DeliveredAt { get; init; }

    [JsonPropertyName("last_error_code")]
    public string? LastErrorCode { get; init; }
}
