using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Amane.Mailer.Contracts.Json;
using Amane.Mailer.Contracts.MailRequests;

namespace Amane.Mailer.Contracts.Security;

public static class MailPayloadHasher
{
    private static readonly ISet<string> IncludedFieldSet =
        MailPayloadHashContract.IncludedFields.ToHashSet(StringComparer.Ordinal);

    public static string ComputeDeliveryPayloadSha256Hex(string requestJson) =>
        ComputeSha256Hex(BuildDeliveryPayloadJson(requestJson));

    /// <summary>
    /// Builds a delivery payload from a DTO constructed by the App before sending. Null optional fields are omitted.
    /// Use the string overload against the raw request JSON when the Mailer verifies an inbound request body.
    /// </summary>
    public static string ComputeDeliveryPayloadSha256Hex(MailRequestCreateRequest request) =>
        ComputeSha256Hex(BuildDeliveryPayloadJson(request));

    public static string BuildDeliveryPayloadJson(string requestJson)
    {
        using var document = JsonDocument.Parse(requestJson);
        if (document.RootElement.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidOperationException("Mail request JSON must be an object.");
        }

        var properties = document.RootElement.EnumerateObject()
            .Where(property => IncludedFieldSet.Contains(property.Name))
            .OrderBy(property => property.Name, StringComparer.Ordinal)
            .Select(property => $"{EscapeJsonString(property.Name)}:{Canonicalize(property.Value)}");

        return "{" + string.Join(",", properties) + "}";
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
