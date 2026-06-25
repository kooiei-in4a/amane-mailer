using Amane.Mailer.Admin;

namespace Amane.Mailer.Tests.Fixtures;

public sealed class MailerAdminFixture() : MailerWebApplicationFixtureBase(workerEnabled: false)
{
    public const string Username = "admin";
    public const string Password = "correct horse battery staple";

    private static readonly string PasswordHash = AdminPasswordHasher.Hash(Password);

    protected override IReadOnlyDictionary<string, string?> ExtraConfiguration =>
        new Dictionary<string, string?>
        {
            ["AMANE_ADMIN_ENABLED"] = "true",
            ["AMANE_ADMIN_USERNAME"] = Username,
            ["AMANE_ADMIN_PASSWORD_HASH"] = PasswordHash,
            ["AMANE_ADMIN_MASK_RECIPIENTS"] = "true",
            ["AMANE_ADMIN_MASK_SUBJECTS"] = "true",
        };
}
