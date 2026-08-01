namespace Amane.Mailer.Setup;

/// <summary>
/// Normalizes public compose env values before configuration fingerprint comparison.
/// Recorded metadata uses bundle-relative host paths; effective inspection reads absolute
/// host paths from the running container environment.
/// </summary>
internal static class SetupFingerprintComposeNormalizer
{
    public static SortedDictionary<string, string> Normalize(
        IReadOnlyDictionary<string, string> compose,
        string? bundleId)
    {
        var normalized = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (var pair in compose)
        {
            normalized[pair.Key] = NormalizeValue(pair.Key, pair.Value, bundleId);
        }

        return normalized;
    }

    private static string NormalizeValue(string key, string value, string? bundleId)
    {
        if (string.IsNullOrEmpty(bundleId))
        {
            return value;
        }

        var bundleSegment = $"bundles/{bundleId}/";
        if (value.Contains(bundleSegment, StringComparison.Ordinal))
        {
            return value.Replace(bundleSegment, "bundles/<bundle-id>/", StringComparison.Ordinal);
        }

        if (!key.EndsWith("_HOST_PATH", StringComparison.Ordinal))
        {
            return value;
        }

        return TryExtractBundleRelativePath(value, bundleId) ?? value;
    }

    private static string? TryExtractBundleRelativePath(string value, string bundleId)
    {
        var unixMarker = $"bundles/{bundleId}/";
        var unixIndex = value.IndexOf(unixMarker, StringComparison.OrdinalIgnoreCase);
        if (unixIndex >= 0)
        {
            return ToPlaceholderSuffix(value, unixIndex, unixMarker.Length);
        }

        var windowsMarker = $"bundles\\{bundleId}\\";
        var windowsIndex = value.IndexOf(windowsMarker, StringComparison.OrdinalIgnoreCase);
        if (windowsIndex >= 0)
        {
            return ToPlaceholderSuffix(value, windowsIndex, windowsMarker.Length);
        }

        return null;
    }

    private static string ToPlaceholderSuffix(string value, int markerIndex, int markerLength)
    {
        var suffix = value[(markerIndex + markerLength)..].Replace('\\', '/');
        return $"bundles/<bundle-id>/{suffix}";
    }
}
