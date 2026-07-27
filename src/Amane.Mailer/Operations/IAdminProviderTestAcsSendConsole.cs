namespace Amane.Mailer.Operations;

/// <summary>
/// Interactive console for <c>admin provider test-acs-send</c>.
/// <para>
/// <see cref="ReadSecret"/> is for ACS connection strings.
/// <see cref="ReadHiddenLine"/> is for PII (sender / recipient) and must not echo to the terminal.
/// Confirmation phrases and non-PII paths may use <see cref="ReadLine"/>.
/// </para>
/// </summary>
public interface IAdminProviderTestAcsSendConsole
{
    string ReadLine(string prompt);

    string ReadSecret(string prompt);

    string ReadHiddenLine(string prompt);

    void WriteLine(string message);

    void WriteError(string message);
}

/// <summary>
/// Interactive-terminal-only console. Rejects redirected stdin. Secrets and PII use
/// <see cref="Console.ReadKey"/> with intercept so keystrokes are not echoed into the PTY
/// transcript.
/// </summary>
public sealed class AdminProviderTestAcsSendConsole : IAdminProviderTestAcsSendConsole
{
    public string ReadLine(string prompt)
    {
        EnsureInteractiveTerminal();
        Console.Write(prompt);
        return Console.ReadLine()
            ?? throw new SecretOperationException(
                AdminProviderTestAcsSendResultCodes.RejectedCancelled,
                "Input was interrupted.");
    }

    public string ReadSecret(string prompt) => ReadWithoutEcho(prompt);

    public string ReadHiddenLine(string prompt) => ReadWithoutEcho(prompt);

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

    private static string ReadWithoutEcho(string prompt)
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
                }

                continue;
            }

            if (!char.IsControl(key.KeyChar))
            {
                buffer.Add(key.KeyChar);
            }
        }
    }
}
