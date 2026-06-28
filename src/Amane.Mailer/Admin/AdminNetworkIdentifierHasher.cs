using System.Security.Cryptography;
using System.Text;

namespace Amane.Mailer.Admin;

/// <summary>
/// Keyed HMAC-SHA256 for admin network identifiers (ADR 0014 D-04 / D-06).
/// </summary>
public sealed class AdminNetworkIdentifierHasher
{
    private readonly byte[] _key;

    public AdminNetworkIdentifierHasher(byte[] key)
    {
        if (key.Length < 32)
            throw new ArgumentException("Audit identifier hash key must be at least 32 bytes.", nameof(key));

        _key = key.ToArray();
    }

    public string HashIdentifier(string normalizedIdentifier)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(normalizedIdentifier);

        Span<byte> hash = stackalloc byte[32];
        HMACSHA256.HashData(_key, Encoding.UTF8.GetBytes(normalizedIdentifier), hash);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    internal static byte[]? TryParseKey(string? rawKey)
    {
        if (string.IsNullOrWhiteSpace(rawKey))
            return null;

        var trimmed = rawKey.Trim();
        if (IsPlaceholder(trimmed))
            return null;

        try
        {
            var decoded = Convert.FromBase64String(trimmed);
            return decoded.Length >= 32 ? decoded : null;
        }
        catch (FormatException)
        {
            return null;
        }
    }

    internal static bool IsPlaceholder(string value) =>
        value.StartsWith("replace-with", StringComparison.OrdinalIgnoreCase)
        || string.Equals(value, "changeme", StringComparison.OrdinalIgnoreCase)
        || string.Equals(value, "placeholder", StringComparison.OrdinalIgnoreCase);
}
