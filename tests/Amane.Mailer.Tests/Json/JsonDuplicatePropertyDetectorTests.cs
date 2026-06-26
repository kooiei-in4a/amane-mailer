using Amane.Mailer.Json;

namespace Amane.Mailer.Tests.Json;

public sealed class JsonDuplicatePropertyDetectorTests
{
    [Fact]
    public void Returns_false_for_unique_properties()
    {
        const string json = """
            {
              "subject": "Subject",
              "to": [{ "email": "a@example.com" }],
              "metadata": { "form_id": "1", "campaign": "spring" }
            }
            """;

        Assert.False(JsonDuplicatePropertyDetector.HasDuplicateProperty(json));
    }

    [Fact]
    public void Detects_top_level_duplicate()
    {
        const string json = """
            { "subject": "A", "subject": "B" }
            """;

        Assert.True(JsonDuplicatePropertyDetector.HasDuplicateProperty(json));
    }

    [Fact]
    public void Detects_nested_recipient_duplicate()
    {
        const string json = """
            { "to": [{ "email": "a@example.com", "email": "b@example.com" }] }
            """;

        Assert.True(JsonDuplicatePropertyDetector.HasDuplicateProperty(json));
    }

    [Fact]
    public void Detects_metadata_duplicate()
    {
        const string json = """
            { "metadata": { "form_id": "1", "form_id": "2" } }
            """;

        Assert.True(JsonDuplicatePropertyDetector.HasDuplicateProperty(json));
    }

    [Fact]
    public void Detects_escaped_key_duplicate()
    {
        // The second key spells "email" with a l escape for the final 'l'.
        // Utf8JsonReader unescapes it, so it collides with the plain "email" key.
        var escapedKey = "emai" + "\\u006C";
        var json = $$"""
            { "to": [{ "email": "a@example.com", "{{escapedKey}}": "b@example.com" }] }
            """;

        Assert.True(JsonDuplicatePropertyDetector.HasDuplicateProperty(json));
    }

    [Fact]
    public void Does_not_flag_same_key_across_sibling_objects()
    {
        // Each recipient object legitimately carries its own "email"; this is the
        // forward-compatible multi-recipient shape and must not be a false positive.
        const string json = """
            {
              "to": [
                { "email": "a@example.com" },
                { "email": "b@example.com" }
              ]
            }
            """;

        Assert.False(JsonDuplicatePropertyDetector.HasDuplicateProperty(json));
    }
}
