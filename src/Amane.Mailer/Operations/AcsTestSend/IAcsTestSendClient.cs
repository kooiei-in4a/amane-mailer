namespace Amane.Mailer.Operations.AcsTestSend;

/// <summary>
/// ACS Email send used only by standalone verification CLI (and later #428). Bypasses Mailer
/// API, Worker, tenant JSON, and DB. Implementations must sanitize provider exceptions before
/// any text leaves the client boundary; prefer canonical failure codes over provider text.
/// </summary>
public interface IAcsTestSendClient
{
    Task<AcsTestSendOutcome> SendAsync(AcsTestSendRequest request, CancellationToken cancellationToken);
}
