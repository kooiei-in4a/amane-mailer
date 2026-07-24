using System.Net;
using System.Text;
using Amane.Mailer.Api;
using Amane.Mailer.Data.Sqlite;
using Amane.Mailer.Data.Sqlite.Models;
using Amane.Mailer.Tests.Fixtures;
using Amane.Mailer.Contracts.MailRequests;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
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
    public async Task Metadata_value_with_secret_is_accepted_when_key_is_allowed()
    {
        var ct = TestContext.Current.CancellationToken;
        using var client = CreateAuthorizedClient();
        var request = MailRequestTestData.CreateRequest(metadata: new Dictionary<string, string>
        {
            ["link"] = "https://example.test/reset?token=replace-with-placeholder",
        });

        using var response = await client.PostAsync(
            "/internal/mail-requests",
            MailRequestTestData.ToJsonContent(request),
            ct);

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        Assert.Equal(
            MailRequestAcceptanceStatus.Accepted,
            await MailRequestTestData.ReadStatusAsync(response, ct));
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
            PayloadHash = global::Amane.Mailer.Contracts.Security.MailPayloadHasher.ComputeDeliveryPayloadSha256Hex(request),
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
    public async Task Unknown_top_level_property_returns_400()
    {
        var ct = TestContext.Current.CancellationToken;
        using var client = CreateAuthorizedClient();
        using var content = RawJsonContent(
            $$"""
            {
              "tenant_id": "{{MailerWebApplicationFixtureBase.TenantId}}",
              "source_service": "{{MailerWebApplicationFixtureBase.SourceService}}",
              "mail_request_id": "{{Guid.NewGuid()}}",
              "purpose": "FormResponseNotification",
              "to": [{ "email": "recipient@example.com" }],
              "subject": "Subject",
              "text_body": "Body",
              "payload_hash": "{{new string('0', 64)}}",
              "unexpected": "value"
            }
            """);

        using var response = await client.PostAsync("/internal/mail-requests", content, ct);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(
            MailerErrorCodes.InvalidRequest,
            await MailRequestTestData.ReadCodeAsync(response, ct));
    }

    [Fact]
    public async Task Unknown_recipient_property_returns_400()
    {
        var ct = TestContext.Current.CancellationToken;
        using var client = CreateAuthorizedClient();
        using var content = RawJsonContent(
            $$"""
            {
              "tenant_id": "{{MailerWebApplicationFixtureBase.TenantId}}",
              "source_service": "{{MailerWebApplicationFixtureBase.SourceService}}",
              "mail_request_id": "{{Guid.NewGuid()}}",
              "purpose": "FormResponseNotification",
              "to": [{ "email": "recipient@example.com", "role": "admin" }],
              "subject": "Subject",
              "text_body": "Body",
              "payload_hash": "{{new string('0', 64)}}"
            }
            """);

        using var response = await client.PostAsync("/internal/mail-requests", content, ct);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(
            MailerErrorCodes.InvalidRequest,
            await MailRequestTestData.ReadCodeAsync(response, ct));
    }

    [Fact]
    public async Task Duplicate_top_level_property_returns_400()
    {
        var ct = TestContext.Current.CancellationToken;
        using var client = CreateAuthorizedClient();
        using var content = RawJsonContent(
            $$"""
            {
              "tenant_id": "{{MailerWebApplicationFixtureBase.TenantId}}",
              "source_service": "{{MailerWebApplicationFixtureBase.SourceService}}",
              "mail_request_id": "{{Guid.NewGuid()}}",
              "purpose": "FormResponseNotification",
              "to": [{ "email": "recipient@example.com" }],
              "subject": "Subject",
              "subject": "Tampered subject",
              "text_body": "Body",
              "payload_hash": "{{new string('0', 64)}}"
            }
            """);

        using var response = await client.PostAsync("/internal/mail-requests", content, ct);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(
            MailerErrorCodes.InvalidRequest,
            await MailRequestTestData.ReadCodeAsync(response, ct));
    }

    [Fact]
    public async Task Duplicate_recipient_property_returns_400()
    {
        var ct = TestContext.Current.CancellationToken;
        using var client = CreateAuthorizedClient();
        using var content = RawJsonContent(
            $$"""
            {
              "tenant_id": "{{MailerWebApplicationFixtureBase.TenantId}}",
              "source_service": "{{MailerWebApplicationFixtureBase.SourceService}}",
              "mail_request_id": "{{Guid.NewGuid()}}",
              "purpose": "FormResponseNotification",
              "to": [{ "email": "recipient@example.com", "email": "tamper@example.com" }],
              "subject": "Subject",
              "text_body": "Body",
              "payload_hash": "{{new string('0', 64)}}"
            }
            """);

        using var response = await client.PostAsync("/internal/mail-requests", content, ct);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(
            MailerErrorCodes.InvalidRequest,
            await MailRequestTestData.ReadCodeAsync(response, ct));
    }

    [Fact]
    public async Task Duplicate_metadata_property_returns_400()
    {
        var ct = TestContext.Current.CancellationToken;
        using var client = CreateAuthorizedClient();
        using var content = RawJsonContent(
            $$"""
            {
              "tenant_id": "{{MailerWebApplicationFixtureBase.TenantId}}",
              "source_service": "{{MailerWebApplicationFixtureBase.SourceService}}",
              "mail_request_id": "{{Guid.NewGuid()}}",
              "purpose": "FormResponseNotification",
              "to": [{ "email": "recipient@example.com" }],
              "subject": "Subject",
              "text_body": "Body",
              "metadata": { "form_id": "1", "form_id": "2" },
              "payload_hash": "{{new string('0', 64)}}"
            }
            """);

        using var response = await client.PostAsync("/internal/mail-requests", content, ct);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(
            MailerErrorCodes.InvalidRequest,
            await MailRequestTestData.ReadCodeAsync(response, ct));
    }

    [Fact]
    public async Task Distinct_metadata_keys_are_accepted()
    {
        var ct = TestContext.Current.CancellationToken;
        using var client = CreateAuthorizedClient();
        var request = MailRequestTestData.CreateRequest(metadata: new Dictionary<string, string>
        {
            ["form_id"] = "42",
            ["campaign"] = "spring",
        });

        using var response = await client.PostAsync(
            "/internal/mail-requests",
            MailRequestTestData.ToJsonContent(request),
            ct);

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        Assert.Equal(
            MailRequestAcceptanceStatus.Accepted,
            await MailRequestTestData.ReadStatusAsync(response, ct));
    }

    [Fact]
    public async Task Structural_rejection_precedes_authorization()
    {
        var ct = TestContext.Current.CancellationToken;
        using var client = CreateClient(token: "wrong-token");
        using var content = RawJsonContent(
            $$"""
            {
              "tenant_id": "{{MailerWebApplicationFixtureBase.TenantId}}",
              "source_service": "{{MailerWebApplicationFixtureBase.SourceService}}",
              "mail_request_id": "{{Guid.NewGuid()}}",
              "purpose": "FormResponseNotification",
              "to": [{ "email": "recipient@example.com" }],
              "subject": "Subject",
              "subject": "Tampered subject",
              "text_body": "Body",
              "payload_hash": "{{new string('0', 64)}}"
            }
            """);

        using var response = await client.PostAsync("/internal/mail-requests", content, ct);

        // A malformed body is rejected as 400 before tenant authorization (401) is evaluated,
        // matching the existing invalid-JSON path.
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(
            MailerErrorCodes.InvalidRequest,
            await MailRequestTestData.ReadCodeAsync(response, ct));
    }

    [Fact]
    public async Task Unknown_property_rejection_precedes_authorization()
    {
        var ct = TestContext.Current.CancellationToken;
        using var client = CreateClient(token: "wrong-token");
        using var content = RawJsonContent(
            $$"""
            {
              "tenant_id": "{{MailerWebApplicationFixtureBase.TenantId}}",
              "source_service": "{{MailerWebApplicationFixtureBase.SourceService}}",
              "mail_request_id": "{{Guid.NewGuid()}}",
              "purpose": "FormResponseNotification",
              "to": [{ "email": "recipient@example.com" }],
              "subject": "Subject",
              "text_body": "Body",
              "payload_hash": "{{new string('0', 64)}}",
              "unexpected": "value"
            }
            """);

        using var response = await client.PostAsync("/internal/mail-requests", content, ct);

        // Unknown properties throw during deserialize, before authorization is evaluated.
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
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
    public async Task Oversized_request_body_without_content_length_returns_413()
    {
        var ct = TestContext.Current.CancellationToken;
        using var client = CreateAuthorizedClient();
        using var content = new UnknownLengthStringContent(
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
    public async Task Japanese_utf8_request_is_accepted()
    {
        var ct = TestContext.Current.CancellationToken;
        using var client = CreateAuthorizedClient();
        var request = MailRequestTestData.CreateRequest(
            subject: "フォーム回答を受け付けました",
            replyTo: null);

        using var response = await client.PostAsync(
            "/internal/mail-requests",
            MailRequestTestData.ToJsonContent(request),
            ct);

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        Assert.Equal(
            MailRequestAcceptanceStatus.Accepted,
            await MailRequestTestData.ReadStatusAsync(response, ct));
    }

    [Fact]
    public async Task Invalid_utf8_in_subject_returns_400_without_persisting()
    {
        var ct = TestContext.Current.CancellationToken;
        using var client = CreateAuthorizedClient();
        var mailRequestId = Guid.NewGuid();
        // Lone continuation byte inside the subject string value.
        using var content = RawBytesContent(BuildCreateRequestBytes(
            mailRequestId,
            subjectPrefix: "bad",
            subjectInvalidUtf8: [0x80],
            subjectSuffix: string.Empty,
            textBody: "Body",
            payloadHash: new string('0', 64)));

        using var response = await client.PostAsync("/internal/mail-requests", content, ct);

        await AssertInvalidUtf8RejectedAsync(response, mailRequestId, ct);
    }

    [Fact]
    public async Task Incomplete_utf8_sequence_in_body_returns_400()
    {
        var ct = TestContext.Current.CancellationToken;
        using var client = CreateAuthorizedClient();
        var mailRequestId = Guid.NewGuid();
        // Leading byte of a 3-byte sequence without continuations.
        using var content = RawBytesContent(BuildCreateRequestBytes(
            mailRequestId,
            subjectPrefix: "Subject",
            subjectInvalidUtf8: [],
            subjectSuffix: string.Empty,
            textBodyPrefix: "hello",
            textBodyInvalidUtf8: [0xE3],
            textBodySuffix: string.Empty,
            payloadHash: new string('0', 64)));

        using var response = await client.PostAsync("/internal/mail-requests", content, ct);

        await AssertInvalidUtf8RejectedAsync(response, mailRequestId, ct);
    }

    [Fact]
    public async Task Invalid_utf8_outside_json_structure_returns_400()
    {
        var ct = TestContext.Current.CancellationToken;
        using var client = CreateAuthorizedClient();
        var mailRequestId = Guid.NewGuid();
        var json = $$"""
            {"tenant_id":"{{MailerWebApplicationFixtureBase.TenantId}}","source_service":"{{MailerWebApplicationFixtureBase.SourceService}}","mail_request_id":"{{mailRequestId}}","purpose":"FormResponseNotification","to":[{"email":"recipient@example.com"}],"subject":"Subject","text_body":"Body","payload_hash":"{{new string('0', 64)}}"}
            """;
        var bytes = Encoding.UTF8.GetBytes(json);
        // Insert an invalid byte immediately after '{' (outside any string value).
        var mutated = new byte[bytes.Length + 1];
        mutated[0] = bytes[0];
        mutated[1] = 0xFF;
        Buffer.BlockCopy(bytes, 1, mutated, 2, bytes.Length - 1);
        using var content = RawBytesContent(mutated);

        using var response = await client.PostAsync("/internal/mail-requests", content, ct);

        await AssertInvalidUtf8RejectedAsync(response, mailRequestId, ct);
    }

    [Fact]
    public async Task Invalid_utf8_is_rejected_before_payload_hash_validation()
    {
        var ct = TestContext.Current.CancellationToken;
        using var client = CreateAuthorizedClient();
        var mailRequestId = Guid.NewGuid();
        // Plausible 64-char hex hash must not turn this into INVALID_PAYLOAD_HASH (422).
        using var content = RawBytesContent(BuildCreateRequestBytes(
            mailRequestId,
            subjectPrefix: "x",
            subjectInvalidUtf8: [0xC0, 0xAF], // overlong encoding of '/'
            subjectSuffix: string.Empty,
            textBody: "Body",
            payloadHash: "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"));

        using var response = await client.PostAsync("/internal/mail-requests", content, ct);

        await AssertInvalidUtf8RejectedAsync(response, mailRequestId, ct);
    }

    [Fact]
    public async Task Max_sized_invalid_utf8_body_returns_400()
    {
        var ct = TestContext.Current.CancellationToken;
        using var client = CreateAuthorizedClient();
        var bytes = new byte[256_000];
        Array.Fill(bytes, (byte)0xFF);
        using var content = RawBytesContent(bytes);

        using var response = await client.PostAsync("/internal/mail-requests", content, ct);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(
            MailerErrorCodes.InvalidRequest,
            await MailRequestTestData.ReadCodeAsync(response, ct));
        Assert.Equal(
            "Request body is not valid UTF-8.",
            await MailRequestTestData.ReadMessageAsync(response, ct));
        var responseText = await response.Content.ReadAsStringAsync(ct);
        Assert.DoesNotContain('\uFFFD', responseText);
        Assert.DoesNotContain("0xFF", responseText, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Oversized_body_with_invalid_utf8_still_returns_413()
    {
        var ct = TestContext.Current.CancellationToken;
        using var client = CreateAuthorizedClient();
        var bytes = new byte[256_001];
        Array.Fill(bytes, (byte)0xFF);
        using var content = RawBytesContent(bytes);

        using var response = await client.PostAsync("/internal/mail-requests", content, ct);

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
    public async Task Sqlite_full_returns_503_storage_full_not_retryable()
    {
        var ct = TestContext.Current.CancellationToken;
        var root = Path.Combine(Path.GetTempPath(), "amane-mailer-storage-full-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var databasePath = Path.Combine(root, "mailer.db");

        try
        {
            var tenantConfigPath = Path.Combine(root, "tenants.json");
            await File.WriteAllTextAsync(tenantConfigPath, TenantConfigJson, ct);

            using var storageFullFactory = new WebApplicationFactory<global::Program>()
                .WithWebHostBuilder(builder =>
                {
                    builder.UseEnvironment("Testing");
                    builder.ConfigureAppConfiguration((_, configuration) =>
                        configuration.AddInMemoryCollection(new Dictionary<string, string?>
                        {
                            ["ConnectionStrings:Mailer"] = $"Data Source={databasePath}",
                            ["MAILER_TENANTS_PATH"] = tenantConfigPath,
                            ["Mailer:Worker:Enabled"] = "False",
                            ["MAIL_SERVICE_TOKEN"] = MailerWebApplicationFixtureBase.Token,
                        }));
                    builder.ConfigureServices(services =>
                    {
                        services.RemoveAll<IHostedService>();
                        services.RemoveAll<MailRequestRepository>();
                        services.AddSingleton<MailRequestRepository>(sp =>
                            new StorageFullMailRequestRepository(
                                sp.GetRequiredService<MailRequestClaimStore>(),
                                sp.GetRequiredService<MailRequestAcceptStore>(),
                                sp.GetRequiredService<MailRequestConsumerMutations>(),
                                sp.GetRequiredService<MailRequestAdminQueries>(),
                                sp.GetRequiredService<WorkerHeartbeatStore>()));
                    });
                });

            using var client = storageFullFactory.CreateClient(new WebApplicationFactoryClientOptions
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
                MailerErrorCodes.StorageFull,
                await MailRequestTestData.ReadCodeAsync(response, ct));
            Assert.False(await MailRequestTestData.ReadRetryableAsync(response, ct));
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

    private static StringContent RawJsonContent(string json) =>
        new(json, Encoding.UTF8, "application/json");

    private static ByteArrayContent RawBytesContent(byte[] bytes)
    {
        var content = new ByteArrayContent(bytes);
        content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/json");
        return content;
    }

    private static byte[] BuildCreateRequestBytes(
        Guid mailRequestId,
        string subjectPrefix,
        byte[] subjectInvalidUtf8,
        string subjectSuffix,
        string textBody,
        string payloadHash) =>
        BuildCreateRequestBytes(
            mailRequestId,
            subjectPrefix,
            subjectInvalidUtf8,
            subjectSuffix,
            textBodyPrefix: textBody,
            textBodyInvalidUtf8: [],
            textBodySuffix: string.Empty,
            payloadHash);

    private static byte[] BuildCreateRequestBytes(
        Guid mailRequestId,
        string subjectPrefix,
        byte[] subjectInvalidUtf8,
        string subjectSuffix,
        string textBodyPrefix,
        byte[] textBodyInvalidUtf8,
        string textBodySuffix,
        string payloadHash)
    {
        var prefix = Encoding.UTF8.GetBytes(
            $$"""
            {"tenant_id":"{{MailerWebApplicationFixtureBase.TenantId}}","source_service":"{{MailerWebApplicationFixtureBase.SourceService}}","mail_request_id":"{{mailRequestId:D}}","purpose":"FormResponseNotification","to":[{"email":"recipient@example.com"}],"subject":"{{subjectPrefix}}
            """);
        var afterSubject = Encoding.UTF8.GetBytes(
            $$"""
            {{subjectSuffix}}","text_body":"{{textBodyPrefix}}
            """);
        var afterBody = Encoding.UTF8.GetBytes(
            $$"""
            {{textBodySuffix}}","payload_hash":"{{payloadHash}}"}
            """);

        return ConcatBytes(prefix, subjectInvalidUtf8, afterSubject, textBodyInvalidUtf8, afterBody);
    }

    private static byte[] ConcatBytes(params byte[][] parts)
    {
        var total = 0;
        foreach (var part in parts)
        {
            total += part.Length;
        }

        var result = new byte[total];
        var offset = 0;
        foreach (var part in parts)
        {
            Buffer.BlockCopy(part, 0, result, offset, part.Length);
            offset += part.Length;
        }

        return result;
    }

    private async Task AssertInvalidUtf8RejectedAsync(
        HttpResponseMessage response,
        Guid mailRequestId,
        CancellationToken cancellationToken)
    {
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(
            MailerErrorCodes.InvalidRequest,
            await MailRequestTestData.ReadCodeAsync(response, cancellationToken));
        Assert.Equal(
            "Request body is not valid UTF-8.",
            await MailRequestTestData.ReadMessageAsync(response, cancellationToken));

        var responseText = await response.Content.ReadAsStringAsync(cancellationToken);
        Assert.DoesNotContain('\uFFFD', responseText);
        Assert.DoesNotContain("0x80", responseText, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("0xFF", responseText, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("0xE3", responseText, StringComparison.OrdinalIgnoreCase);

        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var repository = scope.ServiceProvider.GetRequiredService<MailRequestRepository>();
        var stored = await repository.FindByIdempotencyKeyAsync(
            MailerWebApplicationFixtureBase.TenantId,
            MailerWebApplicationFixtureBase.SourceService,
            mailRequestId,
            cancellationToken);
        Assert.Null(stored);
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

    private sealed class UnknownLengthStringContent(
        string content,
        Encoding encoding,
        string mediaType) : StringContent(content, encoding, mediaType)
    {
        protected override bool TryComputeLength(out long length)
        {
            length = 0;
            return false;
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

    private sealed class StorageFullMailRequestRepository(
        MailRequestClaimStore claimStore,
        MailRequestAcceptStore acceptStore,
        MailRequestConsumerMutations consumerMutations,
        MailRequestAdminQueries adminQueries,
        WorkerHeartbeatStore heartbeatStore)
        : MailRequestRepository(claimStore, acceptStore, consumerMutations, adminQueries, heartbeatStore)
    {
        public override Task<MailRequestIdempotencyRow?> FindByIdempotencyKeyAsync(
            Guid tenantId,
            string sourceService,
            Guid mailRequestId,
            CancellationToken cancellationToken = default) =>
            throw new SqliteException(
                "database or disk is full",
                SqliteDatabaseExceptionClassifier.SqliteFull);

        public override Task InsertAcceptedAsync(
            AcceptedMailRequestInsert insert,
            CancellationToken cancellationToken = default) =>
            throw new SqliteException(
                "database or disk is full",
                SqliteDatabaseExceptionClassifier.SqliteFull);
    }
}
