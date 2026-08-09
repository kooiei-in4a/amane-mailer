using Amane.Mailer.Attachments.Spool;
using Amane.Mailer.Contracts.MailRequests;

namespace Amane.Mailer.Attachments.Validation;

/// <summary>
/// Orchestrates ADR 0022 D-04 steps 3-7 for a single request: attachment count, bounded
/// decode, per-file/total size, digest/length verification, filename, and file-type/structure
/// validation. On any failure the request-scoped staging directory is deleted before returning
/// -- nothing is left behind for a rejected request.
/// </summary>
public static class AttachmentAcceptanceValidator
{
    public static async Task<AttachmentAcceptanceResult> ValidateAndStageAsync(
        IReadOnlyList<MailAttachmentDto>? attachments,
        Guid requestId,
        AttachmentSpool spool,
        CancellationToken cancellationToken)
    {
        if (attachments is null || attachments.Count == 0)
        {
            return AttachmentAcceptanceResult.NoAttachments();
        }

        if (attachments.Count > MailAttachmentLimits.MaxAttachmentCount)
        {
            return AttachmentAcceptanceResult.Failure(MailerErrorCodes.TooManyAttachments);
        }

        spool.EnsureStagingDirectory(requestId);

        try
        {
            var staged = new List<CanonicalAttachmentMetadata>(attachments.Count);
            var normalizedFileNames = new List<string>(attachments.Count);
            long totalDecoded = 0;

            for (var order = 0; order < attachments.Count; order++)
            {
                var attachment = attachments[order];

                var spoolKey = Guid.CreateVersion7();
                var destinationPath = spool.GetStagingFilePath(requestId, spoolKey);
                var decodeResult = await BoundedAttachmentDecoder.DecodeToFileAsync(
                    attachment.ContentBase64,
                    destinationPath,
                    MailAttachmentLimits.MaxPerFileDecodedBytes,
                    cancellationToken);

                if (decodeResult.Status == AttachmentDecodeStatus.InvalidBase64)
                {
                    return Fail(spool, requestId, MailerErrorCodes.AttachmentInvalidBase64);
                }

                if (decodeResult.Status == AttachmentDecodeStatus.TooLarge)
                {
                    return Fail(spool, requestId, MailerErrorCodes.AttachmentTooLarge);
                }

                totalDecoded += decodeResult.DecodedLength;
                if (totalDecoded > MailAttachmentLimits.MaxTotalDecodedBytes)
                {
                    return Fail(spool, requestId, MailerErrorCodes.AttachmentTotalTooLarge);
                }

                if (decodeResult.DecodedLength != attachment.ByteLength)
                {
                    return Fail(spool, requestId, MailerErrorCodes.AttachmentLengthMismatch);
                }

                if (!string.Equals(
                        decodeResult.Sha256Hex,
                        attachment.ContentSha256.Trim(),
                        StringComparison.OrdinalIgnoreCase))
                {
                    return Fail(spool, requestId, MailerErrorCodes.AttachmentDigestMismatch);
                }

                if (!AttachmentFilenameValidator.TryValidate(attachment.FileName, out var normalizedFileName))
                {
                    return Fail(spool, requestId, MailerErrorCodes.AttachmentFilenameInvalid);
                }

                var extension = AttachmentFileTypeCatalog.ExtractExtension(normalizedFileName);
                if (!AttachmentFileTypeCatalog.TryGetByExtension(extension, out var fileType, out var canonicalContentType)
                    || !AttachmentFileTypeCatalog.DeclaredContentTypeMatches(fileType, attachment.ContentType))
                {
                    return Fail(spool, requestId, MailerErrorCodes.AttachmentTypeNotAllowed);
                }

                var decodedContent = await File.ReadAllBytesAsync(destinationPath, cancellationToken);
                var structureResult = AttachmentContentValidator.Validate(fileType, decodedContent);
                if (!structureResult.IsValid)
                {
                    return Fail(spool, requestId, structureResult.FailureCode!);
                }

                normalizedFileNames.Add(normalizedFileName);
                staged.Add(new CanonicalAttachmentMetadata(
                    order,
                    normalizedFileName,
                    canonicalContentType,
                    decodeResult.DecodedLength,
                    decodeResult.Sha256Hex!,
                    spoolKey));
            }

            if (AttachmentFilenameValidator.HasCaseInsensitiveDuplicate(normalizedFileNames))
            {
                return Fail(spool, requestId, MailerErrorCodes.AttachmentDuplicateFilename);
            }

            return AttachmentAcceptanceResult.Success(staged);
        }
        catch
        {
            spool.TryDeleteStaging(requestId);
            throw;
        }
    }

    private static AttachmentAcceptanceResult Fail(AttachmentSpool spool, Guid requestId, string failureCode)
    {
        spool.TryDeleteStaging(requestId);
        return AttachmentAcceptanceResult.Failure(failureCode);
    }
}
