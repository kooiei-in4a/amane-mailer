using System.IO.Compression;

namespace Amane.Mailer.Attachments.Validation;

/// <summary>
/// DOCX/XLSX (Office Open XML) ZIP-structure validation (ADR 0022 D-06): well-formed ZIP,
/// required parts, entry/size caps, path-traversal rejection, and macro rejection.
/// </summary>
public static class OfficeOpenXmlStructureValidator
{
    private const int MaxEntryCount = 1024;
    private const long MaxTotalUncompressedBytes = 32L * 1024 * 1024;
    private const long MaxSingleEntryUncompressedBytes = 16L * 1024 * 1024;

    public static AttachmentStructureResult Validate(byte[] content, AttachmentFileType type)
    {
        var requiredPart = type == AttachmentFileType.Docx ? "word/document.xml" : "xl/workbook.xml";

        try
        {
            using var stream = new MemoryStream(content, writable: false);
            using var archive = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: true);

            if (archive.Entries.Count > MaxEntryCount)
            {
                return AttachmentStructureResult.ContentMismatch();
            }

            var hasContentTypes = false;
            var hasRequiredPart = false;
            long totalUncompressed = 0;

            foreach (var entry in archive.Entries)
            {
                if (!IsSafeEntryName(entry.FullName))
                {
                    return AttachmentStructureResult.ContentMismatch();
                }

                if (entry.Length > MaxSingleEntryUncompressedBytes)
                {
                    return AttachmentStructureResult.ContentMismatch();
                }

                totalUncompressed += entry.Length;
                if (totalUncompressed > MaxTotalUncompressedBytes)
                {
                    return AttachmentStructureResult.ContentMismatch();
                }

                if (string.Equals(entry.FullName, "[Content_Types].xml", StringComparison.Ordinal))
                {
                    hasContentTypes = true;
                }

                if (string.Equals(entry.FullName, requiredPart, StringComparison.Ordinal))
                {
                    hasRequiredPart = true;
                }

                if (entry.FullName.EndsWith("vbaProject.bin", StringComparison.OrdinalIgnoreCase))
                {
                    return AttachmentStructureResult.TypeNotAllowed();
                }
            }

            if (!hasContentTypes || !hasRequiredPart)
            {
                return AttachmentStructureResult.ContentMismatch();
            }

            return AttachmentStructureResult.Valid();
        }
        catch (InvalidDataException)
        {
            return AttachmentStructureResult.ContentMismatch();
        }
        catch (NotSupportedException)
        {
            return AttachmentStructureResult.ContentMismatch();
        }
    }

    private static bool IsSafeEntryName(string entryName)
    {
        if (string.IsNullOrEmpty(entryName))
        {
            return false;
        }

        if (entryName.Contains('\0', StringComparison.Ordinal))
        {
            return false;
        }

        if (Path.IsPathRooted(entryName) || entryName.StartsWith('/') || entryName.StartsWith('\\'))
        {
            return false;
        }

        // Reject traversal via any ".." path segment, forward- or back-slash separated.
        var segments = entryName.Split(['/', '\\'], StringSplitOptions.None);
        return Array.TrueForAll(segments, static segment => segment != "..");
    }
}
