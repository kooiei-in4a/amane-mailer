using System.Text.Json.Serialization;

namespace Amane.Mailer.Contracts.MailRequests;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record MailRequestRescheduleRequest
{
    /// <summary>
    /// New first-dispatch time (UTC). Null clears the schedule gate (immediate when otherwise due).
    /// </summary>
    [JsonPropertyName("scheduled_at")]
    public DateTimeOffset? ScheduledAt { get; init; }
}
