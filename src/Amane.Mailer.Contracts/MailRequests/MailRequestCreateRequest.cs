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

    [JsonPropertyName("payload_hash")]
    public required string PayloadHash { get; init; }
}
