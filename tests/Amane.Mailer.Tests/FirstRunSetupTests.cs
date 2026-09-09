using System.Net;
using System.Text.Json;
using System.Text.Encodings.Web;
using Amane.Mailer.Admin;
using Amane.Mailer.Configuration;
using Amane.Mailer.Data.Sqlite;
using Amane.Mailer.Data.Sqlite.Models;
using Amane.Mailer.Identity;
using Amane.Mailer.Operations;
using Amane.Mailer.Setup;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;

namespace Amane.Mailer.Tests;

public sealed class FirstRunSetupTests
{
    [Fact]
    public async Task Migration_creates_one_uninitialized_instance_row_with_live_sending_disabled()
    {
        var root = CreateRoot("migration");
        try
        {
            var databasePath = Path.Combine(root, "mailer.db");
            var configuration = CreateConfiguration(databasePath);
            await new SqlMigrationRunner(new SqliteConnectionFactory(configuration))
                .ApplyPendingAsync(TestContext.Current.CancellationToken);

            var state = await InstanceRuntimeStateProbe.ReadAsync(
                configuration,
                TestContext.Current.CancellationToken);
            Assert.True(state.IsUninitialized);
            Assert.False(state.LiveSending);
            Assert.Null(state.ProviderType);
            Assert.Null(state.InitializedAt);

            await using var connection = new SqliteConnection(configuration.GetConnectionString("Mailer"));
            await connection.OpenAsync(TestContext.Current.CancellationToken);
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT COUNT(*), MIN(id), MAX(id), MIN(live_sending) FROM instance_configuration;";
            await using var reader = await command.ExecuteReaderAsync(TestContext.Current.CancellationToken);
            Assert.True(await reader.ReadAsync(TestContext.Current.CancellationToken));
            Assert.Equal(1L, reader.GetInt64(0));
            Assert.Equal(1L, reader.GetInt64(1));
            Assert.Equal(1L, reader.GetInt64(2));
            Assert.Equal(0L, reader.GetInt64(3));
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    [Fact]
    public async Task Bootstrap_token_is_create_only_and_initialized_instances_cannot_show_it()
    {
        var root = CreateRoot("bootstrap");
        try
        {
            var databasePath = Path.Combine(root, "mailer.db");
            var tokenPath = Path.Combine(root, "bootstrap", "setup_token");
            var configuration = CreateConfiguration(databasePath, tokenPath);
            await new SqlMigrationRunner(new SqliteConnectionFactory(configuration))
                .ApplyPendingAsync(TestContext.Current.CancellationToken);

            var store = new BootstrapTokenStore(configuration);
            var first = store.EnsureExists();
            var second = store.EnsureExists();
            Assert.Equal(first, second);
            Assert.Equal(32, Convert.FromBase64String(first.Replace('-', '+').Replace('_', '/') + "=").Length);
            if (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS())
            {
                Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(tokenPath));
                Assert.Equal(
                    UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute,
                    File.GetUnixFileMode(Path.GetDirectoryName(tokenPath)!));
            }

            var output = new StringWriter();
            var error = new StringWriter();
            var command = new BootstrapShowCommand();
            Assert.Equal(
                BootstrapShowCommand.SuccessExitCode,
                await command.ExecuteAsync(
                    ["setup", "bootstrap", "show"],
                    configuration,
                    output,
                    error,
                    TestContext.Current.CancellationToken));
            Assert.Equal(first + Environment.NewLine, output.ToString());

            await using (var connection = new SqliteConnection(configuration.GetConnectionString("Mailer")))
            {
                await connection.OpenAsync(TestContext.Current.CancellationToken);
                await using var update = connection.CreateCommand();
                update.CommandText = "UPDATE instance_configuration SET initialized_at = '2026-01-01T00:00:00Z' WHERE id = 1;";
                await update.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
            }

            output.GetStringBuilder().Clear();
            error.GetStringBuilder().Clear();
            Assert.Equal(
                BootstrapShowCommand.FailureExitCode,
                await command.ExecuteAsync(
                    ["setup", "bootstrap", "show"],
                    configuration,
                    output,
                    error,
                    TestContext.Current.CancellationToken));
            Assert.Empty(output.ToString());
            Assert.DoesNotContain(first, error.ToString(), StringComparison.Ordinal);
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    [Fact]
    public async Task Uninitialized_runtime_exposes_only_health_setup_and_ready_routes()
    {
        var root = CreateRoot("http");
        WebApplicationFactory<global::Program>? factory = null;
        try
        {
            var databasePath = Path.Combine(root, "mailer.db");
            var tokenPath = Path.Combine(root, "bootstrap", "setup_token");
            var configuration = CreateConfiguration(databasePath, tokenPath);
            await new SqlMigrationRunner(new SqliteConnectionFactory(configuration))
                .ApplyPendingAsync(TestContext.Current.CancellationToken);

            factory = new WebApplicationFactory<global::Program>().WithWebHostBuilder(builder =>
            {
                builder.UseEnvironment("Testing");
                builder.UseSetting("ConnectionStrings:Mailer", $"Data Source={databasePath}");
                builder.UseSetting("MAILER_BOOTSTRAP_TOKEN_PATH", tokenPath);
                builder.ConfigureServices(services => services.RemoveAll<IHostedService>());
            });

            using var client = factory.CreateClient(new WebApplicationFactoryClientOptions
            {
                AllowAutoRedirect = false,
                BaseAddress = new Uri("https://localhost"),
            });

            using var setup = await client.GetAsync("/setup", TestContext.Current.CancellationToken);
            Assert.Equal(HttpStatusCode.OK, setup.StatusCode);
            Assert.Equal("no-store", setup.Headers.CacheControl?.NoStore == true ? "no-store" : null);
            var setupHtml = await setup.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
            Assert.Contains("初回セットアップ用のBootstrap tokenを入力してください。", setupHtml, StringComparison.Ordinal);
            Assert.Contains(
                "docker compose --env-file .env -f compose.yml exec mailer /app/Amane.Mailer setup bootstrap show",
                setupHtml,
                StringComparison.Ordinal);
            Assert.Contains("Amane Mailerを起動しているサーバー上", setupHtml, StringComparison.Ordinal);
            Assert.Contains("Bootstrap tokenは秘密情報です。", setupHtml, StringComparison.Ordinal);
            Assert.Contains("初回セットアップが完了すると利用できなくなり", setupHtml, StringComparison.Ordinal);
            Assert.Contains("セットアップ状況", setupHtml, StringComparison.Ordinal);
            Assert.Contains("ACS接続設定", setupHtml, StringComparison.Ordinal);
            Assert.Contains("管理者アカウント", setupHtml, StringComparison.Ordinal);
            Assert.Contains("送信元", setupHtml, StringComparison.Ordinal);
            Assert.Contains("セットアップ完了", setupHtml, StringComparison.Ordinal);

            using var health = await client.GetAsync("/healthz", TestContext.Current.CancellationToken);
            Assert.Equal(HttpStatusCode.OK, health.StatusCode);
            using var healthJson = JsonDocument.Parse(
                await health.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
            Assert.True(healthJson.RootElement.GetProperty("healthy").GetBoolean());

            using var ready = await client.GetAsync("/readyz", TestContext.Current.CancellationToken);
            Assert.Equal(HttpStatusCode.ServiceUnavailable, ready.StatusCode);
            using var readyJson = JsonDocument.Parse(
                await ready.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
            Assert.False(readyJson.RootElement.GetProperty("ready").GetBoolean());
            Assert.Equal("uninitialized", readyJson.RootElement.GetProperty("reason").GetString());

            using var api = await client.GetAsync("/api/mail-requests/not-available", TestContext.Current.CancellationToken);
            Assert.Equal(HttpStatusCode.NotFound, api.StatusCode);
            using var admin = await client.GetAsync("/admin", TestContext.Current.CancellationToken);
            Assert.Equal(HttpStatusCode.NotFound, admin.StatusCode);
            using var metrics = await client.GetAsync("/metrics", TestContext.Current.CancellationToken);
            Assert.Equal(HttpStatusCode.NotFound, metrics.StatusCode);
        }
        finally
        {
            if (factory is not null)
                await factory.DisposeAsync();
            DeleteRoot(root);
        }
    }

    [Fact]
    public async Task Setup_auth_requires_https_same_origin_and_antiforgery()
    {
        var root = CreateRoot("security");
        WebApplicationFactory<global::Program>? factory = null;
        try
        {
            var databasePath = Path.Combine(root, "mailer.db");
            var tokenPath = Path.Combine(root, "bootstrap", "setup_token");
            var configuration = CreateConfiguration(databasePath, tokenPath);
            await new SqlMigrationRunner(new SqliteConnectionFactory(configuration))
                .ApplyPendingAsync(TestContext.Current.CancellationToken);
            var bootstrapToken = new BootstrapTokenStore(configuration).EnsureExists();

            factory = new WebApplicationFactory<global::Program>().WithWebHostBuilder(builder =>
            {
                builder.UseEnvironment("Testing");
                builder.UseSetting("ConnectionStrings:Mailer", $"Data Source={databasePath}");
                builder.UseSetting("MAILER_BOOTSTRAP_TOKEN_PATH", tokenPath);
                builder.ConfigureServices(services => services.RemoveAll<IHostedService>());
            });

            using var client = factory.CreateClient(new WebApplicationFactoryClientOptions
            {
                AllowAutoRedirect = false,
                BaseAddress = new Uri("https://localhost"),
            });

            using var page = await client.GetAsync("/setup", TestContext.Current.CancellationToken);
            var html = await page.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
            var requestToken = ReadRequestToken(html);
            var csrfCookie = page.Headers.GetValues("Set-Cookie")
                .Single(value => value.StartsWith("__Host-amane-setup-csrf=", StringComparison.Ordinal))
                .Split(';', 2)[0];

            using var directHttp = factory.CreateClient(new WebApplicationFactoryClientOptions
            {
                AllowAutoRedirect = false,
                BaseAddress = new Uri("http://localhost"),
            });
            using var directResponse = await directHttp.GetAsync(
                "/setup",
                TestContext.Current.CancellationToken);
            Assert.Equal(HttpStatusCode.NotFound, directResponse.StatusCode);

            using var missingOrigin = new HttpRequestMessage(HttpMethod.Post, "/setup/auth")
            {
                Content = new FormUrlEncodedContent(new Dictionary<string, string>
                {
                    ["__RequestVerificationToken"] = requestToken,
                    ["bootstrap_token"] = bootstrapToken,
                }),
            };
            using var missingOriginResponse = await client.SendAsync(
                missingOrigin,
                TestContext.Current.CancellationToken);
            Assert.Equal(HttpStatusCode.Forbidden, missingOriginResponse.StatusCode);

            using var missingAntiforgery = new HttpRequestMessage(HttpMethod.Post, "/setup/auth")
            {
                Content = new FormUrlEncodedContent(new Dictionary<string, string>
                {
                    ["bootstrap_token"] = bootstrapToken,
                }),
            };
            missingAntiforgery.Headers.TryAddWithoutValidation("Origin", "https://localhost");
            using var missingAntiforgeryResponse = await client.SendAsync(
                missingAntiforgery,
                TestContext.Current.CancellationToken);
            Assert.Equal(HttpStatusCode.BadRequest, missingAntiforgeryResponse.StatusCode);

            using var auth = new HttpRequestMessage(HttpMethod.Post, "/setup/auth")
            {
                Content = new FormUrlEncodedContent(new Dictionary<string, string>
                {
                    ["__RequestVerificationToken"] = requestToken,
                    ["bootstrap_token"] = bootstrapToken,
                }),
            };
            auth.Headers.TryAddWithoutValidation("Origin", "https://localhost");
            auth.Headers.TryAddWithoutValidation("Cookie", csrfCookie);
            using var authResponse = await client.SendAsync(
                auth,
                TestContext.Current.CancellationToken);
            Assert.Equal(HttpStatusCode.Redirect, authResponse.StatusCode);
            Assert.Equal("/setup", authResponse.Headers.Location?.OriginalString);

            var authCookie = authResponse.Headers.GetValues("Set-Cookie")
                .Single(value => value.StartsWith("__Host-amane-setup-auth=", StringComparison.Ordinal));
            Assert.Contains("Path=/", authCookie, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("Secure", authCookie, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("HttpOnly", authCookie, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("SameSite=Strict", authCookie, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("Domain=", authCookie, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            if (factory is not null)
                await factory.DisposeAsync();
            DeleteRoot(root);
        }
    }

    [Fact]
    public async Task Setup_auth_rate_limits_repeated_invalid_tokens_at_http_endpoint()
    {
        var root = CreateRoot("auth-rate-limit");
        WebApplicationFactory<global::Program>? factory = null;
        try
        {
            var databasePath = Path.Combine(root, "mailer.db");
            var tokenPath = Path.Combine(root, "bootstrap", "setup_token");
            var configuration = CreateConfiguration(databasePath, tokenPath);
            await new SqlMigrationRunner(new SqliteConnectionFactory(configuration))
                .ApplyPendingAsync(TestContext.Current.CancellationToken);
            var bootstrapToken = new BootstrapTokenStore(configuration).EnsureExists();

            factory = new WebApplicationFactory<global::Program>().WithWebHostBuilder(builder =>
            {
                builder.UseEnvironment("Testing");
                builder.UseSetting("ConnectionStrings:Mailer", $"Data Source={databasePath}");
                builder.UseSetting("MAILER_BOOTSTRAP_TOKEN_PATH", tokenPath);
                builder.UseSetting("Mailer:Worker:Enabled", "false");
                builder.ConfigureServices(services =>
                {
                    services.RemoveAll<IHostedService>();
                    services.AddSingleton<IStartupFilter>(
                        new TestRemoteAddressStartupFilter(IPAddress.Parse("203.0.113.20")));
                });
            });

            using var client = factory.CreateClient(new WebApplicationFactoryClientOptions
            {
                AllowAutoRedirect = false,
                BaseAddress = new Uri("https://localhost"),
            });
            using var page = await client.GetAsync("/setup", TestContext.Current.CancellationToken);
            Assert.Equal(HttpStatusCode.OK, page.StatusCode);
            var requestToken = ReadRequestToken(
                await page.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
            var csrfCookie = page.Headers.GetValues("Set-Cookie")
                .Single(value => value.StartsWith("__Host-amane-setup-csrf=", StringComparison.Ordinal))
                .Split(';', 2)[0];

            for (var attempt = 0; attempt < ApiAuthenticationRateLimiter.PermitLimit; attempt++)
            {
                using var response = await client.SendAsync(
                    CreateSetupAuthRequest(requestToken, csrfCookie, "invalid-bootstrap-token"),
                    TestContext.Current.CancellationToken);
                Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
            }

            using var limited = await client.SendAsync(
                CreateSetupAuthRequest(requestToken, csrfCookie, "invalid-bootstrap-token"),
                TestContext.Current.CancellationToken);
            Assert.Equal(HttpStatusCode.TooManyRequests, limited.StatusCode);

            using var correctAfterLimit = await client.SendAsync(
                CreateSetupAuthRequest(requestToken, csrfCookie, bootstrapToken),
                TestContext.Current.CancellationToken);
            Assert.Equal(HttpStatusCode.TooManyRequests, correctAfterLimit.StatusCode);
        }
        finally
        {
            if (factory is not null)
                await factory.DisposeAsync();
            DeleteRoot(root);
        }
    }

    [Fact]
    public async Task Authenticated_setup_page_explains_each_setup_input_and_finalize_state()
    {
        var root = CreateRoot("setup-guidance");
        WebApplicationFactory<global::Program>? factory = null;
        try
        {
            var databasePath = Path.Combine(root, "mailer.db");
            var tokenPath = Path.Combine(root, "bootstrap", "setup_token");
            var configuration = CreateConfiguration(databasePath, tokenPath);
            await new SqlMigrationRunner(new SqliteConnectionFactory(configuration))
                .ApplyPendingAsync(TestContext.Current.CancellationToken);
            var bootstrapToken = new BootstrapTokenStore(configuration).EnsureExists();

            factory = new WebApplicationFactory<global::Program>().WithWebHostBuilder(builder =>
            {
                builder.UseEnvironment("Testing");
                builder.UseSetting("ConnectionStrings:Mailer", $"Data Source={databasePath}");
                builder.UseSetting("MAILER_BOOTSTRAP_TOKEN_PATH", tokenPath);
                builder.ConfigureServices(services => services.RemoveAll<IHostedService>());
            });

            using var client = factory.CreateClient(new WebApplicationFactoryClientOptions
            {
                AllowAutoRedirect = false,
                BaseAddress = new Uri("https://localhost"),
            });
            var authCookie = await AuthenticateSetupAsync(client, bootstrapToken);
            var html = await ReadAuthenticatedSetupAsync(client, authCookie);

            Assert.Contains("Azure Communication Services（ACS）の接続設定", html, StringComparison.Ordinal);
            Assert.Contains(
                "Amane MailerがAzure Communication Servicesを使ってメールを送信するための接続情報です。",
                html,
                StringComparison.Ordinal);
            Assert.Contains(
                "endpoint=https://xxxxx.communication.azure.com/;accesskey=xxxxxxxx",
                html,
                StringComparison.Ordinal);
            Assert.Contains("Azure Portal", html, StringComparison.Ordinal);
            Assert.Contains("→ Communication Services", html, StringComparison.Ordinal);
            Assert.Contains("→ Primary key の Connection string", html, StringComparison.Ordinal);
            Assert.Contains("accesskey</code>は秘密情報です。", html, StringComparison.Ordinal);

            Assert.Contains("管理画面へログインするための最初の管理者アカウント", html, StringComparison.Ordinal);
            Assert.Contains("Admin username", html, StringComparison.Ordinal);
            Assert.Contains("管理画面へのログインに使うユーザー名", html, StringComparison.Ordinal);
            Assert.Contains("Password", html, StringComparison.Ordinal);
            Assert.Contains("管理画面へのログインに使うパスワード", html, StringComparison.Ordinal);
            Assert.Contains("Confirm password", html, StringComparison.Ordinal);
            Assert.Contains("確認のため同じパスワードを再入力", html, StringComparison.Ordinal);

            Assert.Contains("送信元メールアドレス", html, StringComparison.Ordinal);
            Assert.Contains("Amane Mailerからメールを送信するときに使用する送信元を登録します。", html, StringComparison.Ordinal);
            Assert.Contains("Azure Communication Services Emailで利用可能な送信元メールアドレス", html, StringComparison.Ordinal);
            Assert.Contains("受信者に表示される送信者名", html, StringComparison.Ordinal);
            Assert.Contains("Sender email: noreply@example.com", html, StringComparison.Ordinal);
            Assert.Contains("Display name: Amane System", html, StringComparison.Ordinal);

            Assert.Contains("action=\"/setup/finalize\"", html, StringComparison.Ordinal);
            Assert.Contains("未設定の項目を入力してから、セットアップを完了してください。", html, StringComparison.Ordinal);
        }
        finally
        {
            if (factory is not null)
                await factory.DisposeAsync();
            DeleteRoot(root);
        }
    }

    [Fact]
    public async Task Setup_page_uses_existing_state_for_progress_and_registered_summaries()
    {
        var root = CreateRoot("setup-progress-and-summaries");
        WebApplicationFactory<global::Program>? factory = null;
        try
        {
            var ct = TestContext.Current.CancellationToken;
            var databasePath = Path.Combine(root, "mailer.db");
            var tokenPath = Path.Combine(root, "bootstrap", "setup_token");
            var configuration = CreateConfiguration(databasePath, tokenPath);
            var connections = new SqliteConnectionFactory(configuration);
            await new SqlMigrationRunner(connections).ApplyPendingAsync(ct);
            var bootstrapToken = new BootstrapTokenStore(configuration).EnsureExists();

            factory = new WebApplicationFactory<global::Program>().WithWebHostBuilder(builder =>
            {
                builder.UseEnvironment("Testing");
                builder.UseSetting("ConnectionStrings:Mailer", $"Data Source={databasePath}");
                builder.UseSetting("MAILER_BOOTSTRAP_TOKEN_PATH", tokenPath);
                builder.ConfigureServices(services => services.RemoveAll<IHostedService>());
            });

            using var client = factory.CreateClient(new WebApplicationFactoryClientOptions
            {
                AllowAutoRedirect = false,
                BaseAddress = new Uri("https://localhost"),
            });
            var authCookie = await AuthenticateSetupAsync(client, bootstrapToken);

            const string acsSecret =
                "Endpoint=https://fixture.communication.azure.com/;AccessKey=synthetic-acs-secret";
            var secretPath = Path.Combine(root, "secrets", "acs", "acs_connection_string");
            var instance = new InstanceConfigurationRepository(connections, TimeProvider.System);
            Assert.True(await instance.ConfigureAcsAsync(secretPath, ct));

            var unreadableSecretHtml = await ReadAuthenticatedSetupAsync(client, authCookie);
            Assert.Contains("action=\"/setup/provider\"", unreadableSecretHtml, StringComparison.Ordinal);
            Assert.DoesNotContain("ACS接続情報:</strong> 設定済み", unreadableSecretHtml, StringComparison.Ordinal);

            Assert.True(FirstRunSetupStorage.WriteAcsSecretCreateOnly(secretPath, acsSecret));
            Assert.True(await instance.ConfigureAcsAsync(secretPath, ct));
            var partialHtml = await ReadAuthenticatedSetupAsync(client, authCookie);
            Assert.Contains("ACS接続情報:</strong> 設定済み（秘密情報のため詳細は表示しません）", partialHtml, StringComparison.Ordinal);
            Assert.Contains("ACS接続設定", partialHtml, StringComparison.Ordinal);
            Assert.Contains("設定済み", partialHtml, StringComparison.Ordinal);
            Assert.Contains("管理者アカウント", partialHtml, StringComparison.Ordinal);
            Assert.Contains("送信元", partialHtml, StringComparison.Ordinal);
            Assert.Contains("未設定", partialHtml, StringComparison.Ordinal);
            Assert.DoesNotContain("action=\"/setup/provider\"", partialHtml, StringComparison.Ordinal);
            Assert.DoesNotContain(acsSecret, partialHtml, StringComparison.Ordinal);
            Assert.DoesNotContain("synthetic-acs-secret", partialHtml, StringComparison.Ordinal);

            const string adminUsername = "admin<owner>";
            const string adminPassword = "synthetic-admin-password";
            var users = new AdminUserRepository(connections, TimeProvider.System);
            Assert.True(await users.EnsureInstanceOwnerAsync(
                adminUsername,
                AdminPasswordHasher.Hash(adminPassword),
                ct));

            const string senderDisplayName = "Amane <System> & Mail";
            var senders = new SenderRepository(connections, TimeProvider.System);
            await senders.CreateAsync("noreply@example.com", senderDisplayName, ct);

            var html = await ReadAuthenticatedSetupAsync(client, authCookie);
            Assert.DoesNotContain("未設定", html, StringComparison.Ordinal);
            Assert.Contains("必要な設定がすべて完了しました。", html, StringComparison.Ordinal);
            Assert.Contains(
                $"<dd>{HtmlEncoder.Default.Encode(adminUsername)}</dd>",
                html,
                StringComparison.Ordinal);
            Assert.Contains(
                $"<dd>{HtmlEncoder.Default.Encode(senderDisplayName)}</dd>",
                html,
                StringComparison.Ordinal);
            Assert.DoesNotContain("<owner>", html, StringComparison.Ordinal);
            Assert.DoesNotContain("<System>", html, StringComparison.Ordinal);
            Assert.DoesNotContain(acsSecret, html, StringComparison.Ordinal);
            Assert.DoesNotContain("synthetic-acs-secret", html, StringComparison.Ordinal);
            Assert.DoesNotContain(adminPassword, html, StringComparison.Ordinal);
            Assert.DoesNotContain(secretPath, html, StringComparison.Ordinal);
            Assert.Contains("Password</dt><dd>設定済み（表示しません）</dd>", html, StringComparison.Ordinal);
            Assert.DoesNotContain("type=\"password\"", html, StringComparison.Ordinal);
            Assert.DoesNotContain("action=\"/setup/provider\"", html, StringComparison.Ordinal);
            Assert.DoesNotContain("action=\"/setup/admin\"", html, StringComparison.Ordinal);
            Assert.DoesNotContain("action=\"/setup/sender\"", html, StringComparison.Ordinal);
        }
        finally
        {
            if (factory is not null)
                await factory.DisposeAsync();
            DeleteRoot(root);
        }
    }

    [Fact]
    public async Task Initialized_runtime_hides_every_setup_route_even_when_bootstrap_file_is_stale()
    {
        var ct = TestContext.Current.CancellationToken;
        var root = CreateRoot("initialized-setup-hidden");
        WebApplicationFactory<global::Program>? factory = null;
        try
        {
            var databasePath = Path.Combine(root, "mailer.db");
            var tokenPath = Path.Combine(root, "bootstrap", "setup_token");
            var configuration = CreateConfiguration(databasePath, tokenPath);
            var connections = new SqliteConnectionFactory(configuration);
            await new SqlMigrationRunner(connections).ApplyPendingAsync(ct);

            var instance = new InstanceConfigurationRepository(connections, TimeProvider.System);
            var secretPath = Path.Combine(root, "secrets", "acs_connection_string");
            Assert.True(await instance.ConfigureAcsAsync(secretPath, ct));

            var users = new AdminUserRepository(connections, TimeProvider.System);
            Assert.True(await users.EnsureInstanceOwnerAsync(
                "managed-owner",
                AdminPasswordHasher.Hash("managed-owner-password"),
                ct));

            var senders = new SenderRepository(connections, TimeProvider.System);
            await senders.CreateAsync("noreply@example.com", "Example", ct);
            Assert.True(await instance.FinalizeAsync(ct));

            // This valid token was created before the initialized runtime started and is
            // intentionally left in place to model a stale bootstrap file.
            var staleBootstrapToken = new BootstrapTokenStore(configuration).EnsureExists();
            Assert.True(File.Exists(tokenPath));

            factory = new WebApplicationFactory<global::Program>().WithWebHostBuilder(builder =>
            {
                builder.UseEnvironment("Testing");
                builder.UseSetting("ConnectionStrings:Mailer", $"Data Source={databasePath}");
                builder.UseSetting("MAILER_BOOTSTRAP_TOKEN_PATH", tokenPath);
                builder.UseSetting("Mailer:Worker:Enabled", "false");
                builder.UseSetting("AMANE_ADMIN_ENABLED", "false");
                builder.ConfigureServices(services => services.RemoveAll<IHostedService>());
            });

            using var client = factory.CreateClient(new WebApplicationFactoryClientOptions
            {
                AllowAutoRedirect = false,
                BaseAddress = new Uri("https://localhost"),
            });

            using var setup = await client.GetAsync("/setup", ct);
            Assert.Equal(HttpStatusCode.NotFound, setup.StatusCode);

            foreach (var path in new[]
            {
                "/setup/auth",
                "/setup/provider",
                "/setup/admin",
                "/setup/sender",
                "/setup/finalize",
            })
            {
                using var request = new HttpRequestMessage(HttpMethod.Post, path)
                {
                    Content = new FormUrlEncodedContent(new Dictionary<string, string>
                    {
                        ["__RequestVerificationToken"] = "not-used-after-initialization",
                    }),
                };
                request.Headers.TryAddWithoutValidation("Origin", "https://localhost");
                using var response = await client.SendAsync(request, ct);
                Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
            }

            Assert.Equal(staleBootstrapToken, File.ReadAllText(tokenPath).Trim());
        }
        finally
        {
            if (factory is not null)
                await factory.DisposeAsync();
            DeleteRoot(root);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Initialized_runtime_with_missing_or_corrupt_provider_secret_fails_safe(
        bool corruptSecret)
    {
        var ct = TestContext.Current.CancellationToken;
        var root = CreateRoot("initialized-secret-failsafe");
        WebApplicationFactory<global::Program>? factory = null;
        try
        {
            var databasePath = Path.Combine(root, "mailer.db");
            var tokenPath = Path.Combine(root, "bootstrap", "setup_token");
            var configuration = CreateConfiguration(databasePath, tokenPath);
            var connections = new SqliteConnectionFactory(configuration);
            await new SqlMigrationRunner(connections).ApplyPendingAsync(ct);

            var secretPath = Path.Combine(root, "secrets", "acs", "acs_connection_string");
            Assert.True(FirstRunSetupStorage.WriteAcsSecretCreateOnly(
                secretPath,
                "Endpoint=https://fixture.communication.azure.com/;AccessKey=fixture-only-not-real"));
            var instance = new InstanceConfigurationRepository(connections, TimeProvider.System);
            Assert.True(await instance.ConfigureAcsAsync(secretPath, ct));

            var users = new AdminUserRepository(connections, TimeProvider.System);
            Assert.True(await users.EnsureInstanceOwnerAsync(
                "managed-owner",
                AdminPasswordHasher.Hash("managed-owner-password"),
                ct));

            var senders = new SenderRepository(connections, TimeProvider.System);
            await senders.CreateAsync("noreply@example.com", "Example", ct);
            Assert.True(await instance.FinalizeAsync(ct));

            if (corruptSecret)
            {
                File.WriteAllText(secretPath, "Endpoint=not-https;AccessKey=corrupt");
            }
            else
            {
                File.Delete(secretPath);
            }

            var staleBootstrapToken = new BootstrapTokenStore(configuration).EnsureExists();
            factory = new WebApplicationFactory<global::Program>().WithWebHostBuilder(builder =>
            {
                builder.UseEnvironment("Testing");
                builder.UseSetting("ConnectionStrings:Mailer", $"Data Source={databasePath}");
                builder.UseSetting("MAILER_BOOTSTRAP_TOKEN_PATH", tokenPath);
                builder.UseSetting("Mailer:Worker:Enabled", "false");
                builder.UseSetting("AMANE_ADMIN_ENABLED", "false");
                builder.ConfigureServices(services => services.RemoveAll<IHostedService>());
            });

            using var client = factory.CreateClient(new WebApplicationFactoryClientOptions
            {
                AllowAutoRedirect = false,
                BaseAddress = new Uri("https://localhost"),
            });

            using var ready = await client.GetAsync("/readyz", ct);
            Assert.Equal(HttpStatusCode.ServiceUnavailable, ready.StatusCode);
            using var readyJson = JsonDocument.Parse(
                await ready.Content.ReadAsStringAsync(ct));
            Assert.Equal(
                "provider_secret_missing",
                readyJson.RootElement.GetProperty("reason").GetString());

            using var setup = await client.GetAsync("/setup", ct);
            Assert.Equal(HttpStatusCode.NotFound, setup.StatusCode);
            Assert.Equal(staleBootstrapToken, File.ReadAllText(tokenPath).Trim());
        }
        finally
        {
            if (factory is not null)
                await factory.DisposeAsync();
            DeleteRoot(root);
        }
    }

    [Fact]
    public async Task Database_owned_admin_reset_bumps_epoch_and_revokes_sessions()
    {
        var root = CreateRoot("admin-reset");
        try
        {
            var configuration = CreateConfiguration(Path.Combine(root, "mailer.db"));
            var factory = new SqliteConnectionFactory(configuration);
            await new SqlMigrationRunner(factory).ApplyPendingAsync(TestContext.Current.CancellationToken);

            const string username = "first-owner";
            var repository = new AdminUserRepository(factory, TimeProvider.System);
            var initialHash = AdminPasswordHasher.Hash("initial-owner-password");
            Assert.True(await repository.EnsureInstanceOwnerAsync(
                username,
                initialHash,
                TestContext.Current.CancellationToken));
            var initial = await repository.GetActiveUserByUsernameAsync(
                username,
                TestContext.Current.CancellationToken);
            Assert.NotNull(initial);
            Assert.True(initial.IsInstanceOwner);

            var sessions = new AdminSessionRepository(factory);
            var now = DateTimeOffset.UtcNow;
            const string sessionId = "first-owner-session";
            await sessions.CreateSessionAsync(
                new AdminSessionRow(
                    sessionId,
                    username,
                    now,
                    now,
                    now.AddHours(1),
                    now.AddMinutes(30),
                    null,
                    null,
                    initial.CredentialEpoch),
                maxConcurrentSessions: 3,
                TestContext.Current.CancellationToken);

            var output = new StringWriter();
            var error = new StringWriter();
            const string replacementPassword = "replacement-owner-password";
            var command = new AdminResetPasswordCommand();
            Assert.Equal(
                AdminResetPasswordCommand.SuccessExitCode,
                await command.ExecuteAsync(
                    ["admin", "reset-password", "--username", username],
                    new StringReader(replacementPassword + "\n" + replacementPassword + "\n"),
                    output,
                    error,
                    repository,
                    "admin",
                    TestContext.Current.CancellationToken));

            var changed = await repository.GetActiveUserByUsernameAsync(
                username,
                TestContext.Current.CancellationToken);
            Assert.NotNull(changed);
            Assert.Equal(initial.CredentialEpoch + 1, changed.CredentialEpoch);
            Assert.True(AdminPasswordHasher.Verify(replacementPassword, changed.PasswordHash));
            Assert.DoesNotContain(replacementPassword, output.ToString(), StringComparison.Ordinal);
            Assert.DoesNotContain(replacementPassword, error.ToString(), StringComparison.Ordinal);

            var revoked = await sessions.GetSessionAsync(
                sessionId,
                TestContext.Current.CancellationToken);
            Assert.NotNull(revoked?.RevokedAt);
            Assert.Equal(AdminSessionRevokeReasons.CredentialChanged, revoked.RevokeReason);
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    [Fact]
    public void Initialized_snapshot_ignores_legacy_configuration_without_instance_owner()
    {
        var root = CreateRoot("managed-snapshot");
        try
        {
            var secretPath = Path.Combine(root, "secrets", "acs_connection_string");
            var configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Mailer:TenantsPath"] = Path.Combine(root, "invalid-tenants.json"),
                    ["MAILER_PROVIDER"] = "mailpit",
                    ["ACS_CONNECTION_STRING"] = "Endpoint=https://env.example;AccessKey=env-secret",
                })
                .Build();
            var state = new InstanceRuntimeState(
                InstanceRuntimeStateKind.Initialized,
                "2026-01-01T00:00:00Z",
                false,
                "acs",
                secretPath,
                "2026-01-01T00:00:00Z",
                false);

            var snapshot = MailerConfigurationSnapshot.Load(configuration, "Production", state);
            var tenant = Assert.Single(snapshot.Registry.ListTenants());
            Assert.Equal("acs", tenant.Provider);
            Assert.False(tenant.LiveSending);
            Assert.Empty(snapshot.Options.ProviderOverride);
            Assert.Empty(snapshot.Options.AcsConnectionString);
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    [Fact]
    public void Acs_secret_write_is_create_only_and_requires_valid_protected_content()
    {
        var root = CreateRoot("secret");
        try
        {
            var path = Path.Combine(root, "secrets", "acs_connection_string");
            const string first = "Endpoint=https://example.communication.azure.com/;AccessKey=abc123";
            const string second = "Endpoint=https://other.communication.azure.com/;AccessKey=def456";

            Assert.True(FirstRunSetupStorage.WriteAcsSecretCreateOnly(path, first));
            Assert.True(FirstRunSetupStorage.WriteAcsSecretCreateOnly(path, second));
            Assert.True(FirstRunSetupStorage.TryReadValidAcsSecret(path, out var persisted));
            Assert.Equal(first, persisted);
            Assert.Equal(first, File.ReadAllText(path));
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    private static IConfiguration CreateConfiguration(string databasePath, string? tokenPath = null) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:Mailer"] = $"Data Source={databasePath}",
                ["MAILER_BOOTSTRAP_TOKEN_PATH"] = tokenPath,
            })
            .Build();

    private static string ReadRequestToken(string html)
    {
        const string prefix = "name=\"__RequestVerificationToken\" value=\"";
        var start = html.IndexOf(prefix, StringComparison.Ordinal);
        Assert.True(start >= 0);
        start += prefix.Length;
        var end = html.IndexOf('"', start);
        Assert.True(end > start);
        return html[start..end];
    }

    private static async Task<string> AuthenticateSetupAsync(
        HttpClient client,
        string bootstrapToken)
    {
        var ct = TestContext.Current.CancellationToken;
        using var page = await client.GetAsync("/setup", ct);
        var html = await page.Content.ReadAsStringAsync(ct);
        var requestToken = ReadRequestToken(html);
        var csrfCookie = page.Headers.GetValues("Set-Cookie")
            .Single(value => value.StartsWith("__Host-amane-setup-csrf=", StringComparison.Ordinal))
            .Split(';', 2)[0];

        using var response = await client.SendAsync(
            CreateSetupAuthRequest(requestToken, csrfCookie, bootstrapToken),
            ct);
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal("/setup", response.Headers.Location?.OriginalString);
        return response.Headers.GetValues("Set-Cookie")
            .Single(value => value.StartsWith("__Host-amane-setup-auth=", StringComparison.Ordinal))
            .Split(';', 2)[0];
    }

    private static async Task<string> ReadAuthenticatedSetupAsync(
        HttpClient client,
        string authCookie)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "/setup");
        request.Headers.TryAddWithoutValidation("Cookie", authCookie);
        using var response = await client.SendAsync(request, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
    }

    private static HttpRequestMessage CreateSetupAuthRequest(
        string requestToken,
        string csrfCookie,
        string bootstrapToken)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/setup/auth")
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["__RequestVerificationToken"] = requestToken,
                ["bootstrap_token"] = bootstrapToken,
            }),
        };
        request.Headers.TryAddWithoutValidation("Origin", "https://localhost");
        request.Headers.TryAddWithoutValidation("Cookie", csrfCookie);
        return request;
    }

    private static string CreateRoot(string name)
    {
        var root = Path.Combine(Path.GetTempPath(), "amane-mailer-first-run-" + name, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }

    private static void DeleteRoot(string root)
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(root))
            Directory.Delete(root, recursive: true);
    }

    private sealed class TestRemoteAddressStartupFilter(IPAddress remoteAddress) : IStartupFilter
    {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) =>
            app =>
            {
                app.Use(async (context, nextMiddleware) =>
                {
                    context.Connection.RemoteIpAddress = remoteAddress;
                    await nextMiddleware();
                });

                next(app);
            };
    }
}
