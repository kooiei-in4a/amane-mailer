using System.Diagnostics;

namespace Amane.Mailer.Setup.Assistant;

/// <summary>
/// Best-effort browser launch for the loopback setup assistant web UI.
/// Failures are non-fatal and never block the assistant host.
/// </summary>
internal static class SetupAssistantBrowserLauncher
{
    /// <summary>
    /// Attempts to open the assistant URL in the system browser. Returns false when launch is
    /// unsupported or fails; the caller should print fallback guidance and continue serving.
    /// </summary>
    internal static bool TryOpen(int boundPort) =>
        TryOpen(boundPort, LaunchDefault);

    internal static bool TryOpen(int boundPort, Func<string, bool> launch)
    {
        ArgumentNullException.ThrowIfNull(launch);

        if (OperatingSystem.IsMacOS())
        {
            return false;
        }

        if (OperatingSystem.IsLinux() && !HasDisplay())
        {
            return false;
        }

        var url = SetupAssistantRemoteAccessHints.BuildLoopbackUrl(boundPort);

        try
        {
            return launch(url);
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static bool LaunchDefault(string url)
    {
        if (OperatingSystem.IsWindows())
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
            return true;
        }

        if (OperatingSystem.IsLinux())
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = "xdg-open",
                Arguments = url,
                UseShellExecute = false,
            });
            return true;
        }

        return false;
    }

    private static bool HasDisplay()
    {
        var display = Environment.GetEnvironmentVariable("DISPLAY");
        if (!string.IsNullOrWhiteSpace(display))
        {
            return true;
        }

        var wayland = Environment.GetEnvironmentVariable("WAYLAND_DISPLAY");
        return !string.IsNullOrWhiteSpace(wayland);
    }
}
