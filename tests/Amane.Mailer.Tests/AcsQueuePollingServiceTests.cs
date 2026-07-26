using System.Text;
using Azure;
using Amane.Mailer.Bounce;
using Amane.Mailer.Configuration;
using Amane.Mailer.Data.Sqlite;
using Amane.Mailer.Data.Sqlite.Models;
using Amane.Mailer.Operations;
using Amane.Mailer.Tests.Fixtures;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace Amane.Mailer.Tests;

public sealed class MailerBounceIngestionModeTests
{
    [Theory]
    [InlineData(null, BounceIngestionMode.Off)]
    [InlineData("", BounceIngestionMode.Off)]
    [InlineData("   ", BounceIngestionMode.Off)]
    [InlineData("off", BounceIngestionMode.Off)]
    [InlineData("OFF", BounceIngestionMode.Off)]
    [InlineData("queue", BounceIngestionMode.Queue)]
    [InlineData("QUEUE", BounceIngestionMode.Queue)]
    [InlineData("webhook", BounceIngestionMode.Webhook)]
    public void ParseMode_accepts_documented_literals(string? raw, BounceIngestionMode expected)
    {
        Assert.Equal(expected, MailerBounceIngestionOptions.ParseMode(raw));
    }

    [Theory]
    [InlineData("none")]
    [InlineData("push")]
    [InlineData("both")]
    [InlineData("DefaultEndpointsProtocol=http;AccountKey=SUPERSECRETKEY;QueueEndpoint=http://127.0.0.1:10001/devstoreaccount1;")]
    [InlineData("leak@example.com")]
    public void ParseMode_rejects_unknown_literals_without_echoing_raw_value(string raw)
    {
        var ex = Assert.Throws<InvalidOperationException>(() => MailerBounceIngestionOptions.ParseMode(raw));
        Assert.Contains("must be one of", ex.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(raw, ex.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("AccountKey", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("SUPERSECRETKEY", ex.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("@example.com", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("got '", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Validate_queue_requires_connection_and_name_without_echoing_secrets()
    {
        var secret = "DefaultEndpointsProtocol=http;AccountName=devstoreaccount1;AccountKey=SUPERSECRETKEY;QueueEndpoint=http://127.0.0.1:10001/devstoreaccount1;";
        var options = new MailerBounceIngestionOptions
        {
            Mode = BounceIngestionMode.Queue,
            QueueConnectionString = secret,
            QueueName = "",
        };

        var ex = Assert.Throws<InvalidOperationException>(() => options.Validate());
        Assert.Contains("MAILER_BOUNCE_QUEUE_NAME", ex.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("SUPERSECRETKEY", ex.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(secret, ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Validate_webhook_is_rejected_as_unimplemented()
    {
        var options = new MailerBounceIngestionOptions { Mode = BounceIngestionMode.Webhook };
        var ex = Assert.Throws<InvalidOperationException>(() => options.Validate());
        Assert.Contains("#304", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Load_reads_MAILER_BOUNCE_INGESTION_environment_alias()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["MAILER_BOUNCE_INGESTION"] = "queue",
                ["MAILER_BOUNCE_QUEUE_CONNECTION_STRING"] = "UseDevelopmentStorage=true",
                ["MAILER_BOUNCE_QUEUE_NAME"] = "acs-bounces",
            })
            .Build();

        var options = MailerBounceIngestionOptions.Load(configuration);
        Assert.Equal(BounceIngestionMode.Queue, options.Mode);
        Assert.True(options.IsQueuePollingEnabled);
        Assert.True(options.IsProcessingEnabled);
        Assert.Equal("acs-bounces", options.QueueName);
        options.Validate();
    }

    [Fact]
    public void Enabled_true_with_mode_off_still_enables_processing_without_poller()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Mailer:BounceIngestion:Enabled"] = "true",
            })
            .Build();

        var options = MailerBounceIngestionOptions.Load(configuration);
        Assert.Equal(BounceIngestionMode.Off, options.Mode);
        Assert.True(options.IsProcessingEnabled);
        Assert.False(options.IsQueuePollingEnabled);
    }

    [Fact]
    public void Blank_MAILER_BOUNCE_INGESTION_is_not_supplied_and_falls_through_to_Mode_key()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["MAILER_BOUNCE_INGESTION"] = "   ",
                ["Mailer:BounceIngestion:Mode"] = "queue",
                ["MAILER_BOUNCE_QUEUE_CONNECTION_STRING"] = "UseDevelopmentStorage=true",
                ["MAILER_BOUNCE_QUEUE_NAME"] = "acs-bounces",
            })
            .Build();

        var options = MailerBounceIngestionOptions.Load(configuration);
        Assert.Equal(BounceIngestionMode.Queue, options.Mode);
    }
}

public sealed class AcsQueueMessageBodyDecoderTests
{
    [Fact]
    public void Decode_passes_through_raw_json()
    {
        const string json = """{"id":"eg-1","eventType":"Microsoft.Communication.EmailDeliveryReportReceived"}""";
        Assert.Equal(json, AcsQueueMessageBodyDecoder.Decode(json));
    }

    [Fact]
    public void Decode_unwraps_base64_json()
    {
        const string json = """{"id":"eg-1","eventType":"Microsoft.Communication.EmailDeliveryReportReceived"}""";
        var encoded = Convert.ToBase64String(Encoding.UTF8.GetBytes(json));
        Assert.Equal(json, AcsQueueMessageBodyDecoder.Decode(encoded));
    }
}

public sealed class AcsQueuePollingServiceTests
{
    private static readonly DateTimeOffset FixedNow = DateTimeOffset.Parse("2026-07-26T18:00:00Z");

    private const string BouncedJson = """
        {
          "id": "eg-queue-1",
          "eventType": "Microsoft.Communication.EmailDeliveryReportReceived",
          "eventTime": "2026-07-26T18:00:00Z",
          "data": {
            "messageId": "bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb",
            "status": "Bounced",
            "recipient": "user@example.com"
          }
        }
        """;

    private const string BouncedJsonSecond = """
        {
          "id": "eg-queue-2",
          "eventType": "Microsoft.Communication.EmailDeliveryReportReceived",
          "eventTime": "2026-07-26T18:00:00Z",
          "data": {
            "messageId": "cccccccc-cccc-cccc-cccc-cccccccccccc",
            "status": "Bounced",
            "recipient": "other@example.com"
          }
        }
        """;

    [Fact]
    public async Task Poll_inserts_inbox_row_before_deleting_queue_message()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var db = await OpenMigratedAsync(ct);
        var queue = new FakeAcsEventQueueClient();
        queue.Enqueue("msg-1", "pop-1", BouncedJson);
        queue.OnDelete = async (messageId, _) =>
        {
            Assert.Equal("msg-1", messageId);
            Assert.Equal(1, await CountInboxAsync(db.Factory, "eg-queue-1", ct));
        };

        var metrics = new MailerRuntimeMetrics();
        var service = CreateService(db.Factory, queue, metrics);
        await service.PollOnceAsync(ct);

        Assert.Contains(("msg-1", "pop-1"), queue.Deleted);
        Assert.Empty(queue.Pending);
        Assert.Equal(1, await CountInboxAsync(db.Factory, "eg-queue-1", ct));
        Assert.Equal(0, metrics.CaptureSnapshot().ProviderQueuePollFailedTotal);
    }

    [Fact]
    public async Task Poll_duplicate_event_still_deletes_queue_message()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var db = await OpenMigratedAsync(ct);
        var inbox = new ProviderEventInboxRepository(db.Factory);
        Assert.True(await inbox.TryInsertAsync(
            new ProviderEventInboxInsert
            {
                Id = Guid.Parse("00000000-0000-0000-0000-000000000501"),
                Provider = "acs",
                EventId = "eg-queue-1",
                ProviderMessageId = "bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb",
                DeliveryStatus = "Bounced",
                RecipientEmail = "user@example.com",
                MaxAttempts = 3,
                CreatedAt = FixedNow,
            },
            ct));

        var queue = new FakeAcsEventQueueClient();
        queue.Enqueue("msg-dup", "pop-dup", BouncedJson);
        var service = CreateService(db.Factory, queue, new MailerRuntimeMetrics());
        await service.PollOnceAsync(ct);

        Assert.Contains(("msg-dup", "pop-dup"), queue.Deleted);
        Assert.Equal(1, await CountInboxAsync(db.Factory, "eg-queue-1", ct));
    }

    [Fact]
    public async Task Poll_leaves_message_when_inbox_insert_fails()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var db = await OpenMigratedAsync(ct);
        var queue = new FakeAcsEventQueueClient();
        queue.Enqueue("msg-fail", "pop-fail", BouncedJson);

        var metrics = new MailerRuntimeMetrics();
        var throwingInbox = new ThrowingInboxRepository(db.Factory);
        var service = new AcsQueuePollingService(
            queue,
            throwingInbox,
            new MailerBounceIngestionOptions { Mode = BounceIngestionMode.Queue, MaxAttempts = 3 },
            new BounceIngestionQueue(),
            metrics,
            new FixedUtcTimeProvider(FixedNow),
            CreateCapturingLogger(out var logCapture));

        await service.PollOnceAsync(ct);

        Assert.Empty(queue.Deleted);
        Assert.Contains(queue.Received, messageId => messageId == "msg-fail");
        Assert.Equal(1, metrics.CaptureSnapshot().ProviderQueuePollFailedTotal);
        AssertNoSecretCanaries(logCapture);
    }

    [Fact]
    public async Task Poll_multi_event_message_retains_queue_message_when_later_insert_fails()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var db = await OpenMigratedAsync(ct);
        var batch = $"[{BouncedJson},{BouncedJsonSecond}]";
        var queue = new FakeAcsEventQueueClient();
        queue.Enqueue("msg-batch", "pop-batch", batch);

        var metrics = new MailerRuntimeMetrics();
        var inbox = new ThrowOnSecondInsertRepository(db.Factory);
        var service = new AcsQueuePollingService(
            queue,
            inbox,
            new MailerBounceIngestionOptions { Mode = BounceIngestionMode.Queue, MaxAttempts = 3 },
            new BounceIngestionQueue(),
            metrics,
            new FixedUtcTimeProvider(FixedNow),
            CreateCapturingLogger(out _));

        await service.PollOnceAsync(ct);

        Assert.Empty(queue.Deleted);
        Assert.Equal(1, await CountInboxAsync(db.Factory, "eg-queue-1", ct));
        Assert.Equal(0, await CountInboxAsync(db.Factory, "eg-queue-2", ct));
        Assert.Equal(1, metrics.CaptureSnapshot().ProviderQueuePollFailedTotal);
    }

    [Fact]
    public async Task Poll_mixed_valid_and_unparseable_retains_message_after_inserting_valid()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var db = await OpenMigratedAsync(ct);
        var mixed = """
            [
              {
                "id": "eg-queue-1",
                "eventType": "Microsoft.Communication.EmailDeliveryReportReceived",
                "eventTime": "2026-07-26T18:00:00Z",
                "data": {
                  "messageId": "bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb",
                  "status": "Bounced",
                  "recipient": "user@example.com"
                }
              },
              {
                "id": "eg-invalid",
                "eventType": "Microsoft.Communication.EmailDeliveryReportReceived",
                "data": {}
              }
            ]
            """;
        var queue = new FakeAcsEventQueueClient();
        queue.Enqueue("msg-mixed", "pop-mixed", mixed);

        var metrics = new MailerRuntimeMetrics();
        var service = CreateService(db.Factory, queue, metrics);
        await service.PollOnceAsync(ct);

        Assert.Empty(queue.Deleted);
        Assert.Single(queue.Pending);
        Assert.Equal(1, await CountInboxAsync(db.Factory, "eg-queue-1", ct));
        Assert.Equal(0, await CountInboxAsync(db.Factory, "eg-invalid", ct));
        Assert.Equal(1, metrics.CaptureSnapshot().ProviderQueuePollFailedTotal);

        // Redelivery: UNIQUE absorbs the already-inserted valid event; message still retained.
        await service.PollOnceAsync(ct);
        Assert.Empty(queue.Deleted);
        Assert.Equal(1, await CountInboxAsync(db.Factory, "eg-queue-1", ct));
        Assert.Equal(2, metrics.CaptureSnapshot().ProviderQueuePollFailedTotal);
    }

    [Fact]
    public async Task Poll_retains_unparseable_message_without_delete()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var db = await OpenMigratedAsync(ct);
        var queue = new FakeAcsEventQueueClient();
        queue.Enqueue("msg-bad", "pop-bad", "{ not-json");

        var metrics = new MailerRuntimeMetrics();
        var service = CreateService(db.Factory, queue, metrics);
        await service.PollOnceAsync(ct);

        Assert.Empty(queue.Deleted);
        Assert.Single(queue.Pending);
        Assert.Equal(1, metrics.CaptureSnapshot().ProviderQueuePollFailedTotal);
        Assert.Equal(0, await CountInboxAsync(db.Factory, "eg-queue-1", ct));
    }

    [Fact]
    public async Task Poll_deletes_ignored_delivered_without_inbox_insert()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var db = await OpenMigratedAsync(ct);
        var delivered = """
            {
              "id": "eg-delivered",
              "eventType": "Microsoft.Communication.EmailDeliveryReportReceived",
              "eventTime": "2026-07-26T18:00:00Z",
              "data": {
                "messageId": "cccccccc-cccc-cccc-cccc-cccccccccccc",
                "status": "Delivered",
                "recipient": "user@example.com"
              }
            }
            """;
        var queue = new FakeAcsEventQueueClient();
        queue.Enqueue("msg-delivered", "pop-delivered", delivered);

        var service = CreateService(db.Factory, queue, new MailerRuntimeMetrics());
        await service.PollOnceAsync(ct);

        Assert.Contains(("msg-delivered", "pop-delivered"), queue.Deleted);
        Assert.Equal(0, await CountInboxAsync(db.Factory, "eg-delivered", ct));
    }

    [Fact]
    public async Task Poll_accepts_base64_payload()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var db = await OpenMigratedAsync(ct);
        var queue = new FakeAcsEventQueueClient();
        queue.Enqueue(
            "msg-b64",
            "pop-b64",
            Convert.ToBase64String(Encoding.UTF8.GetBytes(BouncedJson)));

        var service = CreateService(db.Factory, queue, new MailerRuntimeMetrics());
        await service.PollOnceAsync(ct);

        Assert.Contains(("msg-b64", "pop-b64"), queue.Deleted);
        Assert.Equal(1, await CountInboxAsync(db.Factory, "eg-queue-1", ct));
    }

    [Fact]
    public async Task Poll_receive_failure_increments_metric_and_sanitizes_logs()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var db = await OpenMigratedAsync(ct);
        var queue = new FakeAcsEventQueueClient { ThrowOnReceive = true };
        var metrics = new MailerRuntimeMetrics();
        var service = new AcsQueuePollingService(
            queue,
            new ProviderEventInboxRepository(db.Factory),
            new MailerBounceIngestionOptions { Mode = BounceIngestionMode.Queue, MaxAttempts = 3 },
            new BounceIngestionQueue(),
            metrics,
            new FixedUtcTimeProvider(FixedNow),
            CreateCapturingLogger(out var logCapture));

        await service.PollOnceAsync(ct);

        Assert.Equal(1, metrics.CaptureSnapshot().ProviderQueuePollFailedTotal);
        Assert.Empty(queue.Deleted);
        AssertNoSecretCanaries(logCapture);
        Assert.DoesNotContain("ErrorCode=", logCapture.JoinedOutput(), StringComparison.Ordinal);
        Assert.DoesNotContain("CanaryAzureError", logCapture.JoinedOutput(), StringComparison.Ordinal);
    }

    private static ILogger<AcsQueuePollingService> CreateCapturingLogger(out CapturingLoggerProvider capture)
    {
        capture = new CapturingLoggerProvider();
        return new Logger<AcsQueuePollingService>(new LoggerFactory([capture]));
    }

    private static void AssertNoSecretCanaries(CapturingLoggerProvider logCapture)
    {
        var output = logCapture.JoinedOutputWithExceptions();
        // ProviderErrorSanitizer keeps key names as AccountKey=***; assert secret values are gone.
        Assert.DoesNotContain("should-not-leak", output, StringComparison.Ordinal);
        Assert.DoesNotContain("SUPERSECRETKEY", output, StringComparison.Ordinal);
        Assert.DoesNotContain("canary@example.com", output, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("CanaryAzureError", output, StringComparison.Ordinal);
        Assert.DoesNotContain("AccountKey=should-not-leak", output, StringComparison.OrdinalIgnoreCase);
    }

    private static AcsQueuePollingService CreateService(
        SqliteConnectionFactory factory,
        FakeAcsEventQueueClient queue,
        MailerRuntimeMetrics metrics) =>
        new(
            queue,
            new ProviderEventInboxRepository(factory),
            new MailerBounceIngestionOptions { Mode = BounceIngestionMode.Queue, MaxAttempts = 3 },
            new BounceIngestionQueue(),
            metrics,
            new FixedUtcTimeProvider(FixedNow),
            CreateCapturingLogger(out _));

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
        var root = Path.Combine(Path.GetTempPath(), "amane-mailer-queue-poll", Guid.NewGuid().ToString("N"));
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
            SqliteConnection.ClearAllPools();
            await Task.Run(() => Directory.Delete(root, recursive: true));
        }
    }

    private sealed class FixedUtcTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => utcNow;
    }

    private sealed class FakeAcsEventQueueClient : IAcsEventQueueClient
    {
        private readonly List<AcsQueueReceivedMessage> _pending = [];
        private readonly object _gate = new();

        public List<(string MessageId, string PopReceipt)> Deleted { get; } = [];

        public List<string> Received { get; } = [];

        public bool ThrowOnReceive { get; init; }

        public Func<string, string, Task>? OnDelete { get; set; }

        public IReadOnlyCollection<AcsQueueReceivedMessage> Pending
        {
            get
            {
                lock (_gate)
                {
                    return _pending.ToArray();
                }
            }
        }

        public void Enqueue(string messageId, string popReceipt, string body)
        {
            lock (_gate)
            {
                _pending.Add(new AcsQueueReceivedMessage(messageId, popReceipt, body));
            }
        }

        public Task<IReadOnlyList<AcsQueueReceivedMessage>> ReceiveMessagesAsync(
            int maxMessages,
            TimeSpan visibilityTimeout,
            CancellationToken cancellationToken)
        {
            if (ThrowOnReceive)
            {
                throw new RequestFailedException(
                    status: 403,
                    message: "AccountKey=should-not-leak canary@example.com",
                    errorCode: "CanaryAzureError",
                    innerException: null);
            }

            lock (_gate)
            {
                var batch = _pending.Take(maxMessages).ToArray();
                foreach (var message in batch)
                {
                    Received.Add(message.MessageId);
                }

                return Task.FromResult<IReadOnlyList<AcsQueueReceivedMessage>>(batch);
            }
        }

        public async Task DeleteMessageAsync(
            string messageId,
            string popReceipt,
            CancellationToken cancellationToken)
        {
            if (OnDelete is not null)
            {
                await OnDelete(messageId, popReceipt);
            }

            lock (_gate)
            {
                Deleted.Add((messageId, popReceipt));
                _pending.RemoveAll(message =>
                    message.MessageId == messageId && message.PopReceipt == popReceipt);
            }
        }
    }

    private sealed class ThrowingInboxRepository(SqliteConnectionFactory connections)
        : ProviderEventInboxRepository(connections)
    {
        public override Task<bool> TryInsertAsync(
            ProviderEventInboxInsert row,
            CancellationToken cancellationToken = default) =>
            throw new SqliteException("simulated SQLITE_BUSY AccountKey=should-not-leak canary@example.com", 5);
    }

    private sealed class ThrowOnSecondInsertRepository(SqliteConnectionFactory connections)
        : ProviderEventInboxRepository(connections)
    {
        private int _calls;

        public override async Task<bool> TryInsertAsync(
            ProviderEventInboxInsert row,
            CancellationToken cancellationToken = default)
        {
            _calls++;
            if (_calls >= 2)
            {
                throw new SqliteException("simulated SQLITE_BUSY on second insert", 5);
            }

            return await base.TryInsertAsync(row, cancellationToken);
        }
    }
}

