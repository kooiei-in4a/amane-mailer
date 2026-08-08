namespace Amane.Mailer.Contracts.MailRequests;

/// <summary>
/// Fixed attachment limits (ADR 0022 D-01/D-02, v1.3.0 MVP). 1 MiB = 1,048,576 bytes.
/// Shared by runtime validation, SDKs, and tests so the numbers stay in exactly one place.
/// </summary>
public static class MailAttachmentLimits
{
    public const int MaxAttachmentCount = 5;

    public const long MaxPerFileDecodedBytes = 2 * 1024 * 1024;

    public const long MaxTotalDecodedBytes = 5 * 1024 * 1024;

    public const long MaxProviderEnvelopeBytes = 8 * 1024 * 1024;

    public const long MaxConsumerHttpEnvelopeBytes = 16 * 1024 * 1024;

    public const int MinFileNameUtf8Bytes = 1;

    public const int MaxFileNameUtf8Bytes = 255;
}
