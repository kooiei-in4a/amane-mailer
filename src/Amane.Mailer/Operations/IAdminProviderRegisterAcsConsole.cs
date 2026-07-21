namespace Amane.Mailer.Operations;

public interface IAdminProviderRegisterAcsConsole
{
    string ReadLine(string prompt);

    string ReadSecret(string prompt);

    void WriteLine(string message);

    void WriteError(string message);
}

/// <summary>
/// Interactive-terminal-only console. Rejects redirected stdin outright (no piping, no CI
/// automation) and reads secrets one keystroke at a time without echoing them, matching the
/// security bar of amane-flow's SystemAdminAccountAdminConsole.
/// </summary>
public sealed class AdminProviderRegisterAcsConsole : IAdminProviderRegisterAcsConsole
{
    public string ReadLine(string prompt)
    {
        if (Console.IsInputRedirected)
        {
            throw new SecretOperationException(
                AdminProviderRegisterAcsResultCodes.RejectedInputRedirected,
                "This command only accepts input from an interactive terminal.");
        }

        Console.Write(prompt);
        return Console.ReadLine()
            ?? throw new SecretOperationException(AdminProviderRegisterAcsResultCodes.RejectedCancelled, "Input was interrupted.");
    }

    public string ReadSecret(string prompt)
    {
        if (Console.IsInputRedirected)
        {
            throw new SecretOperationException(
                AdminProviderRegisterAcsResultCodes.RejectedInputRedirected,
                "This command only accepts input from an interactive terminal.");
        }

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
                throw new SecretOperationException(AdminProviderRegisterAcsResultCodes.RejectedCancelled, "Input was interrupted.");
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

    public void WriteLine(string message) => Console.WriteLine(message);

    public void WriteError(string message) => Console.Error.WriteLine(message);
}
