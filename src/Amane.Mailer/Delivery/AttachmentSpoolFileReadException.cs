namespace Amane.Mailer.Delivery;

/// <summary>
/// Thrown by an outbound message factory when an attachment's spool file could not be read
/// (missing, permission denied, or otherwise inaccessible) at dispatch time. Deliberately
/// carries no message with the underlying exception text, since that text embeds the private
/// spool path (ADR 0022 D-08/D-14) -- delivery providers must catch this specifically, before
/// their generic provider-exception handler, and map it to the fixed
/// <c>MailDeliveryErrorCodes.AttachmentStorageMissing</c> category instead of sanitizing and
/// persisting the raw message.
/// </summary>
public sealed class AttachmentSpoolFileReadException() : Exception("Attachment spool file could not be read.");
