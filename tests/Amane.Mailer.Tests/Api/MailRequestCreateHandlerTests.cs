using System.Text.Json;
using Amane.Mailer.Api;
using Amane.Mailer.Configuration;
using Amane.Mailer.Contracts.MailRequests;
using Amane.Mailer.Data.Sqlite;
using Amane.Mailer.Data.Sqlite.Models;
using Amane.Mailer.Json;
using Amane.Mailer.Queue;
using Amane.Mailer.Tests.Fixtures;
using Microsoft.AspNetCore.Http;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace Amane.Mailer.Tests.Api;

public sealed class MailRequestCreateHandlerTests : IAsyncLifetime
{
    private string? _root;
    private MailerTenantRegistry? _registry;

    public async ValueTask InitializeAsync()
    {
        _root = Path.Combine(Path.GetTempPath(), "amane-mailer-create-handler-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
        var tenantsPath = Path.Combine(_root, "tenants.json");
        await File.WriteAllTextAsync(tenantsPath, TenantConfigJson);

        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Mailer:TenantsPath"] = tenantsPath,
                ["MAIL_SERVICE_TOKEN"] = MailerWebApplicationFixtureBase.Token,
            })
            .Build();

        _registry = MailerTenantRegistry.Load(configuration);
    }

    public ValueTask DisposeAsync()
    {
        if (_root is not null && Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }

        return ValueTask.CompletedTask;
    }

    [Fact]
    public async Task HandleAsync_returns_401_for_unauthorized_token()
    {
        var request = MailRequestTestData.CreateRequest();
        var body = JsonSerializer.Serialize(request, MailerJsonContext.Default.MailRequestCreateRequest);
        var httpRequest = CreateAuthorizedHttpRequest("wrong-token");

        var result = await MailRequestCreateHandler.HandleAsync(
            httpRequest,
            request,
            body,
            new StubMailRequestRepository(),
            new MailRequestQueue(),
            _registry!,
            TimeProvider.System,
            NullLogger.Instance,
            CancellationToken.None);

        var (statusCode, responseBody) = MailRequestHttpResultAssertions.Inspect(result);
        Assert.Equal(StatusCodes.Status401Unauthorized, statusCode);
        Assert.Contains(MailerErrorCodes.UnauthorizedTenant, responseBody, StringComparison.Ordinal);
    }

    [Fact]
    public async Task HandleAsync_returns_403_for_disallowed_source_service()
    {
        var request = MailRequestTestData.CreateRequest(sourceService: "other-service");
        var body = JsonSerializer.Serialize(request, MailerJsonContext.Default.MailRequestCreateRequest);
        var httpRequest = CreateAuthorizedHttpRequest(MailerWebApplicationFixtureBase.Token);

        var result = await MailRequestCreateHandler.HandleAsync(
            httpRequest,
            request,
            body,
            new StubMailRequestRepository(),
            new MailRequestQueue(),
            _registry!,
            TimeProvider.System,
            NullLogger.Instance,
            CancellationToken.None);

        var (statusCode, responseBody) = MailRequestHttpResultAssertions.Inspect(result);
        Assert.Equal(StatusCodes.Status403Forbidden, statusCode);
        Assert.Contains(MailerErrorCodes.SourceServiceNotAllowed, responseBody, StringComparison.Ordinal);
    }

    [Fact]
    public async Task HandleAsync_returns_422_for_payload_hash_mismatch()
    {
        var request = MailRequestTestData.CreateRequest() with
        {
            PayloadHash = new string('1', 64),
        };
        var body = JsonSerializer.Serialize(request, MailerJsonContext.Default.MailRequestCreateRequest);
        var httpRequest = CreateAuthorizedHttpRequest(MailerWebApplicationFixtureBase.Token);

        var result = await MailRequestCreateHandler.HandleAsync(
            httpRequest,
            request,
            body,
            new StubMailRequestRepository(),
            new MailRequestQueue(),
            _registry!,
            TimeProvider.System,
            NullLogger.Instance,
            CancellationToken.None);

        var (statusCode, responseBody) = MailRequestHttpResultAssertions.Inspect(result);
        Assert.Equal(StatusCodes.Status422UnprocessableEntity, statusCode);
        Assert.Contains(MailerErrorCodes.InvalidPayloadHash, responseBody, StringComparison.Ordinal);
    }

    [Fact]
    public async Task HandleAsync_returns_already_accepted_for_idempotent_repost()
    {
        var request = MailRequestTestData.CreateRequest();
        var body = JsonSerializer.Serialize(request, MailerJsonContext.Default.MailRequestCreateRequest);
        var httpRequest = CreateAuthorizedHttpRequest(MailerWebApplicationFixtureBase.Token);
        var repository = new StubMailRequestRepository
        {
            Existing = new MailRequestIdempotencyRow
            {
                Id = Guid.NewGuid(),
                PayloadHash = request.PayloadHash,
                Status = MailRequestState.Queued,
            },
        };

        var result = await MailRequestCreateHandler.HandleAsync(
            httpRequest,
            request,
            body,
            repository,
            new MailRequestQueue(),
            _registry!,
            TimeProvider.System,
            NullLogger.Instance,
            CancellationToken.None);

        var (statusCode, _) = MailRequestHttpResultAssertions.Inspect(result);
        var response = MailRequestHttpResultAssertions.Value<MailRequestCreateResponse>(result);
        Assert.Equal(StatusCodes.Status202Accepted, statusCode);
        Assert.Equal(MailRequestAcceptanceStatus.AlreadyAccepted, response.Status);
        Assert.Equal(0, repository.InsertCount);
    }

    [Fact]
    public async Task HandleAsync_returns_409_for_idempotency_conflict()
    {
        var request = MailRequestTestData.CreateRequest();
        var body = JsonSerializer.Serialize(request, MailerJsonContext.Default.MailRequestCreateRequest);
        var httpRequest = CreateAuthorizedHttpRequest(MailerWebApplicationFixtureBase.Token);
        var repository = new StubMailRequestRepository
        {
            Existing = new MailRequestIdempotencyRow
            {
                Id = Guid.NewGuid(),
                PayloadHash = new string('f', 64),
                Status = MailRequestState.Queued,
            },
        };

        var result = await MailRequestCreateHandler.HandleAsync(
            httpRequest,
            request,
            body,
            repository,
            new MailRequestQueue(),
            _registry!,
            TimeProvider.System,
            NullLogger.Instance,
            CancellationToken.None);

        var (statusCode, responseBody) = MailRequestHttpResultAssertions.Inspect(result);
        Assert.Equal(StatusCodes.Status409Conflict, statusCode);
        Assert.Contains(MailerErrorCodes.IdempotencyConflict, responseBody, StringComparison.Ordinal);
    }

    [Fact]
    public async Task HandleAsync_returns_storage_full_when_find_throws_sqlite_full()
    {
        var request = MailRequestTestData.CreateRequest();
        var body = JsonSerializer.Serialize(request, MailerJsonContext.Default.MailRequestCreateRequest);
        var httpRequest = CreateAuthorizedHttpRequest(MailerWebApplicationFixtureBase.Token);
        var repository = new StubMailRequestRepository
        {
            FindException = new SqliteException(
                "database or disk is full",
                SqliteDatabaseExceptionClassifier.SqliteFull),
        };

        var result = await MailRequestCreateHandler.HandleAsync(
            httpRequest,
            request,
            body,
            repository,
            new MailRequestQueue(),
            _registry!,
            TimeProvider.System,
            NullLogger.Instance,
            CancellationToken.None);

        var (statusCode, responseBody) = MailRequestHttpResultAssertions.Inspect(result);
        Assert.Equal(StatusCodes.Status503ServiceUnavailable, statusCode);
        Assert.Contains(MailerErrorCodes.StorageFull, responseBody, StringComparison.Ordinal);
    }

    [Fact]
    public async Task HandleAsync_returns_503_when_find_throws_transient_sqlite()
    {
        var request = MailRequestTestData.CreateRequest();
        var body = JsonSerializer.Serialize(request, MailerJsonContext.Default.MailRequestCreateRequest);
        var httpRequest = CreateAuthorizedHttpRequest(MailerWebApplicationFixtureBase.Token);
        var repository = new StubMailRequestRepository
        {
            FindException = new SqliteException(
                "database is locked",
                SqliteDatabaseExceptionClassifier.SqliteBusy),
        };

        var result = await MailRequestCreateHandler.HandleAsync(
            httpRequest,
            request,
            body,
            repository,
            new MailRequestQueue(),
            _registry!,
            TimeProvider.System,
            NullLogger.Instance,
            CancellationToken.None);

        var (statusCode, responseBody) = MailRequestHttpResultAssertions.Inspect(result);
        Assert.Equal(StatusCodes.Status503ServiceUnavailable, statusCode);
        Assert.Contains(MailerErrorCodes.MailerTemporarilyUnavailable, responseBody, StringComparison.Ordinal);
    }

    [Fact]
    public async Task HandleAsync_accepts_new_request()
    {
        var request = MailRequestTestData.CreateRequest();
        var body = JsonSerializer.Serialize(request, MailerJsonContext.Default.MailRequestCreateRequest);
        var httpRequest = CreateAuthorizedHttpRequest(MailerWebApplicationFixtureBase.Token);
        var repository = new StubMailRequestRepository();

        var result = await MailRequestCreateHandler.HandleAsync(
            httpRequest,
            request,
            body,
            repository,
            new MailRequestQueue(),
            _registry!,
            TimeProvider.System,
            NullLogger.Instance,
            CancellationToken.None);

        var (statusCode, _) = MailRequestHttpResultAssertions.Inspect(result);
        var response = MailRequestHttpResultAssertions.Value<MailRequestCreateResponse>(result);
        Assert.Equal(StatusCodes.Status202Accepted, statusCode);
        Assert.Equal(MailRequestAcceptanceStatus.Accepted, response.Status);
        Assert.Equal(1, repository.InsertCount);
    }

    private static HttpRequest CreateAuthorizedHttpRequest(string token)
    {
        var context = new DefaultHttpContext();
        context.Request.Headers.Authorization = $"Bearer {token}";
        return context.Request;
    }

    private sealed class StubMailRequestRepository : MailRequestRepository
    {
        public StubMailRequestRepository()
            : base(
                claimStore: null!,
                acceptStore: null!,
                consumerMutations: null!,
                adminQueries: null!,
                heartbeatStore: null!)
        {
        }

        public MailRequestIdempotencyRow? Existing { get; init; }

        public Exception? FindException { get; init; }

        public int InsertCount { get; private set; }

        public override Task<MailRequestIdempotencyRow?> FindByIdempotencyKeyAsync(
            Guid tenantId,
            string sourceService,
            Guid mailRequestId,
            CancellationToken cancellationToken = default)
        {
            if (FindException is not null)
            {
                throw FindException;
            }

            return Task.FromResult(Existing);
        }

        public override Task InsertAcceptedAsync(
            AcceptedMailRequestInsert insert,
            CancellationToken cancellationToken = default)
        {
            InsertCount++;
            return Task.CompletedTask;
        }
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
