using System.Buffers;
using System.Security.Cryptography;

namespace Amane.Mailer.Attachments.Spool;

public enum AttachmentDecodeStatus
{
    Success,
    InvalidBase64,
    TooLarge,
}

public readonly record struct AttachmentDecodeResult(
    AttachmentDecodeStatus Status,
    long DecodedLength,
    string? Sha256Hex);

/// <summary>
/// Decodes a single attachment's <c>content_base64</c> to a staging file with a hard upper
/// bound enforced before any allocation proportional to the encoded length (ADR 0022 D-02/D-08:
/// bounded decode, never trust declared size). The per-file cap is re-verified against the
/// actual decoded byte count, not just the pre-decode estimate.
/// </summary>
public static class BoundedAttachmentDecoder
{
    public static async Task<AttachmentDecodeResult> DecodeToFileAsync(
        string base64Content,
        string destinationPath,
        long maxDecodedBytes,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(base64Content)
            || base64Content.Length % 4 != 0
            || base64Content.Length > int.MaxValue / 3)
        {
            return new AttachmentDecodeResult(AttachmentDecodeStatus.InvalidBase64, 0, null);
        }

        // 4 encoded chars decode to at most 3 bytes; reject before allocating anything
        // proportional to the encoded length once the upper bound already exceeds the cap.
        var upperBoundLength = (long)base64Content.Length / 4 * 3;
        if (upperBoundLength - 2 > maxDecodedBytes)
        {
            return new AttachmentDecodeResult(AttachmentDecodeStatus.TooLarge, 0, null);
        }

        var rented = ArrayPool<byte>.Shared.Rent((int)upperBoundLength);
        try
        {
            if (!Convert.TryFromBase64String(base64Content, rented, out var decodedLength))
            {
                return new AttachmentDecodeResult(AttachmentDecodeStatus.InvalidBase64, 0, null);
            }

            if (decodedLength > maxDecodedBytes)
            {
                return new AttachmentDecodeResult(AttachmentDecodeStatus.TooLarge, 0, null);
            }

            var sha256Hex = Convert.ToHexString(SHA256.HashData(rented.AsSpan(0, decodedLength)))
                .ToLowerInvariant();

            await using (var file = new FileStream(
                destinationPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 64 * 1024,
                FileOptions.SequentialScan | FileOptions.Asynchronous))
            {
                await file.WriteAsync(rented.AsMemory(0, decodedLength), cancellationToken);
                await file.FlushAsync(cancellationToken);
            }

            return new AttachmentDecodeResult(AttachmentDecodeStatus.Success, decodedLength, sha256Hex);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(rented, clearArray: true);
        }
    }
}
