namespace Amane.Mailer.Attachments.Validation;

/// <summary>Extension to canonical (type, Content-Type) mapping (ADR 0022 D-06).</summary>
public static class AttachmentFileTypeCatalog
{
    private static readonly Dictionary<string, (AttachmentFileType Type, string ContentType)> ByExtension =
        new(StringComparer.Ordinal)
        {
            ["pdf"] = (AttachmentFileType.Pdf, "application/pdf"),
            ["jpg"] = (AttachmentFileType.Jpeg, "image/jpeg"),
            ["jpeg"] = (AttachmentFileType.Jpeg, "image/jpeg"),
            ["png"] = (AttachmentFileType.Png, "image/png"),
            ["docx"] = (
                AttachmentFileType.Docx,
                "application/vnd.openxmlformats-officedocument.wordprocessingml.document"),
            ["xlsx"] = (
                AttachmentFileType.Xlsx,
                "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet"),
            ["csv"] = (AttachmentFileType.Csv, "text/csv"),
            ["txt"] = (AttachmentFileType.Txt, "text/plain"),
        };

    /// <summary>Returns the file extension (lowercase, no leading dot, empty if none).</summary>
    public static string ExtractExtension(string normalizedFileName)
    {
        var dotIndex = normalizedFileName.LastIndexOf('.');
        if (dotIndex < 0 || dotIndex == normalizedFileName.Length - 1)
        {
            return string.Empty;
        }

        return normalizedFileName[(dotIndex + 1)..].ToLowerInvariant();
    }

    public static bool TryGetByExtension(
        string extensionLower,
        out AttachmentFileType type,
        out string canonicalContentType)
    {
        if (ByExtension.TryGetValue(extensionLower, out var entry))
        {
            type = entry.Type;
            canonicalContentType = entry.ContentType;
            return true;
        }

        type = default;
        canonicalContentType = string.Empty;
        return false;
    }

    public static bool DeclaredContentTypeMatches(AttachmentFileType type, string? declaredContentType)
    {
        if (string.IsNullOrWhiteSpace(declaredContentType))
        {
            return false;
        }

        foreach (var entry in ByExtension.Values)
        {
            if (entry.Type == type)
            {
                return string.Equals(
                    entry.ContentType,
                    declaredContentType.Trim(),
                    StringComparison.OrdinalIgnoreCase);
            }
        }

        return false;
    }
}
