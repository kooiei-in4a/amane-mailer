using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace Amane.Mailer.Webhooks;

public sealed class WebhookSignatureService
{
    public const string SignatureHeaderName = "X-Mailer-Signature";
    public const string TimestampHeaderName = "X-Mailer-Timestamp";
    public const string EventIdHeaderName = "X-Mailer-Event-Id";

    public WebhookSignatureHeaders CreateHeaders(Guid eventId, long unixTimestampSeconds, ReadOnlySpan<byte> body, string secret)
    {
        var signature = ComputeSignature(secret, unixTimestampSeconds, body);
        return new WebhookSignatureHeaders(
            eventId,
            unixTimestampSeconds.ToString(CultureInfo.InvariantCulture),
            $"sha256={signature}");
    }

    public static string ComputeSignature(string secret, long unixTimestampSeconds, ReadOnlySpan<byte> body)
    {
        var prefix = Encoding.UTF8.GetBytes(unixTimestampSeconds.ToString(CultureInfo.InvariantCulture) + ".");
        var payload = new byte[prefix.Length + body.Length];
        prefix.CopyTo(payload, 0);
        body.CopyTo(payload.AsSpan(prefix.Length));

        Span<byte> hash = stackalloc byte[32];
        HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), payload, hash);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }
}

public sealed record WebhookSignatureHeaders(
    Guid EventId,
    string Timestamp,
    string Signature);
