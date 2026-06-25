using System.Text.Json.Serialization;

namespace Amane.Mailer.Contracts.MailRequests;

public sealed record MailRequestCreateResponse
{
    [JsonPropertyName("mail_request_id")]
    public required Guid MailRequestId { get; init; }

    /// <summary>
    /// API acceptance status. Use values from <see cref="MailRequestAcceptanceStatus"/>, not worker delivery status.
    /// </summary>
    [JsonPropertyName("status")]
    public required string Status { get; init; }
}
