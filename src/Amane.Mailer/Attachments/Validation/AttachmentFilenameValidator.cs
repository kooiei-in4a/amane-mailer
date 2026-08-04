using System.Buffers;
using System.Text;

namespace Amane.Mailer.Attachments.Validation;

/// <summary>
/// Filename contract (ADR 0022 D-05). <c>file_name</c> is never used as a path component;
/// this validator only decides acceptance and produces the NFC-normalized canonical value used
/// for storage, display, and the payload_hash projection.
/// </summary>
public static class AttachmentFilenameValidator
{
    private const int MinUtf8Bytes = 1;
    private const int MaxUtf8Bytes = 255;

    private static readonly SearchValues<char> DisallowedCharacters =
        SearchValues.Create(['/', '\\', '\0']);

    private static readonly string[] WindowsReservedBaseNames =
    [
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
    ];

    public static bool TryValidate(string? rawFileName, out string normalizedFileName)
    {
        normalizedFileName = string.Empty;

        if (string.IsNullOrEmpty(rawFileName))
        {
            return false;
        }

        var normalized = rawFileName.Normalize(NormalizationForm.FormC);

        if (normalized is "." or "..")
        {
            return false;
        }

        foreach (var character in normalized)
        {
            if (DisallowedCharacters.Contains(character) || char.IsControl(character))
            {
                return false;
            }
        }

        if (normalized[^1] is '.' or ' ')
        {
            return false;
        }

        var utf8ByteCount = Encoding.UTF8.GetByteCount(normalized);
        if (utf8ByteCount is < MinUtf8Bytes or > MaxUtf8Bytes)
        {
            return false;
        }

        if (IsWindowsReservedName(normalized))
        {
            return false;
        }

        normalizedFileName = normalized;
        return true;
    }

    /// <summary>
    /// Case-insensitive duplicate check across the attachments of a single request (D-05).
    /// Compares NFC-normalized filenames; array order (not filename) determines submission order.
    /// </summary>
    public static bool HasCaseInsensitiveDuplicate(IReadOnlyList<string> normalizedFileNames)
    {
        var seen = new HashSet<string>(normalizedFileNames.Count, StringComparer.OrdinalIgnoreCase);
        foreach (var fileName in normalizedFileNames)
        {
            if (!seen.Add(fileName))
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsWindowsReservedName(string normalized)
    {
        // Windows treats "CON", "CON.txt", "com1.tar.gz", etc. as reserved: the base name up to
        // the first '.' is what matters, matched case-insensitively.
        var dotIndex = normalized.IndexOf('.');
        var baseName = dotIndex < 0 ? normalized : normalized[..dotIndex];

        foreach (var reserved in WindowsReservedBaseNames)
        {
            if (string.Equals(baseName, reserved, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }
}
