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
        args.Count >= 2
        && string.Equals(args[0], "setup", StringComparison.Ordinal)
        && string.Equals(args[1], "assistant", StringComparison.Ordinal);

    public static async Task<int> ExecuteAsync(
        IReadOnlyList<string> args,
        TextWriter output,
        TextWriter error,
        CancellationToken cancellationToken)
    {
        if (!SetupAssistantCliParser.TryParse(args, out var cliOptions, out var usageError))
        {
            if (!string.IsNullOrWhiteSpace(usageError))
            {
                error.WriteLine($"setup assistant: {usageError}");
            }

            error.WriteLine(SetupAssistantCliParser.UsageText);
            return UsageErrorExitCode;
        }

        if (cliOptions.Mode == SetupAssistantCliMode.Terminal)
        {
            return await SetupTerminalAssistant.ExecuteAsync(output, error, cancellationToken);
        }

        if (!TryResolvePort(cliOptions.Port, out var port, out var portError))
        {
            error.WriteLine($"setup assistant: {portError}");
            return UsageErrorExitCode;
        }

        var options = new SetupAssistantOptions { Port = port };
        using var sessions = new SetupAssistantSessionManager(options);

        var listening = false;
        try
        {
            await using var host = await SetupAssistantHost.StartAsync(
                options,
                sessions,
                new SetupAssistantOperations(),
                cancellationToken);
            listening = true;

            if (cliOptions.Mode == SetupAssistantCliMode.WebNoBrowser)
            {
                SetupAssistantRemoteAccessHints.WriteNoBrowserStartup(
                    output,
                    host.BoundPort,
                    sessions.OneTimeTokenText);
            }
            else
            {
                SetupAssistantRemoteAccessHints.WriteDefaultStartup(
                    output,
                    host.BoundPort,
                    sessions.OneTimeTokenText);

                if (!SetupAssistantBrowserLauncher.TryOpen(host.BoundPort))
                {
                    SetupAssistantRemoteAccessHints.WriteBrowserFallback(output);
                }
            }

            output.Flush();

            var reason = await host.WaitForShutdownAsync(cancellationToken);
            output.WriteLine($"setup assistant: stopped ({DescribeReason(reason)}).");
            return SuccessExitCode;
        }
        catch (OperationCanceledException)
        {
            output.WriteLine("setup assistant: stopped (interrupted).");
            return SuccessExitCode;
        }
        catch (Exception)
        {
            // A bind failure is terminal: the assistant never retries on 0.0.0.0, on a LAN address,
            // or on any other interface. A failure after the listener is up is a different fault,
            // and neither is reported with the exception body, which can carry a path or a secret.
            error.WriteLine(listening
                ? "setup assistant: stopped after an unexpected error while serving the assistant."
                : "setup assistant: could not bind the loopback listener. No other interface is used.");
            return FailureExitCode;
        }
    }

    private static bool TryResolvePort(int? cliPort, out int port, out string portError)
    {
        if (cliPort.HasValue)
        {
            port = cliPort.Value;
            portError = string.Empty;
            return true;
        }

        if (!SetupAssistantOptions.TryResolvePort(
                Environment.GetEnvironmentVariable(SetupAssistantOptions.PortEnvironmentKey),
                out port))
        {
            portError =
                $"{SetupAssistantOptions.PortEnvironmentKey} must be an integer between 1 and 65535.";
            return false;
        }

        portError = string.Empty;
        return true;
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
