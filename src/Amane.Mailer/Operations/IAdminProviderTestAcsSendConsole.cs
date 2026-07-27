namespace Amane.Mailer.Operations;

/// <summary>
/// Interactive console for <c>admin provider test-acs-send</c>.
/// <para>
/// <see cref="ReadVisibleLine"/> echoes confirmation / path input and detects Ctrl+C.
/// <see cref="ReadSecret"/> is for ACS connection strings (no echo).
/// <see cref="ReadHiddenLine"/> is for PII (sender / recipient; no echo).
/// </para>
/// </summary>
public interface IAdminProviderTestAcsSendConsole
{
    string ReadVisibleLine(string prompt, CancellationToken cancellationToken);

    string ReadSecret(string prompt, CancellationToken cancellationToken);

    string ReadHiddenLine(string prompt, CancellationToken cancellationToken);

    void WriteLine(string message);

    void WriteError(string message);
}

/// <summary>
/// Interactive-terminal-only console. Rejects redirected stdin.
/// <para>
/// During prompts, <see cref="Console.TreatControlCAsInput"/> is set so Ctrl+C is delivered to
/// <see cref="Console.ReadKey"/> as <c>KeyChar == '\\x03'</c> (or Control+C modifiers) instead of
/// competing with <see cref="MailerCliCancellation"/> / SIGINT. That maps to
/// <see cref="AdminProviderTestAcsSendResultCodes.RejectedCancelled"/> (exit 2). Outside prompts,
/// ACS I/O still uses cooperative <c>CancelKeyPress</c> (exit 130).
/// </para>
/// Secrets and PII are not echoed; visible lines are echoed manually.
/// </summary>
public sealed class AdminProviderTestAcsSendConsole : IAdminProviderTestAcsSendConsole
{
    public string ReadVisibleLine(string prompt, CancellationToken cancellationToken) =>
        ReadKeyLine(prompt, echo: true, cancellationToken);

    public string ReadSecret(string prompt, CancellationToken cancellationToken) =>
        ReadKeyLine(prompt, echo: false, cancellationToken);

    public string ReadHiddenLine(string prompt, CancellationToken cancellationToken) =>
        ReadKeyLine(prompt, echo: false, cancellationToken);

    public void WriteLine(string message) => Console.WriteLine(message);

    public void WriteError(string message) => Console.Error.WriteLine(message);

    private static void EnsureInteractiveTerminal()
    {
        if (Console.IsInputRedirected)
        {
            throw new SecretOperationException(
                AdminProviderTestAcsSendResultCodes.RejectedInputRedirected,
                "This command only accepts input from an interactive terminal.");
        }
    }

    private static string ReadKeyLine(string prompt, bool echo, CancellationToken cancellationToken)
    {
        EnsureInteractiveTerminal();
        if (cancellationToken.IsCancellationRequested)
        {
            throw new SecretOperationException(
                AdminProviderTestAcsSendResultCodes.RejectedCancelled,
                "Input was interrupted.");
        }

        // Make Ctrl+C a keystroke so it cannot race with CancelKeyPress / SIGINT on Linux PTY.
        var previousTreatControlCAsInput = Console.TreatControlCAsInput;
        Console.TreatControlCAsInput = true;
        try
        {
            Console.Write(prompt);
            var buffer = new List<char>();
            while (true)
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    throw new SecretOperationException(
                        AdminProviderTestAcsSendResultCodes.RejectedCancelled,
                        "Input was interrupted.");
                }

                var key = Console.ReadKey(intercept: true);
                if (key.Key == ConsoleKey.Enter)
                {
                    Console.WriteLine();
                    return new string(buffer.ToArray());
                }

                if (IsCtrlC(key))
                {
                    throw new SecretOperationException(
                        AdminProviderTestAcsSendResultCodes.RejectedCancelled,
                        "Input was interrupted.");
                }

                if (key.Key == ConsoleKey.Backspace)
                {
                    if (buffer.Count > 0)
                    {
                        buffer.RemoveAt(buffer.Count - 1);
                        if (echo)
                        {
                            Console.Write("\b \b");
                        }
                    }

                    continue;
                }

                if (!char.IsControl(key.KeyChar))
                {
                    buffer.Add(key.KeyChar);
                    if (echo)
                    {
                        Console.Write(key.KeyChar);
                    }
                }
            }
        }
        finally
        {
            Console.TreatControlCAsInput = previousTreatControlCAsInput;
        }
    }

    private static bool IsCtrlC(ConsoleKeyInfo key) =>
        key.KeyChar == '\u0003'
        || (key.Key == ConsoleKey.C && key.Modifiers.HasFlag(ConsoleModifiers.Control));
}
