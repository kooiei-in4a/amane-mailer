namespace Amane.Mailer.Attachments.Validation;

/// <summary>Verified attachment metadata staged to the spool, ready for the SQLite commit (ADR 0022 D-08).</summary>
public sealed record CanonicalAttachmentMetadata(
    int Order,
    string FileName,
    string ContentType,
    long ByteLength,
    string Sha256Hex,
    Guid SpoolKey);

public enum AttachmentAcceptanceStatus
{
    NoAttachments,
    Success,
    Failure,
}

public readonly struct AttachmentAcceptanceResult
{
    private AttachmentAcceptanceResult(
        AttachmentAcceptanceStatus status,
        IReadOnlyList<CanonicalAttachmentMetadata>? attachments,
        string? failureCode)
    {
        Status = status;
        Attachments = attachments;
        FailureCode = failureCode;
    }

    public AttachmentAcceptanceStatus Status { get; }

    public IReadOnlyList<CanonicalAttachmentMetadata>? Attachments { get; }

    public string? FailureCode { get; }

    public static AttachmentAcceptanceResult NoAttachments() =>
        new(AttachmentAcceptanceStatus.NoAttachments, null, null);

    public static AttachmentAcceptanceResult Success(IReadOnlyList<CanonicalAttachmentMetadata> attachments) =>
        new(AttachmentAcceptanceStatus.Success, attachments, null);

    public static AttachmentAcceptanceResult Failure(string failureCode) =>
        new(AttachmentAcceptanceStatus.Failure, null, failureCode);
}
