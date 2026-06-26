using System.Text.Json.Serialization;

namespace Amane.Mailer.Contracts.MailRequests;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record MailRecipientDto
{
    [JsonPropertyName("email")]
    public required string Email { get; init; }

    [JsonPropertyName("display_name")]
    public string? DisplayName { get; init; }
}
