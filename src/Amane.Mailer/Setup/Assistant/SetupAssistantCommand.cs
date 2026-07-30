namespace Amane.Mailer.Setup.Assistant;

/// <summary>
/// <c>setup assistant</c> entry point. It starts the loopback-only assistant host, prints the
/// address and the one-time token to the terminal, and blocks until the session completes, is
/// cancelled, or times out. The normal Mailer runtime is never started by this path and never
/// gains a setup route.
/// </summary>
public static class SetupAssistantCommand
{
    public const int SuccessExitCode = 0;
    public const int FailureExitCode = 1;
    public const int UsageErrorExitCode = 2;

    public static bool IsAssistantCommand(IReadOnlyList<string> args) =>
        args.Count == 2
        && string.Equals(args[0], "setup", StringComparison.Ordinal)
        && string.Equals(args[1], "assistant", StringComparison.Ordinal);

    public static async Task<int> ExecuteAsync(
        TextWriter output,
        TextWriter error,
        CancellationToken cancellationToken)
    {
        if (!SetupAssistantOptions.TryResolvePort(
                Environment.GetEnvironmentVariable(SetupAssistantOptions.PortEnvironmentKey),
                out var port))
        {
            error.WriteLine(
                $"setup assistant: {SetupAssistantOptions.PortEnvironmentKey} must be an integer between 1 and 65535.");
            return UsageErrorExitCode;
        }

        var options = new SetupAssistantOptions { Port = port };
        using var sessions = new SetupAssistantSessionManager(options);

        try
        {
            await using var host = await SetupAssistantHost.StartAsync(
                options,
                sessions,
                new SetupAssistantOperations(),
                cancellationToken);

            output.WriteLine("Amane Mailer Easy Setup Assistant");
            output.WriteLine($"  URL:   {host.BaseAddress}");
            output.WriteLine($"  Token: {sessions.OneTimeTokenText}");
            output.WriteLine("  The token can be used once. Press Ctrl+C to stop the assistant.");
            output.Flush();

            var reason = await host.WaitForShutdownAsync(cancellationToken);
            output.WriteLine($"setup assistant: stopped ({DescribeReason(reason)}).");
            return SuccessExitCode;
        }
        catch (Exception)
        {
            // A bind failure is terminal. The assistant never retries on 0.0.0.0, on a LAN
            // address, or on any other interface.
            error.WriteLine(
                "setup assistant: could not bind the loopback listener. No other interface is used.");
            return FailureExitCode;
        }
    }

    private static string DescribeReason(SetupAssistantShutdownReason reason) => reason switch
    {
        SetupAssistantShutdownReason.Completed => "completed",
        SetupAssistantShutdownReason.Cancelled => "cancelled",
        SetupAssistantShutdownReason.IdleTimeout => "idle timeout",
        SetupAssistantShutdownReason.AbsoluteTimeout => "absolute session lifetime reached",
        SetupAssistantShutdownReason.UnclaimedTokenExpired => "one-time token expired unused",
        _ => "stopped",
    };
}
