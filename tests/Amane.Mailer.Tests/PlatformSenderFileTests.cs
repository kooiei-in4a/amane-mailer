using System.Text.Json;
using Amane.Mailer.Configuration;
using Amane.Mailer.Json;

namespace Amane.Mailer.Tests;

public sealed class PlatformSenderFileTests
{
    [Fact]
    public void Example_file_passes_runtime_validation()
    {
        var path = Path.Combine(FindRepositoryRoot(), "config", "mailer", "platform-sender.example.json");
        var file = JsonSerializer.Deserialize(File.ReadAllText(path), MailerJsonContext.Default.PlatformSenderFile);

        Assert.NotNull(file);
        file.Validate();
        Assert.Equal("platform-sender.json", PlatformSenderFile.CanonicalFileName);
    }

    [Fact]
    public void Rejects_unknown_version()
    {
        var file = Valid() with { Version = 2 };
        var ex = Assert.Throws<InvalidOperationException>(file.Validate);
        Assert.Contains("version", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("Staging")]
    [InlineData("STAGING")]
    [InlineData("Production")]
    [InlineData("")]
    public void Rejects_environment_other_than_lowercase_staging_or_production(string environment)
    {
        var file = Valid() with { Environment = environment };
        var ex = Assert.Throws<InvalidOperationException>(file.Validate);
        Assert.Contains("environment", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Accepts_lowercase_production_environment()
    {
        var file = Valid() with { Environment = "production" };
        file.Validate();
    }

    [Fact]
    public void Rejects_provider_other_than_acs()
    {
        var file = Valid() with { Provider = "mailpit" };
        var ex = Assert.Throws<InvalidOperationException>(file.Validate);
        Assert.Contains("provider", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Rejects_live_sending_true()
    {
        var file = Valid() with { LiveSending = true };
        var ex = Assert.Throws<InvalidOperationException>(file.Validate);
        Assert.Contains("live_sending", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("not-an-email")]
    [InlineData("a b@example.com")]
    [InlineData("")]
    public void Rejects_invalid_sender_email(string email)
    {
        var file = Valid() with { Sender = Valid().Sender with { Email = email } };
        Assert.Throws<InvalidOperationException>(file.Validate);
    }

    [Fact]
    public void Rejects_empty_display_name()
    {
        var file = Valid() with { Sender = Valid().Sender with { DisplayName = "" } };
        Assert.Throws<InvalidOperationException>(file.Validate);
    }

    [Fact]
    public void Rejects_control_characters_in_display_name()
    {
        var file = Valid() with { Sender = Valid().Sender with { DisplayName = "abc" + "\t" + "def" } };
        Assert.Throws<InvalidOperationException>(file.Validate);
    }

    [Fact]
    public void Rejects_overlong_display_name()
    {
        var file = Valid() with { Sender = Valid().Sender with { DisplayName = new string('a', 201) } };
        Assert.Throws<InvalidOperationException>(file.Validate);
    }

    [Fact]
    public void Accepts_valid_file()
    {
        Valid().Validate();
    }

    private static PlatformSenderFile Valid() => new()
    {
        Version = 1,
        Environment = "staging",
        Sender = new PlatformSenderAddress { Email = "sender@example.com", DisplayName = "Example Sender" },
        Provider = "acs",
        LiveSending = false,
    };

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null
               && !File.Exists(Path.Combine(directory.FullName, "Amane.Mailer.slnx")))
        {
            directory = directory.Parent;
        }

        Assert.True(
            directory is not null,
            $"Could not find repository root containing Amane.Mailer.slnx from {AppContext.BaseDirectory}.");
        return directory.FullName;
    }
}
