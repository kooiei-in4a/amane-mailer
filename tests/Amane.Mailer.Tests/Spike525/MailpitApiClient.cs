using System.Net.Http.Json;
using System.Text.Json.Serialization;

namespace Amane.Mailer.Tests.Spike525;

/// <summary>
/// Spike-only (#525) minimal client for Mailpit's HTTP API. IMPORTANT: this API is proven
/// (see Spike525 evidence for S-02) to reconstruct/augment messages rather than exposing
/// literal wire bytes (it synthesizes Received/Message-ID/Return-Path headers, and derives
/// a "Bcc" field from SMTP-envelope-minus-headers even when no Bcc header was ever sent).
/// Never use this client alone as evidence of literal wire/MIME header content — cross-check
/// against <see cref="Spike525SmtpRelay"/> wire captures for that.
/// </summary>
internal sealed class MailpitApiClient(HttpClient httpClient)
{
    internal async Task<MailpitMessageSummary[]> ListMessagesAsync(CancellationToken cancellationToken)
    {
        var page = await httpClient.GetFromJsonAsync<MailpitMessageListResponse>(
            "/api/v1/messages", cancellationToken);
        return page?.Messages ?? [];
    }

    internal async Task<MailpitMessageDetail?> GetMessageAsync(string id, CancellationToken cancellationToken) =>
        await httpClient.GetFromJsonAsync<MailpitMessageDetail>($"/api/v1/message/{id}", cancellationToken);

    internal async Task<string> GetRawAsync(string id, CancellationToken cancellationToken)
    {
        var response = await httpClient.GetAsync($"/api/v1/message/{id}/raw", cancellationToken);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsStringAsync(cancellationToken);
    }
}

internal sealed record MailpitMessageListResponse(
    [property: JsonPropertyName("messages")] MailpitMessageSummary[] Messages);

internal sealed record MailpitMessageSummary(
    [property: JsonPropertyName("ID")] string Id,
    [property: JsonPropertyName("MessageID")] string MessageId,
    [property: JsonPropertyName("Subject")] string Subject,
    [property: JsonPropertyName("To")] MailpitAddress[] To,
    [property: JsonPropertyName("Cc")] MailpitAddress[] Cc,
    [property: JsonPropertyName("Bcc")] MailpitAddress[] Bcc,
    [property: JsonPropertyName("Attachments")] int AttachmentCount);

internal sealed record MailpitMessageDetail(
    [property: JsonPropertyName("ID")] string Id,
    [property: JsonPropertyName("MessageID")] string MessageId,
    [property: JsonPropertyName("Subject")] string Subject,
    [property: JsonPropertyName("To")] MailpitAddress[] To,
    [property: JsonPropertyName("Cc")] MailpitAddress[] Cc,
    [property: JsonPropertyName("Bcc")] MailpitAddress[] Bcc,
    [property: JsonPropertyName("Attachments")] MailpitAttachment[] Attachments);

internal sealed record MailpitAddress(
    [property: JsonPropertyName("Name")] string Name,
    [property: JsonPropertyName("Address")] string Address);

internal sealed record MailpitAttachment(
    [property: JsonPropertyName("PartID")] string PartId,
    [property: JsonPropertyName("FileName")] string FileName,
    [property: JsonPropertyName("ContentType")] string ContentType,
    [property: JsonPropertyName("Size")] long Size,
    [property: JsonPropertyName("Checksums")] MailpitChecksums Checksums);

internal sealed record MailpitChecksums(
    [property: JsonPropertyName("SHA256")] string Sha256);
