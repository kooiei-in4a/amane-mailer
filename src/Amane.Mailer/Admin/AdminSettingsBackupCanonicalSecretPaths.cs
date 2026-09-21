using Amane.Mailer.Setup;

namespace Amane.Mailer.Admin;

internal sealed record AdminSettingsBackupCanonicalSecretPaths(
    string AcsSecretPath,
    string GoogleSecretPath)
{
    internal static AdminSettingsBackupCanonicalSecretPaths Default { get; } = new(
        FirstRunSetupConstants.DefaultAcsSecretPath,
        AdminGoogleSecretStore.DefaultSecretPath);
}
