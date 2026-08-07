using Amane.Mailer.Contracts.MailRequests;

namespace Amane.Mailer.Data.Sqlite.Models;

/// <summary>
/// A canonical recipient row read from <c>mail_request_recipients</c> (ADR 0023 D-03), the sole
/// source of truth for provider dispatch (D-10). Never derived from the legacy
/// <c>mail_requests.recipient_email</c>/<c>recipient_display_name</c> shadow.
/// </summary>
public sealed record MailRequestRecipientRow(
    MailRecipientRole Role,
    int Ordinal,
    string Address,
    string AddressKey,
    string? DisplayName,
    MailRecipientDeliveryState DeliveryState);
