using System.Text.RegularExpressions;

namespace Amane.Mailer.Tests;

/// <summary>
/// Structural regression guard for mode 5 deploy wiring: <c>infra/deploy/compose.yml</c> must
/// pass bounce Queue settings into the mailer service via env + file-secret mount, never a bare
/// <c>MAILER_BOUNCE_QUEUE_CONNECTION_STRING</c> on that service (file-secret boundary mirrors ACS).
/// Plain-text parse only; not a substitute for <c>docker compose config</c> on a real host.
/// </summary>
public sealed class DeployComposeBounceBoundaryTests
{
    [Fact]
    public void Mailer_migrate_has_no_bounce_queue_configuration()
    {
        var block = ServiceBlock("mailer-migrate");

        Assert.DoesNotContain("MAILER_BOUNCE", block, StringComparison.Ordinal);
        Assert.DoesNotContain("/run/secrets/bounce-queue", block, StringComparison.Ordinal);
    }

    [Fact]
    public void Mailer_wires_bounce_mode_name_and_file_secret_read_only()
    {
        var block = ServiceBlock("mailer");

        Assert.Contains("MAILER_BOUNCE_INGESTION:", block, StringComparison.Ordinal);
        Assert.Contains("MAILER_BOUNCE_QUEUE_NAME:", block, StringComparison.Ordinal);
        Assert.Contains(
            "MAILER_BOUNCE_QUEUE_CONNECTION_STRING_FILE: /run/secrets/bounce-queue/queue_connection_string",
            block,
            StringComparison.Ordinal);
        Assert.DoesNotContain("MAILER_BOUNCE_QUEUE_CONNECTION_STRING:", block, StringComparison.Ordinal);
        Assert.Matches(new Regex(@"/run/secrets/bounce-queue:ro\b"), block);
    }

    [Fact]
    public void Env_example_documents_bounce_placeholders_without_bare_connection_string()
    {
        var text = File.ReadAllText(Path.Combine(FindRepositoryRoot(), "infra", "deploy", ".env.example"));

        Assert.Contains("MAILER_BOUNCE_INGESTION=", text, StringComparison.Ordinal);
        Assert.Contains("MAILER_BOUNCE_QUEUE_NAME=", text, StringComparison.Ordinal);
        Assert.Contains("MAILER_BOUNCE_QUEUE_SECRET_HOST_PATH=", text, StringComparison.Ordinal);
        Assert.DoesNotMatch(
            new Regex(@"^MAILER_BOUNCE_QUEUE_CONNECTION_STRING=", RegexOptions.Multiline),
            text);
    }

    private static string ServiceBlock(string serviceName)
    {
        var text = File.ReadAllText(Path.Combine(FindRepositoryRoot(), "infra", "deploy", "compose.yml"));
        var lines = text.Replace("\r\n", "\n").Split('\n');

        var startIndex = Array.FindIndex(lines, l => l == $"  {serviceName}:");
        Assert.True(startIndex >= 0, $"Could not find top-level service '{serviceName}' in compose.yml.");

        var endIndex = lines.Length;
        for (var i = startIndex + 1; i < lines.Length; i++)
        {
            var line = lines[i];
            if (line.Length == 0)
            {
                continue;
            }

            var isTwoSpaceKey = line.Length > 2 && line[0] == ' ' && line[1] == ' ' && line[2] != ' ' && line.TrimEnd().EndsWith(':');
            var isZeroIndentKey = line.Length > 0 && line[0] != ' ' && line.TrimEnd().EndsWith(':');
            if (isTwoSpaceKey || isZeroIndentKey)
            {
                endIndex = i;
                break;
            }
        }

        return string.Join('\n', lines[startIndex..endIndex]);
    }

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