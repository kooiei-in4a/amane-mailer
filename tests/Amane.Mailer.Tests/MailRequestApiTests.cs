using System.Net;
using System.Text;
using Amane.Mailer.Api;
using Amane.Mailer.Data.Sqlite;
using Amane.Mailer.Tests.Fixtures;
using Mailer.Contracts.MailRequests;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;

namespace Amane.Mailer.Tests;

[Collection(MailerTestCollection.Name)]
public sealed class MailRequestApiTests(MailerApiFixture fixture)
    : IClassFixture<MailerApiFixture>, IAsyncLifetime
{
    public async ValueTask InitializeAsync() =>
        await fixture.ResetAsync(TestContext.Current.CancellationToken);

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    [Fact]
    public async Task Post_accepts_new_mail_request()
    {
        var ct = TestContext.Current.CancellationToken;
        using var client = CreateAuthorizedClient();
        var request = MailRequestTestData.CreateRequest();

        using var response = await client.PostAsync(
            "/internal/mail-requests",
            MailRequestTestData.ToJsonContent(request),
            ct);

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        Assert.Equal(
            MailRequestAcceptanceStatus.Accepted,
            await MailRequestTestData.ReadStatusAsync(response, ct));

        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var repository = scope.ServiceProvider.GetRequiredService<MailRequestRepository>();
        var stored = await repository.FindByIdempotencyKeyAsync(
            MailerWebApplicationFixtureBase.TenantId,
            MailerWebApplicationFixtureBase.SourceService,
            request.MailRequestId,
            ct);
        Assert.NotNull(stored);
        Assert.Equal(MailRequestState.Queued, stored.Status);
    }

    [Fact]
    public async Task Reposting_same_id_and_hash_returns_already_accepted()
    {
        var ct = TestContext.Current.CancellationToken;
        using var client = CreateAuthorizedClient();
        var request = MailRequestTestData.CreateRequest();

        using var first = await client.PostAsync(
            "/internal/mail-requests",
            MailRequestTestData.ToJsonContent(request),
            ct);
        using var second = await client.PostAsync(
            "/internal/mail-requests",
            MailRequestTestData.ToJsonContent(request),
            ct);

        Assert.Equal(HttpStatusCode.Accepted, first.StatusCode);
        Assert.Equal(HttpStatusCode.Accepted, second.StatusCode);
        Assert.Equal(
            MailRequestAcceptanceStatus.AlreadyAccepted,
            await MailRequestTestData.ReadStatusAsync(second, ct));
    }

    [Fact]
    public async Task Concurrent_reposting_same_id_and_hash_is_idempotent()
    {
        var ct = TestContext.Current.CancellationToken;
        using var client = CreateAuthorizedClient();
        var request = MailRequestTestData.CreateRequest();

        var tasks = Enumerable.Range(0, 8)
            .Select(async _ =>
            {
                using var response = await client.PostAsync(
                    "/internal/mail-requests",
                    MailRequestTestData.ToJsonContent(request),
                    ct);

                Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
                return await MailRequestTestData.ReadStatusAsync(response, ct);
            });

        var statuses = await Task.WhenAll(tasks);

        Assert.Equal(1, statuses.Count(status => status == MailRequestAcceptanceStatus.Accepted));
        Assert.Equal(7, statuses.Count(status => status == MailRequestAcceptanceStatus.AlreadyAccepted));
    }

    [Fact]
    public async Task Reposting_same_id_with_different_hash_returns_409()
    {
        var ct = TestContext.Current.CancellationToken;
        using var client = CreateAuthorizedClient();
        var mailRequestId = Guid.NewGuid();
        var firstRequest = MailRequestTestData.CreateRequest(mailRequestId: mailRequestId);
        var conflictingRequest = MailRequestTestData.CreateRequest(
            mailRequestId: mailRequestId,
            subject: "Changed subject");

        using var first = await client.PostAsync(
            "/internal/mail-requests",
            MailRequestTestData.ToJsonContent(firstRequest),
            ct);
        using var second = await client.PostAsync(
            "/internal/mail-requests",
            MailRequestTestData.ToJsonContent(conflictingRequest),
            ct);

        Assert.Equal(HttpStatusCode.Accepted, first.StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, second.StatusCode);
        Assert.Equal(
            MailerErrorCodes.IdempotencyConflict,
            await MailRequestTestData.ReadCodeAsync(second, ct));
    }

    [Fact]
    public async Task Unregistered_source_service_returns_403()
    {
        var ct = TestContext.Current.CancellationToken;
        using var client = CreateAuthorizedClient();
        var request = MailRequestTestData.CreateRequest(sourceService: "unknown-service");

        using var response = await client.PostAsync(
            "/internal/mail-requests",
            MailRequestTestData.ToJsonContent(request),
            ct);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal(
            MailerErrorCodes.SourceServiceNotAllowed,
            await MailRequestTestData.ReadCodeAsync(response, ct));
    }

    [Fact]
    public async Task Unauthorized_token_returns_401()
    {
        var ct = TestContext.Current.CancellationToken;
        using var client = CreateClient(token: "wrong-token");
        var request = MailRequestTestData.CreateRequest();

        using var response = await client.PostAsync(
            "/internal/mail-requests",
            MailRequestTestData.ToJsonContent(request),
            ct);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal(
            MailerErrorCodes.UnauthorizedTenant,
            await MailRequestTestData.ReadCodeAsync(response, ct));
    }

    [Fact]
    public async Task Missing_bearer_token_returns_401()
    {
        var ct = TestContext.Current.CancellationToken;
        using var client = fixture.Factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
        });
        var request = MailRequestTestData.CreateRequest();

        using var response = await client.PostAsync(
            "/internal/mail-requests",
            MailRequestTestData.ToJsonContent(request),
            ct);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal(
            MailerErrorCodes.UnauthorizedTenant,
            await MailRequestTestData.ReadCodeAsync(response, ct));
    }

    [Fact]
    public async Task Payload_hash_mismatch_returns_422()
    {
        var ct = TestContext.Current.CancellationToken;
        using var client = CreateAuthorizedClient();
        var request = MailRequestTestData.CreateRequest() with
        {
            PayloadHash = new string('f', 64),
        };

        using var response = await client.PostAsync(
            "/internal/mail-requests",
            MailRequestTestData.ToJsonContent(request),
            ct);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal(
            MailerErrorCodes.InvalidPayloadHash,
            await MailRequestTestData.ReadCodeAsync(response, ct));
    }

    [Fact]
    public async Task Forbidden_metadata_key_returns_422()
    {
        var ct = TestContext.Current.CancellationToken;
        using var client = CreateAuthorizedClient();
        var request = MailRequestTestData.CreateRequest(metadata: new Dictionary<string, string>
        {
            ["reset_url"] = "https://example.com/reset?token=secret",
        });

        using var response = await client.PostAsync(
            "/internal/mail-requests",
            MailRequestTestData.ToJsonContent(request),
            ct);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal(
            MailerErrorCodes.InvalidMetadata,
            await MailRequestTestData.ReadCodeAsync(response, ct));
    }

    [Fact]
    public async Task Oversized_metadata_returns_422()
    {
        var ct = TestContext.Current.CancellationToken;
        using var client = CreateAuthorizedClient();
        var request = MailRequestTestData.CreateRequest(metadata: new Dictionary<string, string>
        {
            ["large"] = new string('x', 5000),
        });

        using var response = await client.PostAsync(
            "/internal/mail-requests",
            MailRequestTestData.ToJsonContent(request),
            ct);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal(
            MailerErrorCodes.InvalidMetadata,
            await MailRequestTestData.ReadCodeAsync(response, ct));
    }

    [Fact]
    public async Task Too_many_recipients_returns_422()
    {
        var ct = TestContext.Current.CancellationToken;
        using var client = CreateAuthorizedClient();
        var request = MailRequestTestData.CreateRequest() with
        {
            To =
            [
                new MailRecipientDto { Email = "one@example.com" },
                new MailRecipientDto { Email = "two@example.com" },
            ],
        };
        request = request with
        {
            PayloadHash = global::Mailer.Contracts.Security.MailPayloadHasher.ComputeDeliveryPayloadSha256Hex(request),
        };

        using var response = await client.PostAsync(
            "/internal/mail-requests",
            MailRequestTestData.ToJsonContent(request),
            ct);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal(
            MailerErrorCodes.TooManyRecipients,
            await MailRequestTestData.ReadCodeAsync(response, ct));
    }

    [Fact]
    public async Task Invalid_reply_to_returns_422()
    {
        var ct = TestContext.Current.CancellationToken;
        using var client = CreateAuthorizedClient();
        var request = MailRequestTestData.CreateRequest(replyTo: "not-an-email");

        using var response = await client.PostAsync(
            "/internal/mail-requests",
            MailRequestTestData.ToJsonContent(request),
            ct);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal(
            MailerErrorCodes.InvalidRequest,
            await MailRequestTestData.ReadCodeAsync(response, ct));
    }

    [Fact]
    public async Task Null_recipients_returns_422()
    {
        var ct = TestContext.Current.CancellationToken;
        using var client = CreateAuthorizedClient();
        using var content = new StringContent(
            $$"""
            {
              "tenant_id": "{{MailerWebApplicationFixtureBase.TenantId}}",
              "source_service": "{{MailerWebApplicationFixtureBase.SourceService}}",
              "mail_request_id": "{{Guid.NewGuid()}}",
              "purpose": "FormResponseNotification",
              "to": null,
              "subject": "Subject",
              "text_body": "Body",
              "payload_hash": "{{new string('0', 64)}}"
            }
            """,
            Encoding.UTF8,
            "application/json");

        using var response = await client.PostAsync("/internal/mail-requests", content, ct);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal(
            MailerErrorCodes.InvalidRequest,
            await MailRequestTestData.ReadCodeAsync(response, ct));
    }

    [Fact]
    public async Task Null_recipient_item_returns_422()
    {
        var ct = TestContext.Current.CancellationToken;
        using var client = CreateAuthorizedClient();
        using var content = new StringContent(
            $$"""
            {
              "tenant_id": "{{MailerWebApplicationFixtureBase.TenantId}}",
              "source_service": "{{MailerWebApplicationFixtureBase.SourceService}}",
              "mail_request_id": "{{Guid.NewGuid()}}",
              "purpose": "FormResponseNotification",
              "to": [null],
              "subject": "Subject",
              "text_body": "Body",
              "payload_hash": "{{new string('0', 64)}}"
            }
            """,
            Encoding.UTF8,
            "application/json");

        using var response = await client.PostAsync("/internal/mail-requests", content, ct);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal(
            MailerErrorCodes.InvalidRequest,
            await MailRequestTestData.ReadCodeAsync(response, ct));
    }

    [Fact]
    public async Task Oversized_request_body_returns_413()
    {
        var ct = TestContext.Current.CancellationToken;
        using var client = CreateAuthorizedClient();
        using var content = new StringContent(
            "{\"html_body\":\"" + new string('x', 260_000) + "\"}",
            Encoding.UTF8,
            "application/json");

        using var response = await client.PostAsync(
            "/internal/mail-requests",
            content,
            ct);

        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
        Assert.Equal(
            MailerErrorCodes.RequestTooLarge,
            await MailRequestTestData.ReadCodeAsync(response, ct));
    }

    [Fact]
    public async Task Temporary_database_unavailable_returns_503_retryable()
    {
        var ct = TestContext.Current.CancellationToken;
        var root = Path.Combine(Path.GetTempPath(), "amane-mailer-unavailable-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var badDatabasePath = Path.Combine(root, "blocked");
        Directory.CreateDirectory(badDatabasePath);

        try
        {
            var tenantConfigPath = Path.Combine(root, "tenants.json");
            await File.WriteAllTextAsync(tenantConfigPath, TenantConfigJson, ct);

            using var unavailableFactory = new WebApplicationFactory<global::Program>()
                .WithWebHostBuilder(builder =>
                {
                    builder.UseEnvironment("Testing");
                    builder.ConfigureAppConfiguration((_, configuration) =>
                        configuration.AddInMemoryCollection(new Dictionary<string, string?>
                        {
                            ["ConnectionStrings:Mailer"] = $"Data Source={badDatabasePath}",
                            ["MAILER_TENANTS_PATH"] = tenantConfigPath,
                            ["Mailer:Worker:Enabled"] = "False",
                            ["MAIL_SERVICE_TOKEN"] = MailerWebApplicationFixtureBase.Token,
                        }));
                    builder.ConfigureServices(services => services.RemoveAll<IHostedService>());
                });

            using var client = unavailableFactory.CreateClient(new WebApplicationFactoryClientOptions
            {
                AllowAutoRedirect = false,
            });
            client.DefaultRequestHeaders.Authorization = new("Bearer", MailerWebApplicationFixtureBase.Token);
            var request = MailRequestTestData.CreateRequest();

            using var response = await client.PostAsync(
                "/internal/mail-requests",
                MailRequestTestData.ToJsonContent(request),
                ct);

            Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
            Assert.Equal(
                MailerErrorCodes.MailerTemporarilyUnavailable,
                await MailRequestTestData.ReadCodeAsync(response, ct));
            Assert.True(await MailRequestTestData.ReadRetryableAsync(response, ct));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Health_and_ready_endpoints_are_ok()
    {
        var ct = TestContext.Current.CancellationToken;
        using var client = fixture.Factory.CreateClient();

        using var health = await client.GetAsync("/healthz", ct);
        using var ready = await client.GetAsync("/readyz", ct);

        Assert.Equal(HttpStatusCode.OK, health.StatusCode);
        Assert.Equal(HttpStatusCode.OK, ready.StatusCode);
    }

    private HttpClient CreateAuthorizedClient() =>
        CreateClient(MailerWebApplicationFixtureBase.Token);

    private HttpClient CreateClient(string token)
    {
        var client = fixture.Factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
        });
        client.DefaultRequestHeaders.Authorization = new("Bearer", token);
        return client;
    }

    private static string TenantConfigJson =>
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
              "provider": "mailpit",
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
}
