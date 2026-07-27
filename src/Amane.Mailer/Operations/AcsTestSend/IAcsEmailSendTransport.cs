namespace Amane.Mailer.Operations.AcsTestSend;

/// <summary>
/// Transport boundary for ACS Email send used by standalone verification. Throws provider
/// exceptions for the caller to classify; never logs request values.
/// </summary>
public interface IAcsEmailSendTransport
{
    Task<AcsEmailSendTransportResult> SendAndWaitAsync(
        AcsTestSendRequest request,
        CancellationToken cancellationToken);
}

/// <summary>
/// Raw transport result before canonical message-id validation in the CLI layer.
/// </summary>
public sealed class AcsEmailSendTransportResult
{
    public required string OperationId { get; init; }

    public required bool Succeeded { get; init; }
}
