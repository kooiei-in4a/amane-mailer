using System.Text.Json.Serialization;

namespace Amane.Mailer.Contracts.MailRequests;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record MailRequestCreateRequest
{
    [JsonPropertyName("tenant_id")]
    public required Guid TenantId { get; init; }

    [JsonPropertyName("source_service")]
    public required string SourceService { get; init; }

    [JsonPropertyName("mail_request_id")]
    public required Guid MailRequestId { get; init; }

    [JsonPropertyName("purpose")]
    public required string Purpose { get; init; }

    /// <summary>
    /// Contract shape is an array for forward compatibility. MVP validation accepts at most one recipient.
    /// </summary>
    [JsonPropertyName("to")]
    public required IReadOnlyList<MailRecipientDto> To { get; init; }

    [JsonPropertyName("subject")]
    public required string Subject { get; init; }

    [JsonPropertyName("html_body")]
    public string? HtmlBody { get; init; }

    [JsonPropertyName("text_body")]
    public string? TextBody { get; init; }

    [JsonPropertyName("reply_to")]
    public string? ReplyTo { get; init; }

    /// <summary>
    /// Optional caller-supplied key/value pairs (string values only).
    /// Keys containing token, password, secret, or url (case-insensitive) are rejected.
    /// Values are stored as sent and are not scanned for secrets; do not place tokens,
    /// passwords, or URL query secrets in metadata values. See Contracts README
    /// "Metadata policy" and OpenAPI metadata description.
    /// </summary>
    [JsonPropertyName("metadata")]
    public IReadOnlyDictionary<string, string>? Metadata { get; init; }

    /// <summary>
    /// Optional first-dispatch time (UTC). Null means immediate. Excluded from payload_hash.
    /// Independent from <c>next_attempt_at</c> (retry backoff).
    /// </summary>
    [JsonPropertyName("scheduled_at")]
    public DateTimeOffset? ScheduledAt { get; init; }

    /// <summary>
    /// Optional attachments (ADR 0022 D-01). Unspecified or an empty array means no attachment;
    /// both are equivalent and omitted from payload_hash (ADR 0022 D-03). Array order is
    /// submission order and part of payload identity; at most
    /// <see cref="MailAttachmentLimits.MaxAttachmentCount"/> entries are accepted.
    /// </summary>
    [JsonPropertyName("attachments")]
    public IReadOnlyList<MailAttachmentDto>? Attachments { get; init; }

    [JsonPropertyName("payload_hash")]
    public required string PayloadHash { get; init; }
}
