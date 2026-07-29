using System.Text.Json;
using System.Text.RegularExpressions;

namespace Amane.Mailer.Setup;

/// <summary>
/// Strict ACTIVE pointer document (ADR 0021 D-03). Bare bundleId strings are rejected.
/// </summary>
public sealed class SetupActivePointer
{
    public const int CurrentSchemaVersion = 1;

    private static readonly Regex SafeBundleId = new(
        "^[A-Za-z0-9][A-Za-z0-9_-]{0,63}$",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    public required int SchemaVersion { get; init; }
    public required string BundleId { get; init; }
    public required long ActivationGeneration { get; init; }

    public static bool TryParse(string text, out SetupActivePointer? pointer)
    {
        pointer = null;
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        var trimmed = text.Trim();
        if (!trimmed.StartsWith('{'))
        {
            return false;
        }

        try
        {
            var parsed = JsonSerializer.Deserialize(
                trimmed,
                SetupApplyJsonContext.Default.SetupActivePointer);
            if (parsed is null
                || parsed.SchemaVersion != CurrentSchemaVersion
                || parsed.ActivationGeneration < 1
                || !IsSafeBundleId(parsed.BundleId))
            {
                return false;
            }

            pointer = parsed;
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    public static bool IsSafeBundleId(string? bundleId) =>
        !string.IsNullOrEmpty(bundleId) && SafeBundleId.IsMatch(bundleId);

    public string ToCanonicalJson() =>
        JsonSerializer.Serialize(this, SetupApplyJsonContext.Default.SetupActivePointer);
}
