using System.Text;
using System.Text.Json;

namespace Amane.Mailer.Bounce;

/// <summary>
/// Decodes Event Grid to Storage Queue message bodies (ADR 0020 F-6 raw JSON; optional Base64).
/// </summary>
public static class AcsQueueMessageBodyDecoder
{
    public static string Decode(string body)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(body);
        var trimmed = body.Trim();

        if (LooksLikeJson(trimmed))
        {
            return trimmed;
        }

        if (TryDecodeBase64Utf8(trimmed, out var decoded) && LooksLikeJson(decoded))
        {
            return decoded;
        }

        return trimmed;
    }

    private static bool LooksLikeJson(string text) =>
        text.Length > 0 && (text[0] is '{' or '[');

    private static bool TryDecodeBase64Utf8(string text, out string decoded)
    {
        decoded = string.Empty;
        try
        {
            var bytes = Convert.FromBase64String(text);
            decoded = Encoding.UTF8.GetString(bytes).Trim();
            if (decoded.Length == 0 || !LooksLikeJson(decoded))
            {
                return false;
            }

            using var _ = JsonDocument.Parse(decoded);
            return true;
        }
        catch (FormatException)
        {
            return false;
        }
        catch (JsonException)
        {
            return false;
        }
        catch (DecoderFallbackException)
        {
            return false;
        }
    }
}
