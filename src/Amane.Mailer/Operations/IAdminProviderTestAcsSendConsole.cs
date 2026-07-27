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
    string ReadVisibleLine(string prompt);

    string ReadSecret(string prompt);

    string ReadHiddenLine(string prompt);

    void WriteLine(string message);

    void WriteError(string message);
}

/// <summary>
/// Interactive-terminal-only console. Rejects redirected stdin. All prompts use
/// <see cref="Console.ReadKey"/> so Ctrl+C is detected deterministically as
/// <see cref="AdminProviderTestAcsSendResultCodes.RejectedCancelled"/> (exit 2).
/// Secrets and PII are not echoed; visible lines are echoed manually.
/// </summary>
public sealed class AdminProviderTestAcsSendConsole : IAdminProviderTestAcsSendConsole
{
    public string ReadVisibleLine(string prompt) => ReadKeyLine(prompt, echo: true);

    public string ReadSecret(string prompt) => ReadKeyLine(prompt, echo: false);

    public string ReadHiddenLine(string prompt) => ReadKeyLine(prompt, echo: false);

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

    private static string ReadKeyLine(string prompt, bool echo)
    {
        EnsureInteractiveTerminal();
        Console.Write(prompt);
        var buffer = new List<char>();
        while (true)
        {
            var key = Console.ReadKey(intercept: true);
            if (key.Key == ConsoleKey.Enter)
            {
                Console.WriteLine();
                return new string(buffer.ToArray());
            }

            if (key.Key == ConsoleKey.C && key.Modifiers.HasFlag(ConsoleModifiers.Control))
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
}
