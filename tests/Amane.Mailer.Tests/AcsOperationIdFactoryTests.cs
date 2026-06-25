using Amane.Mailer.Configuration;
using Amane.Mailer.Delivery;

namespace Amane.Mailer.Tests;

public sealed class AcsOperationIdFactoryTests
{
    private static readonly Guid TenantId = Guid.Parse("00000000-0000-0000-0000-000000000101");
    private static readonly Guid MailRequestId = Guid.Parse("11111111-2222-3333-4444-555555555555");

    [Fact]
    public void Create_is_deterministic_for_same_inputs()
    {
        var first = AcsOperationIdFactory.Create(TenantId, "example-service", MailRequestId);
        var second = AcsOperationIdFactory.Create(TenantId, "example-service", MailRequestId);

        Assert.Equal(first, second);
    }

    [Fact]
    public void Create_changes_when_mail_request_id_changes()
    {
        var baseline = AcsOperationIdFactory.Create(TenantId, "example-service", MailRequestId);
        var other = AcsOperationIdFactory.Create(
            TenantId,
            "example-service",
            Guid.Parse("99999999-8888-7777-6666-555555555555"));

        Assert.NotEqual(baseline, other);
    }

    [Fact]
    public void Create_changes_when_source_service_changes()
    {
        var baseline = AcsOperationIdFactory.Create(TenantId, "example-service", MailRequestId);
        var other = AcsOperationIdFactory.Create(TenantId, "other-service", MailRequestId);

        Assert.NotEqual(baseline, other);
    }

    [Fact]
    public void Create_changes_when_tenant_id_changes()
    {
        var baseline = AcsOperationIdFactory.Create(TenantId, "example-service", MailRequestId);
        var other = AcsOperationIdFactory.Create(
            Guid.Parse("00000000-0000-0000-0000-000000000999"),
            "example-service",
            MailRequestId);

        Assert.NotEqual(baseline, other);
    }
}
