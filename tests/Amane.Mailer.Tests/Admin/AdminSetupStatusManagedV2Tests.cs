using System.Net;
using Amane.Mailer.Data.Sqlite;
using Amane.Mailer.Identity;
using Amane.Mailer.Operations;
using Amane.Mailer.Setup;
using Amane.Mailer.Tests.Fixtures;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;

namespace Amane.Mailer.Tests.Admin;

public sealed class AdminSetupStatusManagedV2Tests
{
    [Fact]
    public async Task Fresh_browser_setup_instance_setup_status_matches_canonical_state()
    {
        var ct = TestContext.Current.CancellationToken;
        var root = Path.Combine(Path.GetTempPath(), "amane-mailer-admin-782", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var databasePath = Path.Combine(root, "mailer.db");
        var connectionString = $"Data Source={databasePath}";
        var tenantConfigPath = Path.Combine(root, "tenants.json");
        await File.WriteAllTextAsync(tenantConfigPath, AcsTenantConfigJson(), ct);
        var secretPath = Path.Combine(root, "secrets", "acs_connection_string");
        const string ownerUsername = "managed-owner";
        const string ownerPassword = "managed-owner-password";
        const string acsSecret = "Endpoint=https://example.communication.azure.com/;AccessKey=abc123";

        try
        {
            var configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["ConnectionStrings:Mailer"] = connectionString,
                })
                .Build();
            var connections = new SqliteConnectionFactory(configuration);
            await new SqlMigrationRunner(connections).ApplyPendingAsync(ct);
            Assert.True(FirstRunSetupStorage.WriteAcsSecretCreateOnly(secretPath, acsSecret));

            var instance = new InstanceConfigurationRepository(connections, TimeProvider.System);
            Assert.True(await instance.ConfigureAcsAsync(secretPath, ct));
            var users = new AdminUserRepository(connections, TimeProvider.System);
            Assert.True(await users.EnsureInstanceOwnerAsync(
                ownerUsername,
                AdminPasswordHasher.Hash(ownerPassword),
                ct));
            var senders = new SenderRepository(connections, TimeProvider.System);
            await senders.CreateAsync("first@example.com", "First", ct);
            Assert.True(await instance.FinalizeAsync(ct));
            Assert.False((await instance.GetAsync(ct))!.LiveSending);

            await using var factory = MailerAdminFixtureHelpers.CreateFactory(
                connectionString,
                tenantConfigPath,
                AdminPasswordHasher.Hash("legacy-password"),
                new Dictionary<string, string?>
                {
                    ["AMANE_ADMIN_ENABLED"] = "false",
                    ["AMANE_ADMIN_USERNAME"] = "legacy-admin",
                },
                useEarlyInstanceProbe: true);

            using var client = CreateClient(factory);
            using (var ready = await client.GetAsync("/readyz", ct))
            {
                Assert.Equal(HttpStatusCode.OK, ready.StatusCode);
            }

            await LoginAsync(client, ownerUsername, ownerPassword, ct);
            using var response = await client.GetAsync("/admin/setup-status", ct);
            var html = await response.Content.ReadAsStringAsync(ct);

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.True(response.Headers.CacheControl?.NoStore);
            Assert.Contains("Deployment</dt>\n                    <dd>Browser Setup", html, StringComparison.Ordinal);
            Assert.Contains("Credential loaded</dt>\n                    <dd>yes", html, StringComparison.Ordinal);
            Assert.Contains("Live sending</dt>\n                    <dd>no", html, StringComparison.Ordinal);
            Assert.Contains("f***@e***.com", html, StringComparison.Ordinal);
            Assert.DoesNotContain("Sender</dt>\n                    <dd>n/a", html, StringComparison.Ordinal);
            Assert.DoesNotContain("Inspect reason", html, StringComparison.Ordinal);
            Assert.DoesNotContain("Deployment</dt>\n                    <dd>Manual Deployment", html, StringComparison.Ordinal);
            Assert.DoesNotContain("AccessKey=", html, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(secretPath, html, StringComparison.Ordinal);
            Assert.DoesNotContain(acsSecret, html, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("first@example.com", html, StringComparison.Ordinal);
            Assert.DoesNotContain("method=\"post\"", html, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    private static string AcsTenantConfigJson() =>
        $$"""
        {
          "version": 1,
          "environment": "develop",
          "tenants": [
            {
              "tenant_id": "{{MailerWebApplicationFixtureBase.TenantId}}",
              "name": "example-develop",
              "source_services": ["{{MailerWebApplicationFixtureBase.SourceService}}"],
              "default_from": {
                "email": "noreply@example.com",
                "display_name": "Example Service"
              },
              "token_env": "MAIL_SERVICE_TOKEN",
              "provider": "acs",
              "live_sending": false,
              "metadata_max_bytes": 4096,
              "retry": {
                "max_attempts": 3,
                "initial_delay_seconds": 1,
                "max_delay_seconds": 2
              }
            }
          ]
        }
        """;

    private static HttpClient CreateClient(WebApplicationFactory<global::Program> factory) =>
        factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
            BaseAddress = new Uri("https://localhost"),
        });

    private static async Task LoginAsync(
        HttpClient client,
        string username,
        string password,
        CancellationToken cancellationToken)
    {
        using var loginPage = await client.GetAsync("/admin/login", cancellationToken);
        Assert.Equal(HttpStatusCode.OK, loginPage.StatusCode);
        var html = await loginPage.Content.ReadAsStringAsync(cancellationToken);
        const string marker = "name=\"__RequestVerificationToken\" value=\"";
        var start = html.IndexOf(marker, StringComparison.Ordinal);
        Assert.True(start >= 0, "No CSRF token in /admin/login.");
        start += marker.Length;
        var end = html.IndexOf('"', start);
        Assert.True(end > start);
        var token = html[start..end];

        using var response = await client.PostAsync(
            "/admin/api/login",
            new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["__RequestVerificationToken"] = token,
                ["username"] = username,
                ["password"] = password,
            }),
            cancellationToken);
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
    }
}
