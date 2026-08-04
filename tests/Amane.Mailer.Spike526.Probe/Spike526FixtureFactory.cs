using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Amane.Mailer.Spike526.Probe;

public static class Spike526FixtureFactory
{
    private const int MiB = 1024 * 1024;

    public static IReadOnlyList<string> FixtureIds { get; } =
        ["F00", "F01", "F02", "F03", "F04", "F05", "F06", "F07", "F08"];

    public static Spike526Fixture Create(string fixtureId)
    {
        var definition = fixtureId switch
        {
            "F00" => new Definition(0, 0, 32, 1, ValidBase64: true, MetadataMatch: true),
            "F01" => new Definition(1, 1 * MiB, 32, 1, ValidBase64: true, MetadataMatch: true),
            "F02" => new Definition(1, (5 * MiB) - 1024, 32, 1, ValidBase64: true, MetadataMatch: true),
            "F03" => new Definition(5, 6 * MiB, 32, 1, ValidBase64: true, MetadataMatch: true),
            "F04" => new Definition(5, 6 * MiB, 256 * 1024, 20, ValidBase64: true, MetadataMatch: true),
            "F05" => new Definition(5, 6 * MiB, 512 * 1024, 20, ValidBase64: true, MetadataMatch: true),
            "F06" => new Definition(5, 7 * MiB, 768 * 1024, 20, ValidBase64: true, MetadataMatch: true),
            "F07" => new Definition(1, 1 * MiB, 32, 1, ValidBase64: true, MetadataMatch: false),
            "F08" => new Definition(1, 128 * 1024, 32, 1, ValidBase64: false, MetadataMatch: true),
            _ => throw new ArgumentOutOfRangeException(nameof(fixtureId), fixtureId, "Unknown #526 fixture."),
        };

        var attachments = CreateAttachments(fixtureId, definition);
        var recipients = CreateRecipients(definition.RecipientCount);
        var repeated = CreateRepeatedBody(definition.BodyUtf8Bytes);

        var request = new Spike526Request
        {
            TenantId = "00000000-0000-0000-0000-000000000526",
            SourceService = "spike526-offline",
            MailRequestId = DeterministicGuid(fixtureId).ToString(),
            Purpose = "Spike526ResourceQualification",
            To = recipients.To,
            Cc = recipients.Cc,
            Bcc = recipients.Bcc,
            Subject = fixtureId is "F04" or "F05" or "F06"
                ? "境界 \\\" subject <>& " + new string('S', 128)
                : "spike526-" + fixtureId,
            TextBody = repeated,
            HtmlBody = "<p>" + repeated + "</p>",
            ReplyTo = "reply@example.invalid",
            Metadata = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["fixture"] = fixtureId,
                ["escaped"] = "quote=\" slash=\\ unicode=境界",
            },
            Attachments = attachments,
            PayloadHash = new string('0', 64),
        };

        return new Spike526Fixture(
            fixtureId,
            request,
            definition.TotalBinaryBytes,
            attachments.Count,
            definition.ValidBase64,
            definition.MetadataMatch);
    }

    public static byte[] SerializeRequest(Spike526Fixture fixture) =>
        JsonSerializer.SerializeToUtf8Bytes(fixture.Request, Spike526JsonContext.Default.Spike526Request);

    public static Spike526ConsumerMeasurement MeasureConsumerEnvelope(Spike526Fixture fixture)
    {
        var bytes = SerializeRequest(fixture);
        return new Spike526ConsumerMeasurement(
            fixture.Id,
            fixture.AttachmentCount,
            fixture.DecodedBinaryBytes,
            fixture.Request.Attachments.Sum(static attachment => (long)attachment.ContentBase64.Length),
            bytes.LongLength);
    }

    public static byte[] DecodeAttachment(Spike526Attachment attachment) =>
        Convert.FromBase64String(attachment.ContentBase64);

    private static List<Spike526Attachment> CreateAttachments(string fixtureId, Definition definition)
    {
        if (definition.AttachmentCount == 0)
        {
            return [];
        }

        var result = new List<Spike526Attachment>(definition.AttachmentCount);
        var remaining = definition.TotalBinaryBytes;
        for (var index = 0; index < definition.AttachmentCount; index++)
        {
            var slots = definition.AttachmentCount - index;
            var length = remaining / slots;
            remaining -= length;

            var bytes = CreateSyntheticBytes(length, seed: 17 + index);
            var digest = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
            var base64 = Convert.ToBase64String(bytes);
            if (!definition.ValidBase64 && index == 0)
            {
                base64 = base64[..^1] + "!";
            }

            var declaredLength = definition.MetadataMatch || index != 0 ? bytes.LongLength : bytes.LongLength + 1;
            var declaredDigest = definition.MetadataMatch || index != 0 ? digest : new string('f', 64);
            result.Add(new Spike526Attachment
            {
                FileName = index == definition.AttachmentCount - 1
                    ? $"{fixtureId}-請求書-{index}.txt".Normalize(NormalizationForm.FormC)
                    : $"{fixtureId}-attachment-{index}.bin",
                ContentType = index == definition.AttachmentCount - 1 ? "text/plain" : "application/octet-stream",
                ByteLength = declaredLength,
                ContentSha256 = declaredDigest,
                ContentBase64 = base64,
            });
        }

        return result;
    }

    internal static (List<Spike526Recipient> To, List<Spike526Recipient> Cc, List<Spike526Recipient> Bcc)
        CreateRecipients(int count)
    {
        var to = new List<Spike526Recipient>();
        var cc = new List<Spike526Recipient>();
        var bcc = new List<Spike526Recipient>();

        for (var index = 0; index < count; index++)
        {
            var recipient = new Spike526Recipient
            {
                Email = $"recipient-{index}@example.invalid",
                DisplayName = index % 3 == 0 ? $"境界 Recipient {index}" : null,
            };

            if (index == 0)
            {
                to.Add(recipient);
            }
            else if (index <= 9)
            {
                cc.Add(recipient);
            }
            else
            {
                bcc.Add(recipient);
            }
        }

        return (to, cc, bcc);
    }

    internal static byte[] CreateSyntheticBytes(int length, int seed)
    {
        var bytes = GC.AllocateUninitializedArray<byte>(length);
        for (var index = 0; index < bytes.Length; index++)
        {
            bytes[index] = (byte)((index * 31 + seed) % 251);
        }

        return bytes;
    }

    internal static Spike526Attachment CreateAttachment(string fileName, string contentType, byte[] bytes)
    {
        var digest = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        return new Spike526Attachment
        {
            FileName = fileName,
            ContentType = contentType,
            ByteLength = bytes.LongLength,
            ContentSha256 = digest,
            ContentBase64 = Convert.ToBase64String(bytes),
        };
    }

    private static string CreateRepeatedBody(int requestedUtf8Bytes)
    {
        if (requestedUtf8Bytes <= 32)
        {
            return "spike526 synthetic body";
        }

        const string unit = "A境界<>&\"\\";
        var unitBytes = Encoding.UTF8.GetByteCount(unit);
        var repeatCount = checked((requestedUtf8Bytes + unitBytes - 1) / unitBytes);
        var builder = new StringBuilder(checked(repeatCount * unit.Length));
        for (var index = 0; index < repeatCount; index++)
        {
            builder.Append(unit);
        }

        return builder.ToString();
    }

    internal static Guid DeterministicGuid(string fixtureId)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes("amane-mailer-spike526:" + fixtureId));
        return new Guid(hash.AsSpan(0, 16));
    }

    private sealed record Definition(
        int AttachmentCount,
        int TotalBinaryBytes,
        int BodyUtf8Bytes,
        int RecipientCount,
        bool ValidBase64,
        bool MetadataMatch);
}
