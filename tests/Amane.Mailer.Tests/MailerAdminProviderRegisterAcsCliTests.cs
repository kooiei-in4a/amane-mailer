using Amane.Mailer.Configuration;
using Amane.Mailer.Operations;
using Amane.Mailer.Tests.TestSupport;

namespace Amane.Mailer.Tests;

public sealed class MailerAdminProviderRegisterAcsCliTests
{
    private const string ValidConnectionString = "Endpoint=https://example.communication.azure.com/;AccessKey=abc123";

    [Fact]
    public void Run_succeeds_and_writes_both_files_on_a_clean_directory()
    {
        using var scratch = new ScratchDirectories();
        var console = new FakeConsole(
            lineResponses: ["Staging", AdminProviderRegisterAcsCommand.IntentPhrase, "sender@example.com", "Example Sender"],
            secretResponses: [ValidConnectionString, ValidConnectionString]);
        var command = new AdminProviderRegisterAcsCommand(console, scratch.AcsDir, scratch.SenderDir);

        var exitCode = command.Run();

        Assert.Equal(0, exitCode);
        Assert.Equal(ValidConnectionString, File.ReadAllText(scratch.AcsFilePath));
        Assert.Contains("sender@example.com", File.ReadAllText(scratch.SenderFilePath));
        Assert.Contains(AdminProviderRegisterAcsResultCodes.Success, string.Join('\n', console.Output));
    }

    [Fact]
    public void Run_rejects_and_writes_nothing_when_environment_confirmation_does_not_match_exactly()
    {
        using var scratch = new ScratchDirectories();
        var console = new FakeConsole(
            lineResponses: ["staging"],
            secretResponses: []);
        var command = new AdminProviderRegisterAcsCommand(console, scratch.AcsDir, scratch.SenderDir);

        var exitCode = command.Run();

        Assert.Equal(2, exitCode);
        Assert.False(File.Exists(scratch.AcsFilePath));
        Assert.False(File.Exists(scratch.SenderFilePath));
        Assert.Contains(AdminProviderRegisterAcsResultCodes.RejectedEnvironmentMismatch, string.Join('\n', console.Errors));
    }

    [Fact]
    public void Run_rejects_when_intent_phrase_does_not_match()
    {
        using var scratch = new ScratchDirectories();
        var console = new FakeConsole(
            lineResponses: ["Staging", "WRONG-PHRASE"],
            secretResponses: []);
        var command = new AdminProviderRegisterAcsCommand(console, scratch.AcsDir, scratch.SenderDir);

        var exitCode = command.Run();

        Assert.Equal(2, exitCode);
        Assert.Contains(AdminProviderRegisterAcsResultCodes.RejectedIntentMismatch, string.Join('\n', console.Errors));
    }

    [Fact]
    public void Run_rejects_when_connection_string_confirmation_does_not_match()
    {
        using var scratch = new ScratchDirectories();
        var console = new FakeConsole(
            lineResponses: ["Staging", AdminProviderRegisterAcsCommand.IntentPhrase],
            secretResponses: [ValidConnectionString, "different-value"]);
        var command = new AdminProviderRegisterAcsCommand(console, scratch.AcsDir, scratch.SenderDir);

        var exitCode = command.Run();

        Assert.Equal(2, exitCode);
        Assert.False(File.Exists(scratch.AcsFilePath));
        Assert.Contains(AdminProviderRegisterAcsResultCodes.RejectedSecretMismatch, string.Join('\n', console.Errors));
    }

    [Fact]
    public void Run_rejects_a_connection_string_that_does_not_look_like_an_acs_value()
    {
        using var scratch = new ScratchDirectories();
        var console = new FakeConsole(
            lineResponses: ["Staging", AdminProviderRegisterAcsCommand.IntentPhrase],
            secretResponses: ["not-a-connection-string", "not-a-connection-string"]);
        var command = new AdminProviderRegisterAcsCommand(console, scratch.AcsDir, scratch.SenderDir);

        var exitCode = command.Run();

        Assert.Equal(2, exitCode);
        Assert.Contains(AdminProviderRegisterAcsResultCodes.RejectedInvalidConnectionString, string.Join('\n', console.Errors));
    }

    [Fact]
    public void Run_rejects_an_invalid_sender_email()
    {
        using var scratch = new ScratchDirectories();
        var console = new FakeConsole(
            lineResponses: ["Staging", AdminProviderRegisterAcsCommand.IntentPhrase, "not-an-email"],
            secretResponses: [ValidConnectionString, ValidConnectionString]);
        var command = new AdminProviderRegisterAcsCommand(console, scratch.AcsDir, scratch.SenderDir);

        var exitCode = command.Run();

        Assert.Equal(2, exitCode);
        Assert.False(File.Exists(scratch.AcsFilePath));
        Assert.Contains(AdminProviderRegisterAcsResultCodes.RejectedInvalidSenderEmail, string.Join('\n', console.Errors));
    }

    [Fact]
    public void Run_rejects_an_empty_display_name()
    {
        using var scratch = new ScratchDirectories();
        var console = new FakeConsole(
            lineResponses: ["Staging", AdminProviderRegisterAcsCommand.IntentPhrase, "sender@example.com", ""],
            secretResponses: [ValidConnectionString, ValidConnectionString]);
        var command = new AdminProviderRegisterAcsCommand(console, scratch.AcsDir, scratch.SenderDir);

        var exitCode = command.Run();

        Assert.Equal(2, exitCode);
        Assert.False(File.Exists(scratch.AcsFilePath));
        Assert.Contains(AdminProviderRegisterAcsResultCodes.RejectedInvalidDisplayName, string.Join('\n', console.Errors));
    }

    [Fact]
    public void Run_rejects_before_any_prompt_when_already_fully_registered()
    {
        using var scratch = new ScratchDirectories();
        File.WriteAllText(scratch.AcsFilePath, ValidConnectionString);
        File.WriteAllText(
            scratch.SenderFilePath,
            """{"version":1,"environment":"staging","sender":{"email":"a@example.com","display_name":"A"},"provider":"acs","live_sending":false}""");
        var console = new FakeConsole(lineResponses: [], secretResponses: []);
        var command = new AdminProviderRegisterAcsCommand(console, scratch.AcsDir, scratch.SenderDir);

        var exitCode = command.Run();

        Assert.Equal(2, exitCode);
        Assert.Contains(AdminProviderRegisterAcsResultCodes.RejectedAlreadyRegistered, string.Join('\n', console.Errors));
    }

    [Fact]
    public void Run_rejects_before_any_prompt_when_state_is_partial()
    {
        using var scratch = new ScratchDirectories();
        File.WriteAllText(scratch.AcsFilePath, ValidConnectionString);
        var console = new FakeConsole(lineResponses: [], secretResponses: []);
        var command = new AdminProviderRegisterAcsCommand(console, scratch.AcsDir, scratch.SenderDir);

        var exitCode = command.Run();

        Assert.Equal(2, exitCode);
        Assert.Contains(AdminProviderRegisterAcsResultCodes.RejectedPartialState, string.Join('\n', console.Errors));
    }

    [Fact]
    public void RunPreflightOnly_succeeds_without_prompting_on_a_clean_directory()
    {
        using var scratch = new ScratchDirectories();
        var console = new FakeConsole(lineResponses: [], secretResponses: []);
        var command = new AdminProviderRegisterAcsCommand(console, scratch.AcsDir, scratch.SenderDir);

        var exitCode = command.RunPreflightOnly();

        Assert.Equal(0, exitCode);
        Assert.False(File.Exists(scratch.AcsFilePath));
    }

    [Fact]
    public void RunPreflightOnly_rejects_a_partial_state()
    {
        using var scratch = new ScratchDirectories();
        File.WriteAllText(scratch.AcsFilePath, ValidConnectionString);
        var console = new FakeConsole(lineResponses: [], secretResponses: []);
        var command = new AdminProviderRegisterAcsCommand(console, scratch.AcsDir, scratch.SenderDir);

        var exitCode = command.RunPreflightOnly();

        Assert.Equal(2, exitCode);
    }

    [Fact]
    public void A_second_concurrent_run_is_rejected_while_the_first_holds_the_lock()
    {
        using var scratch = new ScratchDirectories();
        var blockingLock = ExclusiveOperationLock.Acquire(scratch.AcsDir);
        try
        {
            var console = new FakeConsole(
                lineResponses: ["Staging", AdminProviderRegisterAcsCommand.IntentPhrase, "sender@example.com", "Example Sender"],
                secretResponses: [ValidConnectionString, ValidConnectionString]);
            var command = new AdminProviderRegisterAcsCommand(console, scratch.AcsDir, scratch.SenderDir);

            var exitCode = command.Run();

            Assert.Equal(2, exitCode);
            Assert.Contains(AdminProviderRegisterAcsResultCodes.RejectedConcurrentExecution, string.Join('\n', console.Errors));
        }
        finally
        {
            blockingLock.Dispose();
        }
    }

    [Fact]
    public void IsRegisterAcsCommand_and_IsCheckAcsPreflightCommand_match_only_their_own_argv()
    {
        Assert.True(AdminProviderRegisterAcsCommand.IsRegisterAcsCommand(["admin", "provider", "register-acs"]));
        Assert.False(AdminProviderRegisterAcsCommand.IsRegisterAcsCommand(["admin", "provider", "check-acs-preflight"]));
        Assert.True(AdminProviderRegisterAcsCommand.IsCheckAcsPreflightCommand(["admin", "provider", "check-acs-preflight"]));
        Assert.False(AdminProviderRegisterAcsCommand.IsCheckAcsPreflightCommand(["admin", "provider", "register-acs"]));
    }

    private sealed class FakeConsole(IEnumerable<string> lineResponses, IEnumerable<string> secretResponses)
        : IAdminProviderRegisterAcsConsole
    {
        private readonly Queue<string> _lineResponses = new(lineResponses);
        private readonly Queue<string> _secretResponses = new(secretResponses);

        public List<string> Output { get; } = [];

        public List<string> Errors { get; } = [];

        public string ReadLine(string prompt) => _lineResponses.Count > 0
            ? _lineResponses.Dequeue()
            : throw new InvalidOperationException($"Unexpected ReadLine call: {prompt}");

        public string ReadSecret(string prompt) => _secretResponses.Count > 0
            ? _secretResponses.Dequeue()
            : throw new InvalidOperationException($"Unexpected ReadSecret call: {prompt}");

        public void WriteLine(string message) => Output.Add(message);

        public void WriteError(string message) => Errors.Add(message);
    }

    private sealed class ScratchDirectories : IDisposable
    {
        public string Root { get; } = Path.Combine(
            Path.GetTempPath(), "amane-mailer-register-acs-" + Guid.NewGuid().ToString("N"));

        public string AcsDir { get; }

        public string SenderDir { get; }

        public string AcsFilePath { get; }

        public string SenderFilePath { get; }

        public ScratchDirectories()
        {
            AcsDir = Path.Combine(Root, "secrets", "acs");
            SenderDir = Path.Combine(Root, "config", "platform-sender");
            TestSecretDirectory.CreateSecure(AcsDir);
            TestSecretDirectory.CreateSecure(SenderDir);
            AcsFilePath = Path.Combine(AcsDir, AcsSecretFileNames.CanonicalFileName);
            SenderFilePath = Path.Combine(SenderDir, PlatformSenderFile.CanonicalFileName);
        }

        public void Dispose()
        {
            if (Directory.Exists(Root))
            {
                Directory.Delete(Root, recursive: true);
            }
        }
    }
}
