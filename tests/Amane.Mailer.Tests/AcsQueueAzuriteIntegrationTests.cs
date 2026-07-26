using Azure.Storage.Queues;
using Amane.Mailer.Bounce;
using Amane.Mailer.Configuration;
using Amane.Mailer.Data.Sqlite;
using Amane.Mailer.Operations;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace Amane.Mailer.Tests;

/// <summary>
/// Optional Azurite integration covering Azure.Storage.Queues success-path XML deserialize (#305 / #399 residual).
/// Enable with <c>AMANE_AZURITE_TESTS=1</c> and a reachable Azurite queue endpoint.
/// </summary>
public sealed class AcsQueueAzuriteIntegrationTests
{
    private const string DefaultConnectionString =
        "DefaultEndpointsProtocol=http;AccountName=devstoreaccount1;AccountKey=Eby8vdM02xNOcqFlqUwJPLlmEtlCDXJ1OUzFT50uSRZ6IFsuFq2UVErCz4I6tq/K1SZFPTOtr/KBHBeksoGMGw==;QueueEndpoint=http://127.0.0.1:10001/devstoreaccount1;";

    private static readonly DateTimeOffset FixedNow = DateTimeOffset.Parse("2026-07-26T19:00:00Z");

    [Fact]
    public async Task Azure_queue_client_receives_and_poller_inserts_against_azurite()
    {
        if (!string.Equals(
                Environment.GetEnvironmentVariable("AMANE_AZURITE_TESTS"),
                "1",
                StringComparison.Ordinal))
        {
            // Skip without failing CI when Azurite is not provisioned.
            return;
        }

        var ct = TestContext.Current.CancellationToken;
        var connectionString = Environment.GetEnvironmentVariable("AMANE_AZURITE_QUEUE_CONNECTION_STRING")
            ?? DefaultConnectionString;
        var queueName = "amane-bounce-" + Guid.NewGuid().ToString("N")[..12];

        var serviceClient = new QueueServiceClient(
            connectionString,
            new QueueClientOptions { MessageEncoding = QueueMessageEncoding.None });
        var queue = serviceClient.GetQueueClient(queueName);
        await queue.CreateIfNotExistsAsync(cancellationToken: ct);

        try
        {
            const string eventId = "eg-azurite-1";
            var payload = $$"""
                {
                  "id": "{{eventId}}",
                  "eventType": "Microsoft.Communication.EmailDeliveryReportReceived",
                  "eventTime": "2026-07-26T19:00:00Z",
                  "data": {
                    "messageId": "dddddddd-dddd-dddd-dddd-dddddddddddd",
                    "status": "Bounced",
                    "recipient": "user@example.com"
                  }
                }
                """;
            await queue.SendMessageAsync(payload, cancellationToken: ct);

            await using var db = await OpenMigratedAsync(ct);
            var options = new MailerBounceIngestionOptions
            {
                Mode = BounceIngestionMode.Queue,
                QueueConnectionString = connectionString,
                QueueName = queueName,
                MaxAttempts = 3,
                QueueBatchSize = 8,
                QueueVisibilityTimeoutSeconds = 30,
            };

            var client = new AzureAcsEventQueueClient(options);
            var metrics = new MailerRuntimeMetrics();
            var service = new AcsQueuePollingService(
                client,
                new ProviderEventInboxRepository(db.Factory),
                options,
                new BounceIngestionQueue(),
                metrics,
                new FixedUtcTimeProvider(FixedNow),
                NullLogger<AcsQueuePollingService>.Instance);

            await service.PollOnceAsync(ct);

            Assert.Equal(0, metrics.CaptureSnapshot().ProviderQueuePollFailedTotal);
            Assert.Equal(1, await CountInboxAsync(db.Factory, eventId, ct));

            // Success-path deserialize residual from #399: receive returned a real message body.
            var leftover = await queue.ReceiveMessagesAsync(maxMessages: 1, cancellationToken: ct);
            Assert.Empty(leftover.Value);
        }
        finally
        {
            await queue.DeleteIfExistsAsync(cancellationToken: CancellationToken.None);
        }
    }

    private static async Task<long> CountInboxAsync(
        SqliteConnectionFactory factory,
        string eventId,
        CancellationToken cancellationToken)
    {
        await using var connection = await factory.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT COUNT(*)
            FROM provider_event_inbox
            WHERE provider = 'acs' AND event_id = @EventId;
            """;
        command.Parameters.AddWithValue("@EventId", eventId);
        var result = await command.ExecuteScalarAsync(cancellationToken);
        return result is long count ? count : Convert.ToInt64(result);
    }

    private static async Task<MigratedDb> OpenMigratedAsync(CancellationToken cancellationToken)
    {
        var root = Path.Combine(Path.GetTempPath(), "amane-mailer-azurite", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var databasePath = Path.Combine(root, "mailer.db");
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:Mailer"] = $"Data Source={databasePath}",
            })
            .Build();
        var factory = new SqliteConnectionFactory(configuration);
        await new SqlMigrationRunner(factory).ApplyPendingAsync(cancellationToken);
        return new MigratedDb(root, factory);
    }

    private sealed class MigratedDb(string root, SqliteConnectionFactory factory) : IAsyncDisposable
    {
        public SqliteConnectionFactory Factory { get; } = factory;

        public async ValueTask DisposeAsync()
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            await Task.Run(() => Directory.Delete(root, recursive: true));
        }
    }

    private sealed class FixedUtcTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => utcNow;
    }
}
