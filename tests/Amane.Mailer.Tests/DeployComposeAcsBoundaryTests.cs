using System.Text.RegularExpressions;

namespace Amane.Mailer.Tests;

/// <summary>
/// Structural regression guard for MAILER-ACS-INPUT-01's Compose boundary: Staging/Production
/// deploy (<c>infra/deploy/compose.yml</c>) must only ever wire the file-based
/// <c>ACS_CONNECTION_STRING_FILE</c> secret, never the bare <c>ACS_CONNECTION_STRING</c> env var
/// (that stays local-drill-only, injected by
/// <c>infra/deploy/drills/mail-05a-acs-drill.sh</c>'s own compose override — untouched by this
/// action). Parses the compose file as plain text (no YAML dependency); this is deliberately a
/// fast, no-Docker-required test, not a substitute for `docker compose config` on a real host.
/// </summary>
public sealed class DeployComposeAcsBoundaryTests
{
    [Fact]
    public void Mailer_migrate_has_no_acs_related_configuration()
    {
        var block = ServiceBlock("mailer-migrate");

        Assert.DoesNotContain("ACS_CONNECTION_STRING", block, StringComparison.Ordinal);
        Assert.DoesNotContain("/run/secrets/acs", block, StringComparison.Ordinal);
    }

    [Fact]
    public void Mailer_only_references_the_file_based_secret_read_only()
    {
        var block = ServiceBlock("mailer");

        Assert.Contains("ACS_CONNECTION_STRING_FILE:", block, StringComparison.Ordinal);
        Assert.DoesNotContain("ACS_CONNECTION_STRING:", block, StringComparison.Ordinal);
        Assert.Matches(new Regex(@"/run/secrets/acs:ro\b"), block);

        // Fail-closed flag: without this, MailerOptions would silently accept a bare
        // ACS_CONNECTION_STRING env var here too. See MailerOptionsAcsConnectionStringTests.
        Assert.Contains("MAILER_REQUIRE_ACS_SECRET_FILE: \"true\"", block, StringComparison.Ordinal);
    }

    [Fact]
    public void Mailer_acs_admin_mounts_both_secret_directories_read_write()
    {
        var block = ServiceBlock("mailer-acs-admin");

        Assert.DoesNotContain("ACS_CONNECTION_STRING:", block, StringComparison.Ordinal);
        Assert.Contains("MAILER_ACS_SECRET_DIRECTORY:", block, StringComparison.Ordinal);
        Assert.Contains("MAILER_PLATFORM_SENDER_DIRECTORY:", block, StringComparison.Ordinal);

        // Read-write: mounted without a trailing ":ro" on the source:target line.
        Assert.Matches(new Regex(@"/run/secrets/acs\s*$", RegexOptions.Multiline), block);
        Assert.Matches(new Regex(@"/run/config/platform-sender\s*$", RegexOptions.Multiline), block);
        Assert.DoesNotContain("/run/secrets/acs:ro", block, StringComparison.Ordinal);
        Assert.DoesNotContain("/run/config/platform-sender:ro", block, StringComparison.Ordinal);
    }

    [Fact]
    public void Env_example_does_not_declare_the_bare_acs_connection_string_variable()
    {
        var text = File.ReadAllText(Path.Combine(FindRepositoryRoot(), "infra", "deploy", ".env.example"));

        Assert.DoesNotMatch(new Regex(@"^ACS_CONNECTION_STRING=", RegexOptions.Multiline), text);
        Assert.Contains("MAILER_ACS_SECRET_HOST_PATH=", text, StringComparison.Ordinal);
        Assert.Contains("MAILER_PLATFORM_SENDER_HOST_PATH=", text, StringComparison.Ordinal);
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

            // Next top-level service (two-space indent, non-space char after) or a top-level
            // section (networks:, volumes:, secrets:, zero indent) ends this service's block.
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
