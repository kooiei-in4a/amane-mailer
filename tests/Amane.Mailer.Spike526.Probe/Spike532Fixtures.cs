namespace Amane.Mailer.Spike526.Probe;

// Issue #532 real Docker/cgroup total-memory qualification fixtures, sized to
// the Koo-confirmed MVP limits (#523): 2 MiB max per-file decoded binary, 5 MiB
// max total decoded binary, 5 max attachment count, 8 MiB max ACS provider
// envelope. These limits post-date and differ from the #526 F00-F08/G01-G05
// candidate fixtures (sized against #526's earlier, larger candidate numbers),
// so #532 needs its own fixture family -- but every fixture-construction
// primitive below (synthetic bytes, attachment/recipient DTOs, digest
// computation) is reused unmodified from Spike526FixtureFactory. The body
// text uses a single repeated ASCII character (unlike the multibyte-heavy
// #526 G01/G02 fixtures) so its UTF-8 byte count is exact and the ACS
// envelope boundary (Q03/Q03X) can be calibrated precisely; multibyte/escaping
// edge cases remain covered by #526's existing G-series and are out of scope
// here.
public static class Spike532Fixtures
{
    private const int MiB = 1024 * 1024;

    // Calibrated against the real Spike526AcsEnvelopeCapture exact-capture path
    // (see docs/cd/reports/2026-08-04-issue-532-docker-memory-qualification.md)
    // so that, with the same 5 MiB (at-cap) attachment total, Q03's ACS envelope
    // lands just under and Q03X's lands just over the #523 8 MiB provider
    // envelope policy limit.
    private const int Q03BodyUtf8Bytes = 600_000;
    private const int Q03XBodyUtf8Bytes = 750_000;

    public static IReadOnlyList<string> FixtureIds { get; } =
        ["Q00", "Q01", "Q01X", "Q02", "Q02X", "Q03", "Q03X"];

    public static Spike526Fixture Create(string fixtureId) => fixtureId switch
    {
        "Q00" => Build(fixtureId, attachmentCount: 0, totalBinaryBytes: 0, bodyUtf8Bytes: 32),
        "Q01" => Build(fixtureId, attachmentCount: 1, totalBinaryBytes: 2 * MiB, bodyUtf8Bytes: 64),
        "Q01X" => Build(fixtureId, attachmentCount: 1, totalBinaryBytes: (2 * MiB) + 1, bodyUtf8Bytes: 64),
        "Q02" => Build(fixtureId, attachmentCount: 5, totalBinaryBytes: 5 * MiB, bodyUtf8Bytes: 64),
        "Q02X" => Build(fixtureId, attachmentCount: 5, totalBinaryBytes: (5 * MiB) + 1, bodyUtf8Bytes: 64),
        "Q03" => Build(fixtureId, attachmentCount: 5, totalBinaryBytes: 5 * MiB, bodyUtf8Bytes: Q03BodyUtf8Bytes),
        "Q03X" => Build(fixtureId, attachmentCount: 5, totalBinaryBytes: 5 * MiB, bodyUtf8Bytes: Q03XBodyUtf8Bytes),
        _ => throw new ArgumentOutOfRangeException(nameof(fixtureId), fixtureId, "Unknown #532 fixture."),
    };

    private static Spike526Fixture Build(
        string fixtureId,
        int attachmentCount,
        int totalBinaryBytes,
        int bodyUtf8Bytes)
    {
        var attachments = CreateAttachments(fixtureId, attachmentCount, totalBinaryBytes);
        var recipients = Spike526FixtureFactory.CreateRecipients(1);
        var body = CreateRepeatedBody(bodyUtf8Bytes);

        var request = new Spike526Request
        {
            TenantId = "00000000-0000-0000-0000-000000000532",
            SourceService = "spike532-docker-memory",
            MailRequestId = Spike526FixtureFactory.DeterministicGuid(fixtureId).ToString(),
            Purpose = "Spike532DockerMemoryQualification",
            To = recipients.To,
            Cc = recipients.Cc,
            Bcc = recipients.Bcc,
            Subject = "spike532-" + fixtureId,
            TextBody = body,
            HtmlBody = "<p>" + body + "</p>",
            ReplyTo = "reply@example.invalid",
            Metadata = new Dictionary<string, string>(StringComparer.Ordinal) { ["fixture"] = fixtureId },
            Attachments = attachments,
            PayloadHash = new string('0', 64),
        };

        var decodedBytes = attachments.Sum(static attachment => attachment.ByteLength);
        return new Spike526Fixture(fixtureId, request, decodedBytes, attachments.Count, true, true);
    }

    private static List<Spike526Attachment> CreateAttachments(string fixtureId, int count, int totalBytes)
    {
        if (count == 0)
        {
            return [];
        }

        var result = new List<Spike526Attachment>(count);
        var remaining = totalBytes;
        for (var index = 0; index < count; index++)
        {
            var slots = count - index;
            var length = remaining / slots;
            remaining -= length;

            var bytes = Spike526FixtureFactory.CreateSyntheticBytes(length, seed: 532 + index);
            result.Add(Spike526FixtureFactory.CreateAttachment(
                $"{fixtureId}-attachment-{index}.bin",
                "application/octet-stream",
                bytes));
        }

        return result;
    }

    private static string CreateRepeatedBody(int targetUtf8Bytes) =>
        targetUtf8Bytes <= 0 ? string.Empty : new string('A', targetUtf8Bytes);
}
