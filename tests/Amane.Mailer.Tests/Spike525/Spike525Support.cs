using System.Globalization;
using System.Text;
using System.Text.Json;

namespace Amane.Mailer.Tests.Spike525;

/// <summary>
/// Spike-only (#525) constants and helpers. Not part of the public contract or
/// production code path. Synthetic addresses use RFC 2606 reserved domains
/// (example.com / .invalid) so nothing here can resolve to a real mailbox.
/// </summary>
internal static class Spike525Support
{
    internal const string SyntheticDomain = "example.com";
    internal const string SyntheticInvalidDomain = "spike525.invalid";

    internal static string SyntheticAddress(string localPart) =>
        $"{localPart}@{SyntheticDomain}";

    /// <summary>
    /// Value-free evidence sink. Writes redacted JSON lines to the session scratchpad
    /// (outside the repo), never into the git working tree, per #525 evidence rules
    /// (no raw recipient, no raw BCC, no full provider IDs, no raw MIME body).
    /// </summary>
    internal static class Evidence
    {
        private static readonly string OutputPath = ResolveOutputPath();

        private static string ResolveOutputPath()
        {
            var scratch = Environment.GetEnvironmentVariable("AMANE_SPIKE525_EVIDENCE_PATH");
            if (!string.IsNullOrWhiteSpace(scratch))
            {
                return scratch;
            }

            var dir = Path.Combine(Path.GetTempPath(), "amane-spike525-evidence");
            Directory.CreateDirectory(dir);
            return Path.Combine(dir, "evidence.jsonl");
        }

        internal static void Record(string fixtureId, object valueFreeFields)
        {
            var record = new Dictionary<string, object?>
            {
                ["fixture_id"] = fixtureId,
                ["recorded_at"] = DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture),
            };

            foreach (var prop in valueFreeFields.GetType().GetProperties())
            {
                record[ToSnakeCase(prop.Name)] = prop.GetValue(valueFreeFields);
            }

            var line = JsonSerializer.Serialize(record);
            File.AppendAllText(OutputPath, line + Environment.NewLine, Encoding.UTF8);
        }

        internal static string CurrentOutputPath => OutputPath;

        private static string ToSnakeCase(string name)
        {
            var sb = new StringBuilder();
            for (var i = 0; i < name.Length; i++)
            {
                var c = name[i];
                if (char.IsUpper(c))
                {
                    if (i > 0)
                    {
                        sb.Append('_');
                    }

                    sb.Append(char.ToLowerInvariant(c));
                }
                else
                {
                    sb.Append(c);
                }
            }

            return sb.ToString();
        }
    }

    /// <summary>Short, irreversible-enough fingerprint for evidence logs (never the raw value).</summary>
    internal static string ShortHash(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return "(null)";
        }

        var bytes = System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(value));
        return Convert.ToHexStringLower(bytes)[..12];
    }
}
