using Amane.Mailer.Data.Sqlite;

namespace Amane.Mailer.Admin;

/// <summary>
/// Synchronizes env password hash with <c>admin_config</c> and revokes stale sessions (ADR 0014 D-01).
/// </summary>
public sealed class AdminCredentialSync(
    AdminSessionRepository sessions,
    MailerAdminOptions options)
{
    private int _credentialEpoch;

    public int CredentialEpoch => _credentialEpoch;

    public async Task EnsureSyncedAsync(CancellationToken cancellationToken = default)
    {
        var config = await sessions.GetOrInitializeConfigAsync(options.PasswordHash, cancellationToken);
        if (!string.Equals(config.AppliedPasswordHash, options.PasswordHash, StringComparison.Ordinal))
        {
            var rotated = await sessions.RotateCredentialAsync(
                options.PasswordHash,
                config.CredentialEpoch + 1,
                AdminSessionRevokeReasons.CredentialChanged,
                cancellationToken);
            _credentialEpoch = rotated.CredentialEpoch;
            return;
        }

        _credentialEpoch = config.CredentialEpoch;
    }
}
