namespace Amane.Mailer.Tests.Fixtures;

/// <summary>
/// Admin is off, but Admin UI env vars are intentionally malformed.
/// Mail delivery host startup must still succeed.
/// </summary>
public sealed class MailerAdminDisabledInvalidUiEnvFixture() : MailerWebApplicationFixtureBase(workerEnabled: false)
{
    protected override IReadOnlyDictionary<string, string?> ExtraConfiguration =>
        new Dictionary<string, string?>
        {
            ["AMANE_ADMIN_ENABLED"] = "false",
            ["AMANE_ADMIN_MASK_RECIPIENTS"] = "yes",
            ["AMANE_ADMIN_LOGIN_FAILURE_LIMIT"] = "abc",
            ["AMANE_ADMIN_DB_OPS_ENABLED"] = "nope",
        };
}
