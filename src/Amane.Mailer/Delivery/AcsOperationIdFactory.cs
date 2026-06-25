namespace Amane.Mailer.Delivery;

public static class AcsOperationIdFactory
{
    /// <summary>
    /// RFC 4122 UUIDv5 for ACS LRO correlation. Namespace is the tenant id; name is source_service:mail_request_id.
    /// </summary>
    public static Guid Create(Guid tenantId, string sourceService, Guid mailRequestId)
    {
        var name = $"{sourceService}:{mailRequestId:D}";
        return UuidV5.Create(tenantId, name);
    }
}
