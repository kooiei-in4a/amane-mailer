using Microsoft.Extensions.Configuration;
using System.Security.Cryptography;
using System.Text;
using Amane.Mailer.Configuration;
using Amane.Mailer.Operations;

namespace Amane.Mailer.Setup;

/// <summary>
/// Container-side ephemeral mount attestation (ADR 0021 D-04 step 2).
/// Host generates expected MACs; one-shot recomputes from actually mounted bytes.
/// Required member IDs must exactly match the verifier member set (fail-closed).
/// </summary>
public static class SetupMountAttestation
{
    public const int SessionKeyLength = 32;
    public const int MacLength = 32;
    public const int SessionNonceLength = 16;
    private static readonly byte[] DomainPrefix = "AMANE-MOUNT-ATTEST-V1"u8.ToArray();

    public const string AcsConnectionStringMemberId =
        $"{SetupBundleLayout.SecretsDirectoryName}/{AcsSecretFileNames.CanonicalFileName}";

    public static string EnvMemberId(string envKey) => "env:" + envKey;

    public static HashSet<string> DeriveRequiredMemberIds(
        MailerOptions options,
        IReadOnlyList<MailerTenant> tenants,
        IConfiguration configuration)
    {
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var tenant in tenants)
        {
            ids.Add(EnvMemberId(tenant.TokenEnv));
            if (tenant.Webhook is not null)
            {
                ids.Add(EnvMemberId(tenant.Webhook.SecretEnv));
            }
        }

        // Managed secret-valued env present in the effective container environment.
        AddPresentSecretEnv(ids, configuration, "MAILER_METRICS_BEARER_TOKEN");
        AddPresentSecretEnv(ids, configuration, "AMANE_ADMIN_PASSWORD_HASH");

        if (tenants.Any(t => string.Equals(options.ResolveProvider(t), "acs", StringComparison.Ordinal)))
        {
            ids.Add(AcsConnectionStringMemberId);
        }

        return ids;
    }

    private static void AddPresentSecretEnv(
        HashSet<string> ids,
        IConfiguration configuration,
        string envKey)
    {
        var value = configuration[envKey] ?? Environment.GetEnvironmentVariable(envKey);
        if (!string.IsNullOrWhiteSpace(value))
        {
            ids.Add(EnvMemberId(envKey));
        }
    }

    public static byte[] CreateSessionKey()
    {
        var key = new byte[SessionKeyLength];
        RandomNumberGenerator.Fill(key);
        return key;
    }

    public static byte[] CreateSessionNonce()
    {
        var nonce = new byte[SessionNonceLength];
        RandomNumberGenerator.Fill(nonce);
        return nonce;
    }

    public static byte[] ComputeMac(
        ReadOnlySpan<byte> sessionKey,
        ReadOnlySpan<byte> sessionNonce,
        string memberId,
        ReadOnlySpan<byte> content)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(memberId);

        using var hmac = new HMACSHA256(sessionKey.ToArray());
        hmac.TransformBlock(DomainPrefix, 0, DomainPrefix.Length, null, 0);
        TransformNul(hmac);
        hmac.TransformBlock(sessionNonce.ToArray(), 0, sessionNonce.Length, null, 0);
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
        IReadOnlyCollection<string> requiredMemberIds,
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

        if (requiredMemberIds.Count == 0)
        {
            return Attestation(SetupInspectIntegrityResult.NotVerified, SetupInspectReason.VerifierMalformed);
        }

        byte[]? sessionKey = null;
        byte[]? sessionNonce = null;
        try
        {
            sessionKey = Convert.FromBase64String(verifier.SessionKey);
            if (sessionKey.Length != SessionKeyLength)
            {
                return Attestation(SetupInspectIntegrityResult.NotVerified, SetupInspectReason.VerifierMalformed);
            }

            try
            {
                sessionNonce = Convert.FromBase64String(verifier.SessionNonce);
            }
            catch (FormatException)
            {
                return Attestation(SetupInspectIntegrityResult.NotVerified, SetupInspectReason.VerifierMalformed);
            }

            if (sessionNonce.Length != SessionNonceLength)
            {
                return Attestation(SetupInspectIntegrityResult.NotVerified, SetupInspectReason.VerifierMalformed);
            }

            if (verifier.Members is null || verifier.Members.Count == 0)
            {
                return Attestation(SetupInspectIntegrityResult.NotVerified, SetupInspectReason.VerifierMalformed);
            }

            var verifierIds = new HashSet<string>(StringComparer.Ordinal);
            foreach (var member in verifier.Members)
            {
                if (string.IsNullOrWhiteSpace(member.MemberId)
                    || string.IsNullOrWhiteSpace(member.ExpectedMac))
                {
                    return Attestation(SetupInspectIntegrityResult.NotVerified, SetupInspectReason.VerifierMalformed);
                }

                if (!verifierIds.Add(member.MemberId))
                {
                    return Attestation(SetupInspectIntegrityResult.Mismatch, SetupInspectReason.VerifierMemberSetMismatch);
                }
            }

            var required = new HashSet<string>(requiredMemberIds, StringComparer.Ordinal);
            if (!required.SetEquals(verifierIds))
            {
                return Attestation(SetupInspectIntegrityResult.Mismatch, SetupInspectReason.VerifierMemberSetMismatch);
            }

            foreach (var member in verifier.Members.OrderBy(m => m.MemberId, StringComparer.Ordinal))
            {
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

                var actualMac = ComputeMac(sessionKey, sessionNonce, member.MemberId, actualBytes);
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

            if (sessionNonce is not null)
            {
                CryptographicOperations.ZeroMemory(sessionNonce);
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
