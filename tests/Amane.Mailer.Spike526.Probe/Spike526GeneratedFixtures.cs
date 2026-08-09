using System.Text;

namespace Amane.Mailer.Spike526.Probe;

// Rev.2 (#526 plan authority) requires the ACS envelope estimator's upper-bound
// qualification to include a generated boundary family beyond the F00-F08
// synthetic fixtures, crossed with 1-attachment and 5-attachment variants
// (G01: JSON escaping-heavy strings, G02: multibyte UTF-8 strings, G03: max
// filename/body/recipient candidates, G04: 1 vs 5 attachments, G05:
// provider-boundary-near binary sizes). G04 is covered as an axis crossed with
// each of G01/G02/G03/G05 rather than as a standalone fixture.
public static class Spike526GeneratedFixtures
{
    private const int MiB = 1024 * 1024;

    public static IReadOnlyList<string> GeneratedFixtureIds { get; } =
        ["G01-1", "G01-5", "G02-1", "G02-5", "G03-1", "G03-5", "G05-1", "G05-5"];

    public static Spike526Fixture Create(string fixtureId)
    {
        var (family, attachmentCount) = fixtureId switch
        {
            "G01-1" => ("G01", 1),
            "G01-5" => ("G01", 5),
            "G02-1" => ("G02", 1),
            "G02-5" => ("G02", 5),
            "G03-1" => ("G03", 1),
            "G03-5" => ("G03", 5),
            "G05-1" => ("G05", 1),
            "G05-5" => ("G05", 5),
            _ => throw new ArgumentOutOfRangeException(nameof(fixtureId), fixtureId, "Unknown #526 generated fixture."),
        };

        return family switch
        {
            "G01" => CreateEscapingHeavy(fixtureId, attachmentCount),
            "G02" => CreateMultibyteHeavy(fixtureId, attachmentCount),
            "G03" => CreateMaxLengthFields(fixtureId, attachmentCount),
            "G05" => CreateProviderBoundaryNear(fixtureId, attachmentCount),
            _ => throw new ArgumentOutOfRangeException(nameof(fixtureId), fixtureId, "Unknown #526 generated fixture family."),
        };
    }

    private static Spike526Fixture CreateEscapingHeavy(string fixtureId, int attachmentCount)
    {
        // Quotes, backslashes, and control-escapable characters maximize how much
        // the JSON writer must expand each raw character into escape sequences.
        const string unit = "\"\\\t\n\r<>&'";
        var subject = Repeat(unit, targetChars: 512);
        var body = Repeat(unit, targetChars: 4096);
        var recipients = CreateEscapingRecipients(count: 3);
        var attachments = CreateAttachments(
            attachmentCount,
            totalBytes: 64 * 1024,
            fileNameFactory: static index => $"g01-\"esc\\ape\"-{index}.bin",
            contentType: "application/octet-stream",
            seedBase: 401);

        return BuildFixture(fixtureId, subject, body, recipients, attachments, replyTo: "reply@example.invalid");
    }

    private static Spike526Fixture CreateMultibyteHeavy(string fixtureId, int attachmentCount)
    {
        // CJK (3-byte) and emoji (4-byte, surrogate-pair) characters maximize UTF-8
        // byte density relative to UTF-16 character/codepoint count.
        const string unit = "境界日本語🎌🚀🧭📎";
        var subject = Repeat(unit, targetChars: 256);
        var body = Repeat(unit, targetChars: 4096);
        var recipients = CreateMultibyteRecipients(count: 3);
        var attachments = CreateAttachments(
            attachmentCount,
            totalBytes: 64 * 1024,
            fileNameFactory: static index => $"g02-添付書類-{index}-🎌.bin",
            contentType: "application/octet-stream",
            seedBase: 402);

        return BuildFixture(fixtureId, subject, body, recipients, attachments, replyTo: "reply@example.invalid");
    }

    private static Spike526Fixture CreateMaxLengthFields(string fixtureId, int attachmentCount)
    {
        var subject = Repeat("MaxSubject", targetChars: 512);
        var body = Repeat("Max length body candidate. ", targetChars: 200 * 1024);
        var recipients = Spike526FixtureFactory.CreateRecipients(50);
        var attachments = CreateAttachments(
            attachmentCount,
            totalBytes: 64 * 1024,
            fileNameFactory: static index => new string('f', 200) + "-" + index + ".bin",
            contentType: "application/octet-stream",
            seedBase: 403);

        return BuildFixture(fixtureId, subject, body, recipients, attachments, replyTo: "reply@example.invalid");
    }

    private static Spike526Fixture CreateProviderBoundaryNear(string fixtureId, int attachmentCount)
    {
        // ~9.3 MiB total binary pushes close to ACS's practical whole-message
        // ceiling once Base64 (~33% larger) and JSON/body overhead are added, well
        // beyond the F06 fixture (7 MiB), without depending on a live provider.
        const int totalBinaryBytes = 9 * MiB + (300 * 1024);
        var subject = "spike526-" + fixtureId;
        var body = Repeat("A境界<>&\"\\", targetChars: 768 * 1024);
        var recipients = Spike526FixtureFactory.CreateRecipients(20);
        var attachments = CreateAttachments(
            attachmentCount,
            totalBytes: totalBinaryBytes,
            fileNameFactory: static index => $"g05-boundary-{index}.bin",
            contentType: "application/octet-stream",
            seedBase: 405);

        return BuildFixture(fixtureId, subject, body, recipients, attachments, replyTo: "reply@example.invalid");
    }

    private static Spike526Fixture BuildFixture(
        string fixtureId,
        string subject,
        string body,
        (List<Spike526Recipient> To, List<Spike526Recipient> Cc, List<Spike526Recipient> Bcc) recipients,
        List<Spike526Attachment> attachments,
        string replyTo)
    {
        var totalBinaryBytes = attachments.Sum(static attachment => attachment.ByteLength);
        var request = new Spike526Request
        {
            TenantId = "00000000-0000-0000-0000-000000000526",
            SourceService = "spike526-generated",
            MailRequestId = Spike526FixtureFactory.DeterministicGuid(fixtureId).ToString(),
            Purpose = "Spike526EstimatorBoundaryQualification",
            To = recipients.To,
            Cc = recipients.Cc,
            Bcc = recipients.Bcc,
            Subject = subject,
            TextBody = body,
            HtmlBody = "<p>" + body + "</p>",
            ReplyTo = replyTo,
            Metadata = new Dictionary<string, string>(StringComparer.Ordinal) { ["fixture"] = fixtureId },
            Attachments = attachments,
            PayloadHash = new string('0', 64),
        };

        return new Spike526Fixture(fixtureId, request, totalBinaryBytes, attachments.Count, true, true);
    }

    private static List<Spike526Attachment> CreateAttachments(
        int attachmentCount,
        int totalBytes,
        Func<int, string> fileNameFactory,
        string contentType,
        int seedBase)
    {
        var result = new List<Spike526Attachment>(attachmentCount);
        var remaining = totalBytes;
        for (var index = 0; index < attachmentCount; index++)
        {
            var slots = attachmentCount - index;
            var length = remaining / slots;
            remaining -= length;

            var bytes = Spike526FixtureFactory.CreateSyntheticBytes(length, seed: seedBase + index);
            result.Add(Spike526FixtureFactory.CreateAttachment(fileNameFactory(index), contentType, bytes));
        }

        return result;
    }

    private static (List<Spike526Recipient> To, List<Spike526Recipient> Cc, List<Spike526Recipient> Bcc)
        CreateEscapingRecipients(int count)
    {
        var to = new List<Spike526Recipient> { new() { Email = "to@example.invalid", DisplayName = "\"Quoted\\Name\"" } };
        var cc = new List<Spike526Recipient>();
        for (var index = 1; index < count; index++)
        {
            cc.Add(new Spike526Recipient
            {
                Email = $"cc-{index}@example.invalid",
                DisplayName = $"Name \"{index}\" \\ with <tab>\t",
            });
        }

        return (to, cc, []);
    }

    private static (List<Spike526Recipient> To, List<Spike526Recipient> Cc, List<Spike526Recipient> Bcc)
        CreateMultibyteRecipients(int count)
    {
        var to = new List<Spike526Recipient> { new() { Email = "to@example.invalid", DisplayName = "境界太郎🎌" } };
        var cc = new List<Spike526Recipient>();
        for (var index = 1; index < count; index++)
        {
            cc.Add(new Spike526Recipient
            {
                Email = $"cc-{index}@example.invalid",
                DisplayName = $"日本語テスト🚀{index}",
            });
        }

        return (to, cc, []);
    }

    private static string Repeat(string unit, int targetChars)
    {
        var repeatCount = Math.Max(1, (targetChars + unit.Length - 1) / unit.Length);
        var builder = new StringBuilder(checked(repeatCount * unit.Length));
        for (var index = 0; index < repeatCount; index++)
        {
            builder.Append(unit);
        }

        return builder.ToString();
    }
}
