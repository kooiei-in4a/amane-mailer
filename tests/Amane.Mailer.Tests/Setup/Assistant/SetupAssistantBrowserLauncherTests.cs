using Amane.Mailer.Setup.Assistant;

namespace Amane.Mailer.Tests.Setup.Assistant;

public sealed class SetupAssistantBrowserLauncherTests
{
    [Fact]
    public void MacOS_always_returns_false_without_invoking_launch()
    {
        Assert.SkipWhen(!OperatingSystem.IsMacOS(), "macOS-only behavior.");

        var invoked = false;
        Assert.False(SetupAssistantBrowserLauncher.TryOpen(5280, _ =>
        {
            invoked = true;
            return true;
        }));
        Assert.False(invoked);
    }

    [Fact]
    public void Linux_without_display_returns_false_without_invoking_launch()
    {
        Assert.SkipWhen(!OperatingSystem.IsLinux(), "Linux-only behavior.");

        var previousDisplay = Environment.GetEnvironmentVariable("DISPLAY");
        var previousWayland = Environment.GetEnvironmentVariable("WAYLAND_DISPLAY");
        try
        {
            Environment.SetEnvironmentVariable("DISPLAY", null);
            Environment.SetEnvironmentVariable("WAYLAND_DISPLAY", null);

            var invoked = false;
            Assert.False(SetupAssistantBrowserLauncher.TryOpen(5280, _ =>
            {
                invoked = true;
                return true;
            }));
            Assert.False(invoked);
        }
        finally
        {
            Environment.SetEnvironmentVariable("DISPLAY", previousDisplay);
            Environment.SetEnvironmentVariable("WAYLAND_DISPLAY", previousWayland);
        }
    }

    [Fact]
    public void Injectable_launch_delegate_success_returns_true()
    {
        if (OperatingSystem.IsMacOS())
        {
            return;
        }

        if (OperatingSystem.IsLinux()
            && string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("DISPLAY"))
            && string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("WAYLAND_DISPLAY")))
        {
            return;
        }

        string? captured = null;
        Assert.True(SetupAssistantBrowserLauncher.TryOpen(5280, url =>
        {
            captured = url;
            return true;
        }));
        Assert.Equal("http://127.0.0.1:5280/", captured);
    }

    [Fact]
    public void Injectable_launch_delegate_failure_returns_false()
    {
        if (OperatingSystem.IsMacOS())
        {
            return;
        }

        if (OperatingSystem.IsLinux()
            && string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("DISPLAY"))
            && string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("WAYLAND_DISPLAY")))
        {
            return;
        }

        Assert.False(SetupAssistantBrowserLauncher.TryOpen(5280, _ => false));
    }

    [Fact]
    public void Injectable_launch_delegate_exception_returns_false()
    {
        if (OperatingSystem.IsMacOS())
        {
            return;
        }

        if (OperatingSystem.IsLinux()
            && string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("DISPLAY"))
            && string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("WAYLAND_DISPLAY")))
        {
            return;
        }

        Assert.False(SetupAssistantBrowserLauncher.TryOpen(5280, _ => throw new InvalidOperationException("launch failed")));
    }
}
