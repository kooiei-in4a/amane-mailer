using System.Text;
using System.Text.Json;
using Amane.Mailer.Api;
using Amane.Mailer.Contracts.MailRequests;
using Amane.Mailer.Json;
using Microsoft.AspNetCore.Http;

namespace Amane.Mailer.Tests.Api;

public sealed class MailRequestRequestReaderTests
{
    [Fact]
    public async Task ReadAsync_returns_too_large_when_body_exceeds_limit()
    {
        var oversized = new string('a', MailRequestRequestReader.MaxRequestBodyBytes + 1);
        var context = CreateHttpContext(Encoding.UTF8.GetBytes(oversized));

        var result = await MailRequestRequestReader.ReadAsync(context.Request, CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Equal(MailRequestBodyReadFailure.TooLarge, result.Failure);
    }

    [Fact]
    public async Task ReadAsync_rejects_invalid_utf8()
    {
        var context = CreateHttpContext([0x80, 0x81, 0x82]);

        var result = await MailRequestRequestReader.ReadAsync(context.Request, CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Equal(MailRequestBodyReadFailure.InvalidUtf8, result.Failure);
    }

    [Fact]
    public async Task ReadAsync_returns_body_for_valid_utf8()
    {
        var context = CreateHttpContext(Encoding.UTF8.GetBytes("""{"ok":true}"""));

        var result = await MailRequestRequestReader.ReadAsync(context.Request, CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.Equal("""{"ok":true}""", result.Body);
    }

    [Fact]
    public void IsContentLengthTooLarge_detects_declared_oversize()
    {
        var context = new DefaultHttpContext();
        context.Request.ContentLength = MailRequestRequestReader.MaxRequestBodyBytes + 1;

        Assert.True(MailRequestRequestReader.IsContentLengthTooLarge(context.Request));
    }

    [Fact]
    public void DeserializeStrict_rejects_duplicate_json_property()
    {
        var json = """
            {
              "tenant_id": "00000000-0000-0000-0000-000000000101",
              "tenant_id": "00000000-0000-0000-0000-000000000102",
              "source_service": "example-service",
              "mail_request_id": "00000000-0000-0000-0000-000000000201",
              "purpose": "FormResponseNotification",
              "to": [{ "email": "a@example.com" }],
              "subject": "s",
              "text_body": "t",
              "payload_hash": "0000000000000000000000000000000000000000000000000000000000000000"
            }
            """;

        var result = MailRequestRequestReader.DeserializeStrict(
            json,
            MailerJsonContext.Default.MailRequestCreateRequest);

        Assert.False(result.Succeeded);
        Assert.Equal(MailRequestJsonReadFailure.DuplicateProperty, result.Failure);
    }

    [Fact]
    public void DeserializeStrict_rejects_invalid_json()
    {
        var result = MailRequestRequestReader.DeserializeStrict(
            "{not-json",
            MailerJsonContext.Default.MailRequestCreateRequest);

        Assert.False(result.Succeeded);
        Assert.Equal(MailRequestJsonReadFailure.InvalidJson, result.Failure);
    }

    [Fact]
    public void DeserializeStrict_accepts_valid_create_request()
    {
        var request = MailRequestTestData.CreateRequest();
        var json = JsonSerializer.Serialize(request, MailerJsonContext.Default.MailRequestCreateRequest);

        var result = MailRequestRequestReader.DeserializeStrict(
            json,
            MailerJsonContext.Default.MailRequestCreateRequest);

        Assert.True(result.Succeeded);
        Assert.Equal(request.MailRequestId, result.Value!.MailRequestId);
    }

    private static DefaultHttpContext CreateHttpContext(byte[] body)
    {
        var context = new DefaultHttpContext();
        context.Request.Body = new MemoryStream(body);
        context.Request.ContentLength = body.Length;
        return context;
    }
}
