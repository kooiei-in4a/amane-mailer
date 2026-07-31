using Amane.Mailer.Setup.Assistant;

namespace Amane.Mailer.Tests.Setup.Assistant;

public sealed class SetupAssistantRemoteAccessHintsTests
{
    [Fact]
    public void No_browser_startup_ssh_tunnel_uses_bound_port()
    {
        using var writer = new StringWriter();
        const string token = "synthetic-assistant-token-not-real";

        SetupAssistantRemoteAccessHints.WriteNoBrowserStartup(writer, 5280, token);

        var text = writer.ToString();
        Assert.Contains("ssh -L 5280:127.0.0.1:5280", text, StringComparison.Ordinal);
        Assert.Contains("http://127.0.0.1:5280/", text, StringComparison.Ordinal);
        Assert.DoesNotContain(
            token,
            SetupAssistantRemoteAccessHints.BuildLoopbackUrl(5280),
            StringComparison.Ordinal);
        Assert.Contains("Token:", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Loopback_url_does_not_embed_token()
    {
        const string token = "synthetic-assistant-token-not-real";
        var url = SetupAssistantRemoteAccessHints.BuildLoopbackUrl(5280);
        Assert.Equal("http://127.0.0.1:5280/", url);
        Assert.DoesNotContain(token, url, StringComparison.Ordinal);
    }
}
