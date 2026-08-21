namespace Amane.Mailer.Contracts.Security;

/// <summary>
/// Verified (not Consumer-declared) attachment values used to build the payload_hash
/// attachment projection (ADR 0022 D-03). <c>order</c> is not part of this type: it is always
/// generated from the zero-based position of the attachment within the list passed to
/// <see cref="MailPayloadHasher"/>, never a caller-supplied value.
/// </summary>
public sealed record MailAttachmentHashInput(
    string FileName,
    string ContentType,
    long ByteLength,
    string ContentSha256);
