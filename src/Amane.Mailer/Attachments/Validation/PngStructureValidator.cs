using System.Buffers.Binary;

namespace Amane.Mailer.Attachments.Validation;

/// <summary>
/// PNG chunk-structure validation (ADR 0022 D-06): signature, chunk length/CRC, IHDR-first,
/// IEND-last, and no trailing payload.
/// </summary>
public static class PngStructureValidator
{
    private static readonly byte[] Signature = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

    public static AttachmentStructureResult Validate(ReadOnlySpan<byte> content)
    {
        if (content.Length < Signature.Length || !content[..Signature.Length].SequenceEqual(Signature))
        {
            return AttachmentStructureResult.ContentMismatch();
        }

        var offset = Signature.Length;
        var isFirstChunk = true;

        while (true)
        {
            if (offset + 8 > content.Length)
            {
                return AttachmentStructureResult.ContentMismatch();
            }

            var length = BinaryPrimitives.ReadUInt32BigEndian(content.Slice(offset, 4));
            var type = content.Slice(offset + 4, 4);

            if (isFirstChunk && !type.SequenceEqual("IHDR"u8))
            {
                return AttachmentStructureResult.ContentMismatch();
            }

            isFirstChunk = false;

            if (length > int.MaxValue || offset + 12L + length > content.Length)
            {
                return AttachmentStructureResult.ContentMismatch();
            }

            var data = content.Slice(offset + 8, (int)length);
            var declaredCrc = BinaryPrimitives.ReadUInt32BigEndian(content.Slice(offset + 8 + (int)length, 4));
            var actualCrc = ComputeCrc32(type, data);
            if (declaredCrc != actualCrc)
            {
                return AttachmentStructureResult.ContentMismatch();
            }

            offset += 12 + (int)length;

            if (type.SequenceEqual("IEND"u8))
            {
                return offset == content.Length
                    ? AttachmentStructureResult.Valid()
                    : AttachmentStructureResult.ContentMismatch();
            }
        }
    }

    private static uint ComputeCrc32(ReadOnlySpan<byte> type, ReadOnlySpan<byte> data)
    {
        var crc = 0xFFFFFFFFu;
        crc = UpdateCrc32(crc, type);
        crc = UpdateCrc32(crc, data);
        return crc ^ 0xFFFFFFFFu;
    }

    private static uint UpdateCrc32(uint crc, ReadOnlySpan<byte> bytes)
    {
        foreach (var b in bytes)
        {
            crc = Crc32Table[(crc ^ b) & 0xFF] ^ (crc >> 8);
        }

        return crc;
    }

    private static readonly uint[] Crc32Table = BuildCrc32Table();

    private static uint[] BuildCrc32Table()
    {
        var table = new uint[256];
        for (uint n = 0; n < 256; n++)
        {
            var c = n;
            for (var k = 0; k < 8; k++)
            {
                c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
            }

            table[n] = c;
        }

        return table;
    }
}
