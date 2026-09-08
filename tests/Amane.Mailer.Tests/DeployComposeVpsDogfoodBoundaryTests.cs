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
        Assert.Contains(
            "path /admin /admin/* /setup /setup/*",
            caddyfile,
            StringComparison.Ordinal);
        Assert.Contains(
            "{{JP_IPV4_CIDRS}}",
            caddyfile,
            StringComparison.Ordinal);
        Assert.Contains(
            "{{JP_IPV6_CIDRS}}",
            caddyfile,
            StringComparison.Ordinal);
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
        Assert.Matches(
            new Regex(@"handle @browser_family \{\s+respond 404\s+\}", RegexOptions.Multiline),
            caddyfile);

        var browserMatcherIndex = caddyfile.IndexOf("@browser_jp {", StringComparison.Ordinal);
        var metricsMatcherIndex = caddyfile.IndexOf("@metrics_operator {", StringComparison.Ordinal);
        var metricsMatcherEndIndex = metricsMatcherIndex >= 0
            ? caddyfile.IndexOf("@metrics {", metricsMatcherIndex, StringComparison.Ordinal)
            : -1;
        Assert.True(browserMatcherIndex >= 0);
        Assert.True(metricsMatcherIndex >= 0 && metricsMatcherEndIndex > metricsMatcherIndex);
        var browserMatcher = caddyfile[browserMatcherIndex..metricsMatcherIndex];
        var metricsMatcher = caddyfile[metricsMatcherIndex..metricsMatcherEndIndex];
        Assert.Contains("{{JP_IPV4_CIDRS}}", browserMatcher, StringComparison.Ordinal);
        Assert.Contains("{{JP_IPV6_CIDRS}}", browserMatcher, StringComparison.Ordinal);
        Assert.DoesNotContain("/metrics", browserMatcher, StringComparison.Ordinal);
        Assert.Contains(
            "remote_ip {$MAILER_MANAGEMENT_ALLOWED_CIDRS}",
            metricsMatcher,
            StringComparison.Ordinal);
        Assert.DoesNotContain("{{JP_IPV4_CIDRS}}", metricsMatcher, StringComparison.Ordinal);
        Assert.DoesNotContain("{{JP_IPV6_CIDRS}}", metricsMatcher, StringComparison.Ordinal);

        var browserHandlerIndex = caddyfile.IndexOf("handle @browser_jp {", StringComparison.Ordinal);
        var nonJapanHandlerIndex = caddyfile.IndexOf("handle @browser_family {", StringComparison.Ordinal);
        var metricsHandlerIndex = caddyfile.IndexOf("handle @metrics_operator {", StringComparison.Ordinal);
        var metricsFallbackIndex = caddyfile.IndexOf("handle @metrics {", StringComparison.Ordinal);
        var publicHandlerIndex = caddyfile.IndexOf("handle @public {", StringComparison.Ordinal);
        var fallbackHandlerIndex = publicHandlerIndex >= 0
            ? caddyfile.IndexOf("handle {", publicHandlerIndex, StringComparison.Ordinal)
            : -1;
        Assert.True(browserHandlerIndex >= 0 && browserHandlerIndex < nonJapanHandlerIndex);
        Assert.True(nonJapanHandlerIndex < metricsHandlerIndex);
        Assert.True(metricsHandlerIndex < metricsFallbackIndex);
        Assert.True(metricsFallbackIndex < publicHandlerIndex);
        Assert.True(publicHandlerIndex < fallbackHandlerIndex);

        var browserHandler = caddyfile[browserHandlerIndex..nonJapanHandlerIndex];
        var nonJapanHandler = caddyfile[nonJapanHandlerIndex..metricsHandlerIndex];
        var metricsHandler = caddyfile[metricsHandlerIndex..metricsFallbackIndex];
        var publicHandler = caddyfile[publicHandlerIndex..fallbackHandlerIndex];
        var fallbackHandler = caddyfile[fallbackHandlerIndex..];

        var basicAuthIndex = browserHandler.IndexOf("basic_auth", StringComparison.Ordinal);
        var browserProxyIndex = browserHandler.IndexOf("reverse_proxy mailer:8080", StringComparison.Ordinal);
        Assert.True(basicAuthIndex >= 0 && basicAuthIndex < browserProxyIndex);
        Assert.Contains("header_up -Authorization", browserHandler, StringComparison.Ordinal);
        Assert.DoesNotContain("basic_auth", nonJapanHandler, StringComparison.Ordinal);
        Assert.Contains("respond 404", nonJapanHandler, StringComparison.Ordinal);
        Assert.Contains("reverse_proxy mailer:8080", metricsHandler, StringComparison.Ordinal);
        Assert.DoesNotContain("basic_auth", metricsHandler, StringComparison.Ordinal);
        Assert.DoesNotContain("header_up -Authorization", metricsHandler, StringComparison.Ordinal);
        Assert.DoesNotContain("JP_IPV", metricsHandler, StringComparison.Ordinal);
        Assert.Contains("reverse_proxy mailer:8080", publicHandler, StringComparison.Ordinal);
        Assert.DoesNotContain("basic_auth", publicHandler, StringComparison.Ordinal);
        Assert.Contains("respond 404", fallbackHandler, StringComparison.Ordinal);

        Assert.Contains(
            "@public path /api/* /healthz /readyz",
            caddyfile,
            StringComparison.Ordinal);
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
        Assert.Contains("--basic-auth-username", renderer, StringComparison.Ordinal);
        Assert.Contains("validate_basic_auth_username", renderer, StringComparison.Ordinal);
        Assert.DoesNotContain("maxmind.com", renderer, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("urllib", renderer, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("urlopen", renderer, StringComparison.OrdinalIgnoreCase);
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
                         "real GeoLite",
                         "Caddy Basic Auth password",
                         "Mailer Admin",
                         "Setup bootstrap token",
                         "bcrypt hash",
                         "plaintext password",
                         "MaxMind",
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
                         "GeoLite raw data transferred=false",
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
