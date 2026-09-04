namespace Amane.Mailer.Contracts.Security;

internal static class MailPayloadHashContract
{
    /// <summary>
    /// Fields included in payload_hash. The hash document is the delivery payload, not the routing envelope.
    /// </summary>
    /// <summary>
    /// <c>to</c>, <c>cc</c>, and <c>bcc</c> follow the same omission rule (ADR 0023 D-02): an
    /// absent property, explicit <c>null</c>, and an empty array are all equivalent and the
    /// role is omitted from the hash document entirely (no <c>"to":null</c> or <c>"to":[]</c>
    /// ever appears). This holds even for CC-only/BCC-only requests, where the hash document has
    /// no <c>to</c> key at all. Non-empty roles hash the validated canonical recipient value
    /// (trimmed, case-preserved address; whitespace-only display name treated as absent) -- not
    /// the raw request bytes -- so equivalent-but-differently-formatted submissions hash
    /// identically. See <see cref="MailPayloadHasher"/>.
    /// </summary>
    /// <summary>
    /// <c>attachments</c> is included with a special projection (ADR 0022 D-03): when absent or
    /// an empty array it is omitted entirely from the hash document (byte-identical to the
    /// pre-attachment ADR 0012 hash); when non-empty, each element is re-projected to exactly
    /// <c>file_name</c>, <c>content_type</c>, <c>byte_length</c>, <c>content_sha256</c>, and a
    /// zero-based <c>order</c> generated from array position -- never the raw declared
    /// <c>content_base64</c> or Consumer-declared <c>content_type</c>. See
    /// <see cref="MailPayloadHasher"/>.
    /// </summary>
    public static readonly string[] IncludedFields =
    [
        "purpose",
        "to",
        "cc",
        "bcc",
        "subject",
        "html_body",
        "text_body",
        "reply_to",
        "metadata",
        "attachments",
    ];

    /// <summary>
    /// Fields excluded from payload_hash because they are routing/idempotency envelope values or self-referential.
    /// </summary>
    public static readonly string[] ExcludedFields =
    [
        "tenant_id",
        "source_service",
        "mail_request_id",
        "payload_hash",
        "scheduled_at",
    ];
}
