namespace Amane.Mailer.Tests.Fixtures;

public sealed class MailerAdminMissingHashFixture() : MailerWebApplicationFixtureBase(workerEnabled: false)
{
    protected override IReadOnlyDictionary<string, string?> ExtraConfiguration =>
        new Dictionary<string, string?>
        {
            ["AMANE_ADMIN_ENABLED"] = "true",
            ["AMANE_ADMIN_PASSWORD_HASH"] = string.Empty,
            ["MAILER_ADMIN_PASSWORD_HASH"] = string.Empty,
        };
}
