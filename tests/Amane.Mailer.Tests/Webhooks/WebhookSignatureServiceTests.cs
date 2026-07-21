using System.Text;
using Amane.Mailer.Webhooks;

namespace Amane.Mailer.Tests;

public sealed class WebhookSignatureServiceTests
{
    [Fact]
    public void ComputeSignature_matches_expected_hmac()
    {
        var body = Encoding.UTF8.GetBytes("""{"event_id":"018f7c2a-0000-7000-8000-000000000001"}""");
        var signature = WebhookSignatureService.ComputeSignature("test-secret", 1_700_000_000, body);

        Assert.Matches("^[0-9a-f]{64}$", signature);

        var service = new WebhookSignatureService();
        var headers = service.CreateHeaders(
            Guid.Parse("018f7c2a-0000-7000-8000-000000000001"),
            1_700_000_000,
            body,
            "test-secret");

        Assert.Equal("sha256=" + signature, headers.Signature);
        Assert.Equal("1700000000", headers.Timestamp);
    }
}
