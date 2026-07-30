using Amane.Mailer.Operations;

namespace Amane.Mailer.Setup.Assistant.Terminal;

/// <summary>
/// Interactive terminal input for the setup assistant wizard. Secrets are read without echo;
/// sensitive prompts reject redirected stdin.
/// </summary>
internal interface ISetupTerminalConsole
{
    string ReadLine(string prompt);

    string ReadSecret(string prompt);

    string ReadSensitiveLine(string prompt);

    bool TryReadYesNo(string prompt, out bool value);

    void WriteLine(string message);

    void WriteError(string message);
}

/// <summary>
/// Interactive-terminal-only console. Rejects redirected stdin for secrets and sensitive addresses,
/// and reads secrets one keystroke at a time without echoing them.
/// </summary>
internal sealed class SetupTerminalConsole : ISetupTerminalConsole
{
    public string ReadLine(string prompt)
    {
        EnsureInteractive();
        Console.Write(prompt);
        return Console.ReadLine()
            ?? throw new SecretOperationException(
                AdminProviderRegisterAcsResultCodes.RejectedCancelled,
                "Input was interrupted.");
    }

    public string ReadSecret(string prompt) => ReadSecretCore(prompt);

    public string ReadSensitiveLine(string prompt)
    {
        EnsureInteractive();
        return ReadLine(prompt);
    }

    public bool TryReadYesNo(string prompt, out bool value)
    {
        while (true)
        {
            var raw = ReadLine(prompt).Trim();
            if (string.Equals(raw, "y", StringComparison.OrdinalIgnoreCase)
                || string.Equals(raw, "yes", StringComparison.OrdinalIgnoreCase))
            {
                value = true;
                return true;
            }

            if (string.Equals(raw, "n", StringComparison.OrdinalIgnoreCase)
                || string.Equals(raw, "no", StringComparison.OrdinalIgnoreCase))
            {
                value = false;
                return true;
            }

            WriteError("y または n で入뿯劽してください。");
        }
    }

    public void WriteLine(string message) => Console.WriteLine(message);

    public void WriteError(string message) => Console.Error.WriteLine(message);

    private static string ReadSecretCore(string prompt)
    {
        EnsureInteractive();
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
                    AdminProviderRegisterAcsResultCodes.RejectedCancelled,
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

    private static void EnsureInteractive()
    {
        if (Console.IsInputRedirected)
        {
            throw new SecretOperationException(
                AdminProviderRegisterAcsResultCodes.RejectedInputRedirected,
                "This command only accepts input from an interactive terminal.");
        }
    }
}
