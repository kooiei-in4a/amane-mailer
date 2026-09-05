using System.Text.RegularExpressions;

namespace Amane.Mailer.Tests;

/// <summary>
/// Structural regression guards for the Issue #733 PR1 VPS reference profile. These checks are
/// intentionally dependency-free and complement (rather than replace) `docker compose config`.
/// </summary>
public sealed class DeployComposeVpsDogfoodBoundaryTests
{
    [Fact]
    public void Vps_profile_publishes_only_caddy_http_and_https_ports()
    {
        var compose = ReadRepositoryFile("infra", "deploy", "compose.vps-dogfood.yml");
        var proxy = ServiceBlock(compose, "proxy");
        var mailer = ServiceBlock(compose, "mailer");

        Assert.Contains("profiles:", proxy, StringComparison.Ordinal);
        Assert.Contains("- vps-dogfood", proxy, StringComparison.Ordinal);
        Assert.Contains(
            "\"${MAILER_VPS_HTTP_BIND:-0.0.0.0}:${MAILER_VPS_HTTP_PORT:-80}:80\"",
            proxy,
            StringComparison.Ordinal);
        Assert.Contains(
            "\"${MAILER_VPS_HTTPS_BIND:-0.0.0.0}:${MAILER_VPS_HTTPS_PORT:-443}:443\"",
            proxy,
            StringComparison.Ordinal);
        Assert.DoesNotContain("ports:", mailer, StringComparison.Ordinal);

        var serviceBlocks = Regex.Matches(
                compose,
                "(?ms)^  [a-z0-9][a-z0-9-]*:\\n.*?(?=^  [a-z0-9][a-z0-9-]*:\\n|^\\S|\\z)")
            .Select(match => match.Value)
            .ToArray();
        Assert.Equal(3, serviceBlocks.Length);
        Assert.Single(serviceBlocks, block => block.Contains("ports:", StringComparison.Ordinal));
    }

    [Fact]
    public void Vps_profile_replaces_the_shared_consumer_network_with_a_dedicated_proxy_network()
    {
        var compose = ReadRepositoryFile("infra", "deploy", "compose.vps-dogfood.yml");
        var mailer = ServiceBlock(compose, "mailer");

        Assert.Contains("networks: !override", mailer, StringComparison.Ordinal);
        Assert.Contains("internal: {}", mailer, StringComparison.Ordinal);
        Assert.Contains("vps_proxy:", mailer, StringComparison.Ordinal);
        Assert.Contains(
            "ipv4_address: ${MAILER_VPS_MAILER_IPV4_ADDRESS:-172.30.0.3}",
            mailer,
            StringComparison.Ordinal);
        Assert.DoesNotContain("\n      mailer:", mailer, StringComparison.Ordinal);

        var proxy = ServiceBlock(compose, "proxy");
        Assert.Contains("vps_proxy:", proxy, StringComparison.Ordinal);
        Assert.Contains(
            "ipv4_address: ${MAILER_VPS_PROXY_IPV4_ADDRESS:-172.30.0.2}",
            proxy,
            StringComparison.Ordinal);
        Assert.Contains("caddy_data:/data", proxy, StringComparison.Ordinal);
        Assert.Contains("caddy_config:/config", proxy, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("mailer")]
    [InlineData("mailer-migrate")]
    public void Vps_profile_removes_legacy_tenant_configuration_from_both_mailer_services(
        string serviceName)
    {
        var compose = ReadRepositoryFile("infra", "deploy", "compose.vps-dogfood.yml");
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
    public void Vps_profile_trusts_only_the_fixed_proxy_for_forwarded_headers()
    {
        var compose = ReadRepositoryFile("infra", "deploy", "compose.vps-dogfood.yml");
        var mailer = ServiceBlock(compose, "mailer");

        Assert.Contains(
            "ASPNETCORE_FORWARDEDHEADERS_ENABLED: \"true\"",
            mailer,
            StringComparison.Ordinal);
        Assert.Contains(
            "MAILER_FORWARDED_HEADERS_TRUSTED_PROXIES: ${MAILER_VPS_PROXY_IPV4_ADDRESS:-172.30.0.2}",
            mailer,
            StringComparison.Ordinal);
        Assert.Contains(
            "MAILER_FORWARDED_HEADERS_TRUSTED_NETWORKS: \"\"",
            mailer,
            StringComparison.Ordinal);
        Assert.DoesNotContain("0.0.0.0/0", mailer, StringComparison.Ordinal);
        Assert.Contains(
            "AMANE_ADMIN_ALLOWED_LOCAL_ADDRESS: ${MAILER_VPS_MAILER_IPV4_ADDRESS:-172.30.0.3}",
            mailer,
            StringComparison.Ordinal);
        Assert.Contains("AMANE_ADMIN_ALLOW_HTTP: \"false\"", mailer, StringComparison.Ordinal);
    }

    [Fact]
    public void Caddyfile_separates_operator_management_from_public_api_paths()
    {
        var caddyfile = ReadRepositoryFile(
            "infra",
            "deploy",
            "Caddyfile.vps-dogfood.example");

        Assert.Contains("{$MAILER_PUBLIC_HOSTNAME}", caddyfile, StringComparison.Ordinal);
        Assert.Contains(
            "path /admin /admin/* /setup /setup/* /metrics",
            caddyfile,
            StringComparison.Ordinal);
        Assert.Contains(
            "remote_ip {$MAILER_MANAGEMENT_ALLOWED_CIDRS}",
            caddyfile,
            StringComparison.Ordinal);
        Assert.Matches(
            new Regex(@"handle @management \{\s+respond 404\s+\}", RegexOptions.Multiline),
            caddyfile);
        Assert.Contains(
            "@public path /api/* /healthz /readyz",
            caddyfile,
            StringComparison.Ordinal);
        Assert.Contains("reverse_proxy mailer:8080", caddyfile, StringComparison.Ordinal);
        Assert.Contains("header_up X-Forwarded-Proto {scheme}", caddyfile, StringComparison.Ordinal);
        Assert.DoesNotMatch(
            new Regex(@"^\s*remote_ip\s+0\.0\.0\.0/0", RegexOptions.Multiline),
            caddyfile);
    }

    [Fact]
    public void Vps_env_example_documents_non_secret_edge_values_without_a_wide_management_allowlist()
    {
        var env = ReadRepositoryFile("infra", "deploy", ".env.example");

        Assert.Contains("MAILER_PUBLIC_HOSTNAME=mailer.example.invalid", env, StringComparison.Ordinal);
        Assert.Contains("MAILER_MANAGEMENT_ALLOWED_CIDRS=192.0.2.0/24", env, StringComparison.Ordinal);
        Assert.Contains(
            "MAILER_VPS_PROXY_IMAGE=caddy:2.10.2-alpine@sha256:4c6e91c6ed0e2fa03efd5b44747b625fec79bc9cd06ac5235a779726618e530d",
            env,
            StringComparison.Ordinal);
        Assert.Contains("MAILER_VPS_CADDYFILE_PATH=./Caddyfile.vps-dogfood", env, StringComparison.Ordinal);
        Assert.DoesNotContain("MAILER_MANAGEMENT_ALLOWED_CIDRS=0.0.0.0/0", env, StringComparison.Ordinal);
        Assert.DoesNotContain("MAILER_VPS_PROXY_IMAGE=caddy:latest", env, StringComparison.Ordinal);
    }

    [Fact]
    public void Vps_env_example_has_no_legacy_tenant_or_provider_inputs()
    {
        var env = ReadRepositoryFile("infra", "deploy", ".env.vps-dogfood.example");

        foreach (var key in new[]
                 {
                     "MAILER_TENANTS_HOST_PATH",
                     "MAILER_TENANTS_CONTAINER_PATH",
                     "MAIL_SERVICE_TOKEN",
                     "MAIL_SERVICE_TOKEN_DEVELOP",
                     "MAIL_SERVICE_TOKEN_STAGING",
                     "MAIL_SERVICE_TOKEN_PRODUCTION",
                     "MAILER_PROVIDER"
                 })
        {
            Assert.DoesNotContain($"{key}=", env, StringComparison.Ordinal);
        }

        Assert.Contains("MAILER_METRICS_ENABLED=false", env, StringComparison.Ordinal);
    }

    [Fact]
    public void Vps_runbook_explains_the_server_local_address_and_client_cidr_boundaries()
    {
        var runbook = ReadRepositoryFile("docs", "ops", "vps-dogfood-deployment.md");

        Assert.Contains("Connection.LocalIpAddress", runbook, StringComparison.Ordinal);
        Assert.Contains("MAILER_MANAGEMENT_ALLOWED_CIDRS", runbook, StringComparison.Ordinal);
        Assert.Contains(".env.vps-dogfood.example", runbook, StringComparison.Ordinal);
        Assert.Contains("SQLite managed state", runbook, StringComparison.Ordinal);
        Assert.Contains("provider secret", runbook, StringComparison.Ordinal);
        Assert.Contains("bootstrap token", runbook, StringComparison.Ordinal);
        Assert.Contains("MAIL_SERVICE_TOKEN*", runbook, StringComparison.Ordinal);
        Assert.Contains("proxy bypass", runbook, StringComparison.Ordinal);
        Assert.Contains("down -v", runbook, StringComparison.Ordinal);
        Assert.DoesNotContain(
            "MAILER_TENANTS_HOST_PATH が実 tenant JSON を指し",
            runbook,
            StringComparison.Ordinal);
    }

    private static string ServiceBlock(string compose, string serviceName)
    {
        var lines = compose.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        var startIndex = Array.FindIndex(lines, line => line == $"  {serviceName}:");
        Assert.True(startIndex >= 0, $"Could not find service '{serviceName}' in VPS compose overlay.");

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
