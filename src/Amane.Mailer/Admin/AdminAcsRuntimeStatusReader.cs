using System.Security.Cryptography;
using System.Text;
using Amane.Mailer.Data.Sqlite;
using Amane.Mailer.Setup;

namespace Amane.Mailer.Admin;

internal enum AdminAcsRuntimeState
{
    Applied,
    RestartPending,
    Unavailable,
}

/// <summary>
/// Sanitized managed-ACS runtime observation. It never returns secret material or its location.
/// </summary>
internal sealed record AdminAcsRuntimeStatus(bool Configured, AdminAcsRuntimeState RuntimeState);

internal static class AdminAcsRuntimeStatusReader
{
    public static AdminAcsRuntimeStatus Evaluate(
        InstanceConfigurationRow? instanceConfiguration,
        string startupSecret)
    {
        if (!IsManagedAcs(instanceConfiguration)
            || !FirstRunSetupStorage.TryReadValidAcsSecret(
                instanceConfiguration!.ProviderSecretRef!,
                out var currentSecret))
        {
            return new(false, AdminAcsRuntimeState.Unavailable);
        }

        return new(
            true,
            Classify(startupSecret, currentSecret));
    }

    public static bool IsManagedAcs(InstanceConfigurationRow? instanceConfiguration) =>
        instanceConfiguration?.InitializedAt is not null
        && string.Equals(instanceConfiguration.ProviderType, "acs", StringComparison.Ordinal)
        && !string.IsNullOrWhiteSpace(instanceConfiguration.ProviderSecretRef);

    public static AdminAcsRuntimeState Classify(string startupSecret, string currentSecret)
    {
        if (string.IsNullOrWhiteSpace(startupSecret) || string.IsNullOrWhiteSpace(currentSecret))
            return AdminAcsRuntimeState.Unavailable;

        return SecretsMatch(startupSecret, currentSecret)
            ? AdminAcsRuntimeState.Applied
            : AdminAcsRuntimeState.RestartPending;
    }

    public static bool SecretsMatch(string startupSecret, string currentSecret)
    {
        var startupBytes = Encoding.UTF8.GetBytes(startupSecret.Trim());
        var currentBytes = Encoding.UTF8.GetBytes(currentSecret.Trim());
        var startupDigest = SHA256.HashData(startupBytes);
        var currentDigest = SHA256.HashData(currentBytes);
        try
        {
            return CryptographicOperations.FixedTimeEquals(startupDigest, currentDigest);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(startupBytes);
            CryptographicOperations.ZeroMemory(currentBytes);
            CryptographicOperations.ZeroMemory(startupDigest);
            CryptographicOperations.ZeroMemory(currentDigest);
        }
    }
}
