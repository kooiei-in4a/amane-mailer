using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace Amane.Mailer.Setup;

/// <summary>
/// Owner-only runtime-identity binding stamp. Public surfaces see enum only.
/// </summary>
public sealed class SetupRuntimeIdentityBindingStamp
{
    public const int CurrentSchemaVersion = 1;
    private static readonly byte[] DomainPrefix = "amane-runtime-identity-v1"u8.ToArray();

    public required int SchemaVersion { get; init; }
    public required string BundleId { get; init; }
    public required long ActivationGeneration { get; init; }
    public required string BindingMac { get; init; }

    public static string ComputeBindingMac(
        ReadOnlySpan<byte> sealingKey,
        string normalizedDataPath,
        string? normalizedConnectionString)
    {
        var derivedKey = DeriveBindingKey(sealingKey);
        try
        {
            using var hmac = new HMACSHA256(derivedKey);
            hmac.TransformBlock(DomainPrefix, 0, DomainPrefix.Length, null, 0);
            TransformNul(hmac);
            TransformUtf8(hmac, normalizedDataPath);
            TransformNul(hmac);
            if (!string.IsNullOrEmpty(normalizedConnectionString))
            {
                TransformUtf8(hmac, normalizedConnectionString);
            }

            TransformNul(hmac);
            hmac.TransformFinalBlock([], 0, 0);
            var mac = hmac.Hash ?? throw new CryptographicException("HMAC computation failed.");
            return Convert.ToHexString(mac).ToLowerInvariant();
        }
        finally
        {
            CryptographicOperations.ZeroMemory(derivedKey);
        }
    }

    public static byte[] DeriveBindingKey(ReadOnlySpan<byte> sealingKey)
    {
        // HKDF-Expand style: HMAC(sealingKey, info || 0x01)
        using var hmac = new HMACSHA256(sealingKey.ToArray());
        hmac.TransformBlock(DomainPrefix, 0, DomainPrefix.Length, null, 0);
        hmac.TransformFinalBlock([0x01], 0, 1);
        return hmac.Hash ?? throw new CryptographicException("Key derivation failed.");
    }

    private static void TransformUtf8(HMACSHA256 hmac, string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        hmac.TransformBlock(bytes, 0, bytes.Length, null, 0);
        CryptographicOperations.ZeroMemory(bytes);
    }

    private static void TransformNul(HMACSHA256 hmac) =>
        hmac.TransformBlock([0], 0, 1, null, 0);
}

public static class SetupRuntimeIdentityBindingResult
{
    public const string Matched = "matched";
    public const string Mismatch = "mismatch";
    public const string Missing = "missing";
}
