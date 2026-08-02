namespace Amane.Mailer.Setup.Assistant;

/// <summary>
/// Prints loopback URL, token, and SSH tunnel guidance for remote access to the web assistant.
/// </summary>
internal static class SetupAssistantRemoteAccessHints
{
    internal static void WriteNoBrowserStartup(
        TextWriter output,
        int boundPort,
        string token)
    {
        var url = BuildLoopbackUrl(boundPort);
        output.WriteLine("Amane Mailer Easy Setup Assistant");
        output.WriteLine($"  URL: {url}");
        output.WriteLine($"  Token: {token}");
        output.WriteLine($"  SSH: ssh -L {boundPort}:127.0.0.1:{boundPort} user@example-host");
        output.WriteLine($"  Then open {url} and enter the token on the page.");
    }

    internal static void WriteDefaultStartup(
        TextWriter output,
        int boundPort,
        string token)
    {
        output.WriteLine("Amane Mailer Easy Setup Assistant");
        output.WriteLine($"  URL:   {BuildLoopbackUrl(boundPort)}");
        output.WriteLine($"  Token: {token}");
        output.WriteLine("  The token can be used once. Press Ctrl+C to stop the assistant.");
    }

    internal static void WriteBrowserFallback(TextWriter output)
    {
        output.WriteLine("  Could not open a browser automatically.");
        output.WriteLine("  Open the URL above manually, or restart with --no-browser or --terminal.");
    }

    internal static string BuildLoopbackUrl(int boundPort) =>
        $"http://127.0.0.1:{boundPort.ToString(System.Globalization.CultureInfo.InvariantCulture)}/";
}
