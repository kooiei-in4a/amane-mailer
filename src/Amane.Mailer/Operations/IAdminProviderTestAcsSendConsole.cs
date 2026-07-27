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
/// On Linux PTY, Ctrl+C arrives as SIGINT and is handled by
/// <see cref="MailerCliCancellation"/> (<c>CancelKeyPress</c> with <c>e.Cancel = true</c>).
/// Blocking <see cref="Console.ReadKey"/> never observes that cancel and would hang, so this
/// console polls <see cref="Console.KeyAvailable"/> and treats a cancelled shared token as
/// <see cref="AdminProviderTestAcsSendResultCodes.RejectedCancelled"/> (exit 2). When the
/// keystroke is delivered as input (some hosts), Ctrl+C is also detected from
/// <see cref="Console.ReadKey"/>.
/// </para>
/// Secrets and PII are not echoed; visible lines are echoed manually.
/// </summary>
public sealed class AdminProviderTestAcsSendConsole : IAdminProviderTestAcsSendConsole
{
    private static readonly TimeSpan KeyPollInterval = TimeSpan.FromMilliseconds(50);

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
        Console.Write(prompt);
        var buffer = new List<char>();
        while (true)
        {
            // CancelKeyPress / SIGINT is authoritative on Linux PTY; do not block in ReadKey.
            if (cancellationToken.IsCancellationRequested)
            {
                throw new SecretOperationException(
                    AdminProviderTestAcsSendResultCodes.RejectedCancelled,
                    "Input was interrupted.");
            }

            if (!Console.KeyAvailable)
            {
                Thread.Sleep(KeyPollInterval);
                continue;
            }

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
