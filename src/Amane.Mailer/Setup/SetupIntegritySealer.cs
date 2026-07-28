using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace Amane.Mailer.Setup;

/// <summary>
/// Host at-rest integrity seal (ADR 0021 D-04). Opaque seal bytes must never appear in UI,
/// logs, stdout/stderr, dry-run plans, or public results. Mount attestation belongs to #447.
/// MAC input is domain-separated by bundle identity so seals cannot be moved across bundles.
/// </summary>
public static class SetupIntegritySealer
{
    public const byte SealVersion = 1;
    private static readonly byte[] MagicBytes = "AMIS1"u8.ToArray();
    private static readonly byte[] DomainPrefix = "AMANE-AT-REST-V1"u8.ToArray();
    public static int MagicLength => MagicBytes.Length;
    public const int SealingKeyLength = 32;
    public const int MacLength = 32;

    public static byte[] CreateSealingKey()
    {
        var key = new byte[SealingKeyLength];
        RandomNumberGenerator.Fill(key);
        return key;
    }

    public static byte[] CreateSeal(
        ReadOnlySpan<byte> sealingKey,
        string bundleId,
        string configurationFingerprint,
        int schemaVersion,
        IReadOnlyList<(string RelativePath, byte[] Content)> secretMembers)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(bundleId);
        ArgumentException.ThrowIfNullOrWhiteSpace(configurationFingerprint);

        using var hmac = new HMACSHA256(sealingKey.ToArray());
        hmac.TransformBlock(DomainPrefix, 0, DomainPrefix.Length, null, 0);
        TransformNul(hmac);
        TransformUtf8(hmac, bundleId);
        TransformNul(hmac);
        TransformUtf8(hmac, configurationFingerprint);
        TransformNul(hmac);
        TransformUtf8(hmac, schemaVersion.ToString(CultureInfo.InvariantCulture));
        TransformNul(hmac);

        var ordered = secretMembers
            .OrderBy(m => m.RelativePath, StringComparer.Ordinal)
            .ToArray();

        // Complete member path manifest before contents (copy-across-bundle detection).
        foreach (var member in ordered)
        {
            TransformUtf8(hmac, member.RelativePath);
            TransformNul(hmac);
        }

        TransformNul(hmac);

        foreach (var member in ordered)
        {
            TransformUtf8(hmac, member.RelativePath);
            TransformNul(hmac);
            hmac.TransformBlock(member.Content, 0, member.Content.Length, null, 0);
            TransformNul(hmac);
        }

        hmac.TransformFinalBlock([], 0, 0);
        var mac = hmac.Hash ?? throw new CryptographicException("HMAC computation failed.");

        var seal = new byte[MagicBytes.Length + 1 + MacLength];
        MagicBytes.CopyTo(seal, 0);
        seal[MagicBytes.Length] = SealVersion;
        Buffer.BlockCopy(mac, 0, seal, MagicBytes.Length + 1, MacLength);
        return seal;
    }

    public static bool TryVerifySeal(
        ReadOnlySpan<byte> sealingKey,
        ReadOnlySpan<byte> seal,
        string bundleId,
        string configurationFingerprint,
        int schemaVersion,
        IReadOnlyList<(string RelativePath, byte[] Content)> secretMembers)
    {
        if (seal.Length != MagicBytes.Length + 1 + MacLength)
        {
            return false;
        }

        if (!seal[..MagicBytes.Length].SequenceEqual(MagicBytes))
        {
            return false;
        }

        if (seal[MagicBytes.Length] != SealVersion)
        {
            return false;
        }

        var expected = CreateSeal(
            sealingKey,
            bundleId,
            configurationFingerprint,
            schemaVersion,
            secretMembers);
        try
        {
            return CryptographicOperations.FixedTimeEquals(expected, seal);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(expected);
        }
    }

    private static void TransformUtf8(HMACSHA256 hmac, string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        hmac.TransformBlock(bytes, 0, bytes.Length, null, 0);
    }

    private static void TransformNul(HMACSHA256 hmac) =>
        hmac.TransformBlock([0], 0, 1, null, 0);
}
