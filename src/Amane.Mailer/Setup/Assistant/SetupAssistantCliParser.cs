namespace Amane.Mailer.Setup.Assistant;

/// <summary>
/// Parses <c>setup assistant</c> command-line options after the <c>setup assistant</c> tokens.
/// </summary>
internal static class SetupAssistantCliParser
{
    internal const string UsageText =
        "Usage: dotnet Amane.Mailer.dll setup assistant [--port <1-65535>] [--no-browser] [--terminal]";

    internal static bool TryParse(
        IReadOnlyList<string> args,
        out SetupAssistantCliOptions options,
        out string? usageError)
    {
        options = new SetupAssistantCliOptions();
        usageError = null;

        if (args.Count < 2
            || !string.Equals(args[0], "setup", StringComparison.Ordinal)
            || !string.Equals(args[1], "assistant", StringComparison.Ordinal))
        {
            usageError = "Expected setup assistant.";
            return false;
        }

        var terminal = false;
        var noBrowser = false;
        int? port = null;

        for (var index = 2; index < args.Count; index++)
        {
            var token = args[index];
            if (string.Equals(token, "--terminal", StringComparison.Ordinal))
            {
                if (terminal)
                {
                    usageError = "Duplicate option: --terminal.";
                    return false;
                }

                terminal = true;
                continue;
            }

            if (string.Equals(token, "--no-browser", StringComparison.Ordinal))
            {
                if (noBrowser)
                {
                    usageError = "Duplicate option: --no-browser.";
                    return false;
                }

                noBrowser = true;
                continue;
            }

            if (token.StartsWith("--port=", StringComparison.Ordinal))
            {
                if (port.HasValue)
                {
                    usageError = "Duplicate option: --port.";
                    return false;
                }

                if (!TryParsePort(token["--port=".Length..], out var parsedPort, out usageError))
                {
                    return false;
                }

                port = parsedPort;
                continue;
            }

            if (string.Equals(token, "--port", StringComparison.Ordinal))
            {
                if (port.HasValue)
                {
                    usageError = "Duplicate option: --port.";
                    return false;
                }

                index++;
                if (index >= args.Count)
                {
                    usageError = "--port requires a value.";
                    return false;
                }

                if (!TryParsePort(args[index], out var parsedPort, out usageError))
                {
                    return false;
                }

                port = parsedPort;
                continue;
            }

            usageError = $"Unknown option: {token}.";
            return false;
        }

        if (terminal && (noBrowser || port.HasValue))
        {
            usageError = "--terminal cannot be combined with --no-browser or --port.";
            return false;
        }

        options = new SetupAssistantCliOptions
        {
            Mode = terminal
                ? SetupAssistantCliMode.Terminal
                : noBrowser
                    ? SetupAssistantCliMode.WebNoBrowser
                    : SetupAssistantCliMode.WebWithBrowser,
            Port = port,
        };
        return true;
    }

    private static bool TryParsePort(string rawPort, out int port, out string? usageError)
    {
        usageError = null;
        if (!SetupAssistantOptions.TryResolvePort(rawPort, out port) || port == SetupAssistantOptions.DefaultPort)
        {
            usageError = "--port must be an integer between 1 and 65535.";
            return false;
        }

        return true;
    }
}
