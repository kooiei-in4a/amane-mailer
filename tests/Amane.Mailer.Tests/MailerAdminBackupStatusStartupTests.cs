using Amane.Mailer.Tests.Fixtures;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Amane.Mailer.Tests;

public sealed class MailerAdminBackupStatusStartupTests
{
    [Fact]
    public async Task Enabled_admin_with_invalid_backup_status_threshold_fails_startup()
    {
        await using var fixture = new MailerAdminBackupStatusInvalidThresholdFixture();
        await fixture.InitializeAsync();

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
        {
            using var client = fixture.Factory.CreateClient(new WebApplicationFactoryClientOptions
            {
                AllowAutoRedirect = false,
                BaseAddress = new Uri("https://localhost"),
            });
            using var response = await client.GetAsync("/healthz", TestContext.Current.CancellationToken);
        });

        Assert.Contains("MAILER_BACKUP_STATUS_DB_STALE_AFTER_HOURS", exception.Message, StringComparison.Ordinal);
        Assert.Contains("1", exception.Message, StringComparison.Ordinal);
        Assert.Contains("87600", exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(MailerWebApplicationFixtureBase.Token, exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("ConnectionString", exception.Message, StringComparison.OrdinalIgnoreCase);
    }
}
