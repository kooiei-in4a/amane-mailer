using System.Text.Json.Serialization;

namespace Amane.Mailer.Contracts.MailRequests;

/// <summary>
/// Public attachment contract (ADR 0022 D-01). Array order is submission order and part of
/// payload identity. Mailer re-verifies <see cref="ContentSha256"/>, <see cref="ByteLength"/>,
/// and file type from the decoded binary; declared values here are never trusted as-is.
/// </summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record MailAttachmentDto
{
    [JsonPropertyName("file_name")]
    public required string FileName { get; init; }

    [JsonPropertyName("content_type")]
    public required string ContentType { get; init; }

    /// <summary>RFC 4648 standard Base64, no whitespace.</summary>
    [JsonPropertyName("content_base64")]
    public required string ContentBase64 { get; init; }

    /// <summary>Decoded binary SHA-256, 64-character lowercase hex.</summary>
    [JsonPropertyName("content_sha256")]
    public required string ContentSha256 { get; init; }

    /// <summary>Decoded binary byte length.</summary>
    [JsonPropertyName("byte_length")]
    public required long ByteLength { get; init; }
}
