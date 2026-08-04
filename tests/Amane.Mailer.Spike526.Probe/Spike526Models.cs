using System.Text.Json.Serialization;

namespace Amane.Mailer.Spike526.Probe;

public sealed class Spike526Request
{
    [JsonPropertyName("tenant_id")]
    public required string TenantId { get; init; }

    [JsonPropertyName("source_service")]
    public required string SourceService { get; init; }

    [JsonPropertyName("mail_request_id")]
    public required string MailRequestId { get; init; }

    [JsonPropertyName("purpose")]
    public required string Purpose { get; init; }

    [JsonPropertyName("to")]
    public required List<Spike526Recipient> To { get; init; }

    [JsonPropertyName("cc")]
    public required List<Spike526Recipient> Cc { get; init; }

    [JsonPropertyName("bcc")]
    public required List<Spike526Recipient> Bcc { get; init; }

    [JsonPropertyName("subject")]
    public required string Subject { get; init; }

    [JsonPropertyName("text_body")]
    public required string TextBody { get; init; }

    [JsonPropertyName("html_body")]
    public required string HtmlBody { get; init; }

    [JsonPropertyName("reply_to")]
    public string? ReplyTo { get; init; }

    [JsonPropertyName("metadata")]
    public Dictionary<string, string>? Metadata { get; init; }

    [JsonPropertyName("attachments")]
    public required List<Spike526Attachment> Attachments { get; init; }

    [JsonPropertyName("payload_hash")]
    public required string PayloadHash { get; init; }
}

public sealed class Spike526Recipient
{
    [JsonPropertyName("email")]
    public required string Email { get; init; }

    [JsonPropertyName("display_name")]
    public string? DisplayName { get; init; }
}

public sealed class Spike526Attachment
{
    [JsonPropertyName("file_name")]
    public required string FileName { get; init; }

    [JsonPropertyName("content_type")]
    public required string ContentType { get; init; }

    [JsonPropertyName("byte_length")]
    public required long ByteLength { get; init; }

    [JsonPropertyName("content_sha256")]
    public required string ContentSha256 { get; init; }

    [JsonPropertyName("content_base64")]
    public required string ContentBase64 { get; init; }
}

public sealed record Spike526Fixture(
    string Id,
    Spike526Request Request,
    long DecodedBinaryBytes,
    int AttachmentCount,
    bool ExpectedValidBase64,
    bool ExpectedDeclaredMetadataMatch);

public sealed record Spike526ConsumerMeasurement(
    string Fixture,
    int AttachmentCount,
    long DecodedBinaryBytes,
    long Base64Characters,
    long ConsumerEnvelopeBytes);

public sealed record Spike526AcsCaptureResult(
    string Fixture,
    long RequestBodyBytes,
    string RequestBodySha256,
    string OperationStatus,
    bool ResponseParsed);

public sealed record Spike526TokenBufferResult(
    string Fixture,
    long RequestBytes,
    long DecodedBinaryBytes,
    int AttachmentCount,
    long PeakRetainedTokenBytes,
    long PeakTempBytes,
    bool CleanupComplete);

public sealed record Spike526ProbeResult(
    string Fixture,
    string Mode,
    int Concurrency,
    long DecodedBinaryBytes,
    long ConsumerEnvelopeBytes,
    long AcsEnvelopeBytes,
    long ManagedAllocatedBytes,
    long GcHeapBeforeBytes,
    long GcHeapPeakBytes,
    long GcHeapAfterBytes,
    long ElapsedMilliseconds,
    long PeakWorkingSetBytes,
    long PeakTempBytes,
    bool CleanupComplete,
    bool ProviderInvoked,
    string Result);

public sealed record Spike526CleanupResult(
    int DeletedFiles,
    int RemainingFiles,
    bool OutsideFilePreserved);

[JsonSerializable(typeof(Spike526Request))]
[JsonSerializable(typeof(Spike526ProbeResult))]
[JsonSerializable(typeof(Spike526CleanupResult))]
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.Unspecified,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    WriteIndented = false)]
public partial class Spike526JsonContext : JsonSerializerContext;
