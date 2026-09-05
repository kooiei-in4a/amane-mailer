using Amane.Mailer.Configuration;
using Amane.Mailer.Contracts.MailRequests;
using Amane.Mailer.Data.Sqlite;
using Amane.Mailer.Delivery;
using Amane.Mailer.Operations;
using Amane.Mailer.Tests.Fixtures;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;

namespace Amane.Mailer.Tests;

public sealed class InstanceConfigurationRepositoryTests
{
    [Fact]
    public async Task Live_sending_write_requires_initialized_instance_and_router_reads_current_db_state()
    {
        var ct = TestContext.Current.CancellationToken;
        var root = Path.Combine(Path.GetTempPath(), "amane-mailer-live-gate", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var connectionString = $"Data Source={Path.Combine(root, "mailer.db")}";

        try
        {
            var configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["ConnectionStrings:Mailer"] = connectionString,
                })
                .Build();
            var connections = new SqliteConnectionFactory(configuration);
            await new SqlMigrationRunner(connections).ApplyPendingAsync(ct);
            var repository = new InstanceConfigurationRepository(connections, TimeProvider.System);

            Assert.False(await repository.SetLiveSendingAsync(true, ct));
            Assert.False((await repository.GetAsync(ct))!.LiveSending);

            await using (var connection = new SqliteConnection(connectionString))
            {
                await connection.OpenAsync(ct);
                await using var command = connection.CreateCommand();
                command.CommandText = "UPDATE instance_configuration SET initialized_at = '2026-01-01T00:00:00.0000000Z' WHERE id = 1;";
                Assert.Equal(1, await command.ExecuteNonQueryAsync(ct));
            }

            var state = new InstanceRuntimeState(
                InstanceRuntimeStateKind.Initialized,
                "2026-01-01T00:00:00.0000000Z",
                false,
                "acs",
                null,
                null,
                true);
            var router = new MailDeliveryProviderRouter(
                new MailpitMailDeliveryProvider(new MailerOptions()),
                new AcsMailDeliveryProvider(new MailerOptions()),
                repository,
                state);
            var tenant = CreateAcsTenant();
            var job = MailSendJob.ForSingleRecipient(
                Guid.NewGuid(),
                "amane-v2-internal",
                "subject",
                null,
                "body",
                null,
                "recipient@example.com",
                null);

            var disabled = await router.SendAsync(job, tenant, "acs", ct);
            Assert.Equal(MailDeliveryErrorCodes.LiveSendingDisabled, disabled.ErrorCode);
            Assert.False(disabled.Succeeded);

            Assert.True(await repository.SetLiveSendingAsync(true, ct));
            var enabled = await router.SendAsync(job, tenant, "acs", ct);
            Assert.NotEqual(MailDeliveryErrorCodes.LiveSendingDisabled, enabled.ErrorCode);

            await using (var connection = new SqliteConnection(connectionString))
            {
                await connection.OpenAsync(ct);
                await using var command = connection.CreateCommand();
                command.CommandText = "DROP TABLE instance_configuration;";
                await command.ExecuteNonQueryAsync(ct);
            }

            var readFailure = await router.SendAsync(job, tenant, "acs", ct);
            Assert.Equal(MailDeliveryErrorCodes.LiveSendingDisabled, readFailure.ErrorCode);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    private static MailerTenant CreateAcsTenant() =>
        new()
        {
            TenantId = MailerWebApplicationFixtureBase.TenantId,
            Name = "managed-instance",
            SourceServices = ["amane-v2-internal"],
            DefaultFrom = new MailerAddress { Email = "sender@example.com", DisplayName = "Sender" },
            TokenEnv = "MANAGED_API_KEY",
            Provider = "acs",
            LiveSending = false,
            Retry = new MailerRetryOptions
            {
                MaxAttempts = 3,
                InitialDelaySeconds = 1,
                MaxDelaySeconds = 2,
            },
        };
}
