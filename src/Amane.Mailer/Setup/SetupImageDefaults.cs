namespace Amane.Mailer.Setup;

/// <summary>Shared image defaults and placeholder detection for Setup Core.</summary>
public static class SetupImageDefaults
{
    public const string DefaultRepository = "ghcr.io/kooiei-in4a/amane-mailer";

    /// <summary>Dry-run-only placeholder; never written into a FINALIZED bundle.</summary>
    public const string DryRunImageTagPlaceholder = "replace-with-published-git-sha";

    public static bool IsPlaceholderImageTag(string? imageTag) =>
        !string.IsNullOrEmpty(imageTag)
        && (imageTag.Equals(DryRunImageTagPlaceholder, StringComparison.Ordinal)
            || imageTag.Equals("sha-replace-with-published-git-sha", StringComparison.Ordinal));
}
