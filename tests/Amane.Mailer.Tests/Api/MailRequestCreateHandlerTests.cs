using System.Text.Json;
using Amane.Mailer.Attachments.Provider;
using Amane.Mailer.Api;
using Amane.Mailer.Attachments.Spool;
using Amane.Mailer.Attachments.Validation;
using Amane.Mailer.Configuration;
using Amane.Mailer.Contracts.MailRequests;
using Amane.Mailer.Contracts.Security;
using Amane.Mailer.Data;
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
    private AttachmentSpool? _attachmentSpool;

    public async ValueTask InitializeAsync()
    {
        _root = Path.Combine(Path.GetTempPath(), "amane-mailer-create-handler-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
        var tenantsPath = Path.Combine(_root, "tenants.json");
        await File.WriteAllTextAsync(tenantsPath, TenantConfigJson);
        _attachmentSpool = new AttachmentSpool(
            new AttachmentSpoolOptions { RootDirectory = Path.Combine(_root, "attachment-spool") });

        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Mailer:TenantsPath"] = tenantsPath,
                ["MAIL_SERVICE_TOKEN"] = MailerWebApplicationFixtureBase.Token,
            })
            .Build();

        _registry = MailerTenantRegistry.Load(configuration, "Testing");
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
            _attachmentSpool!,
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
            _attachmentSpool!,
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
            _attachmentSpool!,
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
            _attachmentSpool!,
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
    public async Task HandleAsync_persists_the_validated_canonical_recipient_not_the_raw_request_value()
    {
        // ADR 0023 D-02: the stored/delivered recipient must match the value MailPayloadHasher
        // hashed (trimmed address, whitespace-only display name normalized to absent) -- not
        // the raw request DTO -- so payload_hash always describes the actual delivery payload.
        var draft = new MailRequestCreateRequest
        {
            TenantId = MailerWebApplicationFixtureBase.TenantId,
            SourceService = MailerWebApplicationFixtureBase.SourceService,
            MailRequestId = Guid.NewGuid(),
            Purpose = "FormResponseNotification",
            To =
            [
                new MailRecipientDto
                {
                    Email = "  user@example.com  ",
                    DisplayName = "   ",
                },
            ],
            Subject = "Form response received",
            TextBody = "Hello from Mailer tests",
            PayloadHash = new string('0', 64),
        };
        var request = draft with
        {
            PayloadHash = MailPayloadHasher.ComputeDeliveryPayloadSha256Hex(draft),
        };
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
            _attachmentSpool!,
            TimeProvider.System,
            NullLogger.Instance,
            CancellationToken.None);

        var (statusCode, _) = MailRequestHttpResultAssertions.Inspect(result);
        Assert.Equal(StatusCodes.Status202Accepted, statusCode);
        Assert.Equal(1, repository.InsertCount);
        Assert.Equal("user@example.com", repository.LastInsert!.RecipientEmail);
        Assert.Null(repository.LastInsert!.RecipientDisplayName);
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
            _attachmentSpool!,
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
            _attachmentSpool!,
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
            _attachmentSpool!,
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
            _attachmentSpool!,
            TimeProvider.System,
            NullLogger.Instance,
            CancellationToken.None);

        var (statusCode, _) = MailRequestHttpResultAssertions.Inspect(result);
        var response = MailRequestHttpResultAssertions.Value<MailRequestCreateResponse>(result);
        Assert.Equal(StatusCodes.Status202Accepted, statusCode);
        Assert.Equal(MailRequestAcceptanceStatus.Accepted, response.Status);
        Assert.Equal(1, repository.InsertCount);
    }

    [Theory]
    [MemberData(nameof(NonLegacyRecipientShapes))]
    public async Task HandleAsync_accepts_non_legacy_recipient_shapes_and_persists_all_recipients(
        IReadOnlyList<MailRecipientDto>? to,
        IReadOnlyList<MailRecipientDto>? cc,
        IReadOnlyList<MailRecipientDto>? bcc)
    {
        // ADR 0023: public acceptance persists the complete canonical recipient set. The legacy
        // mail_requests shadow remains compatibility-only and must not reduce the recipient set.
        var request = MailRequestTestData.CreateRequest() with { To = to, Cc = cc, Bcc = bcc };
        request = request with { PayloadHash = MailPayloadHasher.ComputeDeliveryPayloadSha256Hex(request) };
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
            _attachmentSpool!,
            TimeProvider.System,
            NullLogger.Instance,
            CancellationToken.None);

        var (statusCode, responseBody) = MailRequestHttpResultAssertions.Inspect(result);
        var response = MailRequestHttpResultAssertions.Value<MailRequestCreateResponse>(result);
        Assert.Equal(StatusCodes.Status202Accepted, statusCode);
        Assert.Equal(MailRequestAcceptanceStatus.Accepted, response.Status);
        Assert.Equal(1, repository.InsertCount);
        Assert.Equal(
            (to?.Count ?? 0) + (cc?.Count ?? 0) + (bcc?.Count ?? 0),
            repository.LastInsert!.Recipients!.Count);
        Assert.DoesNotContain(MailerErrorCodes.InvalidRequest, responseBody, StringComparison.Ordinal);
    }

    [Fact]
    public void Legacy_shadow_representative_comes_from_the_canonical_aggregate()
    {
        var canonicalRecipients = new CanonicalMailRecipientSet
        {
            To = [],
            Cc = [CanonicalRecipient(MailRecipientRole.Cc, "cc@example.com")],
            Bcc = [],
            All = [CanonicalRecipient(MailRecipientRole.Cc, "cc@example.com")],
        };

        var representative = MailRequestCreateHandler.GetLegacyShadowRepresentative(canonicalRecipients);

        Assert.Equal("cc@example.com", representative.Address);

        var bccOnlyRecipients = new CanonicalMailRecipientSet
        {
            To = [],
            Cc = [],
            Bcc = [CanonicalRecipient(MailRecipientRole.Bcc, "bcc@example.com")],
            All = [CanonicalRecipient(MailRecipientRole.Bcc, "bcc@example.com")],
        };

        Assert.Equal(
            "bcc@example.com",
            MailRequestCreateHandler.GetLegacyShadowRepresentative(bccOnlyRecipients).Address);
    }

    [Fact]
    public void Provider_envelope_estimate_uses_all_canonical_recipients_at_the_acceptance_boundary()
    {
        var tenant = new MailerTenant
        {
            TenantId = Guid.NewGuid(),
            Name = "example-develop",
            SourceServices = ["example-service"],
            DefaultFrom = new MailerAddress { Email = "noreply@example.com" },
            TokenEnv = "MAIL_SERVICE_TOKEN",
            Provider = "mailpit",
            Retry = new MailerRetryOptions { MaxAttempts = 3, InitialDelaySeconds = 1, MaxDelaySeconds = 2 },
        };
        var recipients = new[]
        {
            CanonicalRecipient(MailRecipientRole.To, "to@example.com"),
            CanonicalRecipient(MailRecipientRole.Cc, "cc@example.com"),
            CanonicalRecipient(MailRecipientRole.Bcc, "bcc@example.com"),
        };
        var attachment = new CanonicalAttachmentMetadata(
            Order: 0,
            FileName: "payload.bin",
            ContentType: "application/octet-stream",
            ByteLength: MailAttachmentLimits.MaxTotalDecodedBytes,
            Sha256Hex: new string('a', 64),
            SpoolKey: Guid.NewGuid());

        var bodyLength = Enumerable.Range(1_300, 200)
            .Select(thousands => thousands * 1_000)
            .First(length =>
            {
                var request = MailRequestTestData.CreateRequest() with
                {
                    Subject = "subject",
                    TextBody = new string('x', length),
                };

                var singleRecipientEstimate = AttachmentEnvelopeEstimator.EstimateUpperBound(
                    CreateEnvelopeInput(request, recipients[..1], tenant, attachment));
                var allRecipientEstimate = AttachmentEnvelopeEstimator.EstimateUpperBound(
                    CreateEnvelopeInput(request, recipients, tenant, attachment));

                return singleRecipientEstimate <= MailAttachmentLimits.MaxProviderEnvelopeBytes
                    && allRecipientEstimate > MailAttachmentLimits.MaxProviderEnvelopeBytes;
            });
        var boundaryRequest = MailRequestTestData.CreateRequest() with
        {
            Subject = "subject",
            TextBody = new string('x', bodyLength),
        };

        Assert.True(
            MailRequestCreateHandler.IsWithinProviderEnvelopeEstimate(
                boundaryRequest,
                recipients[..1],
                tenant,
                [attachment]));
        Assert.False(
            MailRequestCreateHandler.IsWithinProviderEnvelopeEstimate(
                boundaryRequest,
                recipients,
                tenant,
                [attachment]));
    }

    public static TheoryData<IReadOnlyList<MailRecipientDto>?, IReadOnlyList<MailRecipientDto>?, IReadOnlyList<MailRecipientDto>?> NonLegacyRecipientShapes()
    {
        var data = new TheoryData<IReadOnlyList<MailRecipientDto>?, IReadOnlyList<MailRecipientDto>?, IReadOnlyList<MailRecipientDto>?>();

        // Two To recipients: a valid shape at the Contracts layer, but not yet persistable.
        data.Add(
            [
                new MailRecipientDto { Email = "one@example.com" },
                new MailRecipientDto { Email = "two@example.com" },
            ],
            null,
            null);

        // CC-only: no To at all.
        data.Add(null, [new MailRecipientDto { Email = "cc@example.com" }], null);

        // BCC-only.
        data.Add(null, null, [new MailRecipientDto { Email = "bcc@example.com" }]);

        // Single To plus a Cc.
        data.Add(
            [new MailRecipientDto { Email = "to@example.com" }],
            [new MailRecipientDto { Email = "cc@example.com" }],
            null);

        // Single To plus a Bcc.
        data.Add(
            [new MailRecipientDto { Email = "to@example.com" }],
            null,
            [new MailRecipientDto { Email = "bcc@example.com" }]);

        // Cc plus Bcc without a To.
        data.Add(
            null,
            [new MailRecipientDto { Email = "cc@example.com" }],
            [new MailRecipientDto { Email = "bcc@example.com" }]);

        // All three roles.
        data.Add(
            [new MailRecipientDto { Email = "to@example.com" }],
            [new MailRecipientDto { Email = "cc@example.com" }],
            [new MailRecipientDto { Email = "bcc@example.com" }]);

        return data;
    }

    private static AttachmentEnvelopeInput CreateEnvelopeInput(
        MailRequestCreateRequest request,
        IReadOnlyList<CanonicalMailRecipient> recipients,
        MailerTenant tenant,
        CanonicalAttachmentMetadata attachment) =>
        new(
            tenant.DefaultFrom.Email,
            recipients
                .Select(recipient => new AttachmentEnvelopeRecipient(recipient.Address, recipient.DisplayName))
                .ToArray(),
            request.Subject,
            request.TextBody,
            request.HtmlBody,
            request.ReplyTo,
            [new AttachmentEnvelopeAttachment(attachment.FileName, attachment.ContentType, attachment.ByteLength)]);

    private static CanonicalMailRecipient CanonicalRecipient(MailRecipientRole role, string address) =>
        new()
        {
            Role = role,
            Ordinal = 0,
            Address = address,
            AddressKey = RecipientEmailNormalizer.Normalize(address),
        };

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
                heartbeatStore: null!,
                attachmentStore: null!,
                attachmentSubmissionStore: null!,
                recipientStore: null!,
                plainSubmissionStore: null!)
        {
        }

        public MailRequestIdempotencyRow? Existing { get; init; }

        public Exception? FindException { get; init; }

        public int InsertCount { get; private set; }

        public AcceptedMailRequestInsert? LastInsert { get; private set; }

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
            LastInsert = insert;
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
