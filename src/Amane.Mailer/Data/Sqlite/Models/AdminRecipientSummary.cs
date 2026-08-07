using Amane.Mailer.Contracts.MailRequests;

namespace Amane.Mailer.Data.Sqlite.Models;

/// <summary>
/// Recipient data safe for normal Admin rendering. BCC address and display name are deliberately
/// omitted at the query boundary; raw BCC is available only through the dedicated reveal path.
/// </summary>
public sealed record AdminRecipientSummary(
    MailRecipientRole Role,
    int Ordinal,
    string? Address,
    string? DisplayName,
    MailRecipientDeliveryState DeliveryState);
