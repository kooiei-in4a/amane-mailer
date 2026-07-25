using Amane.Mailer.Tests.Fixtures;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Amane.Mailer.Tests;

public sealed class MailerAdminDbOpsStartupTests
{
    [Fact]
    public async Task Enabled_admin_with_invalid_db_ops_boolean_fails_startup()
    {
        await using var fixture = new MailerAdminDbOpsInvalidEnabledFixture();
        await fixture.InitializeAsync();

        // MailerStartupValidator eagerly resolves MailerAdminDbOpsOptions after Build (#351).
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
        {
            using var client = fixture.Factory.CreateClient(new WebApplicationFactoryClientOptions
            {
                AllowAutoRedirect = false,
                BaseAddress = new Uri("https://localhost"),
            });
            using var response = await client.GetAsync("/healthz", TestContext.Current.CancellationToken);
        });

        Assert.Contains("AMANE_ADMIN_DB_OPS_ENABLED", exception.Message, StringComparison.Ordinal);
        Assert.Contains("true", exception.Message, StringComparison.Ordinal);
        Assert.Contains("false", exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(MailerWebApplicationFixtureBase.Token, exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("ConnectionString", exception.Message, StringComparison.OrdinalIgnoreCase);
    }
}
