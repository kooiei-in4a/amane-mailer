using Amane.Mailer.Contracts.MailRequests;

namespace Amane.Mailer.Attachments.Validation;

/// <summary>
/// Outcome of a single structural content validator. <see cref="FailureCode"/> is always one of
/// the fixed ADR 0022 D-12 categories; never a provider/parser-specific message (D-13).
/// </summary>
public readonly struct AttachmentStructureResult
{
    private AttachmentStructureResult(bool isValid, string? failureCode)
    {
        IsValid = isValid;
        FailureCode = failureCode;
    }

    public bool IsValid { get; }

    public string? FailureCode { get; }

    public static AttachmentStructureResult Valid() => new(true, null);

    public static AttachmentStructureResult ContentMismatch() =>
        new(false, MailerErrorCodes.AttachmentContentMismatch);

    public static AttachmentStructureResult TypeNotAllowed() =>
        new(false, MailerErrorCodes.AttachmentTypeNotAllowed);

    public static AttachmentStructureResult Encrypted() =>
        new(false, MailerErrorCodes.AttachmentEncrypted);
}
