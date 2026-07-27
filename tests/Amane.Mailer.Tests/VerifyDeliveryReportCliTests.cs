using Amane.Mailer.Bounce;
using Amane.Mailer.Operations;
using Amane.Mailer.Operations.AcsTestSend;
using Amane.Mailer.Operations.VerifyDeliveryReport;
using Azure;
using Microsoft.Extensions.Configuration;

namespace Amane.Mailer.Tests;

public sealed class VerifyDeliveryReportCliTests
{
    private const string ValidAcsConnectionString =
        "Endpoint=https://example.communication.azure.com/;AccessKey=abc123";

    private const string ValidQueueConnectionString =
        "DefaultEndpointsProtocol=https;AccountName=examplestorage;AccountKey=abc123;EndpointSuffix=core.windows.net";

    private static readonly Guid FixedOperationId = Guid.Parse("11111111-2222-3333-4444-555555555555");
    private static readonly string FixedMessageId = FixedOperationId.ToString("D");

    [Fact]
    public void IsVerifyDeliveryReportCommand_matches_only_its_own_argv()
    {
        Assert.True(VerifyDeliveryReportCommand.IsVerifyDeliveryReportCommand(
            ["setup", "verify-delivery-report"]));
        Assert.False(VerifyDeliveryReportCommand.IsVerifyDeliveryReportCommand(
            ["setup", "check-event-grid"]));
        Assert.False(VerifyDeliveryReportCommand.IsVerifyDeliveryReportCommand(
            ["setup", "verify-delivery-report", "extra"]));
        Assert.False(VerifyDeliveryReportCommand.IsVerifyDeliveryReportCommand(
            ["admin", "provider", "test-acs-send"]));
    }

    [Fact]
    public async Task Run_succeeds_when_delivered_report_is_peeked_and_correlated()
    {
        using var scratch = new TestScratch();
        var console = CreateHappyConsole(scratch);
        var fakeAcs = new FakeAcsClient(_ => AcsTestSendOutcome.Succeeded(FixedMessageId));
        var peeker = new FakePeeker(
            approximateCount: 1,
            bodies: [DeliveryReportJson(FixedMessageId, "Delivered", "recipient@example.com")]);
        var command = CreateCommand(console, scratch, fakeAcs, peeker);

        var exitCode = await command.RunAsync(CancellationToken.None);

        Assert.Equal(VerifyDeliveryReportCommand.SuccessExitCode, exitCode);
        Assert.Equal(1, fakeAcs.CallCount);
        Assert.Equal(AdminProviderTestAcsSendCommand.SyntheticSubject, fakeAcs.LastRequest!.Subject);
        Assert.Equal(AdminProviderTestAcsSendCommand.SyntheticPlainTextBody, fakeAcs.LastRequest.PlainTextBody);
        Assert.Equal(0, peeker.ReceiveOrDeleteCallCount);
        var output = string.Join('\n', console.Output);
        Assert.Contains("[PASS] ACS send operation completed", output);
        Assert.Contains("[PASS] Delivery Report observed in Storage Queue", output);
        Assert.Contains("[PASS] Event correlated to the test send", output);
        Assert.Contains("[PASS] Delivery status classified", output);
        Assert.Contains("[ACTION] Confirm receipt in the test mailbox", output);
        AssertNoSecretsOrPii(output, console.Errors, FixedMessageId);
    }

    [Fact]
    public async Task Run_wiring_pass_when_status_is_failed()
    {
        using var scratch = new TestScratch();
        var console = CreateHappyConsole(scratch);
        var fakeAcs = new FakeAcsClient(_ => AcsTestSendOutcome.Succeeded(FixedMessageId));
        var peeker = new FakePeeker(
            approximateCount: 1,
            bodies: [DeliveryReportJson(FixedMessageId, "Failed", "recipient@example.com")]);
        var command = CreateCommand(console, scratch, fakeAcs, peeker);

        var exitCode = await command.RunAsync(CancellationToken.None);

        Assert.Equal(VerifyDeliveryReportCommand.SuccessExitCode, exitCode);
        var output = string.Join('\n', console.Output);
        Assert.Contains("[PASS] Delivery Report observed in Storage Queue", output);
        Assert.Contains("[PASS] Event correlated to the test send", output);
        Assert.Contains("[FAIL] Delivery status classified", output);
        AssertNoSecretsOrPii(output, console.Errors, FixedMessageId);
    }

    [Fact]
    public async Task Run_correlates_after_delayed_arrival()
    {
        using var scratch = new TestScratch();
        var console = CreateHappyConsole(scratch);
        var fakeAcs = new FakeAcsClient(_ => AcsTestSendOutcome.Succeeded(FixedMessageId));
        var peeker = new FakePeeker(
            approximateCount: 1,
            peekSequence:
            [
                [],
                [DeliveryReportJson(FixedMessageId, "Delivered", "recipient@example.com")],
            ]);
        var command = CreateCommand(console, scratch, fakeAcs, peeker);

        var exitCode = await command.RunAsync(CancellationToken.None);

        Assert.Equal(VerifyDeliveryReportCommand.SuccessExitCode, exitCode);
        Assert.True(peeker.PeekCallCount >= 2);
        Assert.Contains("[PASS] Event correlated to the test send", string.Join('\n', console.Output));
    }

    [Fact]
    public async Task Run_timeout_distinguishes_acs_success_from_missing_report()
    {
        using var scratch = new TestScratch();
        var console = CreateHappyConsole(scratch);
        var fakeAcs = new FakeAcsClient(_ => AcsTestSendOutcome.Succeeded(FixedMessageId));
        var peeker = new FakePeeker(approximateCount: 0, bodies: []);
        var command = CreateCommand(
            console,
            scratch,
            fakeAcs,
            peeker,
            timeoutSeconds: "30",
            pollIntervalSeconds: "1");

        var exitCode = await command.RunAsync(CancellationToken.None);

        Assert.Equal(VerifyDeliveryReportCommand.FailureExitCode, exitCode);
        Assert.Equal(1, fakeAcs.CallCount);
        var output = string.Join('\n', console.Output);
        var errors = string.Join('\n', console.Errors);
        Assert.Contains("[PASS] ACS send operation completed", output);
        Assert.Contains("[FAIL] Delivery Report observed in Storage Queue", output);
        Assert.Contains("[FAIL] Event correlated to the test send", output);
        Assert.Contains(VerifyDeliveryReportResultCodes.FailedDeliveryReportTimeout, errors);
        AssertNoSecretsOrPii(output, console.Errors, FixedMessageId);
    }

    [Fact]
    public async Task Run_timeout_with_other_message_id_does_not_pass()
    {
        using var scratch = new TestScratch();
        var console = CreateHappyConsole(scratch);
        var otherId = Guid.Parse("99999999-8888-7777-6666-555555555555").ToString("D");
        var fakeAcs = new FakeAcsClient(_ => AcsTestSendOutcome.Succeeded(FixedMessageId));
        var peeker = new FakePeeker(
            approximateCount: 1,
            bodies: [DeliveryReportJson(otherId, "Delivered", "other@example.com")]);
        var command = CreateCommand(
            console,
            scratch,
            fakeAcs,
            peeker,
            timeoutSeconds: "30",
            pollIntervalSeconds: "1");

        var exitCode = await command.RunAsync(CancellationToken.None);

        Assert.Equal(VerifyDeliveryReportCommand.FailureExitCode, exitCode);
        var output = string.Join('\n', console.Output);
        Assert.Contains("[WARN] Other Delivery Report events were visible but none matched this send", output);
        Assert.DoesNotContain(otherId, output);
        Assert.DoesNotContain(FixedMessageId, output);
    }

    [Fact]
    public async Task Run_backlog_outside_peek_window_is_warn_action_not_pass()
    {
        using var scratch = new TestScratch();
        var console = CreateHappyConsole(scratch);
        var fakeAcs = new FakeAcsClient(_ => AcsTestSendOutcome.Succeeded(FixedMessageId));
        var peeker = new FakePeeker(
            approximateCount: AzureAcsEventQueuePeeker.MaxPeekMessages + 5,
            bodies: [DeliveryReportJson(
                Guid.Parse("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee").ToString("D"),
                "Delivered",
                "other@example.com")]);
        var command = CreateCommand(
            console,
            scratch,
            fakeAcs,
            peeker,
            timeoutSeconds: "30",
            pollIntervalSeconds: "1");

        var exitCode = await command.RunAsync(CancellationToken.None);

        Assert.Equal(VerifyDeliveryReportCommand.FailureExitCode, exitCode);
        var output = string.Join('\n', console.Output);
        var errors = string.Join('\n', console.Errors);
        Assert.Contains("[WARN] Queue backlog exceeds read-only peek window", output);
        Assert.Contains("[ACTION] Use a dedicated empty Staging queue", output);
        Assert.Contains(VerifyDeliveryReportResultCodes.FailedDeliveryReportBacklog, errors);
        Assert.DoesNotContain("success: operation=verify_delivery_report", output);
    }

    [Fact]
    public async Task Run_malformed_events_do_not_false_pass()
    {
        using var scratch = new TestScratch();
        var console = CreateHappyConsole(scratch);
        var fakeAcs = new FakeAcsClient(_ => AcsTestSendOutcome.Succeeded(FixedMessageId));
        var peeker = new FakePeeker(
            approximateCount: 1,
            bodies: ["{not-json", """{"id":"x","eventType":"Microsoft.Communication.EmailDeliveryReportReceived"}"""]);
        var command = CreateCommand(
            console,
            scratch,
            fakeAcs,
            peeker,
            timeoutSeconds: "30",
            pollIntervalSeconds: "1");

        var exitCode = await command.RunAsync(CancellationToken.None);

        Assert.Equal(VerifyDeliveryReportCommand.FailureExitCode, exitCode);
        Assert.Contains(
            "[WARN] One or more queue messages could not be parsed as Delivery Reports",
            string.Join('\n', console.Output));
    }

    [Fact]
    public async Task Run_queue_auth_failure_is_classified()
    {
        using var scratch = new TestScratch();
        var console = CreateHappyConsole(scratch);
        var fakeAcs = new FakeAcsClient(_ => AcsTestSendOutcome.Succeeded(FixedMessageId));
        var peeker = new FakePeeker(
            approximateCount: 0,
            bodies: [],
            peekException: new RequestFailedException(403, "Forbidden accesskey=SHOULD-NOT-LEAK"));
        var command = CreateCommand(console, scratch, fakeAcs, peeker);

        var exitCode = await command.RunAsync(CancellationToken.None);

        Assert.Equal(VerifyDeliveryReportCommand.FailureExitCode, exitCode);
        var joined = string.Join('\n', console.Output.Concat(console.Errors));
        Assert.Contains(VerifyDeliveryReportResultCodes.FailedQueueAuthentication, joined);
        Assert.DoesNotContain("SHOULD-NOT-LEAK", joined);
        Assert.DoesNotContain("accesskey=", joined, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Run_queue_not_found_is_classified()
    {
        using var scratch = new TestScratch();
        var console = CreateHappyConsole(scratch);
        var fakeAcs = new FakeAcsClient(_ => AcsTestSendOutcome.Succeeded(FixedMessageId));
        var peeker = new FakePeeker(
            approximateCount: 0,
            bodies: [],
            peekException: new RequestFailedException(404, "Not Found", "QueueNotFound", null));
        var command = CreateCommand(console, scratch, fakeAcs, peeker);

        var exitCode = await command.RunAsync(CancellationToken.None);

        Assert.Equal(VerifyDeliveryReportCommand.FailureExitCode, exitCode);
        Assert.Contains(
            VerifyDeliveryReportResultCodes.FailedQueueNotFound,
            string.Join('\n', console.Errors));
    }

    [Fact]
    public async Task Run_rejects_environment_mismatch_before_send_or_peek()
    {
        using var scratch = new TestScratch();
        var console = new FakeConsole(
            lineResponses: ["staging"],
            secretResponses: [],
            hiddenResponses: []);
        var fakeAcs = new FakeAcsClient(_ => throw new InvalidOperationException("should not send"));
        var peeker = new FakePeeker(approximateCount: 0, bodies: []);
        var command = CreateCommand(console, scratch, fakeAcs, peeker);

        var exitCode = await command.RunAsync(CancellationToken.None);

        Assert.Equal(VerifyDeliveryReportCommand.RejectedExitCode, exitCode);
        Assert.Equal(0, fakeAcs.CallCount);
        Assert.Equal(0, peeker.PeekCallCount);
    }

    [Fact]
    public async Task Run_rejects_production_looking_queue_name()
    {
        using var scratch = new TestScratch();
        File.WriteAllText(scratch.AcsSecretPath, ValidAcsConnectionString);
        File.WriteAllText(scratch.QueueSecretPath, ValidQueueConnectionString);
        var console = new FakeConsole(
            lineResponses: ["Staging", VerifyDeliveryReportCommand.IntentPhrase],
            secretResponses: [],
            hiddenResponses: ["sender@example.com", "recipient@example.com"]);
        var fakeAcs = new FakeAcsClient(_ => throw new InvalidOperationException("should not send"));
        var peeker = new FakePeeker(approximateCount: 0, bodies: []);
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ACS_CONNECTION_STRING_FILE"] = scratch.AcsSecretPath,
                ["MAILER_BOUNCE_QUEUE_CONNECTION_STRING_FILE"] = scratch.QueueSecretPath,
                ["MAILER_BOUNCE_QUEUE_NAME"] = "acs-reports-prod",
                ["MAILER_VERIFY_DELIVERY_REPORT_TIMEOUT_SECONDS"] = "30",
                ["MAILER_VERIFY_DELIVERY_REPORT_POLL_INTERVAL_SECONDS"] = "1",
            })
            .Build();
        var command = new VerifyDeliveryReportCommand(
            console,
            configuration,
            fakeAcs,
            new FixedPeekerFactory(peeker),
            () => FixedOperationId,
            (_, _) => Task.CompletedTask);

        var exitCode = await command.RunAsync(CancellationToken.None);

        Assert.Equal(VerifyDeliveryReportCommand.RejectedExitCode, exitCode);
        Assert.Equal(0, fakeAcs.CallCount);
        Assert.Contains(
            VerifyDeliveryReportResultCodes.RejectedInvalidQueueName,
            string.Join('\n', console.Errors));
    }

    [Fact]
    public async Task Run_prompt_cancel_returns_rejected_not_130()
    {
        var console = new CancellingVisibleConsole();
        var configuration = new ConfigurationBuilder().AddInMemoryCollection().Build();
        var command = new VerifyDeliveryReportCommand(
            console,
            configuration,
            new FakeAcsClient(_ => throw new InvalidOperationException("should not send")),
            new FixedPeekerFactory(new FakePeeker(0, [])),
            () => FixedOperationId);

        var exitCode = await command.RunAsync(CancellationToken.None);

        Assert.Equal(VerifyDeliveryReportCommand.RejectedExitCode, exitCode);
        Assert.Contains(
            VerifyDeliveryReportResultCodes.RejectedCancelled,
            string.Join('\n', console.Errors));
    }

    [Fact]
    public async Task Run_does_not_call_receive_or_delete_on_peeker()
    {
        using var scratch = new TestScratch();
        var console = CreateHappyConsole(scratch);
        var fakeAcs = new FakeAcsClient(_ => AcsTestSendOutcome.Succeeded(FixedMessageId));
        var peeker = new FakePeeker(
            approximateCount: 1,
            bodies: [DeliveryReportJson(FixedMessageId, "Delivered", "recipient@example.com")]);
        var command = CreateCommand(console, scratch, fakeAcs, peeker);

        _ = await command.RunAsync(CancellationToken.None);

        Assert.Equal(0, peeker.ReceiveOrDeleteCallCount);
        Assert.True(peeker.PeekCallCount > 0);
        Assert.True(peeker.PropertiesCallCount > 0);
    }

    [Fact]
    public void Inspector_keeps_delivered_unlike_production_parser()
    {
        var json = DeliveryReportJson(FixedMessageId, "Delivered", "recipient@example.com");
        var production = AcsEventParser.ParseOne(json);
        Assert.Equal(AcsEventParseOutcome.Ignored, production.Outcome);

        var observations = DeliveryReportEventInspector.InspectBody(json);
        Assert.Single(observations);
        Assert.Equal(DeliveryReportPeekKind.DeliveryReport, observations[0].Kind);
        Assert.Equal(FixedMessageId, observations[0].MessageId);
        Assert.Equal("Delivered", observations[0].Status);
        Assert.Null(GetRecipientIfAny(observations[0]));
    }

    [Fact]
    public void Inspector_does_not_expose_recipient_or_status_message()
    {
        var json = """
            {
              "id": "eg-1",
              "eventType": "Microsoft.Communication.EmailDeliveryReportReceived",
              "eventTime": "2026-07-27T00:00:00Z",
              "data": {
                "messageId": "11111111-2222-3333-4444-555555555555",
                "status": "Bounced",
                "recipient": "secret-recipient@example.com",
                "deliveryStatusDetails": { "statusMessage": "550 raw provider text" }
              }
            }
            """;
        var observation = Assert.Single(DeliveryReportEventInspector.InspectBody(json));
        Assert.Equal(DeliveryReportPeekKind.DeliveryReport, observation.Kind);
        Assert.DoesNotContain("secret-recipient", observation.MessageId ?? string.Empty);
        Assert.DoesNotContain("550", observation.Status ?? string.Empty);
        Assert.Null(GetRecipientIfAny(observation));
    }

    [Theory]
    [InlineData("production")]
    [InlineData("prod")]
    [InlineData("acs-prod")]
    [InlineData("reports-production")]
    [InlineData("mail-prod-queue")]
    public void LooksLikeProductionQueueName_detects_obvious_names(string name) =>
        Assert.True(VerifyDeliveryReportCommand.LooksLikeProductionQueueName(name));

    [Theory]
    [InlineData("staging-acs-delivery-reports")]
    [InlineData("acs-delivery-reports")]
    [InlineData("dev-bounce-queue")]
    public void LooksLikeProductionQueueName_allows_staging_names(string name) =>
        Assert.False(VerifyDeliveryReportCommand.LooksLikeProductionQueueName(name));

    private static object? GetRecipientIfAny(DeliveryReportPeekObservation observation)
    {
        // Guardrail: observation type must not grow a Recipient property that tests would miss.
        return observation.GetType().GetProperty("Recipient")?.GetValue(observation);
    }

    private static FakeConsole CreateHappyConsole(TestScratch scratch) =>
        new(
            lineResponses: ["Staging", VerifyDeliveryReportCommand.IntentPhrase],
            secretResponses: [],
            hiddenResponses: ["sender@example.com", "recipient@example.com"]);

    private static VerifyDeliveryReportCommand CreateCommand(
        IAdminProviderTestAcsSendConsole console,
        TestScratch scratch,
        FakeAcsClient fakeAcs,
        FakePeeker peeker,
        string timeoutSeconds = "30",
        string pollIntervalSeconds = "1")
    {
        File.WriteAllText(scratch.AcsSecretPath, ValidAcsConnectionString);
        File.WriteAllText(scratch.QueueSecretPath, ValidQueueConnectionString);

        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ACS_CONNECTION_STRING_FILE"] = scratch.AcsSecretPath,
                ["MAILER_BOUNCE_QUEUE_CONNECTION_STRING_FILE"] = scratch.QueueSecretPath,
                ["MAILER_BOUNCE_QUEUE_NAME"] = "staging-acs-delivery-reports",
                ["MAILER_VERIFY_DELIVERY_REPORT_TIMEOUT_SECONDS"] = timeoutSeconds,
                ["MAILER_VERIFY_DELIVERY_REPORT_POLL_INTERVAL_SECONDS"] = pollIntervalSeconds,
            })
            .Build();

        var now = DateTimeOffset.Parse("2026-07-27T00:00:00Z");
        return new VerifyDeliveryReportCommand(
            console,
            configuration,
            fakeAcs,
            new FixedPeekerFactory(peeker),
            () => FixedOperationId,
            (delay, _) =>
            {
                now += delay;
                return Task.CompletedTask;
            },
            () => now);
    }

    private static string DeliveryReportJson(string messageId, string status, string recipient) =>
        $$"""
        {
          "id": "eg-1",
          "eventType": "Microsoft.Communication.EmailDeliveryReportReceived",
          "eventTime": "2026-07-27T00:00:00Z",
          "data": {
            "messageId": "{{messageId}}",
            "status": "{{status}}",
            "recipient": "{{recipient}}",
            "deliveryStatusDetails": { "statusMessage": "" }
          }
        }
        """;

    private static void AssertNoSecretsOrPii(
        string output,
        IReadOnlyList<string> errors,
        string messageId)
    {
        var joined = output + "\n" + string.Join('\n', errors);
        Assert.DoesNotContain(ValidAcsConnectionString, joined);
        Assert.DoesNotContain(ValidQueueConnectionString, joined);
        Assert.DoesNotContain("sender@example.com", joined);
        Assert.DoesNotContain("recipient@example.com", joined);
        Assert.DoesNotContain(messageId, joined);
        Assert.DoesNotContain("AccessKey=", joined, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("AccountKey=", joined, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(AdminProviderTestAcsSendCommand.SyntheticSubject, joined);
    }

    private sealed class FakeAcsClient(Func<AcsTestSendRequest, AcsTestSendOutcome> handler)
        : IAcsTestSendClient
    {
        public int CallCount { get; private set; }

        public AcsTestSendRequest? LastRequest { get; private set; }

        public Task<AcsTestSendOutcome> SendAsync(
            AcsTestSendRequest request,
            CancellationToken cancellationToken)
        {
            CallCount++;
            LastRequest = request;
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(handler(request));
        }
    }

    private sealed class FakePeeker : IAcsEventQueuePeeker
    {
        private readonly int? _approximateCount;
        private readonly Queue<IReadOnlyList<PeekedQueueMessageBody>> _sequence;
        private readonly Exception? _peekException;

        public FakePeeker(
            int? approximateCount,
            IReadOnlyList<string>? bodies = null,
            IReadOnlyList<IReadOnlyList<string>>? peekSequence = null,
            Exception? peekException = null)
        {
            _approximateCount = approximateCount;
            _peekException = peekException;
            _sequence = new Queue<IReadOnlyList<PeekedQueueMessageBody>>();
            if (peekSequence is not null)
            {
                foreach (var round in peekSequence)
                {
                    _sequence.Enqueue(round.Select(b => new PeekedQueueMessageBody(b)).ToArray());
                }
            }
            else
            {
                _sequence.Enqueue((bodies ?? []).Select(b => new PeekedQueueMessageBody(b)).ToArray());
            }
        }

        public int PeekCallCount { get; private set; }

        public int PropertiesCallCount { get; private set; }

        public int ReceiveOrDeleteCallCount { get; private set; }

        public Task<IReadOnlyList<PeekedQueueMessageBody>> PeekMessagesAsync(
            int maxMessages,
            CancellationToken cancellationToken)
        {
            PeekCallCount++;
            cancellationToken.ThrowIfCancellationRequested();
            if (_peekException is not null)
            {
                return Task.FromException<IReadOnlyList<PeekedQueueMessageBody>>(_peekException);
            }

            if (_sequence.Count == 0)
            {
                return Task.FromResult<IReadOnlyList<PeekedQueueMessageBody>>([]);
            }

            if (_sequence.Count == 1)
            {
                return Task.FromResult(_sequence.Peek());
            }

            return Task.FromResult(_sequence.Dequeue());
        }

        public Task<int?> GetApproximateMessageCountAsync(CancellationToken cancellationToken)
        {
            PropertiesCallCount++;
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(_approximateCount);
        }
    }

    private sealed class FixedPeekerFactory(IAcsEventQueuePeeker peeker) : IAcsEventQueuePeekerFactory
    {
        public IAcsEventQueuePeeker Create(string connectionString, string queueName) => peeker;
    }

    private sealed class FakeConsole(
        IReadOnlyList<string> lineResponses,
        IReadOnlyList<string> secretResponses,
        IReadOnlyList<string> hiddenResponses)
        : IAdminProviderTestAcsSendConsole
    {
        private int _lineIndex;
        private int _secretIndex;
        private int _hiddenIndex;

        public List<string> Output { get; } = [];

        public List<string> Errors { get; } = [];

        public string ReadVisibleLine(string prompt, CancellationToken cancellationToken)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                throw new SecretOperationException(
                    VerifyDeliveryReportResultCodes.RejectedCancelled,
                    "Input was interrupted.");
            }

            if (_lineIndex >= lineResponses.Count)
            {
                throw new InvalidOperationException($"Unexpected ReadVisibleLine for prompt: {prompt}");
            }

            return lineResponses[_lineIndex++];
        }

        public string ReadSecret(string prompt, CancellationToken cancellationToken)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                throw new SecretOperationException(
                    VerifyDeliveryReportResultCodes.RejectedCancelled,
                    "Input was interrupted.");
            }

            if (_secretIndex >= secretResponses.Count)
            {
                throw new InvalidOperationException($"Unexpected ReadSecret for prompt: {prompt}");
            }

            return secretResponses[_secretIndex++];
        }

        public string ReadHiddenLine(string prompt, CancellationToken cancellationToken)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                throw new SecretOperationException(
                    VerifyDeliveryReportResultCodes.RejectedCancelled,
                    "Input was interrupted.");
            }

            if (_hiddenIndex >= hiddenResponses.Count)
            {
                throw new InvalidOperationException($"Unexpected ReadHiddenLine for prompt: {prompt}");
            }

            return hiddenResponses[_hiddenIndex++];
        }

        public void WriteLine(string message) => Output.Add(message);

        public void WriteError(string message) => Errors.Add(message);
    }

    private sealed class CancellingVisibleConsole : IAdminProviderTestAcsSendConsole
    {
        public List<string> Errors { get; } = [];

        public string ReadVisibleLine(string prompt, CancellationToken cancellationToken) =>
            throw new SecretOperationException(
                VerifyDeliveryReportResultCodes.RejectedCancelled,
                "Input was interrupted.");

        public string ReadSecret(string prompt, CancellationToken cancellationToken) =>
            throw new InvalidOperationException($"Unexpected ReadSecret: {prompt}");

        public string ReadHiddenLine(string prompt, CancellationToken cancellationToken) =>
            throw new InvalidOperationException($"Unexpected ReadHiddenLine: {prompt}");

        public void WriteLine(string message)
        {
        }

        public void WriteError(string message) => Errors.Add(message);
    }

    private sealed class TestScratch : IDisposable
    {
        private readonly string _root;

        public TestScratch()
        {
            _root = Path.Combine(
                Path.GetTempPath(),
                "amane-mailer-verify-delivery-report-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_root);
            AcsSecretPath = Path.Combine(_root, "acs_connection_string");
            QueueSecretPath = Path.Combine(_root, "queue_connection_string");
        }

        public string AcsSecretPath { get; }

        public string QueueSecretPath { get; }

        public void Dispose()
        {
            try
            {
                if (Directory.Exists(_root))
                {
                    Directory.Delete(_root, recursive: true);
                }
            }
            catch
            {
                // Best-effort cleanup for temp test dirs.
            }
        }
    }
}

public sealed class DeliveryReportQueuePollerTests
{
    private static readonly string MessageId = Guid.Parse("11111111-2222-3333-4444-555555555555").ToString("D");

    [Fact]
    public async Task Poll_returns_correlated_on_first_peek()
    {
        var peeker = new SequencePeeker(
            approximateCount: 1,
            [
                [new PeekedQueueMessageBody(BuildJson(MessageId, "Delivered"))],
            ]);
        var now = DateTimeOffset.Parse("2026-07-27T00:00:00Z");
        var poller = new DeliveryReportQueuePoller(
            peeker,
            (delay, _) =>
            {
                now += delay;
                return Task.CompletedTask;
            },
            () => now);

        var result = await poller.PollAsync(
            MessageId,
            TimeSpan.FromSeconds(30),
            TimeSpan.FromSeconds(1),
            CancellationToken.None);

        Assert.Equal(DeliveryReportPollOutcome.Correlated, result.Outcome);
        Assert.Equal("Delivered", result.DeliveryStatus);
    }

    [Fact]
    public async Task Poll_times_out_when_only_other_ids_exist()
    {
        var other = Guid.Parse("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee").ToString("D");
        var peeker = new SequencePeeker(
            approximateCount: 1,
            [
                [new PeekedQueueMessageBody(BuildJson(other, "Delivered"))],
            ]);
        var now = DateTimeOffset.Parse("2026-07-27T00:00:00Z");
        var poller = new DeliveryReportQueuePoller(
            peeker,
            (delay, _) =>
            {
                now += delay;
                return Task.CompletedTask;
            },
            () => now);

        var result = await poller.PollAsync(
            MessageId,
            TimeSpan.FromSeconds(30),
            TimeSpan.FromSeconds(1),
            CancellationToken.None);

        Assert.Equal(DeliveryReportPollOutcome.TimedOut, result.Outcome);
        Assert.True(result.SawOtherDeliveryReport);
        Assert.False(result.BacklogPreventsConfirmation);
    }

    private static string BuildJson(string messageId, string status) =>
        $$"""
        {
          "id": "eg-1",
          "eventType": "Microsoft.Communication.EmailDeliveryReportReceived",
          "eventTime": "2026-07-27T00:00:00Z",
          "data": { "messageId": "{{messageId}}", "status": "{{status}}", "recipient": "a@example.com" }
        }
        """;

    private sealed class SequencePeeker(
        int? approximateCount,
        IReadOnlyList<IReadOnlyList<PeekedQueueMessageBody>> rounds) : IAcsEventQueuePeeker
    {
        private int _index;

        public Task<IReadOnlyList<PeekedQueueMessageBody>> PeekMessagesAsync(
            int maxMessages,
            CancellationToken cancellationToken)
        {
            var round = rounds[Math.Min(_index, rounds.Count - 1)];
            _index++;
            return Task.FromResult(round);
        }

        public Task<int?> GetApproximateMessageCountAsync(CancellationToken cancellationToken) =>
            Task.FromResult(approximateCount);
    }
}
