namespace Amane.Mailer.Delivery;

public static class AcsOperationIdFactory
{
    /// <summary>
    /// RFC 4122 UUIDv5 for ACS LRO correlation. Namespace is the Sender id and the
    /// name is mail_request_id. Physical compatibility sentinel values are excluded.
    /// </summary>
    public static Guid Create(Guid senderId, Guid mailRequestId) =>
        UuidV5.Create(senderId, mailRequestId.ToString("D"));

    internal static Guid Create(Guid senderId, string _, Guid mailRequestId) =>
        Create(senderId, mailRequestId);
}
