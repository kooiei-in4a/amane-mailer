namespace Amane.Mailer.Contracts.MailRequests;

/// <summary>
/// Recipient role for the canonical recipient set (ADR 0023 D-01).
/// </summary>
public enum MailRecipientRole
{
    To,
    Cc,
    Bcc,
}
