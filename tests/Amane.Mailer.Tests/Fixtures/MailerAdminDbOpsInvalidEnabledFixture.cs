namespace Amane.Mailer.Tests.Fixtures;

/// <summary>
/// Admin enabled with an invalid DbOps boolean — host startup must fail closed (#351).
/// </summary>
public sealed class MailerAdminDbOpsInvalidEnabledFixture() : MailerWebApplicationFixtureBase(workerEnabled: false)
{
    protected override IReadOnlyDictionary<string, string?> ExtraConfiguration =>
        new Dictionary<string, string?>
        {
            ["AMANE_ADMIN_ENABLED"] = "true",
            ["AMANE_ADMIN_USERNAME"] = MailerAdminFixture.Username,
            ["AMANE_ADMIN_PASSWORD_HASH"] = MailerAdminFixture.PasswordHash,
            ["AMANE_ADMIN_ALLOWED_LOCAL_ADDRESS"] = "127.0.0.1",
            ["AMANE_ADMIN_DB_OPS_ENABLED"] = "yes",
        };
}
