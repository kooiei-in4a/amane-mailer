using System.Net;
using System.Text;
using Amane.Mailer.Api;
using Amane.Mailer.Data.Sqlite;
using Amane.Mailer.Data.Sqlite.Models;
using Amane.Mailer.Tests.Fixtures;
using Amane.Mailer.Contracts.MailRequests;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace Amane.Mailer.Tests;

/// <summary>
/// Regression suite for #343 / #355 — raw HTTP bytes / strict UTF-8 request body rejection.
/// </summary>
[Collection(MailerTestCollection.Name)]
public sealed class HttpEncodingTests(MailerApiFixture fixture)
    : IClassFixture<MailerApiFixture>, IAsyncLifetime
{
    public async ValueTask InitializeAsync() =>
        await fixture.ResetAsync(TestContext.Current.CancellationToken);

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    [Fact]
    public async Task Japanese_utf8_request_is_accepted()
    {
        var ct = TestContext.Current.CancellationToken;
        using var client = CreateAuthorizedClient();
        var request = MailRequestTestData.CreateRequest(
            subject: "フォーム回答を受け付けました",
            replyTo: null);

        using var response = await client.PostAsync(
            "/api/mail-requests",
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

        using var response = await client.PostAsync("/api/mail-requests", content, ct);

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

        using var response = await client.PostAsync("/api/mail-requests", content, ct);

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

        using var response = await client.PostAsync("/api/mail-requests", content, ct);

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

        using var response = await client.PostAsync("/api/mail-requests", content, ct);

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

        using var response = await client.PostAsync("/api/mail-requests", content, ct);

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
        // /api/mail-requests accepts attachments (ADR 0022 D-02), so its cap is
        // MailAttachmentLimits.MaxConsumerHttpEnvelopeBytes (16 MiB), not the base 256,000 bytes.
        var ct = TestContext.Current.CancellationToken;
        using var client = CreateAuthorizedClient();
        var bytes = new byte[16 * 1024 * 1024 + 1];
        Array.Fill(bytes, (byte)0xFF);
        using var content = RawBytesContent(bytes);

        using var response = await client.PostAsync("/api/mail-requests", content, ct);

        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
        Assert.Equal(
            MailerErrorCodes.RequestTooLarge,
            await MailRequestTestData.ReadCodeAsync(response, ct));
    }

    [Fact]
    public async Task Reschedule_with_invalid_utf8_returns_400()
    {
        var ct = TestContext.Current.CancellationToken;
        using var client = CreateAuthorizedClient();
        var request = MailRequestTestData.CreateRequest(
            scheduledAt: DateTimeOffset.UtcNow.AddHours(2));

        using var post = await client.PostAsync(
            "/api/mail-requests",
            MailRequestTestData.ToJsonContent(request),
            ct);
        Assert.Equal(HttpStatusCode.Accepted, post.StatusCode);

        // Invalid byte outside the JSON string value: {"scheduled_at":null} with 0xFF after '{'.
        var valid = Encoding.UTF8.GetBytes("""{"scheduled_at":null}""");
        var mutated = new byte[valid.Length + 1];
        mutated[0] = valid[0];
        mutated[1] = 0xFF;
        Buffer.BlockCopy(valid, 1, mutated, 2, valid.Length - 1);
        using var content = new ByteArrayContent(mutated);
        content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/json");

        using var reschedule = await client.PostAsync(RescheduleUrl(request.MailRequestId), content, ct);

        Assert.Equal(HttpStatusCode.BadRequest, reschedule.StatusCode);
        Assert.Equal(
            MailerErrorCodes.InvalidRequest,
            await MailRequestTestData.ReadCodeAsync(reschedule, ct));
        Assert.Equal(
            "Request body is not valid UTF-8.",
            await MailRequestTestData.ReadMessageAsync(reschedule, ct));
        var responseText = await reschedule.Content.ReadAsStringAsync(ct);
        Assert.DoesNotContain('\uFFFD', responseText);
        Assert.DoesNotContain("0xFF", responseText, StringComparison.OrdinalIgnoreCase);

        // Original scheduled request remains unchanged (not cancelled / not cleared).
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var repository = scope.ServiceProvider.GetRequiredService<MailRequestRepository>();
        var stored = await repository.FindByIdempotencyKeyAsync(
            MailerWebApplicationFixtureBase.TenantId,
            MailerWebApplicationFixtureBase.SourceService,
            request.MailRequestId,
            ct);
        Assert.NotNull(stored);
        Assert.Equal(MailRequestState.Queued, stored.Status);
        Assert.NotNull(stored.ScheduledAt);
    }

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

    private static string RescheduleUrl(Guid mailRequestId) =>
        $"/api/mail-requests/{mailRequestId:D}/reschedule" +
        $"?tenant_id={MailerWebApplicationFixtureBase.TenantId:D}" +
        $"&source_service={MailerWebApplicationFixtureBase.SourceService}";

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
}
