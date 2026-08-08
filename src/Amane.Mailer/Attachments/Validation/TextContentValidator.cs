using System.Text;

namespace Amane.Mailer.Attachments.Validation;

/// <summary>
/// CSV/TXT content validation (ADR 0022 D-07): strict UTF-8 (BOM allowed and stripped), NUL and
/// non-TAB/CR/LF C0-control/DEL rejection, 64 KiB max line length, and (CSV only) balanced
/// quote/record structure. Does not validate business column schema.
/// </summary>
public static class TextContentValidator
{
    private const int MaxLineUtf8Bytes = 64 * 1024;

    private static readonly byte[] Utf8Bom = [0xEF, 0xBB, 0xBF];

    // Reject invalid UTF-8 instead of substituting U+FFFD, matching MailRequestRequestReader's
    // strict body-decoding policy (#343).
    private static readonly UTF8Encoding StrictUtf8 = new(
        encoderShouldEmitUTF8Identifier: false,
        throwOnInvalidBytes: true);

    public static AttachmentStructureResult ValidateTxt(byte[] content) => ValidateCommon(content, isCsv: false);

    public static AttachmentStructureResult ValidateCsv(byte[] content) => ValidateCommon(content, isCsv: true);

    private static AttachmentStructureResult ValidateCommon(byte[] content, bool isCsv)
    {
        var span = (ReadOnlySpan<byte>)content;
        if (span.Length >= Utf8Bom.Length && span[..Utf8Bom.Length].SequenceEqual(Utf8Bom))
        {
            span = span[Utf8Bom.Length..];
        }

        string text;
        try
        {
            text = StrictUtf8.GetString(span);
        }
        catch (DecoderFallbackException)
        {
            return AttachmentStructureResult.ContentMismatch();
        }

        foreach (var ch in text)
        {
            if (ch is '\t' or '\n' or '\r')
            {
                continue;
            }

            if (ch == '\0' || ch < 0x20 || ch == 0x7F)
            {
                return AttachmentStructureResult.ContentMismatch();
            }
        }

        if (HasOverlongLine(text))
        {
            return AttachmentStructureResult.ContentMismatch();
        }

        if (isCsv && !HasWellFormedCsvQuoting(text))
        {
            return AttachmentStructureResult.ContentMismatch();
        }

        return AttachmentStructureResult.Valid();
    }

    private static bool HasOverlongLine(string text)
    {
        var lineStart = 0;
        for (var i = 0; i <= text.Length; i++)
        {
            if (i != text.Length && text[i] != '\n')
            {
                continue;
            }

            var line = text.AsSpan(lineStart, i - lineStart);
            if (line.Length > 0 && line[^1] == '\r')
            {
                line = line[..^1];
            }

            if (Encoding.UTF8.GetByteCount(line) > MaxLineUtf8Bytes)
            {
                return true;
            }

            lineStart = i + 1;
        }

        return false;
    }

    private static bool HasWellFormedCsvQuoting(string text)
    {
        var inQuotes = false;
        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] != '"')
            {
                continue;
            }

            if (!inQuotes)
            {
                inQuotes = true;
                continue;
            }

            if (i + 1 < text.Length && text[i + 1] == '"')
            {
                i++; // escaped quote inside a quoted field
                continue;
            }

            inQuotes = false;
        }

        // An unterminated quoted field at EOF is malformed.
        return !inQuotes;
    }
}
