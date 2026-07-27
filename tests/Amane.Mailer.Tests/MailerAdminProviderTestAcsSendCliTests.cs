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
            lineResponses:
            [
                "Staging",
                AdminProviderTestAcsSendCommand.IntentPhrase,
                "sender@example.com",
                "recipient@example.com",
                "",
            ],
            secretResponses: []);

        var fake = new FakeAcsClient(_ => AcsTestSendOutcome.Succeeded(FixedOperationId.ToString("D")));
        var command = CreateCommand(console, scratch, fake);

        var exitCode = await command.RunAsync(CancellationToken.None);

        Assert.Equal(AdminProviderTestAcsSendCommand.SuccessExitCode, exitCode);
        Assert.Equal(FixedOperationId.ToString("D"), File.ReadAllText(scratch.HandoffPath).Trim());
        Assert.Equal(ValidConnectionString, File.ReadAllText(scratch.AcsSecretPath));
        var output = string.Join('\n', console.Output);
        Assert.Contains("[PASS] ACS authentication", output);
        Assert.Contains("[PASS] Send request accepted", output);
        Assert.Contains("[PASS] ACS send operation completed", output);
        Assert.Contains("[PASS] Message ID handoff file written", output);
        Assert.Contains("[ACTION] Confirm receipt in the test mailbox", output);
        Assert.Contains(AdminProviderTestAcsSendResultCodes.Success, output);
        Assert.DoesNotContain(ValidConnectionString, output);
        Assert.DoesNotContain("sender@example.com", output);
        Assert.DoesNotContain("recipient@example.com", output);
        Assert.DoesNotContain(AdminProviderTestAcsSendCommand.SyntheticSubject, output);
        Assert.DoesNotContain(AdminProviderTestAcsSendCommand.SyntheticPlainTextBody, output);
        Assert.DoesNotContain(FixedOperationId.ToString("D"), output);
        Assert.Equal(1, fake.CallCount);
        Assert.Equal(AdminProviderTestAcsSendCommand.SyntheticSubject, fake.LastRequest!.Subject);
        Assert.Equal(AdminProviderTestAcsSendCommand.SyntheticPlainTextBody, fake.LastRequest.PlainTextBody);
    }

    [Fact]
    public async Task Run_prefers_ACS_CONNECTION_STRING_FILE_over_TTY_and_does_not_prompt_for_secret()
    {
        using var scratch = new TestScratch();
        File.WriteAllText(scratch.AcsSecretPath, ValidConnectionString);

        var console = new FakeConsole(
            lineResponses:
            [
                "Staging",
                AdminProviderTestAcsSendCommand.IntentPhrase,
                "sender@example.com",
                "recipient@example.com",
                "",
            ],
            secretResponses: ["should-not-be-read", "should-not-be-read"]);

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
            lineResponses:
            [
                "Staging",
                AdminProviderTestAcsSendCommand.IntentPhrase,
                "sender@example.com",
                "recipient@example.com",
                "",
                scratch.HandoffPath,
            ],
            secretResponses: [ValidConnectionString, ValidConnectionString]);

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
        var console = new FakeConsole(lineResponses: ["staging"], secretResponses: []);
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
            secretResponses: []);
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
            lineResponses:
            [
                "Staging",
                AdminProviderTestAcsSendCommand.IntentPhrase,
                "sender@example.com",
                "not-an-email",
            ],
            secretResponses: []);
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
    public async Task Run_distinguishes_authentication_failure()
    {
        using var scratch = new TestScratch();
        File.WriteAllText(scratch.AcsSecretPath, ValidConnectionString);
        var console = new FakeConsole(
            lineResponses:
            [
                "Staging",
                AdminProviderTestAcsSendCommand.IntentPhrase,
                "sender@example.com",
                "recipient@example.com",
                "",
            ],
            secretResponses: []);
        var fake = new FakeAcsClient(_ =>
            AcsTestSendOutcome.Failed(AdminProviderTestAcsSendResultCodes.FailedAcsAuthentication));
        var command = CreateCommand(console, scratch, fake);

        var exitCode = await command.RunAsync(CancellationToken.None);

        Assert.Equal(AdminProviderTestAcsSendCommand.FailureExitCode, exitCode);
        Assert.False(File.Exists(scratch.HandoffPath));
        var joined = string.Join('\n', console.Output) + "\n" + string.Join('\n', console.Errors);
        Assert.Contains("[FAIL] ACS authentication", joined);
        Assert.Contains(AdminProviderTestAcsSendResultCodes.FailedAcsAuthentication, joined);
        Assert.DoesNotContain(FixedOperationId.ToString("D"), joined);
    }

    [Fact]
    public async Task Run_distinguishes_sender_rejection_after_authentication()
    {
        using var scratch = new TestScratch();
        File.WriteAllText(scratch.AcsSecretPath, ValidConnectionString);
        var console = new FakeConsole(
            lineResponses:
            [
                "Staging",
                AdminProviderTestAcsSendCommand.IntentPhrase,
                "sender@example.com",
                "recipient@example.com",
                "",
            ],
            secretResponses: []);
        var fake = new FakeAcsClient(_ =>
            AcsTestSendOutcome.Failed(
                AdminProviderTestAcsSendResultCodes.FailedAcsSenderRejected,
                authenticationSucceeded: true));
        var command = CreateCommand(console, scratch, fake);

        var exitCode = await command.RunAsync(CancellationToken.None);

        Assert.Equal(AdminProviderTestAcsSendCommand.FailureExitCode, exitCode);
        var joined = string.Join('\n', console.Output) + "\n" + string.Join('\n', console.Errors);
        Assert.Contains("[PASS] ACS authentication", joined);
        Assert.Contains("[FAIL] Send request accepted", joined);
        Assert.Contains(AdminProviderTestAcsSendResultCodes.FailedAcsSenderRejected, joined);
    }

    [Fact]
    public async Task Run_distinguishes_operation_failure_after_accept()
    {
        using var scratch = new TestScratch();
        File.WriteAllText(scratch.AcsSecretPath, ValidConnectionString);
        var console = new FakeConsole(
            lineResponses:
            [
                "Staging",
                AdminProviderTestAcsSendCommand.IntentPhrase,
                "sender@example.com",
                "recipient@example.com",
                "",
            ],
            secretResponses: []);
        var fake = new FakeAcsClient(_ =>
            AcsTestSendOutcome.Failed(
                AdminProviderTestAcsSendResultCodes.FailedAcsOperation,
                authenticationSucceeded: true,
                sendRequestAccepted: true,
                providerMessageId: FixedOperationId.ToString("D")));
        var command = CreateCommand(console, scratch, fake);

        var exitCode = await command.RunAsync(CancellationToken.None);

        Assert.Equal(AdminProviderTestAcsSendCommand.FailureExitCode, exitCode);
        Assert.False(File.Exists(scratch.HandoffPath));
        var joined = string.Join('\n', console.Output) + "\n" + string.Join('\n', console.Errors);
        Assert.Contains("[PASS] Send request accepted", joined);
        Assert.Contains("[FAIL] ACS send operation completed", joined);
        Assert.DoesNotContain(FixedOperationId.ToString("D"), joined);
    }

    [Fact]
    public async Task Run_maps_cooperative_cancellation_to_OperationCanceledException()
    {
        using var scratch = new TestScratch();
        File.WriteAllText(scratch.AcsSecretPath, ValidConnectionString);
        var console = new FakeConsole(
            lineResponses:
            [
                "Staging",
                AdminProviderTestAcsSendCommand.IntentPhrase,
                "sender@example.com",
                "recipient@example.com",
                "",
            ],
            secretResponses: []);
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
    public async Task Run_rejects_relative_message_id_handoff_path()
    {
        using var scratch = new TestScratch();
        File.WriteAllText(scratch.AcsSecretPath, ValidConnectionString);
        var console = new FakeConsole(
            lineResponses:
            [
                "Staging",
                AdminProviderTestAcsSendCommand.IntentPhrase,
                "sender@example.com",
                "recipient@example.com",
                "",
                "relative/handoff.txt",
            ],
            secretResponses: []);
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
    public async Task Run_does_not_modify_existing_acs_secret_file()
    {
        using var scratch = new TestScratch();
        File.WriteAllText(scratch.AcsSecretPath, ValidConnectionString);
        var before = File.GetLastWriteTimeUtc(scratch.AcsSecretPath);
        var console = new FakeConsole(
            lineResponses:
            [
                "Staging",
                AdminProviderTestAcsSendCommand.IntentPhrase,
                "sender@example.com",
                "recipient@example.com",
                "",
            ],
            secretResponses: []);
        var fake = new FakeAcsClient(_ => AcsTestSendOutcome.Succeeded(FixedOperationId.ToString("D")));
        var command = CreateCommand(console, scratch, fake);

        _ = await command.RunAsync(CancellationToken.None);

        Assert.Equal(ValidConnectionString, File.ReadAllText(scratch.AcsSecretPath));
        Assert.Equal(before, File.GetLastWriteTimeUtc(scratch.AcsSecretPath));
    }

    private static AdminProviderTestAcsSendCommand CreateCommand(
        FakeConsole console,
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
        IReadOnlyList<string> secretResponses)
        : IAdminProviderRegisterAcsConsole
    {
        private int _lineIndex;
        private int _secretIndex;

        public List<string> Output { get; } = [];

        public List<string> Errors { get; } = [];

        public int SecretReadCount => _secretIndex;

        public string ReadLine(string prompt)
        {
            if (_lineIndex >= lineResponses.Count)
            {
                throw new InvalidOperationException($"Unexpected ReadLine for prompt: {prompt}");
            }

            return lineResponses[_lineIndex++];
        }

        public string ReadSecret(string prompt)
        {
            if (_secretIndex >= secretResponses.Count)
            {
                throw new InvalidOperationException($"Unexpected ReadSecret for prompt: {prompt}");
            }

            return secretResponses[_secretIndex++];
        }

        public void WriteLine(string message) => Output.Add(message);

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
