using Amane.Mailer.Operations;
using Amane.Mailer.Operations.AdminBootstrap;
using Amane.Mailer.Operations.AcsSetup;
using Amane.Mailer.Setup;
using Amane.Mailer.Setup.Assistant;
using Amane.Mailer.Setup.Assistant.Terminal;

namespace Amane.Mailer.Tests.Setup.Assistant;

public sealed class SetupTerminalWizardTests
{
    private const string ServiceToken = "synthetic-mail-token-not-real";
    private const string AcsConnectionString =
        "endpoint=https://synthetic.example.communication.azure.com/;accesskey=SYNTHETICACCESSKEY000000000000000000000000000000=";

    [Theory]
    [InlineData("1", SetupMode.LocalMailpit)]
    [InlineData("2", SetupMode.StagingNoSend)]
    [InlineData("3", SetupMode.StagingVerification)]
    [InlineData("4", SetupMode.ProductionAcs)]
    public async Task Modes_1_through_4_complete_main_setup(string modeChoice, SetupMode expectedMode)
    {
        var operations = new FakeSetupAssistantOperations();
        var console = BuildConsoleForMode(modeChoice, expectedMode);
        using var output = new StringWriter();
        using var error = new StringWriter();

        var exit = await new SetupTerminalWizard(
            console,
            operations,
            output,
            error,
            CancellationToken.None).RunAsync();

        Assert.Equal(SetupAssistantCommand.SuccessExitCode, exit);
        Assert.Equal(1, operations.ApplyCalls);
        Assert.Equal(expectedMode, operations.LastMainSetupInput?.Mode);
    }

    [Fact]
    public async Task Cancel_before_apply_returns_exit_0()
    {
        var operations = new FakeSetupAssistantOperations();
        var console = new FakeSetupTerminalConsole();
        console.EnqueueLine("", "y", "cancel");

        using var output = new StringWriter();
        using var error = new StringWriter();
        var exit = await new SetupTerminalWizard(
            console,
            operations,
            output,
            error,
            CancellationToken.None).RunAsync();

        Assert.Equal(SetupAssistantCommand.SuccessExitCode, exit);
        Assert.Equal(0, operations.ApplyCalls);
    }

    [Fact]
    public async Task Mid_operation_cancel_returns_exit_130_when_apply_is_in_flight()
    {
        var hold = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var operations = new FakeSetupAssistantOperations { ApplyHold = hold };
        var console = BuildConsoleForMode("1", SetupMode.LocalMailpit);
        using var output = new StringWriter();
        using var error = new StringWriter();
        using var cts = new CancellationTokenSource();

        var run = new SetupTerminalWizard(
            console,
            operations,
            output,
            error,
            cts.Token).RunAsync();
        while (operations.ApplyCalls == 0)
        {
            await Task.Delay(10, TestContext.Current.CancellationToken);
        }

        await cts.CancelAsync();
        hold.TrySetResult();
        var exit = await run;

        Assert.Equal(SetupTerminalAssistant.CancelledMidOperationExitCode, exit);
    }

    [Fact]
    public async Task Redirected_secret_input_is_rejected()
    {
        var operations = new FakeSetupAssistantOperations();
        var console = new FakeSetupTerminalConsole { RejectRedirectedSecrets = true };
        console.EnqueueLine("", "y", "1");
        console.EnqueueLine("example-develop", "example-service", "noreply@example.com", "Example Service");

        using var output = new StringWriter();
        using var error = new StringWriter();
        var exit = await new SetupTerminalWizard(
            console,
            operations,
            output,
            error,
            CancellationToken.None).RunAsync();

        Assert.Equal(SetupAssistantCommand.FailureExitCode, exit);
        Assert.Contains("interactive TTY", error.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Service_token_is_not_written_to_output()
    {
        var operations = new FakeSetupAssistantOperations();
        var console = BuildConsoleForMode("1", SetupMode.LocalMailpit);
        using var output = new StringWriter();
        using var error = new StringWriter();

        _ = await new SetupTerminalWizard(
            console,
            operations,
            output,
            error,
            CancellationToken.None).RunAsync();

        var combined = output.ToString() + error.ToString();
        Assert.DoesNotContain(ServiceToken, combined, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Main_success_with_admin_failure_still_returns_exit_0()
    {
        var operations = new FakeSetupAssistantOperations
        {
            AdminBootstrap = FakeSetupAssistantOperations.FailedAdmin(AdminBootstrapResultCode.FailedUnexpected),
        };
        var console = BuildConsoleForMode("1", SetupMode.LocalMailpit, enableAdmin: true);
        using var output = new StringWriter();
        using var error = new StringWriter();

        var exit = await new SetupTerminalWizard(
            console,
            operations,
            output,
            error,
            CancellationToken.None).RunAsync();

        Assert.Equal(SetupAssistantCommand.SuccessExitCode, exit);
        Assert.Equal(1, operations.ApplyCalls);
        Assert.NotNull(operations.LastAdminBootstrapInput);
        Assert.Contains("mainSetup.status: succeeded", output.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Main_success_then_admin_choice_cancel_keeps_exit_0()
    {
        var operations = new FakeSetupAssistantOperations();
        var console = BuildConsoleForMode("1", SetupMode.LocalMailpit);
        // Replace the trailing "n" with cancel for Admin choice.
        // BuildConsoleForMode already enqueued "n"; dequeue by using a fresh console.
        console = BuildConsoleForMode("1", SetupMode.LocalMailpit, enableAdmin: false);
        // The last enqueued line is "n"; we need "cancel" instead — rebuild.
        console = new FakeSetupTerminalConsole();
        console.EnqueueLine("", "y", "1");
        console.EnqueueLine("example-develop", "example-service", "noreply@example.com", "Example Service");
        console.EnqueueSecret(ServiceToken, ServiceToken);
        console.EnqueueLine("y"); // mailpit confirm
        console.EnqueueLine("cancel"); // admin choice

        using var output = new StringWriter();
        using var error = new StringWriter();
        var exit = await new SetupTerminalWizard(
            console,
            operations,
            output,
            error,
            CancellationToken.None).RunAsync();

        Assert.Equal(SetupAssistantCommand.SuccessExitCode, exit);
        Assert.Equal(1, operations.ApplyCalls);
        Assert.Null(operations.LastAdminBootstrapInput);
        Assert.Contains("mainSetup.status: succeeded", output.ToString(), StringComparison.Ordinal);
        Assert.Contains("adminBootstrap.status: cancelled", output.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Main_success_with_admin_unexpected_exception_keeps_exit_0_and_manual_action()
    {
        var operations = new FakeSetupAssistantOperations
        {
            BootstrapThrows = new InvalidOperationException("secret-path-or-username must not leak"),
        };
        var console = BuildConsoleForMode("1", SetupMode.LocalMailpit, enableAdmin: true);
        using var output = new StringWriter();
        using var error = new StringWriter();

        var exit = await new SetupTerminalWizard(
            console,
            operations,
            output,
            error,
            CancellationToken.None).RunAsync();

        Assert.Equal(SetupAssistantCommand.SuccessExitCode, exit);
        var combined = output.ToString() + error.ToString();
        Assert.Contains("mainSetup.status: succeeded", combined, StringComparison.Ordinal);
        Assert.Contains("最終状態を確認できませんでした", combined, StringComparison.Ordinal);
        Assert.DoesNotContain("secret-path-or-username", combined, StringComparison.Ordinal);
        Assert.DoesNotContain("local-admin", combined, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Main_success_with_admin_operation_canceled_keeps_exit_0()
    {
        var operations = new FakeSetupAssistantOperations { BootstrapCancels = true };
        var console = BuildConsoleForMode("1", SetupMode.LocalMailpit, enableAdmin: true);
        using var output = new StringWriter();
        using var error = new StringWriter();

        var exit = await new SetupTerminalWizard(
            console,
            operations,
            output,
            error,
            CancellationToken.None).RunAsync();

        Assert.Equal(SetupAssistantCommand.SuccessExitCode, exit);
        Assert.Contains("mainSetup.status: succeeded", output.ToString(), StringComparison.Ordinal);
        Assert.Contains("adminBootstrap.status: cancelled", output.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void Yes_no_rejection_message_is_valid_japanese()
    {
        var sourcePath = Path.Combine(
            AppContext.BaseDirectory,
            "..",
            "..",
            "..",
            "..",
            "..",
            "src",
            "Amane.Mailer",
            "Setup",
            "Assistant",
            "Terminal",
            "SetupTerminalConsole.cs");
        sourcePath = Path.GetFullPath(sourcePath);
        Assert.True(File.Exists(sourcePath), sourcePath);
        var source = File.ReadAllText(sourcePath);
        Assert.Contains("y または n で入力してください。", source, StringComparison.Ordinal);
        Assert.DoesNotContain("入뿯劽", source, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Mode_5_prints_manual_guidance_without_apply()
    {
        var operations = new FakeSetupAssistantOperations();
        var console = new FakeSetupTerminalConsole();
        console.EnqueueLine("", "y", "5");

        using var output = new StringWriter();
        using var error = new StringWriter();
        var exit = await new SetupTerminalWizard(
            console,
            operations,
            output,
            error,
            CancellationToken.None).RunAsync();

        Assert.Equal(SetupAssistantCommand.SuccessExitCode, exit);
        Assert.Equal(0, operations.ApplyCalls);
        Assert.Contains("production-queue", output.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    private static FakeSetupTerminalConsole BuildConsoleForMode(
        string modeChoice,
        SetupMode mode,
        bool enableAdmin = false)
    {
        var console = new FakeSetupTerminalConsole();
        console.EnqueueLine("", "y", modeChoice);
        console.EnqueueLine("example-develop", "example-service", "noreply@example.com", "Example Service");
        console.EnqueueSecret(ServiceToken, ServiceToken);

        switch (mode)
        {
            case SetupMode.LocalMailpit:
                console.EnqueueLine("y");
                break;
            case SetupMode.StagingNoSend:
                console.EnqueueSecret(AcsConnectionString, AcsConnectionString);
                console.EnqueueLine("Example Service");
                console.EnqueueLine(AcsEnvironmentConfirmation.Staging, AcsRegisterOperation.IntentPhrase);
                break;
            case SetupMode.StagingVerification:
                console.EnqueueSecret(AcsConnectionString, AcsConnectionString);
                console.EnqueueLine("Example Service");
                console.EnqueueLine(AcsEnvironmentConfirmation.Staging, AcsRegisterOperation.IntentPhrase);
                console.EnqueueLine(
                    "qa-recipient@example.com",
                    AcsEnvironmentConfirmation.Staging,
                    AcsStagingVerificationOperation.IntentPhrase);
                break;
            case SetupMode.ProductionAcs:
                console.EnqueueSecret(AcsConnectionString, AcsConnectionString);
                console.EnqueueLine("Example Service");
                console.EnqueueLine(AcsEnvironmentConfirmation.Production, AcsRegisterOperation.IntentPhrase);
                console.EnqueueLine(
                    AcsEnvironmentConfirmation.Production,
                    AcsLiveSendingApproval.EnablePhrase);
                break;
        }

        if (enableAdmin)
        {
            EnqueueAdminBootstrap(console);
        }
        else
        {
            console.EnqueueLine("n");
        }

        return console;
    }

    private static void EnqueueAdminBootstrap(FakeSetupTerminalConsole console)
    {
        console.EnqueueLine(
            "y",
            "1",
            "http://127.0.0.1:5280/",
            "Development",
            "127.0.0.1",
            "y",
            "n",
            "y",
            "local-admin");
        console.EnqueueSecret("synthetic-admin-password-not-real", "synthetic-admin-password-not-real");
    }
}
