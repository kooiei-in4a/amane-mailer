namespace Amane.Mailer.Setup;

/// <summary>
/// Easy Setup safety caps for public env overrides. These are product limits for Managed
/// bundles, not Docker engine hard maximums.
/// </summary>
public static class SetupPublicEnvLimits
{
    public const int MaxHealthcheckRetries = 100;
    public const int MaxLogMaxFile = 1000;
}
