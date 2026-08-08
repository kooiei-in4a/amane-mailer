namespace Amane.Mailer.Attachments.Validation;

/// <summary>
/// JPEG marker-structure validation (ADR 0022 D-06): SOI, well-formed marker segments, EOI,
/// and no trailing payload after EOI.
/// </summary>
public static class JpegStructureValidator
{
    private const byte MarkerPrefix = 0xFF;
    private const byte Soi = 0xD8;
    private const byte Eoi = 0xD9;
    private const byte Sos = 0xDA;

    public static AttachmentStructureResult Validate(ReadOnlySpan<byte> content)
    {
        if (content.Length < 4 || content[0] != MarkerPrefix || content[1] != Soi)
        {
            return AttachmentStructureResult.ContentMismatch();
        }

        var offset = 2;
        while (true)
        {
            if (offset + 1 >= content.Length || content[offset] != MarkerPrefix)
            {
                return AttachmentStructureResult.ContentMismatch();
            }

            // Skip fill bytes (0xFF padding between markers).
            var markerOffset = offset + 1;
            while (markerOffset < content.Length && content[markerOffset] == MarkerPrefix)
            {
                markerOffset++;
            }

            if (markerOffset >= content.Length)
            {
                return AttachmentStructureResult.ContentMismatch();
            }

            var marker = content[markerOffset];
            offset = markerOffset + 1;

            if (marker == Eoi)
            {
                // No trailing payload permitted after EOI.
                return offset == content.Length
                    ? AttachmentStructureResult.Valid()
                    : AttachmentStructureResult.ContentMismatch();
            }

            // Standalone markers with no length/payload: TEM (0x01) and RST0-RST7 (0xD0-0xD7).
            if (marker == 0x01 || marker is >= 0xD0 and <= 0xD7)
            {
                continue;
            }

            if (offset + 1 >= content.Length)
            {
                return AttachmentStructureResult.ContentMismatch();
            }

            var segmentLength = (content[offset] << 8) | content[offset + 1];
            if (segmentLength < 2 || offset + segmentLength > content.Length)
            {
                return AttachmentStructureResult.ContentMismatch();
            }

            offset += segmentLength;

            if (marker == Sos)
            {
                offset = SkipEntropyCodedData(content, offset);
                if (offset < 0)
                {
                    return AttachmentStructureResult.ContentMismatch();
                }
            }
        }
    }

    /// <summary>
    /// Advances past entropy-coded scan data following SOS, respecting 0xFF 0x00 byte stuffing
    /// and RST markers (which do not terminate the scan), stopping at the next real marker.
    /// </summary>
    private static int SkipEntropyCodedData(ReadOnlySpan<byte> content, int offset)
    {
        while (offset < content.Length)
        {
            if (content[offset] != MarkerPrefix)
            {
                offset++;
                continue;
            }

            if (offset + 1 >= content.Length)
            {
                return -1;
            }

            var next = content[offset + 1];
            if (next == 0x00 || (next >= 0xD0 && next <= 0xD7))
            {
                // Stuffed literal 0xFF or a restart marker: still scan data, keep scanning.
                offset += 2;
                continue;
            }

            if (next == MarkerPrefix)
            {
                // Fill byte; re-check at the next position.
                offset++;
                continue;
            }

            // A genuine marker: hand control back to the outer loop.
            return offset;
        }

        return -1;
    }
}
