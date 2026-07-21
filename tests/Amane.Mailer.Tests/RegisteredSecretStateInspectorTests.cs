using Amane.Mailer.Operations;

namespace Amane.Mailer.Tests;

public sealed class RegisteredSecretStateInspectorTests
{
    private const string ValidPlatformSenderJson = """
        {
          "version": 1,
          "environment": "staging",
          "sender": { "email": "sender@example.com", "display_name": "Example Sender" },
          "provider": "acs",
          "live_sending": false
        }
        """;

    [Fact]
    public void Both_absent_is_clean()
    {
        using var scratch = new ScratchDirectory();

        var state = RegisteredSecretStateInspector.Inspect(scratch.AcsPath, scratch.SenderPath);

        Assert.Equal(RegisteredSecretState.Clean, state);
    }

    [Fact]
    public void Both_empty_files_present_is_clean()
    {
        using var scratch = new ScratchDirectory();
        File.WriteAllText(scratch.AcsPath, "   ");

        var state = RegisteredSecretStateInspector.Inspect(scratch.AcsPath, scratch.SenderPath);

        Assert.Equal(RegisteredSecretState.Clean, state);
    }

    [Fact]
    public void Both_populated_and_valid_is_fully_registered()
    {
        using var scratch = new ScratchDirectory();
        File.WriteAllText(scratch.AcsPath, "Endpoint=https://example;AccessKey=secret");
        File.WriteAllText(scratch.SenderPath, ValidPlatformSenderJson);

        var state = RegisteredSecretStateInspector.Inspect(scratch.AcsPath, scratch.SenderPath);

        Assert.Equal(RegisteredSecretState.FullyRegistered, state);
    }

    [Fact]
    public void Acs_present_but_sender_absent_is_partial()
    {
        using var scratch = new ScratchDirectory();
        File.WriteAllText(scratch.AcsPath, "Endpoint=https://example;AccessKey=secret");

        var state = RegisteredSecretStateInspector.Inspect(scratch.AcsPath, scratch.SenderPath);

        Assert.Equal(RegisteredSecretState.PartialOrCorrupt, state);
    }

    [Fact]
    public void Sender_present_but_acs_absent_is_partial()
    {
        using var scratch = new ScratchDirectory();
        File.WriteAllText(scratch.SenderPath, ValidPlatformSenderJson);

        var state = RegisteredSecretStateInspector.Inspect(scratch.AcsPath, scratch.SenderPath);

        Assert.Equal(RegisteredSecretState.PartialOrCorrupt, state);
    }

    [Fact]
    public void Corrupt_sender_json_with_acs_absent_is_partial_not_clean()
    {
        using var scratch = new ScratchDirectory();
        File.WriteAllText(scratch.SenderPath, "{ not valid json");

        var state = RegisteredSecretStateInspector.Inspect(scratch.AcsPath, scratch.SenderPath);

        Assert.Equal(RegisteredSecretState.PartialOrCorrupt, state);
    }

    [Fact]
    public void Sender_json_failing_schema_validation_is_partial()
    {
        using var scratch = new ScratchDirectory();
        File.WriteAllText(scratch.AcsPath, "Endpoint=https://example;AccessKey=secret");
        File.WriteAllText(
            scratch.SenderPath,
            ValidPlatformSenderJson.Replace("\"live_sending\": false", "\"live_sending\": true"));

        var state = RegisteredSecretStateInspector.Inspect(scratch.AcsPath, scratch.SenderPath);

        Assert.Equal(RegisteredSecretState.PartialOrCorrupt, state);
    }

    private sealed class ScratchDirectory : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(), "amane-mailer-state-" + Guid.NewGuid().ToString("N"));

        public string AcsPath { get; }

        public string SenderPath { get; }

        public ScratchDirectory()
        {
            Directory.CreateDirectory(Path);
            AcsPath = System.IO.Path.Combine(Path, "acs_connection_string");
            SenderPath = System.IO.Path.Combine(Path, "platform-sender.json");
        }

        public void Dispose()
        {
            if (Directory.Exists(Path))
            {
                Directory.Delete(Path, recursive: true);
            }
        }
    }
}
