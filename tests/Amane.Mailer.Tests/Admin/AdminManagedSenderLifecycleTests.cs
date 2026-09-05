using System.Net;
using System.Text.RegularExpressions;
using Amane.Mailer.Admin;
using Amane.Mailer.Data.Sqlite;
using Amane.Mailer.Identity;
using Amane.Mailer.Operations;
using Amane.Mailer.Setup;
using Amane.Mailer.Tests.Fixtures;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Amane.Mailer.Tests.Admin;

public sealed class AdminManagedSenderLifecycleTests
{
    [Fact]
    public async Task Managed_owner_can_manage_senders_keys_and_live_gate_while_scoped_admin_is_denied()
    {
        var ct = TestContext.Current.CancellationToken;
        var root = Path.Combine(Path.GetTempPath(), "amane-mailer-admin-732", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var databasePath = Path.Combine(root, "mailer.db");
        var connectionString = $"Data Source={databasePath}";
        var tenantConfigPath = Path.Combine(root, "tenants.json");
        await File.WriteAllTextAsync(tenantConfigPath, MailerAdminFixtureHelpers.TenantConfigJson, ct);
        var secretPath = Path.Combine(root, "secrets", "acs_connection_string");
        const string ownerUsername = "managed-owner";
        const string ownerPassword = "managed-owner-password";

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
            Assert.True(FirstRunSetupStorage.WriteAcsSecretCreateOnly(
                secretPath,
                "Endpoint=https://example.communication.azure.com/;AccessKey=abc123"));

            var instance = new InstanceConfigurationRepository(connections, TimeProvider.System);
            Assert.True(await instance.ConfigureAcsAsync(secretPath, ct));
            var users = new AdminUserRepository(connections, TimeProvider.System);
            Assert.True(await users.EnsureInstanceOwnerAsync(
                ownerUsername,
                AdminPasswordHasher.Hash(ownerPassword),
                ct));
            var senders = new SenderRepository(connections, TimeProvider.System);
            var firstSender = await senders.CreateAsync("first@example.com", "First", ct);
            Assert.True(await instance.FinalizeAsync(ct));

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

            using var ownerClient = CreateClient(factory);
            await LoginAsync(ownerClient, ownerUsername, ownerPassword, ct);

            using (var senderList = await ownerClient.GetAsync("/admin/senders", ct))
            {
                Assert.Equal(HttpStatusCode.OK, senderList.StatusCode);
                var html = await senderList.Content.ReadAsStringAsync(ct);
                Assert.Contains(firstSender.Email, html, StringComparison.Ordinal);
                Assert.Contains("API Keys", html, StringComparison.Ordinal);
                Assert.Contains("/admin/senders", html, StringComparison.Ordinal);
            }

            var createSenderToken = await ReadCsrfTokenAsync(ownerClient, "/admin/senders", ct);
            using (var createSender = await ownerClient.PostAsync(
                "/admin/senders",
                Form(
                    createSenderToken,
                    ("email", "created@example.com"),
                    ("display_name", "Created"),
                    ("confirmation", "confirm")),
                ct))
            {
                Assert.Equal(HttpStatusCode.SeeOther, createSender.StatusCode);
            }

            createSenderToken = await ReadCsrfTokenAsync(ownerClient, "/admin/senders", ct);
            using (var duplicateSender = await ownerClient.PostAsync(
                "/admin/senders",
                Form(
                    createSenderToken,
                    ("email", " CREATED@EXAMPLE.COM "),
                    ("display_name", "Duplicate"),
                    ("confirmation", "confirm")),
                ct))
            {
                Assert.Equal(HttpStatusCode.Conflict, duplicateSender.StatusCode);
            }

            var detailToken = await ReadCsrfTokenAsync(
                ownerClient,
                $"/admin/senders/{firstSender.SenderId:D}",
                ct);
            var createdKeyId = Guid.Empty;
            var createdPlaintext = string.Empty;
            using (var createKey = await ownerClient.PostAsync(
                $"/admin/senders/{firstSender.SenderId:D}/api-keys",
                Form(
                    detailToken,
                    ("name", "managed-key"),
                    ("confirmation", "confirm")),
                ct))
            {
                Assert.Equal(HttpStatusCode.OK, createKey.StatusCode);
                Assert.Contains("no-store", createKey.Headers.CacheControl?.ToString() ?? string.Empty, StringComparison.Ordinal);
                var html = await createKey.Content.ReadAsStringAsync(ct);
                Assert.Contains("このキーは今だけ表示されます。", html, StringComparison.Ordinal);
                var keyMatch = Regex.Match(
                    html,
                    @"amk_([0-9a-f]{32})\.[A-Za-z0-9_-]+",
                    RegexOptions.CultureInvariant);
                Assert.True(keyMatch.Success);
                createdKeyId = Guid.ParseExact(keyMatch.Groups[1].Value, "N");
                createdPlaintext = keyMatch.Value;
            }

            using (var detail = await ownerClient.GetAsync(
                $"/admin/senders/{firstSender.SenderId:D}",
                ct))
            {
                var html = await detail.Content.ReadAsStringAsync(ct);
                Assert.DoesNotContain("amk_", html, StringComparison.Ordinal);
                Assert.Contains("managed-key", html, StringComparison.Ordinal);
            }

            var senderMutationToken = await ReadCsrfTokenAsync(
                ownerClient,
                $"/admin/senders/{firstSender.SenderId:D}",
                ct);
            using (var disableSender = await ownerClient.PostAsync(
                $"/admin/senders/{firstSender.SenderId:D}/disable",
                Form(senderMutationToken, ("confirmation", "confirm")),
                ct))
            {
                Assert.Equal(HttpStatusCode.SeeOther, disableSender.StatusCode);
            }

            Assert.False((await senders.FindAsync(firstSender.SenderId, ct))!.Enabled);
            senderMutationToken = await ReadCsrfTokenAsync(
                ownerClient,
                $"/admin/senders/{firstSender.SenderId:D}",
                ct);
            using (var enableSender = await ownerClient.PostAsync(
                $"/admin/senders/{firstSender.SenderId:D}/enable",
                Form(senderMutationToken, ("confirmation", "confirm")),
                ct))
            {
                Assert.Equal(HttpStatusCode.SeeOther, enableSender.StatusCode);
            }

            Assert.True((await senders.FindAsync(firstSender.SenderId, ct))!.Enabled);

            var otherSender = await senders.CreateAsync("other@example.com", "Other", ct);
            var otherKey = await senders.CreateApiKeyAsync(otherSender.SenderId, "other", ct);
            var revokeToken = await ReadCsrfTokenAsync(
                ownerClient,
                $"/admin/senders/{firstSender.SenderId:D}",
                ct);
            using (var crossSenderRevoke = await ownerClient.PostAsync(
                $"/admin/senders/{firstSender.SenderId:D}/api-keys/{otherKey.KeyId:D}/revoke",
                Form(revokeToken, ("confirmation", "confirm")),
                ct))
            {
                Assert.Equal(HttpStatusCode.NotFound, crossSenderRevoke.StatusCode);
            }

            Assert.NotNull(await senders.AuthenticateAsync(otherKey.Plaintext, ct));

            revokeToken = await ReadCsrfTokenAsync(
                ownerClient,
                $"/admin/senders/{firstSender.SenderId:D}",
                ct);
            using (var revoke = await ownerClient.PostAsync(
                $"/admin/senders/{firstSender.SenderId:D}/api-keys/{createdKeyId:D}/revoke",
                Form(revokeToken, ("confirmation", "confirm")),
                ct))
            {
                Assert.Equal(HttpStatusCode.SeeOther, revoke.StatusCode);
            }

            Assert.Null(await senders.AuthenticateAsync(createdPlaintext, ct));
            Assert.NotNull(await senders.AuthenticateAsync(otherKey.Plaintext, ct));

            await users.CreateOrUpdateScopedUserAsync(
                "scoped-admin-732",
                AdminPasswordHasher.Hash("scoped-password"),
                [firstSender.SenderId],
                ct);
            using (var scopedClient = CreateClient(factory))
            {
                await LoginAsync(scopedClient, "scoped-admin-732", "scoped-password", ct);
                using var denied = await scopedClient.GetAsync("/admin/senders", ct);
                Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
            }

            await users.CreateBreakGlassUserAsync(
                "break-glass-732",
                AdminPasswordHasher.Hash("break-glass-password"),
                ct);
            using (var breakGlassClient = CreateClient(factory))
            {
                await LoginAsync(breakGlassClient, "break-glass-732", "break-glass-password", ct);
                using var denied = await breakGlassClient.GetAsync("/admin/senders", ct);
                Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
                using var audit = await breakGlassClient.GetAsync("/admin/audit-log", ct);
                Assert.Equal(HttpStatusCode.OK, audit.StatusCode);
                var auditHtml = await audit.Content.ReadAsStringAsync(ct);
                Assert.DoesNotContain(
                    $"<td>{AdminAuditLog.EventTypes.ApiKeyCreated}</td>",
                    auditHtml,
                    StringComparison.Ordinal);
                Assert.DoesNotContain(
                    $"<td>{AdminAuditLog.EventTypes.InstanceLiveSendingEnabled}</td>",
                    auditHtml,
                    StringComparison.Ordinal);
            }

            using (var ops = await ownerClient.GetAsync("/admin/ops", ct))
            {
                Assert.Equal(HttpStatusCode.OK, ops.StatusCode);
                var html = await ops.Content.ReadAsStringAsync(ct);
                Assert.Contains("Instance-wide Admin", html, StringComparison.Ordinal);
                Assert.DoesNotContain("Scoped tenants", html, StringComparison.Ordinal);
                Assert.Contains("live_sending", html, StringComparison.Ordinal);
                Assert.Contains("configured / safe", html, StringComparison.Ordinal);
            }

            var opsToken = await ReadCsrfTokenAsync(ownerClient, "/admin/ops", ct);
            using (var enable = await ownerClient.PostAsync(
                "/admin/ops/live-sending",
                Form(opsToken, ("operation", "enable"), ("confirmation", "confirm")),
                ct))
            {
                Assert.Equal(HttpStatusCode.SeeOther, enable.StatusCode);
            }

            Assert.True((await instance.GetAsync(ct))!.LiveSending);
            opsToken = await ReadCsrfTokenAsync(ownerClient, "/admin/ops", ct);
            using (var disable = await ownerClient.PostAsync(
                "/admin/ops/live-sending",
                Form(opsToken, ("operation", "disable"), ("confirmation", "confirm")),
                ct))
            {
                Assert.Equal(HttpStatusCode.SeeOther, disable.StatusCode);
            }

            Assert.False((await instance.GetAsync(ct))!.LiveSending);
            var auditRows = await factory.Services.GetRequiredService<AdminAuditRepository>().ListRecentAsync(20, ct);
            Assert.Contains(auditRows, row => row.EventType == AdminAuditLog.EventTypes.SenderCreated);
            Assert.Contains(auditRows, row => row.EventType == AdminAuditLog.EventTypes.SenderEnabled);
            Assert.Contains(auditRows, row => row.EventType == AdminAuditLog.EventTypes.SenderDisabled);
            Assert.Contains(auditRows, row => row.EventType == AdminAuditLog.EventTypes.ApiKeyCreated);
            Assert.Contains(auditRows, row => row.EventType == AdminAuditLog.EventTypes.ApiKeyRevoked);
            Assert.Contains(auditRows, row => row.EventType == AdminAuditLog.EventTypes.InstanceLiveSendingEnabled);
            Assert.Contains(auditRows, row => row.EventType == AdminAuditLog.EventTypes.InstanceLiveSendingDisabled);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

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
        var token = await ReadCsrfTokenAsync(client, "/admin/login", cancellationToken);
        using var response = await client.PostAsync(
            "/admin/api/login",
            Form(token, ("username", username), ("password", password)),
            cancellationToken);
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
    }

    private static async Task<string> ReadCsrfTokenAsync(
        HttpClient client,
        string path,
        CancellationToken cancellationToken)
    {
        using var response = await client.GetAsync(path, cancellationToken);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var html = await response.Content.ReadAsStringAsync(cancellationToken);
        const string marker = "name=\"__RequestVerificationToken\" value=\"";
        var start = html.IndexOf(marker, StringComparison.Ordinal);
        Assert.True(start >= 0, $"No CSRF token in {path}.");
        start += marker.Length;
        var end = html.IndexOf('"', start);
        Assert.True(end > start);
        return html[start..end];
    }

    private static FormUrlEncodedContent Form(
        string csrfToken,
        params (string Name, string Value)[] values)
    {
        var form = new Dictionary<string, string>
        {
            ["__RequestVerificationToken"] = csrfToken,
        };
        foreach (var (name, value) in values)
            form[name] = value;
        return new FormUrlEncodedContent(form);
    }
}
