namespace Amane.Mailer.Data.Sqlite.Models;

/// <summary>Canonical attachment metadata read back from <c>mail_request_attachments</c>.</summary>
public sealed record AttachmentMetadataRow(
    int Order,
    string FileName,
    string ContentType,
    long ByteLength,
    string ContentSha256,
    Guid SpoolKey);
