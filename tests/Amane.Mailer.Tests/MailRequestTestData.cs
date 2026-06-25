using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Amane.Mailer.Contracts.MailRequests;
using Amane.Mailer.Contracts.Security;

namespace Amane.Mailer.Tests;

internal static class MailRequestTestData
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public static MailRequestCreateRequest CreateRequest(
        Guid? mailRequestId = null,
        string sourceService = Fixtures.MailerWebApplicationFixtureBase.SourceService,
        string subject = "Form response received",
        string? replyTo = null,
        IReadOnlyDictionary<string, string>? metadata = null)
    {
        var request = new MailRequestCreateRequest
        {
            TenantId = Fixtures.MailerWebApplicationFixtureBase.TenantId,
            SourceService = sourceService,
            MailRequestId = mailRequestId ?? Guid.NewGuid(),
            Purpose = "FormResponseNotification",
            To =
            [
                new MailRecipientDto
                {
                    Email = "recipient@example.com",
                    DisplayName = "Recipient",
                },
            ],
            Subject = subject,
            HtmlBody = "<p>Hello from Mailer tests</p>",
            TextBody = "Hello from Mailer tests",
            ReplyTo = replyTo,
            Metadata = metadata ?? new Dictionary<string, string>
            {
                ["form_id"] = "form-123",
            },
            PayloadHash = new string('0', 64),
        };

        return request with
        {
            PayloadHash = MailPayloadHasher.ComputeDeliveryPayloadSha256Hex(request),
        };
    }

    public static JsonContent ToJsonContent(MailRequestCreateRequest request) =>
        JsonContent.Create(request, options: JsonOptions);

    public static async Task<string?> ReadCodeAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        using var doc = JsonDocument.Parse(body);
        return doc.RootElement.GetProperty("code").GetString();
    }

    public static async Task<string?> ReadStatusAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        using var doc = JsonDocument.Parse(body);
        return doc.RootElement.GetProperty("status").GetString();
    }

    public static async Task<bool> ReadRetryableAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        using var doc = JsonDocument.Parse(body);
        return doc.RootElement.GetProperty("retryable").GetBoolean();
    }
}
