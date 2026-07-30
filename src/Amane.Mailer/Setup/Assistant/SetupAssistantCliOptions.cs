namespace Amane.Mailer.Setup.Assistant;

/// <summary>
/// Parsed command-line options for <c>setup assistant</c>.
/// </summary>
internal sealed class SetupAssistantCliOptions
{
    internal SetupAssistantCliMode Mode { get; init; } = SetupAssistantCliMode.WebWithBrowser;

    /// <summary>
    /// Explicit port from <c>--port</c>. When null, port is resolved from environment or defaults to ephemeral.
    /// </summary>
    internal int? Port { get; init; }
}

/// <summary>
/// Mutually exclusive run modes for the setup assistant CLI.
/// </summary>
internal enum SetupAssistantCliMode
{
    /// <summary>Start the loopback web host and attempt to open a browser.</summary>
    WebWithBrowser,

    /// <summary>Start the loopback web host without opening a browser.</summary>
    WebNoBrowser,

    /// <summary>Run the terminal wizard instead of the web host.</summary>
    Terminal,
}
