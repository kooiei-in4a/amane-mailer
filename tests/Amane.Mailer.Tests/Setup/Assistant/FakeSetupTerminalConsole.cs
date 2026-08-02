using Amane.Mailer.Operations;
using Amane.Mailer.Setup.Assistant.Terminal;

namespace Amane.Mailer.Tests.Setup.Assistant;

internal sealed class FakeSetupTerminalConsole : ISetupTerminalConsole
{
    private readonly Queue<string> _lines = new();
    private readonly Queue<string> _secrets = new();

    internal bool RejectRedirectedSecrets { get; init; }

    internal void EnqueueLine(params string[] values)
    {
        foreach (var value in values)
        {
            _lines.Enqueue(value);
        }
    }

    internal void EnqueueSecret(params string[] values)
    {
        foreach (var value in values)
        {
            _secrets.Enqueue(value);
        }
    }

    public string ReadLine(string prompt)
    {
        if (_lines.Count == 0)
        {
            throw new InvalidOperationException("Unexpected ReadLine prompt: " + prompt);
        }

        return _lines.Dequeue();
    }

    public string ReadSecret(string prompt)
    {
        if (RejectRedirectedSecrets)
        {
            throw new SecretOperationException(
                AdminProviderRegisterAcsResultCodes.RejectedInputRedirected,
                "This command only accepts input from an interactive terminal.");
        }

        if (_secrets.Count == 0)
        {
            throw new InvalidOperationException("Unexpected ReadSecret prompt: " + prompt);
        }

        return _secrets.Dequeue();
    }

    public string ReadSensitiveLine(string prompt) => ReadLine(prompt);

    public bool TryReadYesNo(string prompt, out bool value)
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

        value = false;
        return false;
    }

    public void WriteLine(string message)
    {
    }

    public void WriteError(string message)
    {
    }
}
