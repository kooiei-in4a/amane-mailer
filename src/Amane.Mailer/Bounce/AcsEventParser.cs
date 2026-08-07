using System.Text.Json;
using Amane.Mailer.Json;

namespace Amane.Mailer.Bounce;

/// <summary>
/// AOT-safe ACS Event Grid delivery-report parser (issue #302 / ADR 0020).
/// Keeps every well-formed <c>EmailDeliveryReportReceived</c> status for recipient correlation.
/// </summary>
public static class AcsEventParser
{
    public const string EmailDeliveryReportReceivedEventType =
        "Microsoft.Communication.EmailDeliveryReportReceived";

    public static AcsEventParseResult ParseOne(string json)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(json);

        try
        {
            using var document = JsonDocument.Parse(json);
            return ParseElement(document.RootElement);
        }
        catch (JsonException)
        {
            return Unparseable();
        }
    }

    public static IReadOnlyList<AcsEventParseResult> ParseMany(string json)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(json);

        try
        {
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind == JsonValueKind.Array)
            {
                if (document.RootElement.GetArrayLength() == 0)
                {
                    return [Ignored()];
                }

                var results = new List<AcsEventParseResult>(document.RootElement.GetArrayLength());
                foreach (var element in document.RootElement.EnumerateArray())
                {
                    results.Add(ParseElement(element));
                }

                return results;
            }

            return [ParseElement(document.RootElement)];
        }
        catch (JsonException)
        {
            return [Unparseable()];
        }
    }

    private static AcsEventParseResult ParseElement(JsonElement element)
    {
        AcsEventGridEventDto? dto;
        try
        {
            dto = element.Deserialize(MailerJsonContext.Default.AcsEventGridEventDto);
        }
        catch (JsonException)
        {
            return Unparseable();
        }

        if (dto is null)
        {
            return Unparseable();
        }

        if (!string.Equals(dto.EventType, EmailDeliveryReportReceivedEventType, StringComparison.Ordinal))
        {
            return Ignored();
        }

        var data = dto.Data;
        if (data is null
            || string.IsNullOrWhiteSpace(dto.Id)
            || string.IsNullOrWhiteSpace(data.MessageId)
            || string.IsNullOrWhiteSpace(data.Status))
        {
            return Unparseable();
        }

        DateTimeOffset? occurredAt = dto.EventTime == default ? null : dto.EventTime;

        return new AcsEventParseResult
        {
            Outcome = AcsEventParseOutcome.DeliveryReport,
            Report = new ProviderDeliveryReport
            {
                EventId = dto.Id,
                MessageId = data.MessageId,
                Status = data.Status,
                Recipient = data.Recipient,
                StatusMessage = data.DeliveryStatusDetails?.StatusMessage,
                OccurredAt = occurredAt,
            },
        };
    }

    private static AcsEventParseResult Ignored() => new()
    {
        Outcome = AcsEventParseOutcome.Ignored,
    };

    private static AcsEventParseResult Unparseable() => new()
    {
        Outcome = AcsEventParseOutcome.Unparseable,
    };
}
