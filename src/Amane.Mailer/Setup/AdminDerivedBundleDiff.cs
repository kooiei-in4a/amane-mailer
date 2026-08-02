namespace Amane.Mailer.Setup;

/// <summary>
/// Mechanical exact-delta guard for Admin-derived bundles. Only Admin enable keys may change;
/// other compose/secret logical values and file secrets must remain identical aside from
/// bundle-id path rewriting.
/// </summary>
internal static class AdminDerivedBundleDiff
{
    private static readonly HashSet<string> AllowedComposeKeys = new(StringComparer.Ordinal)
    {
        "AMANE_ADMIN_ENABLED",
        "AMANE_ADMIN_USERNAME",
        "AMANE_ADMIN_ALLOWED_LOCAL_ADDRESS",
        "AMANE_ADMIN_ALLOW_HTTP",
        // Production HTTPS Admin may flip this false→true with the reverse-proxy contract.
        "ASPNETCORE_FORWARDEDHEADERS_ENABLED",
    };

    private static readonly HashSet<string> AllowedSecretsKeys = new(StringComparer.Ordinal)
    {
        "AMANE_ADMIN_PASSWORD_HASH",
    };

    internal static bool TryValidate(
        string sourceBundleId,
        string candidateBundleId,
        IReadOnlyDictionary<string, string> sourceCompose,
        IReadOnlyDictionary<string, string> sourceSecrets,
        IReadOnlyDictionary<string, string> candidateCompose,
        IReadOnlyDictionary<string, string> candidateSecrets,
        string sourceTenantsJson,
        string candidateTenantsJson,
        string? sourcePlatformSenderJson,
        string? candidatePlatformSenderJson,
        byte[]? sourceAcsBytes,
        byte[]? candidateAcsBytes,
        out string reasonCode)
    {
        reasonCode = "admin_derived_diff_rejected";

        if (!string.Equals(sourceTenantsJson, candidateTenantsJson, StringComparison.Ordinal)
            || !string.Equals(
                sourcePlatformSenderJson ?? string.Empty,
                candidatePlatformSenderJson ?? string.Empty,
                StringComparison.Ordinal)
            || !BytesEqual(sourceAcsBytes, candidateAcsBytes))
        {
            reasonCode = "admin_derived_file_secret_changed";
            return false;
        }

        if (!TryValidateEnv(
                sourceBundleId,
                candidateBundleId,
                sourceCompose,
                candidateCompose,
                AllowedComposeKeys,
                out reasonCode))
        {
            return false;
        }

        if (!TryValidateEnv(
                sourceBundleId,
                candidateBundleId,
                sourceSecrets,
                candidateSecrets,
                AllowedSecretsKeys,
                out reasonCode))
        {
            return false;
        }

        if (sourceCompose.TryGetValue("AMANE_ADMIN_PII_LIST_MODE", out var sourcePii)
            != candidateCompose.TryGetValue("AMANE_ADMIN_PII_LIST_MODE", out var candidatePii)
            || !string.Equals(sourcePii, candidatePii, StringComparison.Ordinal))
        {
            reasonCode = "admin_derived_pii_list_mode_changed";
            return false;
        }

        reasonCode = "ok";
        return true;
    }

    private static bool TryValidateEnv(
        string sourceBundleId,
        string candidateBundleId,
        IReadOnlyDictionary<string, string> source,
        IReadOnlyDictionary<string, string> candidate,
        HashSet<string> allowedChangedKeys,
        out string reasonCode)
    {
        reasonCode = "admin_derived_diff_rejected";
        var keys = new HashSet<string>(source.Keys, StringComparer.Ordinal);
        keys.UnionWith(candidate.Keys);

        foreach (var key in keys)
        {
            var sourcePresent = source.ContainsKey(key);
            var candidatePresent = candidate.ContainsKey(key);
            if (allowedChangedKeys.Contains(key))
                continue;

            if (sourcePresent != candidatePresent)
            {
                reasonCode = "admin_derived_disallowed_env_diff";
                return false;
            }

            var sourceValue = sourcePresent ? source[key] : string.Empty;
            var candidateValue = candidatePresent ? candidate[key] : string.Empty;
            var normalizedSource = RewriteBundleId(sourceValue, sourceBundleId, candidateBundleId);
            if (!string.Equals(normalizedSource, candidateValue, StringComparison.Ordinal))
            {
                reasonCode = "admin_derived_disallowed_env_diff";
                return false;
            }
        }

        reasonCode = "ok";
        return true;
    }

    private static string RewriteBundleId(string value, string sourceBundleId, string candidateBundleId) =>
        value.Replace(
            $"bundles/{sourceBundleId}/",
            $"bundles/{candidateBundleId}/",
            StringComparison.Ordinal);

    private static bool BytesEqual(byte[]? left, byte[]? right)
    {
        if (left is null && right is null)
            return true;
        if (left is null || right is null || left.Length != right.Length)
            return false;

        return left.AsSpan().SequenceEqual(right);
    }
}
