namespace Mailer.Contracts.Security;

public static class MailPayloadHashContract
{
    /// <summary>
    /// Fields included in payload_hash. The hash document is the delivery payload, not the routing envelope.
    /// </summary>
    public static readonly string[] IncludedFields =
    [
        "source_service",
        "purpose",
        "to",
        "subject",
        "html_body",
        "text_body",
        "reply_to",
        "metadata",
    ];

    /// <summary>
    /// Fields excluded from payload_hash because they are routing/idempotency envelope values or self-referential.
    /// </summary>
    public static readonly string[] ExcludedFields =
    [
        "tenant_id",
        "mail_request_id",
        "payload_hash",
    ];
}
