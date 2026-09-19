namespace Amane.Mailer.Tests;

/// <summary>
/// Structural regression guards for the Issue #771 shared platform-edge deployment profile.
/// The platform reverse proxy remains outside this Compose project.
/// </summary>
public sealed class DeployComposeSharedEdgeBoundaryTests
{
    [Fact]
    public void Shared_edge_profile_keeps_reverse_proxy_platform_owned_and_publishes_no_ports()
    {
        var compose = ReadRepositoryFile("infra", "deploy", "compose.shared-edge.yml");

        Assert.DoesNotContain("\n  proxy:", compose, StringComparison.Ordinal);
        Assert.DoesNotContain("\n    ports:", compose, StringComparison.Ordinal);
        Assert.Contains("  shared_edge:\n    external: true", compose, StringComparison.Ordinal);
        Assert.Contains(
            "name: ${MAILER_SHARED_EDGE_NETWORK_NAME:?Set MAILER_SHARED_EDGE_NETWORK_NAME}",
            compose,
            StringComparison.Ordinal);
    }

    [Fact]
    public void Mailer_joins_only_internal_and_the_external_shared_edge()
    {
        var compose = ReadRepositoryFile("infra", "deploy", "compose.shared-edge.yml");
        var mailer = ServiceBlock(compose, "mailer");

        Assert.Contains("networks: !override", mailer, StringComparison.Ordinal);
        Assert.Contains("internal: {}", mailer, StringComparison.Ordinal);
        Assert.Contains("shared_edge:", mailer, StringComparison.Ordinal);
        Assert.Contains(
            "- ${MAILER_SHARED_EDGE_ALIAS:-mailer}",
            mailer,
            StringComparison.Ordinal);
        Assert.Contains(
            "ipv4_address: ${MAILER_SHARED_EDGE_MAILER_IPV4_ADDRESS:?Set MAILER_SHARED_EDGE_MAILER_IPV4_ADDRESS}",
            mailer,
            StringComparison.Ordinal);
        Assert.DoesNotContain("\n      mailer:", mailer, StringComparison.Ordinal);
    }

    [Fact]
    public void Migration_service_remains_internal_only()
    {
        var compose = ReadRepositoryFile("infra", "deploy", "compose.shared-edge.yml");
        var migrate = ServiceBlock(compose, "mailer-migrate");

        Assert.Contains("networks: !override", migrate, StringComparison.Ordinal);
        Assert.Contains("internal: {}", migrate, StringComparison.Ordinal);
        Assert.DoesNotContain("shared_edge:", migrate, StringComparison.Ordinal);
        Assert.DoesNotContain("ports:", migrate, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("mailer")]
    [InlineData("mailer-migrate")]
    public void Managed_v2_services_remove_legacy_tenant_and_provider_inputs(string serviceName)
    {
        var compose = ReadRepositoryFile("infra", "deploy", "compose.shared-edge.yml");
        var service = ServiceBlock(compose, serviceName);

        foreach (var key in new[]
                 {
                     "MAILER_TENANTS_PATH",
                     "MAIL_SERVICE_TOKEN",
                     "MAIL_SERVICE_TOKEN_DEVELOP",
                     "MAIL_SERVICE_TOKEN_STAGING",
                     "MAIL_SERVICE_TOKEN_PRODUCTION",
                     "MAILER_PROVIDER"
                 })
        {
            Assert.Contains($"{key}: !reset null", service, StringComparison.Ordinal);
        }

        Assert.Contains("volumes: !override", service, StringComparison.Ordinal);
        Assert.DoesNotContain("tenants.json", service, StringComparison.Ordinal);
        Assert.DoesNotContain("MAILER_TENANTS_HOST_PATH", service, StringComparison.Ordinal);
    }

    [Fact]
    public void Forwarded_headers_trust_only_the_configured_proxy_and_admin_uses_fixed_edge_address()
    {
        var compose = ReadRepositoryFile("infra", "deploy", "compose.shared-edge.yml");
        var mailer = ServiceBlock(compose, "mailer");

        Assert.Contains(
            "ASPNETCORE_FORWARDEDHEADERS_ENABLED: \"true\"",
            mailer,
            StringComparison.Ordinal);
        Assert.Contains(
            "MAILER_FORWARDED_HEADERS_TRUSTED_PROXIES: ${MAILER_SHARED_EDGE_PROXY_IPV4_ADDRESS:?Set MAILER_SHARED_EDGE_PROXY_IPV4_ADDRESS}",
            mailer,
            StringComparison.Ordinal);
        Assert.Contains(
            "MAILER_FORWARDED_HEADERS_TRUSTED_NETWORKS: \"\"",
            mailer,
            StringComparison.Ordinal);
        Assert.Contains(
            "AMANE_ADMIN_ALLOWED_LOCAL_ADDRESS: ${MAILER_SHARED_EDGE_MAILER_IPV4_ADDRESS:?Set MAILER_SHARED_EDGE_MAILER_IPV4_ADDRESS}",
            mailer,
            StringComparison.Ordinal);
        Assert.Contains("AMANE_ADMIN_ALLOW_HTTP: \"false\"", mailer, StringComparison.Ordinal);
        Assert.DoesNotContain("0.0.0.0/0", mailer, StringComparison.Ordinal);
    }

    [Fact]
    public void Shared_edge_env_example_documents_repository_profile_without_edge_ownership()
    {
        var env = ReadRepositoryFile("infra", "deploy", ".env.shared-edge.example");

        Assert.Contains(
            "MAILER_SHARED_EDGE_NETWORK_NAME=replace-with-existing-shared-edge-network",
            env,
            StringComparison.Ordinal);
        Assert.Contains("MAILER_SHARED_EDGE_ALIAS=mailer", env, StringComparison.Ordinal);
        Assert.Contains("MAILER_SHARED_EDGE_PROXY_IPV4_ADDRESS=", env, StringComparison.Ordinal);
        Assert.Contains("MAILER_SHARED_EDGE_MAILER_IPV4_ADDRESS=", env, StringComparison.Ordinal);
        Assert.Contains(
            "MAILER_COMPOSE_FILE=compose.yml:compose.shared-edge.yml:compose.image-digest.yml",
            env,
            StringComparison.Ordinal);
        Assert.DoesNotContain("MAIL_SERVICE_TOKEN=", env, StringComparison.Ordinal);
        Assert.DoesNotContain("MAILER_PROVIDER=", env, StringComparison.Ordinal);
        Assert.DoesNotContain("MAILER_VPS_PROXY_IMAGE=", env, StringComparison.Ordinal);
        Assert.DoesNotContain("MAILER_PUBLIC_HOSTNAME=", env, StringComparison.Ordinal);
    }

    private static string ServiceBlock(string compose, string serviceName)
    {
        var lines = compose.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        var startIndex = Array.FindIndex(lines, line => line == $"  {serviceName}:");
        Assert.True(startIndex >= 0, $"Could not find service '{serviceName}' in shared-edge compose overlay.");

        var endIndex = lines.Length;
        for (var i = startIndex + 1; i < lines.Length; i++)
        {
            var line = lines[i];
            var isServiceKey = line.Length > 2
                && line[0] == ' '
                && line[1] == ' '
                && line[2] != ' '
                && line.TrimEnd().EndsWith(':');
            var isTopLevelKey = line.Length > 0
                && line[0] != ' '
                && line.TrimEnd().EndsWith(':');
            if (isServiceKey || isTopLevelKey)
            {
                endIndex = i;
                break;
            }
        }

        return string.Join('\n', lines[startIndex..endIndex]);
    }

    private static string ReadRepositoryFile(params string[] segments)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null
               && !File.Exists(Path.Combine(directory.FullName, "Amane.Mailer.slnx")))
        {
            directory = directory.Parent;
        }

        Assert.True(directory is not null, "Could not locate the repository root.");
        var path = directory!.FullName;
        foreach (var segment in segments)
        {
            path = Path.Combine(path, segment);
        }

        return File.ReadAllText(path);
    }
}
