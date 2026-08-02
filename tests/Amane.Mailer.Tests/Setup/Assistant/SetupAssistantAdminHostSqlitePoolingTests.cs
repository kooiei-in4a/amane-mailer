using Amane.Mailer.Setup.Assistant;
using Microsoft.Data.Sqlite;

namespace Amane.Mailer.Tests.Setup.Assistant;

public sealed class SetupAssistantAdminHostSqlitePoolingTests
{
    [Fact]
    public void BuildAdminHostMailerConnectionString_disables_pooling()
    {
        var path = Path.Combine(Path.GetTempPath(), "amane-admin-host-pool-" + Guid.NewGuid().ToString("N"), "mailer.db");
        var connectionString = SetupAssistantOperations.BuildAdminHostMailerConnectionString(path);
        var builder = new SqliteConnectionStringBuilder(connectionString);

        Assert.False(builder.Pooling);
        Assert.Equal(Path.GetFullPath(path), Path.GetFullPath(builder.DataSource));
    }

    [Fact]
    public void BuildAdminHostMailerConnectionString_rejects_blank_path()
    {
        Assert.Throws<ArgumentException>(() =>
            SetupAssistantOperations.BuildAdminHostMailerConnectionString(" "));
    }
}
