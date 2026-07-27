using System.Text.Json;
using Amane.Mailer.Bounce;
using Amane.Mailer.Json;

namespace Amane.Mailer.Operations.VerifyDeliveryReport;

/// <summary>
/// Verification-oriented Delivery Report inspector for #428.
/// Unlike <see cref="AcsEventParser"/>, this keeps <c>Delivered</c> statuses so normal E2E
/// wiring checks can succeed. Never returns recipient or provider statusMessage.
/// </summary>
public static class DeliveryReportEventInspector
{
    public static IReadOnlyList<DeliveryReportPeekObservation> InspectBody(string body)
    {
        if (string.IsNullOrWhiteSpace(body))
        {
            return [DeliveryReportPeekObservation.Malformed()];
        }

        string decoded;
        try
        {
            decoded = AcsQueueMessageBodyDecoder.Decode(body);
        }
        catch
        {
            return [DeliveryReportPeekObservation.Malformed()];
        }

        try
        {
            using var document = JsonDocument.Parse(decoded);
            if (document.RootElement.ValueKind == JsonValueKind.Array)
            {
                if (document.RootElement.GetArrayLength() == 0)
                {
                    return [DeliveryReportPeekObservation.Ignored()];
                }

                var results = new List<DeliveryReportPeekObservation>(document.RootElement.GetArrayLength());
                foreach (var element in document.RootElement.EnumerateArray())
                {
                    results.Add(InspectElement(element));
                }

                return results;
            }

            return [InspectElement(document.RootElement)];
        }
        catch (JsonException)
        {
            return [DeliveryReportPeekObservation.Malformed()];
        }
    }

    private static DeliveryReportPeekObservation InspectElement(JsonElement element)
    {
        AcsEventGridEventDto? dto;
        try
        {
            dto = element.Deserialize(MailerJsonContext.Default.AcsEventGridEventDto);
        }
        catch (JsonException)
        {
            return DeliveryReportPeekObservation.Malformed();
        }

        if (dto is null)
        {
            return DeliveryReportPeekObservation.Malformed();
        }

        if (!string.Equals(
                dto.EventType,
                AcsEventParser.EmailDeliveryReportReceivedEventType,
                StringComparison.Ordinal))
        {
            return DeliveryReportPeekObservation.Ignored();
        }

        var data = dto.Data;
        if (data is null
            || string.IsNullOrWhiteSpace(data.MessageId)
            || string.IsNullOrWhiteSpace(data.Status))
        {
            return DeliveryReportPeekObservation.Malformed();
        }

        // Exact match correlation (ADR 0020 D-03 / F-1). Do not normalize.
        return DeliveryReportPeekObservation.DeliveryReport(
            data.MessageId.Trim(),
            data.Status.Trim());
    }
}

/// <summary>
/// Safe observation extracted from a peeked queue body. Omits recipient and statusMessage.
/// </summary>
public sealed class DeliveryReportPeekObservation
{
    public required DeliveryReportPeekKind Kind { get; init; }

    public string? MessageId { get; init; }

    public string? Status { get; init; }

    public static DeliveryReportPeekObservation DeliveryReport(string messageId, string status) =>
        new()
        {
            Kind = DeliveryReportPeekKind.DeliveryReport,
            MessageId = messageId,
            Status = status,
        };

    public static DeliveryReportPeekObservation Ignored() =>
        new() { Kind = DeliveryReportPeekKind.Ignored };

    public static DeliveryReportPeekObservation Malformed() =>
        new() { Kind = DeliveryReportPeekKind.Malformed };
}

public enum DeliveryReportPeekKind
{
    DeliveryReport,
    Ignored,
    Malformed,
}
