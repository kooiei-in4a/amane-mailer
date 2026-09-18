using System.Diagnostics;
using System.Text.RegularExpressions;

namespace Amane.Mailer.Tests;

/// <summary>
/// Structural regression guards for the Issue #744 VPS management-edge reference profile. These
/// checks are intentionally dependency-free and complement (rather than replace) `docker compose config`.
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
    public void Caddyfile_requires_japan_basic_auth_for_browser_management_and_preserves_metrics_boundary()
    {
        var caddyfile = ReadRepositoryFile(
            "infra",
            "deploy",
            "Caddyfile.vps-dogfood.example");

        Assert.Contains("{$MAILER_PUBLIC_HOSTNAME}", caddyfile, StringComparison.Ordinal);
        Assert.Contains("basic_auth", caddyfile, StringComparison.Ordinal);
        Assert.Contains(
            "{{CADDY_BASIC_AUTH_USERNAME}} {{CADDY_BASIC_AUTH_BCRYPT_HASH}}",
            caddyfile,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "admin {{CADDY_BASIC_AUTH_BCRYPT_HASH}}",
            caddyfile,
            StringComparison.Ordinal);
        Assert.Contains("header_up -Authorization", caddyfile, StringComparison.Ordinal);
        Assert.Contains("\troute {", caddyfile, StringComparison.Ordinal);

        // The renderer requires each CIDR marker exactly once. The Japan allow-list is therefore a
        // single IP-only matcher shared by the browser management surface and /api.
        Assert.Single(Regex.Matches(caddyfile, Regex.Escape("{{JP_IPV4_CIDRS}}")));
        Assert.Single(Regex.Matches(caddyfile, Regex.Escape("{{JP_IPV6_CIDRS}}")));

        var jpMatcher = ExtractBraceBlock(caddyfile, "@jp {");
        Assert.Contains("{{JP_IPV4_CIDRS}}", jpMatcher, StringComparison.Ordinal);
        Assert.Contains("{{JP_IPV6_CIDRS}}", jpMatcher, StringComparison.Ordinal);
        Assert.DoesNotContain("path ", jpMatcher, StringComparison.Ordinal);
        Assert.DoesNotContain("/metrics", jpMatcher, StringComparison.Ordinal);
        Assert.DoesNotContain("basic_auth", jpMatcher, StringComparison.Ordinal);

        var browserFamilyMatcher = ExtractBraceBlock(caddyfile, "@browser_family {");
        Assert.Contains(
            "path /admin /admin/* /setup /setup/*",
            browserFamilyMatcher,
            StringComparison.Ordinal);
        var apiFamilyMatcher = ExtractBraceBlock(caddyfile, "@api_family {");
        Assert.Contains("path /api /api/*", apiFamilyMatcher, StringComparison.Ordinal);
        Assert.DoesNotContain("remote_ip", apiFamilyMatcher, StringComparison.Ordinal);
        Assert.DoesNotContain("JP_IPV", apiFamilyMatcher, StringComparison.Ordinal);

        var metricsMatcher = ExtractBraceBlock(caddyfile, "@metrics_operator {");
        Assert.Contains(
            "remote_ip {$MAILER_MANAGEMENT_ALLOWED_CIDRS}",
            metricsMatcher,
            StringComparison.Ordinal);
        Assert.DoesNotContain("JP_IPV", metricsMatcher, StringComparison.Ordinal);

        Assert.Contains("@public path /healthz /readyz", caddyfile, StringComparison.Ordinal);
        Assert.DoesNotMatch(
            new Regex(@"@public path[^\n]*/api", RegexOptions.Multiline),
            caddyfile);

        // Route order: browser management, then /api, then the two /metrics handlers, then the
        // country-unrestricted public paths, then the closed fallback. Each family is claimed by
        // exactly one outer handle so a non-JP request cannot fall through to a later handler.
        var route = ExtractBraceBlock(caddyfile, "\troute {");
        var browserHandlerIndex = route.IndexOf("handle @browser_family {", StringComparison.Ordinal);
        var apiHandlerIndex = route.IndexOf("handle @api_family {", StringComparison.Ordinal);
        var metricsHandlerIndex = route.IndexOf("handle @metrics_operator {", StringComparison.Ordinal);
        var metricsFallbackIndex = route.IndexOf("handle @metrics {", StringComparison.Ordinal);
        var publicHandlerIndex = route.IndexOf("handle @public {", StringComparison.Ordinal);
        Assert.True(browserHandlerIndex >= 0);
        Assert.True(browserHandlerIndex < apiHandlerIndex);
        Assert.True(apiHandlerIndex < metricsHandlerIndex);
        Assert.True(metricsHandlerIndex < metricsFallbackIndex);
        Assert.True(metricsFallbackIndex < publicHandlerIndex);
        var fallbackHandlerIndex = route.IndexOf(
            "\n\t\thandle {",
            publicHandlerIndex,
            StringComparison.Ordinal);
        Assert.True(fallbackHandlerIndex > publicHandlerIndex);

        // Browser management: JP gate -> Basic Auth -> upstream with Caddy Authorization stripped;
        // non-JP / undecidable -> 404 before any Basic Auth challenge.
        var browserHandler = ExtractBraceBlock(caddyfile, "handle @browser_family {");
        var browserJpBlock = ExtractBraceBlock(browserHandler, "handle @jp {");
        var browserBasicAuthIndex = browserJpBlock.IndexOf("basic_auth", StringComparison.Ordinal);
        var browserProxyIndex = browserJpBlock.IndexOf(
            "reverse_proxy mailer:8080",
            StringComparison.Ordinal);
        Assert.True(browserBasicAuthIndex >= 0 && browserBasicAuthIndex < browserProxyIndex);
        Assert.Contains("header_up -Authorization", browserJpBlock, StringComparison.Ordinal);
        var browserOutsideJp = browserHandler.Replace(browserJpBlock, string.Empty, StringComparison.Ordinal);
        Assert.DoesNotContain("basic_auth", browserOutsideJp, StringComparison.Ordinal);
        Assert.DoesNotContain("reverse_proxy", browserOutsideJp, StringComparison.Ordinal);
        Assert.Contains("respond 404", browserOutsideJp, StringComparison.Ordinal);

        // Consumer API: JP gate -> upstream, with the client Authorization header forwarded
        // unchanged and no Caddy Basic Auth anywhere; non-JP / undecidable -> 404, upstream never
        // reached, no Basic Auth challenge.
        var apiHandler = ExtractBraceBlock(caddyfile, "handle @api_family {");
        Assert.DoesNotContain("basic_auth", apiHandler, StringComparison.Ordinal);
        Assert.DoesNotContain("-Authorization", apiHandler, StringComparison.Ordinal);
        Assert.DoesNotContain("header_down", apiHandler, StringComparison.Ordinal);
        Assert.DoesNotMatch(
            new Regex("www-authenticate", RegexOptions.IgnoreCase),
            apiHandler);
        var apiJpBlock = ExtractBraceBlock(apiHandler, "handle @jp {");
        Assert.Contains("reverse_proxy mailer:8080", apiJpBlock, StringComparison.Ordinal);
        var apiOutsideJp = apiHandler.Replace(apiJpBlock, string.Empty, StringComparison.Ordinal);
        Assert.DoesNotContain("reverse_proxy", apiOutsideJp, StringComparison.Ordinal);
        Assert.Contains("respond 404", apiOutsideJp, StringComparison.Ordinal);

        // Metrics keeps its own operator CIDR boundary and Mailer's bearer auth, unchanged.
        var metricsHandler = ExtractBraceBlock(caddyfile, "handle @metrics_operator {");
        Assert.Contains("reverse_proxy mailer:8080", metricsHandler, StringComparison.Ordinal);
        Assert.DoesNotContain("basic_auth", metricsHandler, StringComparison.Ordinal);
        Assert.DoesNotContain("header_up -Authorization", metricsHandler, StringComparison.Ordinal);
        Assert.DoesNotContain("JP_IPV", metricsHandler, StringComparison.Ordinal);

        // Liveness / readiness stay public and country-unrestricted.
        var publicHandler = ExtractBraceBlock(caddyfile, "handle @public {");
        Assert.Contains("reverse_proxy mailer:8080", publicHandler, StringComparison.Ordinal);
        Assert.DoesNotContain("basic_auth", publicHandler, StringComparison.Ordinal);
        Assert.DoesNotContain("remote_ip", publicHandler, StringComparison.Ordinal);
        Assert.DoesNotContain("JP_IPV", publicHandler, StringComparison.Ordinal);
        Assert.DoesNotContain("/api", publicHandler, StringComparison.Ordinal);

        var fallbackHandler = route[fallbackHandlerIndex..];
        Assert.Contains("respond 404", fallbackHandler, StringComparison.Ordinal);

        Assert.Contains("reverse_proxy mailer:8080", caddyfile, StringComparison.Ordinal);
        Assert.Contains("header_up X-Forwarded-Proto {scheme}", caddyfile, StringComparison.Ordinal);
        Assert.DoesNotMatch(
            new Regex(@"^\s*remote_ip\s+0\.0\.0\.0/0", RegexOptions.Multiline),
            caddyfile);
        Assert.DoesNotContain("$2a$", caddyfile, StringComparison.Ordinal);
        Assert.DoesNotContain("$2b$", caddyfile, StringComparison.Ordinal);
        Assert.DoesNotContain("$2y$", caddyfile, StringComparison.Ordinal);

        var gitignore = ReadRepositoryFile(".gitignore");
        Assert.Contains(
            "infra/deploy/Caddyfile.vps-dogfood",
            gitignore,
            StringComparison.Ordinal);
    }

    /// <summary>
    /// Return <paramref name="opener"/> (which must end with <c>{</c>) plus everything through its
    /// matching close brace. Used to reason about one Caddyfile matcher/handler block in isolation.
    /// </summary>
    private static string ExtractBraceBlock(string text, string opener)
    {
        var start = text.IndexOf(opener, StringComparison.Ordinal);
        Assert.True(start >= 0, $"Could not find block '{opener}'.");
        var depth = 0;
        for (var i = start + opener.Length - 1; i < text.Length; i++)
        {
            if (text[i] == '{')
            {
                depth++;
            }
            else if (text[i] == '}')
            {
                depth--;
                if (depth == 0)
                {
                    return text[start..(i + 1)];
                }
            }
        }

        Assert.Fail($"Unbalanced braces after '{opener}'.");
        return string.Empty;
    }

    [Fact]
    public void Caddyfile_template_and_renderer_have_no_download_or_plaintext_secret_path()
    {
        var caddyfile = ReadRepositoryFile(
            "infra",
            "deploy",
            "Caddyfile.vps-dogfood.example");
        var renderer = ReadRepositoryFile(
            "infra",
            "deploy",
            "render-vps-management-edge.py");

        Assert.Contains("{{JP_IPV4_CIDRS}}", caddyfile, StringComparison.Ordinal);
        Assert.Contains("{{JP_IPV6_CIDRS}}", caddyfile, StringComparison.Ordinal);
        Assert.Contains("{{CADDY_BASIC_AUTH_BCRYPT_HASH}}", caddyfile, StringComparison.Ordinal);
        Assert.Contains("ipaddress", renderer, StringComparison.Ordinal);
        Assert.Contains("--self-test", renderer, StringComparison.Ordinal);
        Assert.Contains("--ipv4-zone", renderer, StringComparison.Ordinal);
        Assert.Contains("--ipv6-zone", renderer, StringComparison.Ordinal);
        Assert.Contains("--basic-auth-username", renderer, StringComparison.Ordinal);
        Assert.Contains("--basic-auth-hash-file", renderer, StringComparison.Ordinal);
        Assert.Contains("validate_basic_auth_username", renderer, StringComparison.Ordinal);
        Assert.DoesNotContain("GeoLite", renderer, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("MaxMind", renderer, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("geoname_id", renderer, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("--ipv4-blocks", renderer, StringComparison.Ordinal);
        Assert.DoesNotContain("--ipv6-blocks", renderer, StringComparison.Ordinal);
        Assert.DoesNotContain("--locations", renderer, StringComparison.Ordinal);
        Assert.DoesNotContain("urllib", renderer, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("urlopen", renderer, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("urlretrieve", renderer, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("requests", renderer, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("curl", renderer, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("--password", renderer, StringComparison.Ordinal);
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

    [Fact]
    public void Vps_runbooks_document_safe_live_edge_apply_and_issue_744_745_ownership()
    {
        var deploymentRunbooks = new[]
        {
            ReadRepositoryFile("docs", "ops", "vps-dogfood-deployment.md"),
            ReadRepositoryFile("docs", "ops", "vps-dogfood-deployment.en.md")
        };

        foreach (var runbook in deploymentRunbooks)
        {
            foreach (var required in new[]
                     {
                         "OPERATOR / agent-dev01",
                         "REMOTE VPS READ-ONLY",
                         "REMOTE VPS PRIVILEGED LIVE MUTATION",
                         "operator-side candidate",
                         "SSH stdin",
                         "com.docker.compose.project",
                         "com.docker.compose.service",
                         "Config.Image",
                         "single-file",
                         "bind mount",
                         "RW=false",
                         "exactly one",
                         "Caddy 2.10.2",
                         "/etc/caddy/Caddyfile",
                         "/srv/platform/edge/Caddyfile",
                         "IPv4 CIDR count",
                         "IPv6 CIDR count",
                         "SHA-256",
                         "candidate bytes",
                         "candidate SHA-256",
                         "IPdeny zone",
                         "Caddy Basic Auth password",
                         "Mailer Admin",
                         "Setup bootstrap token",
                         "bcrypt hash",
                         "plaintext password",
                         "https://www.ipdeny.com/ipblocks/data/aggregated/jp-aggregated.zone",
                         "https://www.ipdeny.com/ipv6/ipaddresses/aggregated/jp-aggregated.zone",
                         "download timestamp",
                         "input bytes",
                         "input SHA-256",
                         "Last-Modified",
                         "one CIDR per line",
                         "extra token",
                         "wrong address family",
                         "--ipv4-zone",
                         "--ipv6-zone",
                         "--basic-auth-hash-file",
                         "Human approval",
                         "root",
                         "Bash",
                         "ssh -t",
                         "sudo -v",
                         "sudo -n true",
                         "sudo -n bash",
                         "interactive sudo",
                         "interactive sudo authentication",
                         "TTY",
                         "credential cache",
                         "ROOT_BASH",
                         "sudo password",
                         "scp --",
                         "generated Caddyfile candidate only",
                         "temporary candidate",
                         "TEMPORARY_VPS_CANDIDATE_REQUIRED=true",
                         "PERSISTENT_VPS_STAGING_REQUIRED=false",
                         "deploy / non-privileged",
                         "root / sudo Bash",
                         "candidate_remote",
                         "expected_bytes",
                         "expected_sha256",
                         "remote_bytes",
                         "remote_sha256",
                         "0600",
                         "owner=deploy",
                         "regular file",
                         "not symlink",
                         "not Git managed",
                         "Compose configuration",
                         "cleanup",
                         "rm",
                         "owner",
                         "group",
                         "mode",
                         "device",
                         "inode",
                         "current",
                         "last-known-good",
                         "last-known-good backup",
                         "same-inode",
                         "same inode",
                         "in-place",
                         "fsync",
                         "container-visible",
                         "HOST_CADDY_SHA",
                         "CANDIDATE_SHA",
                         "CONTAINER_CADDY_SHA",
                         "caddy validate --config -",
                         "caddy reload",
                         "transaction",
                         "failure handler",
                         "mutation_started=false",
                         "rollback_in_progress=false",
                         "automatic rollback",
                         "rollback_current_in_place",
                         "rollback on validate failure",
                         "rollback on reload failure",
                         "preserve",
                         "0644 is Fresh baseline only",
                         "candidate file persisted on VPS=false",
                         "IPdeny raw data transferred=false",
                         "bcrypt input file transferred=false",
                         "/healthz",
                         "/readyz",
                         "/api",
                         "/admin",
                         "/setup",
                         ":8080",
                         "SSH",
                         "allow-all"
                     })
            {
                Assert.Contains(required, runbook, StringComparison.Ordinal);
            }

            var codeBlocks = Regex.Matches(
                    runbook,
                    @"(?ms)^[ \t]*~~~[^\n]*\n(?<body>.*?)^[ \t]*~~~\s*$")
                .Select(match => match.Groups["body"].Value)
                .ToArray();

            foreach (var body in codeBlocks)
            {
                if (!Regex.IsMatch(body, @"\bdocker (?:ps|inspect|exec)\b", RegexOptions.CultureInvariant))
                {
                    continue;
                }

                Assert.Contains("ssh", body, StringComparison.OrdinalIgnoreCase);
                Assert.Contains("# Run on VPS", body, StringComparison.Ordinal);
            }

            foreach (var forbidden in new[]
                     {
                         "sudo sh -c",
                         "sudo -S",
                         "sudo --stdin",
                         "SUDO_PASSWORD",
                         "sudo_password",
                         "docker exec proxy",
                         "docker inspect proxy",
                         "expected_mode=600",
                         "expected_mode = 600",
                         "chmod 600",
                         "atomic rename",
                         "atomic replace",
                         "mv -f",
                         "rename current",
                         "install directly over live current",
                         "/srv/platform/edge/staging/",
                         "staging_candidate",
                         "--mount",
                         "/secure/geolite",
                         "/secure/operator-secrets"
                     })
            {
                Assert.DoesNotContain(forbidden, runbook, StringComparison.OrdinalIgnoreCase);
            }

            Assert.DoesNotMatch(
                new Regex(@"\bsudo\s+(?:-n\s+)?(?:/bin/)?sh\b", RegexOptions.IgnoreCase),
                runbook);
            Assert.DoesNotMatch(
                new Regex(@"(?is)\bsh\b[^\n]*set\s+-Eeuo\s+pipefail|set\s+-Eeuo\s+pipefail[^\n]*\bsh\b"),
                runbook);
            Assert.DoesNotMatch(
                new Regex(@"(?im)^\s*ssh\s+-t\b[^\n]*<<"),
                runbook);
            Assert.Matches(
                new Regex(@"(?is)ssh\s+-t\b.*?sudo\s+-v\b.*?sudo\s+-n\s+true\b.*?sudo\s+-n\s+bash\b.*?<<'ROOT_BASH'", RegexOptions.CultureInvariant),
                runbook);
            Assert.Contains("sudo -v = interactive authentication", runbook, StringComparison.Ordinal);
            Assert.Contains("sudo -n true = post-authentication credential-cache verification only", runbook, StringComparison.Ordinal);
            Assert.Contains("sudo -n bash = already-authenticated transaction execution only", runbook, StringComparison.Ordinal);
            Assert.True(
                runbook.IndexOf("sudo -v = interactive authentication", StringComparison.Ordinal)
                    < runbook.IndexOf("sudo -n true = post-authentication credential-cache verification only", StringComparison.Ordinal));
            Assert.True(
                runbook.IndexOf("sudo -n true = post-authentication credential-cache verification only", StringComparison.Ordinal)
                    < runbook.IndexOf("sudo -n bash = already-authenticated transaction execution only", StringComparison.Ordinal));
            Assert.Contains("sudo password is entered only at the interactive sudo prompt and is never supplied by script/stdin.", runbook, StringComparison.Ordinal);
            Assert.Contains("transaction uses Bash ERR trap / pipefail semantics;", runbook, StringComparison.OrdinalIgnoreCase);

            var standaloneInteractiveSsh = codeBlocks.Single(body =>
                body.Contains("ssh -t \"$VPS_ALIAS\"", StringComparison.Ordinal));
            Assert.DoesNotContain("<<", standaloneInteractiveSsh, StringComparison.Ordinal);
            Assert.DoesNotContain("sudo -v", standaloneInteractiveSsh, StringComparison.Ordinal);

            var interactiveSudoPhase = codeBlocks.Single(body =>
                body.Contains("cleanup_temporary_candidate", StringComparison.Ordinal)
                && body.Contains("sudo -v", StringComparison.Ordinal)
                && body.Contains("sudo -n true", StringComparison.Ordinal));
            Assert.DoesNotContain("<<", interactiveSudoPhase, StringComparison.Ordinal);
            Assert.DoesNotContain("sudo -n bash", interactiveSudoPhase, StringComparison.Ordinal);

            var privilegedTransaction = codeBlocks.Single(body =>
                body.Contains("sudo -n bash", StringComparison.Ordinal)
                && body.Contains("<<'ROOT_BASH'", StringComparison.Ordinal));
            Assert.DoesNotContain("sudo -v", privilegedTransaction, StringComparison.Ordinal);
            Assert.Contains("sudo -n bash -s", privilegedTransaction, StringComparison.Ordinal);
            Assert.Contains("<<'ROOT_BASH'", privilegedTransaction, StringComparison.Ordinal);
            Assert.Contains("candidate_remote=$1", privilegedTransaction, StringComparison.Ordinal);
            Assert.Contains("write_contents_in_place \"$candidate_remote\"", privilegedTransaction, StringComparison.Ordinal);
            Assert.DoesNotContain("cat \"$candidate\" | ssh", privilegedTransaction, StringComparison.Ordinal);
            Assert.DoesNotContain("exec 3<&0", privilegedTransaction, StringComparison.Ordinal);
            Assert.DoesNotContain("/dev/fd/3", privilegedTransaction, StringComparison.Ordinal);
            Assert.Contains("scp -- \"$candidate\" \"${VPS_ALIAS}:${candidate_remote}\"", runbook, StringComparison.Ordinal);
            Assert.Contains("test \"$remote_bytes\" = \"$expected_bytes\"", runbook, StringComparison.Ordinal);
            Assert.Contains("test \"$remote_sha256\" = \"$expected_sha256\"", runbook, StringComparison.Ordinal);
            Assert.Contains("rm -f -- \"$candidate_remote\"", runbook, StringComparison.Ordinal);

            foreach (var body in codeBlocks)
            {
                Assert.DoesNotMatch(
                    new Regex(@"(?is)cat\s+""\$candidate""\s*\|\s*ssh\b.*?\bsudo\b"),
                    body);
                Assert.DoesNotMatch(
                    new Regex(@"(?is)(?:printf|echo|cat)\b.*?\|\s*sudo\b"),
                    body);
            }

            Assert.Contains("deploy", runbook, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("read-only", runbook, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("root-owned", runbook, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("install -o", runbook, StringComparison.Ordinal);
            Assert.Contains("same inode", runbook, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("caddy validate", runbook, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("rollback", runbook, StringComparison.OrdinalIgnoreCase);

            foreach (var staleSourceReference in new[]
                     {
                         "GeoLite",
                         "GeoLite2",
                         "MaxMind",
                         "geoname_id",
                         "registered_country_geoname_id",
                         "represented_country_geoname_id",
                         "Country CSV",
                         "--ipv4-blocks",
                         "--ipv6-blocks",
                         "--locations"
                     })
            {
                Assert.DoesNotContain(staleSourceReference, runbook, StringComparison.OrdinalIgnoreCase);
            }
        }

        var smokeRunbooks = new[]
        {
            ReadRepositoryFile("docs", "ops", "vps-dogfood-smoke.md"),
            ReadRepositoryFile("docs", "ops", "vps-dogfood-smoke.en.md")
        };

        foreach (var runbook in smokeRunbooks)
        {
            foreach (var required in new[]
                     {
                         "#744",
                         "#745",
                         "JP / non-JP",
                         "Basic Auth",
                         "Mailer",
                         "/api",
                         ":8080",
                         "SSH",
                         "reset/setup",
                         "real ACS send",
                         "UX dogfood",
                         "historical"
                     })
            {
                Assert.Contains(required, runbook, StringComparison.Ordinal);
            }
        }
    }

    [Fact]
    public void Vps_runbook_transaction_defines_and_directly_exercises_value_free_acceptance_contract()
    {
        var deploymentRunbooks = new[]
        {
            ReadRepositoryFile("docs", "ops", "vps-dogfood-deployment.md"),
            ReadRepositoryFile("docs", "ops", "vps-dogfood-deployment.en.md")
        };

        foreach (var runbook in deploymentRunbooks)
        {
            var transaction = ExtractRootBash(runbook);
            var definition = "run_approved_value_free_acceptance_checks() {";
            var definitionIndex = transaction.IndexOf(definition, StringComparison.Ordinal);
            Assert.True(definitionIndex >= 0, "The privileged transaction must define the acceptance helper.");

            var invocations = Regex.Matches(
                    transaction,
                    @"(?m)^\s*run_approved_value_free_acceptance_checks(?<arguments>[^\r\n]*)$")
                .Select(match => (Match: match, Arguments: match.Groups["arguments"].Value.Trim()))
                .Where(item => !item.Arguments.StartsWith("()", StringComparison.Ordinal))
                .ToArray();

            Assert.Equal(2, invocations.Length);
            Assert.Contains(invocations, item => item.Arguments.StartsWith("candidate", StringComparison.Ordinal));
            Assert.Contains(invocations, item => item.Arguments.StartsWith("rollback", StringComparison.Ordinal));
            Assert.All(
                invocations,
                invocation => Assert.True(
                    transaction.IndexOf(invocation.Match.Value, StringComparison.Ordinal) > definitionIndex,
                    "Every acceptance helper invocation must occur after its definition."));

            var helper = ExtractAcceptanceHelper(transaction);
            Assert.Contains("candidate)", helper, StringComparison.Ordinal);
            Assert.Contains("rollback)", helper, StringComparison.Ordinal);
            Assert.Contains("unknown acceptance mode; fail closed", helper, StringComparison.Ordinal);
            Assert.Contains("return 1", helper, StringComparison.Ordinal);
            Assert.Contains("/healthz", helper, StringComparison.Ordinal);
            Assert.Contains("/readyz", helper, StringComparison.Ordinal);
            Assert.Contains("/api/mail-requests/00000000-0000-0000-0000-000000000000", transaction, StringComparison.Ordinal);
            Assert.Contains("--request GET", transaction, StringComparison.Ordinal);
            Assert.Contains("\"code\":\"UNAUTHORIZED\"", transaction, StringComparison.Ordinal);
            Assert.DoesNotContain("--request POST", transaction, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("curl -X POST", transaction, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("/admin", helper, StringComparison.Ordinal);
            Assert.Contains("/setup", helper, StringComparison.Ordinal);
            Assert.Contains("www-authenticate", transaction, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("HostConfig.PortBindings", transaction, StringComparison.Ordinal);
            Assert.Contains("8080/tcp", transaction, StringComparison.Ordinal);

            var rollbackIndex = transaction.IndexOf("rollback_current_in_place()", StringComparison.Ordinal);
            var oldReloadIndex = transaction.IndexOf(
                "docker exec \"$container\" caddy reload --config \"$container_path\" --adapter caddyfile || return 1",
                rollbackIndex,
                StringComparison.Ordinal);
            var rollbackAcceptanceIndex = transaction.IndexOf(
                "run_approved_value_free_acceptance_checks rollback",
                oldReloadIndex,
                StringComparison.Ordinal);
            Assert.True(rollbackIndex >= 0);
            Assert.True(oldReloadIndex > rollbackIndex);
            Assert.True(rollbackAcceptanceIndex > oldReloadIndex);

            Assert.Contains("baseline_admin_state", transaction, StringComparison.Ordinal);
            Assert.Contains("baseline_setup_state", transaction, StringComparison.Ordinal);
            Assert.Contains("assert_http_state /admin \"$baseline_admin_state\"", transaction, StringComparison.Ordinal);
            Assert.Contains("assert_http_state /setup \"$baseline_setup_state\"", transaction, StringComparison.Ordinal);
            Assert.Contains("SSH recovery", runbook, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("standalone SSH TTY", runbook, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("ssh ", helper, StringComparison.OrdinalIgnoreCase);

            AssertShellSyntax(transaction);
            AssertUnknownAcceptanceModeFailsClosed(helper);
        }
    }

    [Fact]
    public void Sha256sum_field_selection_yields_the_digest_only_for_the_shipped_awk_form()
    {
        const string record = "deadbeef  /srv/platform/edge/Caddyfile";

        // The shipped form reads field 1, which is what the host/container SHA guards compare.
        var shipped = RunBashCapturingStandardOutput(
            @"printf '%s\n' 'deadbeef  /srv/platform/edge/Caddyfile' | awk '{print $1}'");
        Assert.Equal("deadbeef", shipped);

        // The pre-fix `'"'"'` escaping is meaningless inside the quoted <<'ROOT_BASH' heredoc:
        // awk receives a truthy string constant as its pattern and prints the whole record, so
        // `test "$host_sha" = "$candidate_sha256"` fails against a byte-identical candidate.
        var overEscaped = RunBashCapturingStandardOutput(
            @"printf '%s\n' 'deadbeef  /srv/platform/edge/Caddyfile' | awk '""'""'{print $1}'""'""'");
        Assert.NotEqual("deadbeef", overEscaped);
        Assert.Equal(record, overEscaped);

        // ROOT_BASH runs as `bash -s -- <candidate_remote> ...`, so $1 is a real path there.
        // The over-escaped form is broken in that shape too.
        var overEscapedWithPositionalArguments = RunBashCapturingStandardOutput(
            "set -- /srv/platform/edge/Caddyfile.candidate\n"
            + @"printf '%s\n' 'deadbeef  /srv/platform/edge/Caddyfile' | awk '""'""'{print $1}'""'""'");
        Assert.NotEqual("deadbeef", overEscapedWithPositionalArguments);
    }

    [Fact]
    public void Vps_runbook_transaction_selects_the_sha_field_without_shell_escaping()
    {
        var deploymentRunbooks = new[]
        {
            ReadRepositoryFile("docs", "ops", "vps-dogfood-deployment.md"),
            ReadRepositoryFile("docs", "ops", "vps-dogfood-deployment.en.md")
        };

        foreach (var runbook in deploymentRunbooks)
        {
            var transaction = ExtractRootBash(runbook);

            Assert.Contains(
                @"host_sha=""$(sha256sum ""$current"" | awk '{print $1}')""",
                transaction,
                StringComparison.Ordinal);
            Assert.Contains(
                @"container_sha=""$(docker exec ""$container"" sha256sum ""$container_path"" "
                + @"| awk '{print $1}')""",
                transaction,
                StringComparison.Ordinal);

            // A quoted heredoc performs no expansion, so the `'"'"'` single-quote bridge must
            // never reappear anywhere in the privileged transaction.
            Assert.DoesNotContain(@"'""'""'", transaction, StringComparison.Ordinal);

            // The guards these two assignments feed are unchanged.
            Assert.Contains(
                @"test ""$host_sha"" = ""$candidate_sha256""",
                transaction,
                StringComparison.Ordinal);
            Assert.Contains(
                @"test ""$container_sha"" = ""$candidate_sha256""",
                transaction,
                StringComparison.Ordinal);
        }
    }

    [Fact]
    public void Vps_runbook_resolves_mailer_from_its_own_compose_project_on_the_live_split_topology()
    {
        var deploymentRunbooks = new[]
        {
            ReadRepositoryFile("docs", "ops", "vps-dogfood-deployment.md"),
            ReadRepositoryFile("docs", "ops", "vps-dogfood-deployment.en.md")
        };

        foreach (var runbook in deploymentRunbooks)
        {
            var transaction = ExtractRootBash(runbook);

            // The live VPS runs the edge proxy and the mailer in two distinct Compose
            // projects (stg-mailer-01: amane-platform-edge / amane-mailer-vps). Both must be
            // named and resolved separately.
            Assert.Contains("edge_project=amane-platform-edge", transaction, StringComparison.Ordinal);
            Assert.Contains("mailer_project=amane-mailer-vps", transaction, StringComparison.Ordinal);

            // Proxy resolution stays on the edge project.
            Assert.Contains(
                "docker ps --quiet --filter label=com.docker.compose.project=$edge_project "
                + "--filter label=com.docker.compose.service=$service --filter status=running",
                transaction,
                StringComparison.Ordinal);

            var mailerProbe = ExtractShellFunction(transaction, "assert_mailer_8080_unpublished");

            // Mailer backend exposure verification resolves from the mailer project, by
            // Compose labels, never by a hard-coded container name.
            Assert.Contains("com.docker.compose.project=$mailer_project", mailerProbe, StringComparison.Ordinal);
            Assert.Contains("com.docker.compose.service=mailer", mailerProbe, StringComparison.Ordinal);
            Assert.DoesNotContain("com.docker.compose.project=$project", mailerProbe, StringComparison.Ordinal);
            Assert.DoesNotContain("amane-mailer-vps-mailer-1", transaction, StringComparison.Ordinal);
            Assert.DoesNotContain("amane-platform-edge-proxy-1", transaction, StringComparison.Ordinal);

            // exactly-one / fail-closed semantics retained.
            Assert.Contains("test \"$mailer_count\" -eq 1", mailerProbe, StringComparison.Ordinal);

            // Functional teeth against a Docker stub mirroring the live split topology.
            Assert.Equal(0, RunMailerProbeScenario(mailerProbe, SplitProjectHealthyScenario));
            Assert.NotEqual(0, RunMailerProbeScenario(mailerProbe, MailerLookedUpInEdgeProjectScenario));
            Assert.NotEqual(0, RunMailerProbeScenario(mailerProbe, ZeroMailerContainersScenario));
            Assert.NotEqual(0, RunMailerProbeScenario(mailerProbe, MultipleMailerContainersScenario));
            Assert.NotEqual(0, RunMailerProbeScenario(mailerProbe, MailerPort8080PublishedScenario));
        }
    }

    // A minimal `docker` shim: `docker ps` returns ids from PS_<project>_<service> shell
    // variables, `docker inspect` returns labels/port-bindings from LP_/LS_/PB_ variables.
    private const string DockerStub = @"
docker() {
  if [ ""$1"" = ps ]; then
    local a proj='' svc='' key
    for a in ""$@""; do
      case ""$a"" in
        label=com.docker.compose.project=*) proj=""${a##*=}"" ;;
        label=com.docker.compose.service=*) svc=""${a##*=}"" ;;
      esac
    done
    key=""PS_${proj//[!A-Za-z0-9]/_}_${svc}""
    if [ -n ""${!key:-}"" ]; then printf '%s\n' ""${!key}""; fi
    return 0
  fi
  if [ ""$1"" = inspect ]; then
    local fmt=""$3"" id=""$4"" key
    case ""$fmt"" in
      *PortBindings*) key=""PB_${id}""; if [ -n ""${!key:-}"" ]; then printf '%s\n' ""${!key}""; else printf '{}\n'; fi ;;
      *com.docker.compose.project*) key=""LP_${id}""; printf '%s\n' ""${!key:-}"" ;;
      *com.docker.compose.service*) key=""LS_${id}""; printf '%s\n' ""${!key:-}"" ;;
    esac
    return 0
  fi
  return 0
}
";

    // proxy in amane-platform-edge, exactly one mailer in amane-mailer-vps, no host port.
    private const string SplitProjectHealthyScenario = @"
edge_project=amane-platform-edge
mailer_project=amane-mailer-vps
PS_amane_platform_edge_proxy='p1'
PS_amane_mailer_vps_mailer='m1'
LP_m1='amane-mailer-vps'
LS_m1='mailer'
PB_m1='{}'
";

    // Same topology, but the mailer lookup is wrongly pointed at the edge project.
    private const string MailerLookedUpInEdgeProjectScenario = @"
edge_project=amane-platform-edge
mailer_project=amane-platform-edge
PS_amane_platform_edge_proxy='p1'
PS_amane_mailer_vps_mailer='m1'
LP_m1='amane-mailer-vps'
LS_m1='mailer'
PB_m1='{}'
";

    private const string ZeroMailerContainersScenario = @"
edge_project=amane-platform-edge
mailer_project=amane-mailer-vps
PS_amane_platform_edge_proxy='p1'
";

    private const string MultipleMailerContainersScenario = @"
edge_project=amane-platform-edge
mailer_project=amane-mailer-vps
PS_amane_mailer_vps_mailer=$'m1\nm2'
LP_m1='amane-mailer-vps'
LS_m1='mailer'
PB_m1='{}'
LP_m2='amane-mailer-vps'
LS_m2='mailer'
PB_m2='{}'
";

    private const string MailerPort8080PublishedScenario = @"
edge_project=amane-platform-edge
mailer_project=amane-mailer-vps
PS_amane_mailer_vps_mailer='m1'
LP_m1='amane-mailer-vps'
LS_m1='mailer'
PB_m1='{""8080/tcp"":[{""HostIp"":""0.0.0.0"",""HostPort"":""8080""}]}'
";

    private static int RunMailerProbeScenario(string mailerProbe, string scenario)
    {
        var script = "set -Eeuo pipefail\n"
            + DockerStub + "\n"
            + scenario + "\n"
            + mailerProbe + "\n"
            + "assert_mailer_8080_unpublished\n";

        using var process = StartBash(string.Empty);
        process.StandardInput.Write(script);
        process.StandardInput.Close();

        var standardError = process.StandardError.ReadToEnd();
        Assert.True(process.WaitForExit(5000), $"Mailer probe scenario did not finish promptly: {standardError}");
        return process.ExitCode;
    }

    private static string ExtractShellFunction(string transaction, string name)
    {
        var match = Regex.Match(
            transaction,
            @"(?ms)^(?<indent>[ \t]*)" + Regex.Escape(name) + @"\(\) \{\r?\n(?<body>.*?)^\k<indent>\}$");
        Assert.True(match.Success, $"Could not isolate shell function '{name}'.");
        return match.Value.Replace("\r\n", "\n", StringComparison.Ordinal);
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

    private static string ExtractRootBash(string runbook)
    {
        var match = Regex.Match(
            runbook,
            @"(?ms)^[ \t]*if sudo -n bash -s -- .*?<<'ROOT_BASH'\n(?<body>.*?)^[ \t]*ROOT_BASH\s*$");
        Assert.True(match.Success, "Could not locate the privileged ROOT_BASH transaction.");
        return string.Join(
            "\n",
            match.Groups["body"].Value
                .Replace("\r\n", "\n", StringComparison.Ordinal)
                .Split('\n')
                .Select(line => line.StartsWith("   ", StringComparison.Ordinal) ? line[3..] : line));
    }

    private static string ExtractAcceptanceHelper(string transaction)
    {
        var start = transaction.IndexOf(
            "run_approved_value_free_acceptance_checks() {",
            StringComparison.Ordinal);
        var end = transaction.IndexOf(
            "\n  write_contents_in_place() {",
            start,
            StringComparison.Ordinal);
        Assert.True(start >= 0 && end > start, "Could not isolate the acceptance helper.");
        return transaction[start..end];
    }

    private static void AssertShellSyntax(string transaction)
    {
        using var process = StartBash("-n");
        process.StandardInput.Write(transaction);
        process.StandardInput.Close();

        var standardError = process.StandardError.ReadToEnd();
        Assert.True(process.WaitForExit(5000), "bash -n did not finish promptly.");
        Assert.True(
            process.ExitCode == 0,
            $"Extracted ROOT_BASH failed bash -n: {standardError}");
    }

    private static void AssertUnknownAcceptanceModeFailsClosed(string helper)
    {
        using var process = StartBash(string.Empty);
        process.StandardInput.Write(helper);
        process.StandardInput.WriteLine();
        process.StandardInput.WriteLine(
            "if run_approved_value_free_acceptance_checks unexpected-mode; then exit 1; else exit 0; fi");
        process.StandardInput.Close();

        var standardError = process.StandardError.ReadToEnd();
        Assert.True(process.WaitForExit(5000), "Acceptance helper self-test did not finish promptly.");
        Assert.True(
            process.ExitCode == 0,
            $"Unknown acceptance mode was not rejected: {standardError}");
    }

    private static string RunBashCapturingStandardOutput(string script)
    {
        var process = Process.Start(new ProcessStartInfo
        {
            FileName = "bash",
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        });

        Assert.NotNull(process);
        using var bash = process!;
        bash.StandardInput.Write(script);
        bash.StandardInput.Close();

        var standardOutput = bash.StandardOutput.ReadToEnd();
        var standardError = bash.StandardError.ReadToEnd();
        Assert.True(bash.WaitForExit(5000), "bash did not finish promptly.");
        Assert.True(bash.ExitCode == 0, $"bash exited {bash.ExitCode}: {standardError}");
        return standardOutput.TrimEnd('\n');
    }

    private static Process StartBash(string arguments)
    {
        var process = Process.Start(new ProcessStartInfo
        {
            FileName = "bash",
            Arguments = arguments,
            RedirectStandardInput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        });

        Assert.NotNull(process);
        return process!;
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
