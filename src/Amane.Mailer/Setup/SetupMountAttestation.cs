using System.Security.Cryptography;
using System.Text;
using Amane.Mailer.Operations;

namespace Amane.Mailer.Setup;

/// <summary>
/// Container-side ephemeral mount attestation (ADR 0021 D-04 step 2).
/// Host generates expected MACs; one-shot recomputes from actually mounted bytes.
/// </summary>
public static class SetupMountAttestation
{
    public const int SessionKeyLength = 32;
    public const int MacLength = 32;
    private static readonly byte[] DomainPrefix = "AMANE-MOUNT-ATTEST-V1"u8.ToArray();

    public const string AcsConnectionStringMemberId =
        $"{SetupBundleLayout.SecretsDirectoryName}/{AcsSecretFileNames.CanonicalFileName}";

    public static string EnvMemberId(string envKey) => "env:" + envKey;

    public static byte[] CreateSessionKey()
    {
        var key = new byte[SessionKeyLength];
        RandomNumberGenerator.Fill(key);
        return key;
    }

    public static byte[] ComputeMac(
        ReadOnlySpan<byte> sessionKey,
        string memberId,
        ReadOnlySpan<byte> content)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(memberId);

        using var hmac = new HMACSHA256(sessionKey.ToArray());
        hmac.TransformBlock(DomainPrefix, 0, DomainPrefix.Length, null, 0);
        TransformNul(hmac);
        TransformUtf8(hmac, memberId);
        TransformNul(hmac);
        hmac.TransformBlock(content.ToArray(), 0, content.Length, null, 0);
        hmac.TransformFinalBlock([], 0, 0);
        return hmac.Hash ?? throw new CryptographicException("HMAC computation failed.");
    }

    public static bool FixedTimeEqualsMac(ReadOnlySpan<byte> expected, ReadOnlySpan<byte> actual) =>
        expected.Length == actual.Length
        && expected.Length == MacLength
        && CryptographicOperations.FixedTimeEquals(expected, actual);

    public static SetupInspectAttestationSummary Verify(
        SetupMountVerifierDocument verifier,
        string expectedBundleId,
        Func<string, byte[]?> resolveMemberBytes,
        DateTimeOffset utcNow)
    {
        if (verifier.SchemaVersion != SetupMountVerifierDocument.CurrentSchemaVersion)
        {
            return Attestation(SetupInspectIntegrityResult.NotVerified, SetupInspectReason.VerifierMalformed);
        }

        if (!string.Equals(verifier.BundleId, expectedBundleId, StringComparison.Ordinal))
        {
            return Attestation(SetupInspectIntegrityResult.Mismatch, SetupInspectReason.VerifierBundleMismatch);
        }

        if (verifier.ExpiresAtUnix <= utcNow.ToUnixTimeSeconds())
        {
            return Attestation(SetupInspectIntegrityResult.NotVerified, SetupInspectReason.VerifierExpired);
        }

        byte[]? sessionKey = null;
        try
        {
            sessionKey = Convert.FromBase64String(verifier.SessionKey);
            if (sessionKey.Length != SessionKeyLength)
            {
                return Attestation(SetupInspectIntegrityResult.NotVerified, SetupInspectReason.VerifierMalformed);
            }

            if (verifier.Members.Count == 0)
            {
                return Attestation(SetupInspectIntegrityResult.NotVerified, SetupInspectReason.VerifierMalformed);
            }

            foreach (var member in verifier.Members.OrderBy(m => m.MemberId, StringComparer.Ordinal))
            {
                if (string.IsNullOrWhiteSpace(member.MemberId)
                    || string.IsNullOrWhiteSpace(member.ExpectedMac))
                {
                    return Attestation(SetupInspectIntegrityResult.NotVerified, SetupInspectReason.VerifierMalformed);
                }

                byte[] expectedMac;
                try
                {
                    expectedMac = Convert.FromBase64String(member.ExpectedMac);
                }
                catch (FormatException)
                {
                    return Attestation(SetupInspectIntegrityResult.NotVerified, SetupInspectReason.VerifierMalformed);
                }

                if (expectedMac.Length != MacLength)
                {
                    return Attestation(SetupInspectIntegrityResult.NotVerified, SetupInspectReason.VerifierMalformed);
                }

                var actualBytes = resolveMemberBytes(member.MemberId);
                if (actualBytes is null || actualBytes.Length == 0)
                {
                    CryptographicOperations.ZeroMemory(expectedMac);
                    return Attestation(SetupInspectIntegrityResult.Mismatch, SetupInspectReason.SecretMissing);
                }

                var actualMac = ComputeMac(sessionKey, member.MemberId, actualBytes);
                try
                {
                    if (!FixedTimeEqualsMac(expectedMac, actualMac))
                    {
                        return Attestation(SetupInspectIntegrityResult.Mismatch, SetupInspectReason.MountMismatch);
                    }
                }
                finally
                {
                    CryptographicOperations.ZeroMemory(actualMac);
                    CryptographicOperations.ZeroMemory(expectedMac);
                    CryptographicOperations.ZeroMemory(actualBytes);
                }
            }

            return Attestation(SetupInspectIntegrityResult.Matched, reason: null);
        }
        catch (FormatException)
        {
            return Attestation(SetupInspectIntegrityResult.NotVerified, SetupInspectReason.VerifierMalformed);
        }
        finally
        {
            if (sessionKey is not null)
            {
                CryptographicOperations.ZeroMemory(sessionKey);
            }
        }
    }

    private static SetupInspectAttestationSummary Attestation(string result, string? reason) =>
        new()
        {
            Result = result,
            Reason = reason,
            Scope = "container-mount",
        };

    private static void TransformUtf8(HMACSHA256 hmac, string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        hmac.TransformBlock(bytes, 0, bytes.Length, null, 0);
        CryptographicOperations.ZeroMemory(bytes);
    }

    private static void TransformNul(HMACSHA256 hmac) =>
        hmac.TransformBlock([0], 0, 1, null, 0);
}
