using System.Security.Cryptography;
using System.Text;

namespace Amane.Mailer.Setup;

/// <summary>
/// Host at-rest integrity seal (ADR 0021 D-04). Opaque seal bytes must never appear in UI,
/// logs, stdout/stderr, dry-run plans, or public results. Mount attestation belongs to #447.
/// </summary>
public static class SetupIntegritySealer
{
    public const byte SealVersion = 1;
    private static readonly byte[] MagicBytes = "AMIS1"u8.ToArray();
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
        IReadOnlyList<(string RelativePath, byte[] Content)> secretMembers)
    {
        using var hmac = new HMACSHA256(sealingKey.ToArray());
        foreach (var member in secretMembers.OrderBy(m => m.RelativePath, StringComparer.Ordinal))
        {
            var pathBytes = Encoding.UTF8.GetBytes(member.RelativePath);
            hmac.TransformBlock(pathBytes, 0, pathBytes.Length, null, 0);
            hmac.TransformBlock([0], 0, 1, null, 0);
            hmac.TransformBlock(member.Content, 0, member.Content.Length, null, 0);
            hmac.TransformBlock([0], 0, 1, null, 0);
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

        var expected = CreateSeal(sealingKey, secretMembers);
        try
        {
            return CryptographicOperations.FixedTimeEquals(expected, seal);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(expected);
        }
    }
}
