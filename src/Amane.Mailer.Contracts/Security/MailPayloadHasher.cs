using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Amane.Mailer.Contracts.Json;
using Amane.Mailer.Contracts.MailRequests;

namespace Amane.Mailer.Contracts.Security;

public static class MailPayloadHasher
{
    private const string AttachmentsFieldName = "attachments";

    private static readonly ISet<string> IncludedFieldSet =
        MailPayloadHashContract.IncludedFields.ToHashSet(StringComparer.Ordinal);

    public static string ComputeDeliveryPayloadSha256Hex(
        string requestJson,
        IReadOnlyList<MailAttachmentHashInput>? attachments = null) =>
        ComputeSha256Hex(BuildDeliveryPayloadJson(requestJson, attachments));

    /// <summary>
    /// Builds a delivery payload from a DTO constructed by the App before sending. Null optional fields are omitted.
    /// Use the string overload against the raw request JSON when the Mailer verifies an inbound request body.
    /// </summary>
    public static string ComputeDeliveryPayloadSha256Hex(MailRequestCreateRequest request) =>
        ComputeSha256Hex(BuildDeliveryPayloadJson(request));

    /// <summary>
    /// Builds the canonical hash document from raw request JSON. The <c>attachments</c> JSON
    /// property itself is never used for hashing (it carries content_base64 and an unverified
    /// declared content_type): callers that need attachments reflected in the hash must pass the
    /// Mailer-verified <paramref name="attachments"/> list (ADR 0022 D-03/D-04 step 12), computed
    /// from decoded binaries after validation. A null or empty list omits <c>attachments</c> from
    /// the hash document entirely, matching the pre-attachment ADR 0012 hash.
    /// </summary>
    public static string BuildDeliveryPayloadJson(
        string requestJson,
        IReadOnlyList<MailAttachmentHashInput>? attachments = null)
    {
        using var document = JsonDocument.Parse(requestJson);
        if (document.RootElement.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidOperationException("Mail request JSON must be an object.");
        }

        var properties = document.RootElement.EnumerateObject()
            .Where(property =>
                IncludedFieldSet.Contains(property.Name)
                && !string.Equals(property.Name, AttachmentsFieldName, StringComparison.Ordinal))
            .Select(property => (property.Name, CanonicalValue: Canonicalize(property.Value)))
            .ToList();

        if (attachments is { Count: > 0 })
        {
            properties.Add((AttachmentsFieldName, CanonicalizeAttachments(attachments)));
        }

        return "{" + string.Join(
            ",",
            properties
                .OrderBy(property => property.Name, StringComparer.Ordinal)
                .Select(property => $"{EscapeJsonString(property.Name)}:{property.CanonicalValue}")) + "}";
    }

    /// <summary>
    /// Builds a delivery payload from a DTO constructed by the App before sending. Null optional fields are omitted.
    /// Use the string overload against the raw request JSON when the Mailer verifies an inbound request body.
    /// </summary>
    public static string BuildDeliveryPayloadJson(MailRequestCreateRequest request)
    {
        var properties = new List<(string Name, string CanonicalValue)>
        {
            ("source_service", EscapeJsonString(request.SourceService)),
            ("purpose", EscapeJsonString(request.Purpose)),
            ("to", CanonicalizeRecipients(
                request.To as MailRecipientDto[] ?? request.To.ToArray())),
            ("subject", EscapeJsonString(request.Subject)),
        };

        if (request.HtmlBody is not null)
        {
            properties.Add(("html_body", EscapeJsonString(request.HtmlBody)));
        }

        if (request.TextBody is not null)
        {
            properties.Add(("text_body", EscapeJsonString(request.TextBody)));
        }

        if (request.ReplyTo is not null)
        {
            properties.Add(("reply_to", EscapeJsonString(request.ReplyTo)));
        }

        if (request.Metadata is not null)
        {
            properties.Add(("metadata", CanonicalizeMetadata(request.Metadata)));
        }

        if (request.Attachments is { Count: > 0 })
        {
            var attachmentInputs = request.Attachments
                .Select(attachment => new MailAttachmentHashInput(
                    attachment.FileName,
                    attachment.ContentType,
                    attachment.ByteLength,
                    attachment.ContentSha256))
                .ToArray();
            properties.Add((AttachmentsFieldName, CanonicalizeAttachments(attachmentInputs)));
        }

        return "{" + string.Join(
            ",",
            properties
                .OrderBy(property => property.Name, StringComparer.Ordinal)
                .Select(property => $"{EscapeJsonString(property.Name)}:{property.CanonicalValue}")) + "}";
    }

    public static string ComputeSha256Hex(string json)
    {
        using var document = JsonDocument.Parse(json);
        var canonicalJson = Canonicalize(document.RootElement);
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(canonicalJson));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    public static string Canonicalize(string json)
    {
        using var document = JsonDocument.Parse(json);
        return Canonicalize(document.RootElement);
    }

    private static string Canonicalize(JsonElement element) =>
        element.ValueKind switch
        {
            JsonValueKind.Object => CanonicalizeObject(element),
            JsonValueKind.Array => CanonicalizeArray(element),
            JsonValueKind.String => EscapeJsonString(element.GetString() ?? string.Empty),
            JsonValueKind.Number => CanonicalizeNumber(element),
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            JsonValueKind.Null => "null",
            _ => throw new InvalidOperationException($"Unsupported JSON value kind: {element.ValueKind}"),
        };

    private static string CanonicalizeObject(JsonElement element)
    {
        var properties = element.EnumerateObject()
            .OrderBy(property => property.Name, StringComparer.Ordinal)
            .Select(property => $"{EscapeJsonString(property.Name)}:{Canonicalize(property.Value)}");

        return "{" + string.Join(",", properties) + "}";
    }

    private static string CanonicalizeArray(JsonElement element) =>
        "[" + string.Join(",", element.EnumerateArray().Select(Canonicalize)) + "]";

    private static string CanonicalizeNumber(JsonElement element)
    {
        // Mail payload values are strings, arrays, objects, booleans, or null. This numeric path is kept for
        // JSON completeness, but it is not intended to be a full ECMAScript Number.toString implementation.
        if (element.TryGetInt64(out var integer))
        {
            return integer.ToString(CultureInfo.InvariantCulture);
        }

        if (element.TryGetDecimal(out var decimalValue))
        {
            return decimalValue.ToString("G29", CultureInfo.InvariantCulture);
        }

        return element.GetDouble().ToString("G17", CultureInfo.InvariantCulture);
    }

    private static string CanonicalizeRecipients(MailRecipientDto[] value)
    {
        using var document = JsonDocument.Parse(
            JsonSerializer.Serialize(value, MailerContractsJsonContext.Default.MailRecipientDtoArray));
        return Canonicalize(document.RootElement);
    }

    /// <summary>
    /// Projects verified attachment values to the fixed 5-field hash object (ADR 0022 D-03):
    /// file_name (NFC), content_type (D-06 canonical), byte_length, content_sha256 (lowercase
    /// hex), and a zero-based order generated from list position. Object keys are sorted
    /// ordinally per field, matching RFC 8785 JCS key ordering.
    /// </summary>
    private static string CanonicalizeAttachments(IReadOnlyList<MailAttachmentHashInput> attachments)
    {
        var items = attachments.Select((attachment, index) =>
        {
            var fields = new (string Name, string Value)[]
            {
                ("byte_length", attachment.ByteLength.ToString(CultureInfo.InvariantCulture)),
                ("content_sha256", EscapeJsonString(attachment.ContentSha256)),
                ("content_type", EscapeJsonString(attachment.ContentType)),
                ("file_name", EscapeJsonString(attachment.FileName.Normalize(NormalizationForm.FormC))),
                ("order", index.ToString(CultureInfo.InvariantCulture)),
            };

            return "{" + string.Join(
                ",",
                fields
                    .OrderBy(field => field.Name, StringComparer.Ordinal)
                    .Select(field => $"{EscapeJsonString(field.Name)}:{field.Value}")) + "}";
        });

        return "[" + string.Join(",", items) + "]";
    }

    private static string CanonicalizeMetadata(IReadOnlyDictionary<string, string> value)
    {
        var metadata = value as Dictionary<string, string> ?? new Dictionary<string, string>(value);
        using var document = JsonDocument.Parse(
            JsonSerializer.Serialize(metadata, MailerContractsJsonContext.Default.DictionaryStringString));
        return Canonicalize(document.RootElement);
    }

    private static string EscapeJsonString(string value)
    {
        var builder = new StringBuilder(value.Length + 2);
        builder.Append('"');

        foreach (var character in value)
        {
            switch (character)
            {
                case '"':
                    builder.Append("\\\"");
                    break;
                case '\\':
                    builder.Append("\\\\");
                    break;
                case '\b':
                    builder.Append("\\b");
                    break;
                case '\f':
                    builder.Append("\\f");
                    break;
                case '\n':
                    builder.Append("\\n");
                    break;
                case '\r':
                    builder.Append("\\r");
                    break;
                case '\t':
                    builder.Append("\\t");
                    break;
                default:
                    if (character < 0x20)
                    {
                        builder.Append("\\u");
                        builder.Append(((int)character).ToString("x4", CultureInfo.InvariantCulture));
                    }
                    else
                    {
                        builder.Append(character);
                    }

                    break;
            }
        }

        builder.Append('"');
        return builder.ToString();
    }
}
