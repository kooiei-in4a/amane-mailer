using Amane.Mailer.Setup.Assistant;

namespace Amane.Mailer.Tests.Setup.Assistant;

public sealed class SetupAssistantCliParserTests
{
    [Fact]
    public void Default_args_select_web_with_browser()
    {
        Assert.True(
            SetupAssistantCliParser.TryParse(["setup", "assistant"], out var options, out var error),
            error);
        Assert.Equal(SetupAssistantCliMode.WebWithBrowser, options.Mode);
        Assert.Null(options.Port);
    }

    [Fact]
    public void No_browser_selects_web_without_browser()
    {
        Assert.True(
            SetupAssistantCliParser.TryParse(
                ["setup", "assistant", "--no-browser"],
                out var options,
                out _));
        Assert.Equal(SetupAssistantCliMode.WebNoBrowser, options.Mode);
        Assert.Null(options.Port);
    }

    [Fact]
    public void Terminal_selects_terminal_mode()
    {
        Assert.True(
            SetupAssistantCliParser.TryParse(
                ["setup", "assistant", "--terminal"],
                out var options,
                out _));
        Assert.Equal(SetupAssistantCliMode.Terminal, options.Mode);
        Assert.Null(options.Port);
    }

    [Fact]
    public void Terminal_with_no_browser_is_rejected()
    {
        Assert.False(
            SetupAssistantCliParser.TryParse(
                ["setup", "assistant", "--terminal", "--no-browser"],
                out _,
                out var error));
        Assert.Contains("--terminal cannot be combined", error, StringComparison.Ordinal);
    }

    [Fact]
    public void Terminal_with_port_is_rejected()
    {
        Assert.False(
            SetupAssistantCliParser.TryParse(
                ["setup", "assistant", "--terminal", "--port", "5280"],
                out _,
                out var error));
        Assert.Contains("--terminal cannot be combined", error, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("--port", "5280", 5280)]
    [InlineData("--port=5280", null, 5280)]
    public void Port_parses_space_and_equals_forms(string flag, string? value, int expected)
    {
        string[] args = value is null
            ? ["setup", "assistant", flag]
            : ["setup", "assistant", flag, value!];
        Assert.True(SetupAssistantCliParser.TryParse(args, out var options, out var error), error);
        Assert.Equal(expected, options.Port);
        Assert.Equal(SetupAssistantCliMode.WebWithBrowser, options.Mode);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-1")]
    [InlineData("70000")]
    [InlineData("not-a-number")]
    public void Port_rejects_out_of_range_and_non_numeric(string rawPort)
    {
        Assert.False(
            SetupAssistantCliParser.TryParse(
                ["setup", "assistant", "--port", rawPort],
                out _,
                out var error));
        Assert.Contains("--port must be an integer", error, StringComparison.Ordinal);
    }

    [Fact]
    public void Port_requires_value_after_flag()
    {
        Assert.False(
            SetupAssistantCliParser.TryParse(
                ["setup", "assistant", "--port"],
                out _,
                out var error));
        Assert.Equal("--port requires a value.", error);
    }

    [Fact]
    public void Duplicate_terminal_is_rejected()
    {
        Assert.False(
            SetupAssistantCliParser.TryParse(
                ["setup", "assistant", "--terminal", "--terminal"],
                out _,
                out var error));
        Assert.Equal("Duplicate option: --terminal.", error);
    }

    [Fact]
    public void Duplicate_no_browser_is_rejected()
    {
        Assert.False(
            SetupAssistantCliParser.TryParse(
                ["setup", "assistant", "--no-browser", "--no-browser"],
                out _,
                out var error));
        Assert.Equal("Duplicate option: --no-browser.", error);
    }

    [Fact]
    public void Duplicate_port_is_rejected()
    {
        Assert.False(
            SetupAssistantCliParser.TryParse(
                ["setup", "assistant", "--port", "5280", "--port", "5281"],
                out _,
                out var error));
        Assert.Equal("Duplicate option: --port.", error);
    }

    [Fact]
    public void Unknown_option_is_rejected()
    {
        Assert.False(
            SetupAssistantCliParser.TryParse(
                ["setup", "assistant", "--verbose"],
                out _,
                out var error));
        Assert.Equal("Unknown option: --verbose.", error);
    }

    [Fact]
    public void Missing_setup_assistant_prefix_is_rejected()
    {
        Assert.False(
            SetupAssistantCliParser.TryParse(["setup"], out _, out var error));
        Assert.Equal("Expected setup assistant.", error);
    }

    [Fact]
    public void Cli_port_takes_priority_over_environment_when_resolving()
    {
        var previous = Environment.GetEnvironmentVariable(SetupAssistantOptions.PortEnvironmentKey);
        try
        {
            Environment.SetEnvironmentVariable(SetupAssistantOptions.PortEnvironmentKey, "5281");
            Assert.True(
                SetupAssistantCliParser.TryParse(
                    ["setup", "assistant", "--port", "5280"],
                    out var options,
                    out _));

            var resolved = ResolveAssistantPort(options.Port);
            Assert.Equal(5280, resolved);
        }
        finally
        {
            Environment.SetEnvironmentVariable(SetupAssistantOptions.PortEnvironmentKey, previous);
        }
    }

    [Fact]
    public void Environment_port_is_used_when_cli_omits_port()
    {
        var previous = Environment.GetEnvironmentVariable(SetupAssistantOptions.PortEnvironmentKey);
        try
        {
            Environment.SetEnvironmentVariable(SetupAssistantOptions.PortEnvironmentKey, "5282");
            Assert.True(
                SetupAssistantCliParser.TryParse(["setup", "assistant"], out var options, out _));

            var resolved = ResolveAssistantPort(options.Port);
            Assert.Equal(5282, resolved);
        }
        finally
        {
            Environment.SetEnvironmentVariable(SetupAssistantOptions.PortEnvironmentKey, previous);
        }
    }

    private static int ResolveAssistantPort(int? cliPort)
    {
        if (cliPort.HasValue)
        {
            return cliPort.Value;
        }

        if (!SetupAssistantOptions.TryResolvePort(
                Environment.GetEnvironmentVariable(SetupAssistantOptions.PortEnvironmentKey),
                out var port))
        {
            throw new InvalidOperationException("Environment port should resolve in this test.");
        }

        return port;
    }
}
