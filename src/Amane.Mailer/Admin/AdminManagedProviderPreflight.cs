using Amane.Mailer.Data.Sqlite;
using Amane.Mailer.Setup;

namespace Amane.Mailer.Admin;

/// <summary>
/// Shared Admin read of Browser managed-v2 provider preflight.
/// Matches the <c>/admin/ops</c> configured / safe check; never returns secret material.
/// </summary>
internal static class AdminManagedProviderPreflight
{
    public static bool IsSafe(InstanceConfigurationRow? configuration) =>
        configuration is not null
        && string.Equals(configuration.ProviderType, "acs", StringComparison.Ordinal)
        && !string.IsNullOrWhiteSpace(configuration.ProviderSecretRef)
        && FirstRunSetupStorage.TryReadValidAcsSecret(configuration.ProviderSecretRef, out _);
}
