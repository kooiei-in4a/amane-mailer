using System.Text;
using System.Text.Json;

namespace Amane.Mailer.Json;

/// <summary>
/// Detects JSON objects that contain duplicate property names. System.Text.Json deserialization is
/// last-write-wins for duplicate members, which would let a caller smuggle a value past the
/// payload-hash check; the mail request contract requires every key within an object to be unique.
/// The scan covers every object in the body, including the top-level request, each recipient, and
/// the metadata object. Arbitrary metadata keys remain allowed, but a repeated key is rejected.
/// </summary>
internal static class JsonDuplicatePropertyDetector
{
    /// <summary>
    /// Returns <c>true</c> when any JSON object in <paramref name="json"/> repeats a property name.
    /// The caller must pass well-formed JSON; malformed input surfaces as <see cref="JsonException"/>.
    /// </summary>
    public static bool HasDuplicateProperty(string json)
    {
        var reader = new Utf8JsonReader(Encoding.UTF8.GetBytes(json));
        var scopes = new Stack<HashSet<string>>();

        while (reader.Read())
        {
            switch (reader.TokenType)
            {
                case JsonTokenType.StartObject:
                    scopes.Push(new HashSet<string>(StringComparer.Ordinal));
                    break;
                case JsonTokenType.EndObject:
                    scopes.Pop();
                    break;
                case JsonTokenType.PropertyName when !scopes.Peek().Add(reader.GetString()!):
                    return true;
            }
        }

        return false;
    }
}
