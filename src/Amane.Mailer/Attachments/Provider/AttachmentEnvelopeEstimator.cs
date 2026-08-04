using System.Text.Json;

namespace Amane.Mailer.Attachments.Provider;

public sealed record AttachmentEnvelopeInput(
    string SenderEmail,
    string RecipientEmail,
    string? RecipientDisplayName,
    string Subject,
    string? TextBody,
    string? HtmlBody,
    string? ReplyTo,
    IReadOnlyList<AttachmentEnvelopeAttachment> Attachments);

public sealed record AttachmentEnvelopeAttachment(string FileName, string ContentType, long ByteLength);

/// <summary>
/// Conservative upper-bound estimator for the serialized provider envelope (ADR 0022 D-02),
/// adapted from the #526 spike's <c>Spike526AcsEnvelopeCapture.EstimateUpperBound</c> (offline
/// qualification: 15 cases, zero underestimates against real ACS SDK capture -- see
/// docs/cd/reports/2026-08-04-issue-532-docker-memory-qualification.md). Used as a fast,
/// best-effort pre-check at accept time. The authoritative gate re-checked at Worker dispatch
/// time (ADR 0022 D-02/D-08 step 2) uses exact pre-serialization of the actual provider request,
/// not this estimate.
/// </summary>
public static class AttachmentEnvelopeEstimator
{
    public static long EstimateUpperBound(AttachmentEnvelopeInput input)
    {
        long estimate = 16 * 1024;
        estimate += EncodedJsonBytes(input.SenderEmail) + 256;
        estimate += EncodedJsonBytes(input.Subject) + 256;
        estimate += EncodedJsonBytes(input.TextBody ?? string.Empty) + 256;
        estimate += EncodedJsonBytes(input.HtmlBody ?? string.Empty) + 256;
        estimate += EncodedJsonBytes(input.ReplyTo ?? string.Empty) + 128;
        estimate += EncodedJsonBytes(input.RecipientEmail)
            + EncodedJsonBytes(input.RecipientDisplayName ?? string.Empty) + 512;

        foreach (var attachment in input.Attachments)
        {
            estimate += Base64EncodedLength(attachment.ByteLength);
            estimate += EncodedJsonBytes(attachment.FileName);
            estimate += EncodedJsonBytes(attachment.ContentType);
            estimate += 1024;
        }

        return estimate;
    }

    private static long EncodedJsonBytes(string value) =>
        JsonEncodedText.Encode(value).EncodedUtf8Bytes.Length;

    private static long Base64EncodedLength(long binaryLength) =>
        checked(((binaryLength + 2) / 3) * 4);
}
