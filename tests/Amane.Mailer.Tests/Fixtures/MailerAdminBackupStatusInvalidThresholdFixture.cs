namespace Amane.Mailer.Tests.Fixtures;

/// <summary>
/// Admin enabled with an invalid backup-status freshness threshold — host startup must fail closed.
/// </summary>
public sealed class MailerAdminBackupStatusInvalidThresholdFixture() : MailerWebApplicationFixtureBase(workerEnabled: false)
{
    protected override IReadOnlyDictionary<string, string?> ExtraConfiguration =>
        new Dictionary<string, string?>
        {
            ["AMANE_ADMIN_ENABLED"] = "true",
            ["AMANE_ADMIN_USERNAME"] = MailerAdminFixture.Username,
            ["AMANE_ADMIN_PASSWORD_HASH"] = MailerAdminFixture.PasswordHash,
            ["AMANE_ADMIN_ALLOWED_LOCAL_ADDRESS"] = "127.0.0.1",
            ["MAILER_BACKUP_STATUS_DB_STALE_AFTER_HOURS"] = "0",
        };
}
