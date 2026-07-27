using System.Net.Sockets;
using Azure;
using Amane.Mailer.Operations;
using Amane.Mailer.Operations.AcsTestSend;
using Microsoft.Extensions.Configuration;

namespace Amane.Mailer.Tests;

public sealed class MailerAdminProviderTestAcsSendCliTests
{
    private const string ValidConnectionString =
        "Endpoint=https://example.communication.azure.com/;AccessKey=abc123";

    private static readonly Guid FixedOperationId = Guid.Parse("11111111-2222-3333-4444-555555555555");

    [Fact]
    public void IsTestAcsSendCommand_matches_only_its_own_argv()
    {
        Assert.True(AdminProviderTestAcsSendCommand.IsTestAcsSendCommand(
            ["admin", "provider", "test-acs-send"]));
        Assert.False(AdminProviderTestAcsSendCommand.IsTestAcsSendCommand(
            ["admin", "provider", "register-acs"]));
        Assert.False(AdminProviderTestAcsSendCommand.IsTestAcsSendCommand(
            ["admin", "provider", "test-acs-send", "extra"]));
    }

    [Fact]
    public async Task Run_succeeds_with_secret_file_fake_provider_and_writes_message_id_handoff()
    {
        using var scratch = new TestScratch();
        File.WriteAllText(scratch.AcsSecretPath, ValidConnectionString);

        var console = new FakeConsole(
            lineResponses: ["Staging", AdminProviderTestAcsSendCommand.IntentPhrase],
            secretResponses: [],
            hiddenResponses: ["sender@example.com", "recipient@example.com"]);

        var fake = new FakeAcsClient(_ => AcsTestSendOutcome.Succeeded(FixedOperationId.ToString("D")));
        var command = CreateCommand(console, scratch, fake);

        var exitCode = await command.RunAsync(CancellationToken.None);

        Assert.Equal(AdminProviderTestAcsSendCommand.SuccessExitCode, exitCode);
        Assert.Equal(FixedOperationId.ToString("D"), File.ReadAllText(scratch.HandoffPath).Trim());
        Assert.Single(File.ReadAllLines(scratch.HandoffPath));
        Assert.Equal(2, console.HiddenReadCount);
        var output = string.Join('\n', console.Output);
        Assert.Contains("[PASS] ACS authentication", output);
        Assert.Contains("[PASS] Message ID handoff file written", output);
        Assert.DoesNotContain(ValidConnectionString, output);
        Assert.DoesNotContain("sender@example.com", output);
        Assert.DoesNotContain("recipient@example.com", output);
        Assert.DoesNotContain(FixedOperationId.ToString("D"), output);
        Assert.Equal(AdminProviderTestAcsSendCommand.SyntheticSubject, fake.LastRequest!.Subject);
    }

    [Fact]
    public async Task Run_prefers_ACS_CONNECTION_STRING_FILE_over_TTY_and_does_not_prompt_for_secret()
    {
        using var scratch = new TestScratch();
        File.WriteAllText(scratch.AcsSecretPath, ValidConnectionString);

        var console = new FakeConsole(
            lineResponses: ["Staging", AdminProviderTestAcsSendCommand.IntentPhrase],
            secretResponses: ["should-not-be-read", "should-not-be-read"],
            hiddenResponses: ["sender@example.com", "recipient@example.com"]);

        var fake = new FakeAcsClient(_ => AcsTestSendOutcome.Succeeded(FixedOperationId.ToString("D")));
        var command = CreateCommand(console, scratch, fake);

        var exitCode = await command.RunAsync(CancellationToken.None);

        Assert.Equal(AdminProviderTestAcsSendCommand.SuccessExitCode, exitCode);
        Assert.Equal(0, console.SecretReadCount);
        Assert.Contains("Using ACS connection string from configured secret file.", console.Output);
    }

    [Fact]
    public async Task Run_falls_back_to_TTY_secret_when_no_secret_file_is_configured()
    {
        using var scratch = new TestScratch();
        var console = new FakeConsole(
            lineResponses: ["Staging", AdminProviderTestAcsSendCommand.IntentPhrase, scratch.HandoffPath],
            secretResponses: [ValidConnectionString, ValidConnectionString],
            hiddenResponses: ["sender@example.com", "recipient@example.com"]);

        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>())
            .Build();
        var fake = new FakeAcsClient(_ => AcsTestSendOutcome.Succeeded(FixedOperationId.ToString("D")));
        var command = new AdminProviderTestAcsSendCommand(
            console,
            configuration,
            fake,
            () => FixedOperationId);

        var exitCode = await command.RunAsync(CancellationToken.None);

        Assert.Equal(AdminProviderTestAcsSendCommand.SuccessExitCode, exitCode);
        Assert.Equal(2, console.SecretReadCount);
        Assert.Equal(FixedOperationId.ToString("D"), File.ReadAllText(scratch.HandoffPath).Trim());
    }

    [Fact]
    public async Task Run_rejects_environment_mismatch_before_any_send()
    {
        using var scratch = new TestScratch();
        var console = new FakeConsole(lineResponses: ["staging"], secretResponses: [], hiddenResponses: []);
        var fake = new FakeAcsClient(_ => throw new InvalidOperationException("should not send"));
        var command = CreateCommand(console, scratch, fake);

        var exitCode = await command.RunAsync(CancellationToken.None);

        Assert.Equal(AdminProviderTestAcsSendCommand.RejectedExitCode, exitCode);
        Assert.Equal(0, fake.CallCount);
        Assert.Contains(
            AdminProviderTestAcsSendResultCodes.RejectedEnvironmentMismatch,
            string.Join('\n', console.Errors));
    }

    [Fact]
    public async Task Run_rejects_intent_mismatch_before_any_send()
    {
        using var scratch = new TestScratch();
        var console = new FakeConsole(
            lineResponses: ["Staging", "WRONG"],
            secretResponses: [],
            hiddenResponses: []);
        var fake = new FakeAcsClient(_ => throw new InvalidOperationException("should not send"));
        var command = CreateCommand(console, scratch, fake);

        var exitCode = await command.RunAsync(CancellationToken.None);

        Assert.Equal(AdminProviderTestAcsSendCommand.RejectedExitCode, exitCode);
        Assert.Equal(0, fake.CallCount);
        Assert.Contains(
            AdminProviderTestAcsSendResultCodes.RejectedIntentMismatch,
            string.Join('\n', console.Errors));
    }

    [Fact]
    public async Task Run_rejects_invalid_recipient_email()
    {
        using var scratch = new TestScratch();
        File.WriteAllText(scratch.AcsSecretPath, ValidConnectionString);
        var console = new FakeConsole(
            lineResponses: ["Staging", AdminProviderTestAcsSendCommand.IntentPhrase],
            secretResponses: [],
            hiddenResponses: ["sender@example.com", "not-an-email"]);
        var fake = new FakeAcsClient(_ => throw new InvalidOperationException("should not send"));
        var command = CreateCommand(console, scratch, fake);

        var exitCode = await command.RunAsync(CancellationToken.None);

        Assert.Equal(AdminProviderTestAcsSendCommand.RejectedExitCode, exitCode);
        Assert.Equal(0, fake.CallCount);
        Assert.Contains(
            AdminProviderTestAcsSendResultCodes.RejectedInvalidRecipientEmail,
            string.Join('\n', console.Errors));
    }

    [Fact]
    public async Task Run_rejects_drive_relative_handoff_path_on_windows_shape()
    {
        using var scratch = new TestScratch();
        File.WriteAllText(scratch.AcsSecretPath, ValidConnectionString);
        var console = new FakeConsole(
            lineResponses: ["Staging", AdminProviderTestAcsSendCommand.IntentPhrase, "C:message-id.txt"],
            secretResponses: [],
            hiddenResponses: ["sender@example.com", "recipient@example.com"]);
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ACS_CONNECTION_STRING_FILE"] = scratch.AcsSecretPath,
            })
            .Build();
        var fake = new FakeAcsClient(_ => AcsTestSendOutcome.Succeeded(FixedOperationId.ToString("D")));
        var command = new AdminProviderTestAcsSendCommand(
            console,
            configuration,
            fake,
            () => FixedOperationId);

        var exitCode = await command.RunAsync(CancellationToken.None);

        Assert.Equal(AdminProviderTestAcsSendCommand.RejectedExitCode, exitCode);
        Assert.Equal(0, fake.CallCount);
        Assert.Contains(
            AdminProviderTestAcsSendResultCodes.RejectedMessageIdHandoffPathInvalid,
            string.Join('\n', console.Errors));
    }

    [Fact]
    public async Task Run_distinguishes_authentication_failure()
    {
        using var scratch = new TestScratch();
        File.WriteAllText(scratch.AcsSecretPath, ValidConnectionString);
        var console = new FakeConsole(
            lineResponses: ["Staging", AdminProviderTestAcsSendCommand.IntentPhrase],
            secretResponses: [],
            hiddenResponses: ["sender@example.com", "recipient@example.com"]);
        var fake = new FakeAcsClient(_ =>
            AcsTestSendOutcome.Failed(
                AdminProviderTestAcsSendResultCodes.FailedAcsAuthentication,
                authenticationState: AcsEvaluationState.Failed));
        var command = CreateCommand(console, scratch, fake);

        var exitCode = await command.RunAsync(CancellationToken.None);

        Assert.Equal(AdminProviderTestAcsSendCommand.FailureExitCode, exitCode);
        Assert.False(File.Exists(scratch.HandoffPath));
        var joined = string.Join('\n', console.Output) + "\n" + string.Join('\n', console.Errors);
        Assert.Contains("[FAIL] ACS authentication", joined);
        Assert.Contains(AdminProviderTestAcsSendResultCodes.FailedAcsAuthentication, joined);
        Assert.DoesNotContain("[FAIL] ACS network reachability", joined);
    }

    [Fact]
    public async Task Run_rejects_not_set_and_mismatched_message_ids_without_writing_handoff()
    {
        using var scratch = new TestScratch();
        File.WriteAllText(scratch.AcsSecretPath, ValidConnectionString);

        foreach (var badId in new[] { "NOT_SET", "not-a-guid", "22222222-2222-2222-2222-222222222222", "id\nextra" })
        {
            var console = new FakeConsole(
                lineResponses: ["Staging", AdminProviderTestAcsSendCommand.IntentPhrase],
                secretResponses: [],
                hiddenResponses: ["sender@example.com", "recipient@example.com"]);
            var fake = new FakeAcsClient(_ =>
                AcsTestSendOutcome.Succeeded(badId));
            var command = CreateCommand(console, scratch, fake);

            var exitCode = await command.RunAsync(CancellationToken.None);

            Assert.Equal(AdminProviderTestAcsSendCommand.FailureExitCode, exitCode);
            Assert.False(File.Exists(scratch.HandoffPath));
            Assert.Contains(
                AdminProviderTestAcsSendResultCodes.FailedAcsMessageIdInvalid,
                string.Join('\n', console.Errors));
        }
    }

    [Fact]
    public void RequireCanonicalMessageId_canonicalizes_matching_uuid()
    {
        var canonical = AdminProviderTestAcsSendCommand.RequireCanonicalMessageId(
            FixedOperationId.ToString("D").ToUpperInvariant(),
            FixedOperationId);
        Assert.Equal(FixedOperationId.ToString("D"), canonical);
    }

    [Fact]
    public async Task Run_maps_cooperative_cancellation_to_OperationCanceledException()
    {
        using var scratch = new TestScratch();
        File.WriteAllText(scratch.AcsSecretPath, ValidConnectionString);
        var console = new FakeConsole(
            lineResponses: ["Staging", AdminProviderTestAcsSendCommand.IntentPhrase],
            secretResponses: [],
            hiddenResponses: ["sender@example.com", "recipient@example.com"]);
        using var cts = new CancellationTokenSource();
        var fake = new FakeAcsClient(_ =>
        {
            cts.Cancel();
            cts.Token.ThrowIfCancellationRequested();
            return AcsTestSendOutcome.Succeeded(FixedOperationId.ToString("D"));
        });
        var command = CreateCommand(console, scratch, fake);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => command.RunAsync(cts.Token));
        Assert.False(File.Exists(scratch.HandoffPath));
    }

    [Fact]
    public async Task Run_does_not_modify_existing_acs_secret_file()
    {
        using var scratch = new TestScratch();
        File.WriteAllText(scratch.AcsSecretPath, ValidConnectionString);
        var before = File.GetLastWriteTimeUtc(scratch.AcsSecretPath);
        var console = new FakeConsole(
            lineResponses: ["Staging", AdminProviderTestAcsSendCommand.IntentPhrase],
            secretResponses: [],
            hiddenResponses: ["sender@example.com", "recipient@example.com"]);
        var fake = new FakeAcsClient(_ => AcsTestSendOutcome.Succeeded(FixedOperationId.ToString("D")));
        var command = CreateCommand(console, scratch, fake);

        _ = await command.RunAsync(CancellationToken.None);

        Assert.Equal(ValidConnectionString, File.ReadAllText(scratch.AcsSecretPath));
        Assert.Equal(before, File.GetLastWriteTimeUtc(scratch.AcsSecretPath));
    }

    [Fact]
    public async Task Run_rejects_existing_handoff_file_before_provider_call()
    {
        using var scratch = new TestScratch();
        File.WriteAllText(scratch.AcsSecretPath, ValidConnectionString);
        const string staleId = "99999999-9999-9999-9999-999999999999";
        File.WriteAllText(scratch.HandoffPath, staleId + Environment.NewLine);
        var beforeWrite = File.GetLastWriteTimeUtc(scratch.HandoffPath);

        var console = new FakeConsole(
            lineResponses: ["Staging", AdminProviderTestAcsSendCommand.IntentPhrase],
            secretResponses: [],
            hiddenResponses: ["sender@example.com", "recipient@example.com"]);
        var fake = new FakeAcsClient(_ => AcsTestSendOutcome.Succeeded(FixedOperationId.ToString("D")));
        var command = CreateCommand(console, scratch, fake);

        var exitCode = await command.RunAsync(CancellationToken.None);

        Assert.Equal(AdminProviderTestAcsSendCommand.RejectedExitCode, exitCode);
        Assert.Equal(0, fake.CallCount);
        Assert.Equal(staleId, File.ReadAllText(scratch.HandoffPath).Trim());
        Assert.Equal(beforeWrite, File.GetLastWriteTimeUtc(scratch.HandoffPath));
        Assert.Contains(
            AdminProviderTestAcsSendResultCodes.RejectedMessageIdHandoffPathExists,
            string.Join('\n', console.Errors));
    }

    [Fact]
    public async Task Run_shows_network_failure_without_authentication_fail_line()
    {
        using var scratch = new TestScratch();
        File.WriteAllText(scratch.AcsSecretPath, ValidConnectionString);
        var console = new FakeConsole(
            lineResponses: ["Staging", AdminProviderTestAcsSendCommand.IntentPhrase],
            secretResponses: [],
            hiddenResponses: ["sender@example.com", "recipient@example.com"]);
        var fake = new FakeAcsClient(_ =>
            AcsTestSendOutcome.Failed(
                AdminProviderTestAcsSendResultCodes.FailedAcsNetwork,
                authenticationState: AcsEvaluationState.NotEvaluated));
        var command = CreateCommand(console, scratch, fake);

        var exitCode = await command.RunAsync(CancellationToken.None);

        Assert.Equal(AdminProviderTestAcsSendCommand.FailureExitCode, exitCode);
        Assert.False(File.Exists(scratch.HandoffPath));
        var joined = string.Join('\n', console.Output) + "\n" + string.Join('\n', console.Errors);
        Assert.Contains("[FAIL] ACS network reachability", joined);
        Assert.Contains(AdminProviderTestAcsSendResultCodes.FailedAcsNetwork, joined);
        Assert.DoesNotContain("[FAIL] ACS authentication", joined);
        Assert.DoesNotContain("[PASS] ACS authentication", joined);
    }

    [Fact]
    public async Task Run_shows_timeout_failure_without_authentication_pass_line()
    {
        using var scratch = new TestScratch();
        File.WriteAllText(scratch.AcsSecretPath, ValidConnectionString);
        var console = new FakeConsole(
            lineResponses: ["Staging", AdminProviderTestAcsSendCommand.IntentPhrase],
            secretResponses: [],
            hiddenResponses: ["sender@example.com", "recipient@example.com"]);
        var fake = new FakeAcsClient(_ =>
            AcsTestSendOutcome.Failed(
                AdminProviderTestAcsSendResultCodes.FailedAcsTimeout,
                authenticationState: AcsEvaluationState.NotEvaluated));
        var command = CreateCommand(console, scratch, fake);

        var exitCode = await command.RunAsync(CancellationToken.None);

        Assert.Equal(AdminProviderTestAcsSendCommand.FailureExitCode, exitCode);
        Assert.False(File.Exists(scratch.HandoffPath));
        var joined = string.Join('\n', console.Output) + "\n" + string.Join('\n', console.Errors);
        Assert.Contains(AdminProviderTestAcsSendResultCodes.FailedAcsTimeout, joined);
        Assert.DoesNotContain("[FAIL] ACS authentication", joined);
        Assert.DoesNotContain("[PASS] ACS authentication", joined);
    }

    [Fact]
    public async Task Run_maps_cancel_key_press_during_prompt_to_rejected_cancelled_not_exit_130()
    {
        using var scratch = new TestScratch();
        using var cts = new CancellationTokenSource();
        var console = new CancelKeyPressDuringPromptConsole(cts);
        var fake = new FakeAcsClient(_ => throw new InvalidOperationException("should not send"));
        var command = CreateCommand(console, scratch, fake);

        var exitCode = await command.RunAsync(cts.Token);

        Assert.Equal(AdminProviderTestAcsSendCommand.RejectedExitCode, exitCode);
        Assert.Equal(0, fake.CallCount);
        Assert.Contains(
            AdminProviderTestAcsSendResultCodes.RejectedCancelled,
            string.Join('\n', console.Errors));
    }

    [Fact]
    public async Task Run_maps_visible_line_ctrl_c_to_rejected_cancelled()
    {
        using var scratch = new TestScratch();
        var console = new CancellingVisibleConsole();
        var fake = new FakeAcsClient(_ => throw new InvalidOperationException("should not send"));
        var command = CreateCommand(console, scratch, fake);

        var exitCode = await command.RunAsync(CancellationToken.None);

        Assert.Equal(AdminProviderTestAcsSendCommand.RejectedExitCode, exitCode);
        Assert.Equal(0, fake.CallCount);
        Assert.Contains(
            AdminProviderTestAcsSendResultCodes.RejectedCancelled,
            string.Join('\n', console.Errors));
    }

    private static AdminProviderTestAcsSendCommand CreateCommand(
        IAdminProviderTestAcsSendConsole console,
        TestScratch scratch,
        FakeAcsClient fake)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ACS_CONNECTION_STRING_FILE"] = scratch.AcsSecretPath,
                ["MAILER_ACS_TEST_SEND_MESSAGE_ID_FILE"] = scratch.HandoffPath,
            })
            .Build();

        return new AdminProviderTestAcsSendCommand(
            console,
            configuration,
            fake,
            () => FixedOperationId);
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

        public int SecretReadCount => _secretIndex;

        public int HiddenReadCount => _hiddenIndex;

        public string ReadVisibleLine(string prompt, CancellationToken cancellationToken)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                throw new SecretOperationException(
                    AdminProviderTestAcsSendResultCodes.RejectedCancelled,
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
                    AdminProviderTestAcsSendResultCodes.RejectedCancelled,
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
                    AdminProviderTestAcsSendResultCodes.RejectedCancelled,
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
                AdminProviderTestAcsSendResultCodes.RejectedCancelled,
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

    /// <summary>
    /// Simulates Linux PTY Ctrl+C: CancelKeyPress cancels the shared token while a prompt is
    /// active; the console must map that to REJECTED_CANCELLED (exit 2), not exit 130.
    /// </summary>
    private sealed class CancelKeyPressDuringPromptConsole(CancellationTokenSource cts)
        : IAdminProviderTestAcsSendConsole
    {
        public List<string> Errors { get; } = [];

        public string ReadVisibleLine(string prompt, CancellationToken cancellationToken)
        {
            cts.Cancel();
            if (cancellationToken.IsCancellationRequested)
            {
                throw new SecretOperationException(
                    AdminProviderTestAcsSendResultCodes.RejectedCancelled,
                    "Input was interrupted.");
            }

            throw new InvalidOperationException("Expected cancellation during prompt.");
        }

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
                "amane-mailer-test-acs-send-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_root);
            AcsSecretPath = Path.Combine(_root, "acs_connection_string");
            HandoffPath = Path.Combine(_root, "message-id.txt");
        }

        public string AcsSecretPath { get; }

        public string HandoffPath { get; }

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

public sealed class AzureAcsTestSendClientTests
{
    private static readonly Guid OperationId = Guid.Parse("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee");

    private static AcsTestSendRequest Request() =>
        new()
        {
            ConnectionString = "Endpoint=https://example.communication.azure.com/;AccessKey=abc123",
            SenderEmail = "sender@example.com",
            RecipientEmail = "recipient@example.com",
            Subject = AdminProviderTestAcsSendCommand.SyntheticSubject,
            PlainTextBody = AdminProviderTestAcsSendCommand.SyntheticPlainTextBody,
            OperationId = OperationId,
        };

    [Fact]
    public async Task Maps_401_to_authentication_failure_without_leaking_raw_text()
    {
        var client = new AzureAcsTestSendClient(new ThrowingTransport(
            new RequestFailedException(
                401,
                "Unauthorized endpoint=https://leak.example/;accesskey=SECRETKEY sender@example.com")));

        var outcome = await client.SendAsync(Request(), CancellationToken.None);

        Assert.Equal(AdminProviderTestAcsSendResultCodes.FailedAcsAuthentication, outcome.CanonicalFailureCode);
        Assert.Equal(AcsEvaluationState.Failed, outcome.AuthenticationState);
    }

    [Fact]
    public async Task Maps_network_exception_to_network_failure_not_authentication()
    {
        var client = new AzureAcsTestSendClient(new ThrowingTransport(new SocketException()));

        var outcome = await client.SendAsync(Request(), CancellationToken.None);

        Assert.Equal(AdminProviderTestAcsSendResultCodes.FailedAcsNetwork, outcome.CanonicalFailureCode);
        Assert.Equal(AcsEvaluationState.NotEvaluated, outcome.AuthenticationState);
        Assert.NotEqual(AdminProviderTestAcsSendResultCodes.FailedAcsAuthentication, outcome.CanonicalFailureCode);
    }

    [Fact]
    public async Task Maps_generic_400_to_send_request_failure()
    {
        var client = new AzureAcsTestSendClient(new ThrowingTransport(
            new RequestFailedException(400, "Bad Request")));

        var outcome = await client.SendAsync(Request(), CancellationToken.None);

        Assert.Equal(AdminProviderTestAcsSendResultCodes.FailedAcsSendRequest, outcome.CanonicalFailureCode);
    }

    [Fact]
    public async Task Maps_structured_sender_error_code_to_sender_rejected()
    {
        var client = new AzureAcsTestSendClient(new ThrowingTransport(
            new RequestFailedException(
                400,
                "Sender not authorized",
                "SenderNotRecognized",
                null)));

        var outcome = await client.SendAsync(Request(), CancellationToken.None);

        Assert.Equal(AdminProviderTestAcsSendResultCodes.FailedAcsSenderRejected, outcome.CanonicalFailureCode);
        Assert.Equal(AcsEvaluationState.Succeeded, outcome.AuthenticationState);
    }

    [Fact]
    public async Task Maps_404_to_send_request_failure_not_sender_rejected()
    {
        var client = new AzureAcsTestSendClient(new ThrowingTransport(
            new RequestFailedException(404, "Not Found")));

        var outcome = await client.SendAsync(Request(), CancellationToken.None);

        Assert.Equal(AdminProviderTestAcsSendResultCodes.FailedAcsSendRequest, outcome.CanonicalFailureCode);
    }

    [Fact]
    public async Task Maps_429_to_timeout_bucket_without_claiming_authentication()
    {
        var client = new AzureAcsTestSendClient(new ThrowingTransport(
            new RequestFailedException(429, "Too Many Requests")));

        var outcome = await client.SendAsync(Request(), CancellationToken.None);

        Assert.Equal(AdminProviderTestAcsSendResultCodes.FailedAcsTimeout, outcome.CanonicalFailureCode);
        Assert.Equal(AcsEvaluationState.NotEvaluated, outcome.AuthenticationState);
    }

    [Fact]
    public async Task Maps_408_to_timeout_bucket_without_claiming_authentication()
    {
        var client = new AzureAcsTestSendClient(new ThrowingTransport(
            new RequestFailedException(408, "Request Timeout")));

        var outcome = await client.SendAsync(Request(), CancellationToken.None);

        Assert.Equal(AdminProviderTestAcsSendResultCodes.FailedAcsTimeout, outcome.CanonicalFailureCode);
        Assert.Equal(AcsEvaluationState.NotEvaluated, outcome.AuthenticationState);
    }

    [Fact]
    public async Task Maps_5xx_to_timeout_bucket_without_claiming_authentication()
    {
        var client = new AzureAcsTestSendClient(new ThrowingTransport(
            new RequestFailedException(503, "Service Unavailable")));

        var outcome = await client.SendAsync(Request(), CancellationToken.None);

        Assert.Equal(AdminProviderTestAcsSendResultCodes.FailedAcsTimeout, outcome.CanonicalFailureCode);
        Assert.Equal(AcsEvaluationState.NotEvaluated, outcome.AuthenticationState);
    }

    [Fact]
    public async Task Maps_timeout_exception_without_claiming_authentication()
    {
        var client = new AzureAcsTestSendClient(new ThrowingTransport(new TimeoutException("timed out")));

        var outcome = await client.SendAsync(Request(), CancellationToken.None);

        Assert.Equal(AdminProviderTestAcsSendResultCodes.FailedAcsTimeout, outcome.CanonicalFailureCode);
        Assert.Equal(AcsEvaluationState.NotEvaluated, outcome.AuthenticationState);
    }

    [Fact]
    public async Task Maps_non_cooperative_operation_canceled_without_claiming_authentication()
    {
        var client = new AzureAcsTestSendClient(new ThrowingTransport(new OperationCanceledException()));

        var outcome = await client.SendAsync(Request(), CancellationToken.None);

        Assert.Equal(AdminProviderTestAcsSendResultCodes.FailedAcsTimeout, outcome.CanonicalFailureCode);
        Assert.Equal(AcsEvaluationState.NotEvaluated, outcome.AuthenticationState);
    }

    [Fact]
    public async Task Maps_lro_failure_without_leaking_operation_id_in_code()
    {
        var client = new AzureAcsTestSendClient(new FixedTransport(
            new AcsEmailSendTransportResult
            {
                OperationId = OperationId.ToString("D"),
                Succeeded = false,
            }));

        var outcome = await client.SendAsync(Request(), CancellationToken.None);

        Assert.Equal(AdminProviderTestAcsSendResultCodes.FailedAcsOperation, outcome.CanonicalFailureCode);
        Assert.True(outcome.SendRequestAccepted);
        Assert.Equal(OperationId.ToString("D"), outcome.ProviderMessageId);
    }

    [Fact]
    public async Task Propagates_cooperative_cancellation()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var client = new AzureAcsTestSendClient(new ThrowingTransport(
            new OperationCanceledException(cts.Token)));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => client.SendAsync(Request(), cts.Token));
    }

    private sealed class ThrowingTransport(Exception exception) : IAcsEmailSendTransport
    {
        public Task<AcsEmailSendTransportResult> SendAndWaitAsync(
            AcsTestSendRequest request,
            CancellationToken cancellationToken) =>
            Task.FromException<AcsEmailSendTransportResult>(exception);
    }

    private sealed class FixedTransport(AcsEmailSendTransportResult result) : IAcsEmailSendTransport
    {
        public Task<AcsEmailSendTransportResult> SendAndWaitAsync(
            AcsTestSendRequest request,
            CancellationToken cancellationToken) =>
            Task.FromResult(result);
    }
}
