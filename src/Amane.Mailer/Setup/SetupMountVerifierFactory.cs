using System.Security.Cryptography;
using System.Text;
using Amane.Mailer.Operations;

namespace Amane.Mailer.Setup;

/// <summary>
/// Builds the ephemeral mount verifier for one inspect invocation from the bundle's own secret
/// members (ADR 0021 D-04). Session key/nonce live for a single inspect and are zeroed here; the
/// document itself must never be logged, persisted into runtime env, or copied into public results.
/// </summary>
public static class SetupMountVerifierFactory
{
    /// <summary>Verifier lifetime. Short enough that a leaked document cannot be replayed later.</summary>
    public static readonly TimeSpan VerifierLifetime = TimeSpan.FromMinutes(5);

    public static bool TryCreate(
        ISetupFileSystem fileSystem,
        TrustedSetupHostLayout layout,
        string bundleId,
        DateTimeOffset utcNow,
        out SetupMountVerifierDocument? verifier,
        out SetupDockerResult result)
    {
        ArgumentNullException.ThrowIfNull(fileSystem);
        ArgumentNullException.ThrowIfNull(layout);
        verifier = null;

        if (!SetupActivePointer.IsSafeBundleId(bundleId))
        {
            result = SetupDockerResult.Fail(
                SetupDockerResultCode.InvalidBundleInventory,
                "Bundle id is invalid.");
            return false;
        }

        var bundleRoot = Path.GetFullPath(SetupBundleLayout.BundleRoot(layout.ManagedRoot, bundleId));
        if (!SetupPathGuard.TryEnsurePathSafeUnderRoot(
                fileSystem, layout.ManagedRoot, bundleRoot, out _, out _))
        {
            result = SetupDockerResult.Fail(
                SetupDockerResultCode.UnsafePath,
                "Bundle path rejected.");
            return false;
        }

        var secretsEnvPath = Path.GetFullPath(Path.Combine(
            SetupBundleLayout.EnvDir(bundleRoot),
            SetupBundleLayout.SecretsEnvFileName));
        if (!fileSystem.FileExists(secretsEnvPath))
        {
            result = SetupDockerResult.Fail(
                SetupDockerResultCode.InvalidBundleInventory,
                "Bundle secret environment file is missing.");
            return false;
        }

        byte[]? secretsEnvBytes = null;
        byte[]? acsBytes = null;
        byte[]? sessionKey = null;
        byte[]? sessionNonce = null;
        var memberContents = new List<byte[]>();
        try
        {
            secretsEnvBytes = fileSystem.ReadAllBytes(secretsEnvPath);
            if (!ManagedComposeEnvComposer.TryParseEnvFile(secretsEnvBytes, out var entries, out var parseFailure))
            {
                result = parseFailure!;
                return false;
            }

            if (entries.Count == 0)
            {
                result = SetupDockerResult.Fail(
                    SetupDockerResultCode.InvalidBundleInventory,
                    "Bundle secret environment file has no members.");
                return false;
            }

            var acsPath = Path.GetFullPath(Path.Combine(
                SetupBundleLayout.SecretsDir(bundleRoot),
                AcsSecretFileNames.CanonicalFileName));
            var hasAcs = fileSystem.FileExists(acsPath);
            if (hasAcs)
            {
                if (SetupPathGuard.IsUnsafeLink(fileSystem.InspectSymlinkOrReparsePoint(acsPath)))
                {
                    result = SetupDockerResult.Fail(
                        SetupDockerResultCode.UnsafePath,
                        "Bundle secret path rejected.");
                    return false;
                }

                acsBytes = fileSystem.ReadAllBytes(acsPath);
            }

            sessionKey = SetupMountAttestation.CreateSessionKey();
            sessionNonce = SetupMountAttestation.CreateSessionNonce();

            var members = new List<SetupMountVerifierMember>(entries.Count + 1);
            foreach (var key in entries.Keys.OrderBy(static k => k, StringComparer.Ordinal))
            {
                var content = Encoding.UTF8.GetBytes(entries[key]);
                memberContents.Add(content);
                members.Add(BuildMember(
                    sessionKey,
                    sessionNonce,
                    SetupMountAttestation.EnvMemberId(key),
                    content));
            }

            if (hasAcs && acsBytes is not null)
            {
                members.Add(BuildMember(
                    sessionKey,
                    sessionNonce,
                    SetupMountAttestation.AcsConnectionStringMemberId,
                    acsBytes));
            }

            verifier = new SetupMountVerifierDocument
            {
                SchemaVersion = SetupMountVerifierDocument.CurrentSchemaVersion,
                BundleId = bundleId,
                SessionKey = Convert.ToBase64String(sessionKey),
                SessionNonce = Convert.ToBase64String(sessionNonce),
                ExpiresAtUnix = (utcNow + VerifierLifetime).ToUnixTimeSeconds(),
                Members = members,
            };
            result = SetupDockerResult.Ok();
            return true;
        }
        catch (IOException)
        {
            result = SetupDockerResult.Fail(
                SetupDockerResultCode.FailedUnexpected,
                "Mount verifier material could not be read.");
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            result = SetupDockerResult.Fail(
                SetupDockerResultCode.FailedUnexpected,
                "Mount verifier material could not be read.");
            return false;
        }
        finally
        {
            ZeroIfNotNull(secretsEnvBytes);
            ZeroIfNotNull(acsBytes);
            ZeroIfNotNull(sessionKey);
            ZeroIfNotNull(sessionNonce);
            foreach (var content in memberContents)
            {
                CryptographicOperations.ZeroMemory(content);
            }
        }
    }

    private static SetupMountVerifierMember BuildMember(
        byte[] sessionKey,
        byte[] sessionNonce,
        string memberId,
        byte[] content)
    {
        var mac = SetupMountAttestation.ComputeMac(sessionKey, sessionNonce, memberId, content);
        try
        {
            return new SetupMountVerifierMember
            {
                MemberId = memberId,
                ExpectedMac = Convert.ToBase64String(mac),
            };
        }
        finally
        {
            CryptographicOperations.ZeroMemory(mac);
        }
    }

    private static void ZeroIfNotNull(byte[]? buffer)
    {
        if (buffer is not null)
        {
            CryptographicOperations.ZeroMemory(buffer);
        }
    }
}
