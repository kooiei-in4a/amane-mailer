namespace Amane.Mailer.Operations.AcsSetup;

/// <summary>
/// Exact environment confirmation phrases shared by register-acs, test-acs-send, and Easy Setup
/// ACS workflow. Matching is ordinal and never case-folds or trims.
/// </summary>
public static class AcsEnvironmentConfirmation
{
    public const string Staging = "Staging";
    public const string Production = "Production";

    public const string InternalStaging = "staging";
    public const string InternalProduction = "production";

    /// <summary>
    /// Maps an exact operator confirmation to the platform-sender / tenant schema environment.
    /// Only <see cref="Staging"/> and <see cref="Production"/> are accepted.
    /// </summary>
    public static bool TryMap(string? confirmation, out string internalEnvironment)
    {
        if (string.Equals(confirmation, Staging, StringComparison.Ordinal))
        {
            internalEnvironment = InternalStaging;
            return true;
        }

        if (string.Equals(confirmation, Production, StringComparison.Ordinal))
        {
            internalEnvironment = InternalProduction;
            return true;
        }

        internalEnvironment = string.Empty;
        return false;
    }

    public static bool IsExactStaging(string? confirmation) =>
        string.Equals(confirmation, Staging, StringComparison.Ordinal);

    public static bool IsExactProduction(string? confirmation) =>
        string.Equals(confirmation, Production, StringComparison.Ordinal);
}
