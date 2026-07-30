using Amane.Mailer.Setup.Assistant.Terminal;

namespace Amane.Mailer.Setup.Assistant;

/// <summary>
/// Terminal-mode setup assistant entry point.
/// </summary>
internal static class SetupTerminalAssistant
{
    internal const int CancelledMidOperationExitCode = 130;

    /// <summary>
    /// Runs the terminal setup wizard. Returns a process exit code.
    /// </summary>
    internal static Task<int> ExecuteAsync(
        TextWriter output,
        TextWriter error,
        CancellationToken cancellationToken) =>
        new SetupTerminalWizard(
            new SetupTerminalConsole(),
            new SetupAssistantOperations(),
            output,
            error,
            cancellationToken).RunAsync();
}
