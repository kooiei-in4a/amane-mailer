using System.Text;

namespace Amane.Mailer.Attachments.Validation;

/// <summary>
/// Minimal structural PDF validation (ADR 0022 D-06): signature, trailer/xref presence,
/// encryption rejection, and "only ASCII whitespace after the last %%EOF" (rejects trailing
/// payload / polyglot files).
/// </summary>
public static class PdfStructureValidator
{
    private static readonly byte[] Signature = "%PDF-"u8.ToArray();
    private static readonly byte[] EofMarker = "%%EOF"u8.ToArray();
    private static readonly byte[] EncryptToken = "/Encrypt"u8.ToArray();
    private static readonly byte[] StartXrefToken = "startxref"u8.ToArray();
    private static readonly byte[] TrailerToken = "trailer"u8.ToArray();
    private static readonly byte[] XrefStreamToken = "/XRefStm"u8.ToArray();

    public static AttachmentStructureResult Validate(ReadOnlySpan<byte> content)
    {
        if (content.Length < Signature.Length || !content[..Signature.Length].SequenceEqual(Signature))
        {
            return AttachmentStructureResult.ContentMismatch();
        }

        var lastEof = content.LastIndexOf(EofMarker);
        if (lastEof < 0)
        {
            return AttachmentStructureResult.ContentMismatch();
        }

        var afterEof = content[(lastEof + EofMarker.Length)..];
        foreach (var b in afterEof)
        {
            if (b is not (0x09 or 0x0A or 0x0D or 0x20))
            {
                // Trailing payload / polyglot content after the terminal %%EOF.
                return AttachmentStructureResult.ContentMismatch();
            }
        }

        if (content.IndexOf(StartXrefToken) < 0)
        {
            return AttachmentStructureResult.ContentMismatch();
        }

        // Either a classic trailer dictionary or a cross-reference stream (/Type /XRef, which
        // carries /XRefStm in hybrid files, or is itself referenced solely via startxref).
        var hasClassicTrailer = content.IndexOf(TrailerToken) >= 0;
        var hasXrefStream = content.IndexOf(XrefStreamToken) >= 0 || content.IndexOf("/Type/XRef"u8) >= 0
            || content.IndexOf("/Type /XRef"u8) >= 0;
        if (!hasClassicTrailer && !hasXrefStream)
        {
            return AttachmentStructureResult.ContentMismatch();
        }

        if (content.IndexOf(EncryptToken) >= 0)
        {
            return AttachmentStructureResult.Encrypted();
        }

        return AttachmentStructureResult.Valid();
    }
}
