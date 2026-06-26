using Amane.Mailer.Delivery;

namespace Amane.Mailer.Tests;

public sealed class ProviderErrorSanitizerTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Sanitize_returns_placeholder_for_blank_input(string? raw)
    {
        var result = ProviderErrorSanitizer.Sanitize(raw);

        Assert.Equal("Provider returned an error with no message.", result);
    }

    [Fact]
    public void Sanitize_masks_acs_connection_string_fragments()
    {
        const string raw =
            "Connection refused for endpoint=https://res.communication.azure.com/;accesskey=AbC123SecretKeyValue==";

        var result = ProviderErrorSanitizer.Sanitize(raw);

        Assert.DoesNotContain("res.communication.azure.com", result, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("AbC123SecretKeyValue", result, StringComparison.Ordinal);
        Assert.Contains("endpoint=***", result, StringComparison.Ordinal);
        Assert.Contains("accesskey=***", result, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("token=eyJhbGciOiJIUzI1Ni8.payload.signature", "token=***")]
    [InlineData("password=Sup3rSecret!", "password=***")]
    [InlineData("secret=abcdef123456", "secret=***")]
    [InlineData("SharedAccessKey=base64key==", "SharedAccessKey=***")]
    public void Sanitize_masks_credential_assignments(string raw, string expectedFragment)
    {
        var result = ProviderErrorSanitizer.Sanitize(raw);

        Assert.Contains(expectedFragment, result, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Sanitize_does_not_mask_unrelated_key_words()
    {
        const string raw = "monkey=3 retries exhausted";

        var result = ProviderErrorSanitizer.Sanitize(raw);

        Assert.Equal("monkey=3 retries exhausted", result);
    }

    [Fact]
    public void Sanitize_masks_url_query_string()
    {
        const string raw =
            "Request to https://res.blob.core.windows.net/path?sig=SECRETSIG&se=2025-01-01 failed";

        var result = ProviderErrorSanitizer.Sanitize(raw);

        Assert.DoesNotContain("SECRETSIG", result, StringComparison.Ordinal);
        Assert.DoesNotContain("sig=", result, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("?***", result, StringComparison.Ordinal);
    }

    [Fact]
    public void Sanitize_masks_bearer_tokens()
    {
        const string raw = "Authorization failed: Bearer abc123.def456.ghi789 was rejected";

        var result = ProviderErrorSanitizer.Sanitize(raw);

        Assert.DoesNotContain("abc123.def456.ghi789", result, StringComparison.Ordinal);
        Assert.Contains("Bearer ***", result, StringComparison.Ordinal);
    }

    [Fact]
    public void Sanitize_masks_email_addresses()
    {
        const string raw = "550 5.1.1 recipient user@example.com does not exist";

        var result = ProviderErrorSanitizer.Sanitize(raw);

        Assert.DoesNotContain("user@example.com", result, StringComparison.Ordinal);
        Assert.Contains("***", result, StringComparison.Ordinal);
        Assert.Contains("does not exist", result, StringComparison.Ordinal);
    }

    [Fact]
    public void Sanitize_collapses_newlines_into_single_line()
    {
        const string raw = "First line.\r\nSecond line.\n\tIndented third.";

        var result = ProviderErrorSanitizer.Sanitize(raw);

        Assert.DoesNotContain("\n", result, StringComparison.Ordinal);
        Assert.DoesNotContain("\r", result, StringComparison.Ordinal);
        Assert.Equal("First line. Second line. Indented third.", result);
    }

    [Fact]
    public void Sanitize_truncates_overlong_messages()
    {
        var raw = new string('x', ProviderErrorSanitizer.MaxLength + 50);

        var result = ProviderErrorSanitizer.Sanitize(raw);

        Assert.True(result.Length <= ProviderErrorSanitizer.MaxLength + 3);
        Assert.EndsWith("...", result, StringComparison.Ordinal);
    }

    [Fact]
    public void Sanitize_preserves_harmless_status_messages()
    {
        const string raw = "The SMTP server returned status 451: temporary local problem, try again later.";

        var result = ProviderErrorSanitizer.Sanitize(raw);

        Assert.Equal(raw, result);
    }

    // Reviewer PoC cases — secrets that leaked before the regex fixes.

    [Theory]
    [InlineData(@"password=""hunter2"" failed", "hunter2")]
    [InlineData(@"SharedAccessKey='Zm9vYmFy' failed", "Zm9vYmFy")]
    [InlineData("api-key: sk_test_1234567890 failed", "sk_test_1234567890")]
    [InlineData("token=eyJhbGci\r\npayload.signature failed", "payload.signature")]
    [InlineData("550 recipient ユーザー@例え.テスト does not exist", "ユーザー@例え.テスト")]
    public void Sanitize_masks_credential_and_email_variants(string raw, string leaked)
    {
        var result = ProviderErrorSanitizer.Sanitize(raw);

        Assert.DoesNotContain(leaked, result, StringComparison.Ordinal);
    }

    [Fact]
    public void Sanitize_handles_colon_separator_for_header_style_credentials()
    {
        const string raw = "api-key: sk_test_1234567890abcdef failed";

        var result = ProviderErrorSanitizer.Sanitize(raw);

        Assert.DoesNotContain("sk_test_1234567890abcdef", result, StringComparison.Ordinal);
        Assert.Contains("failed", result, StringComparison.Ordinal);
    }

    [Fact]
    public void Sanitize_masks_idn_email_address()
    {
        const string raw = "550 recipient ユーザー@例え.テスト does not exist";

        var result = ProviderErrorSanitizer.Sanitize(raw);

        Assert.DoesNotContain("ユーザー@例え.テスト", result, StringComparison.Ordinal);
        Assert.Contains("does not exist", result, StringComparison.Ordinal);
    }

    [Fact]
    public void Sanitize_is_idempotent()
    {
        const string raw =
            "endpoint=https://res.communication.azure.com/;accesskey=KEY== for user@example.com";

        var once = ProviderErrorSanitizer.Sanitize(raw);
        var twice = ProviderErrorSanitizer.Sanitize(once);

        Assert.Equal(once, twice);
    }

    [Fact]
    public void Sanitize_scrubs_realistic_acs_request_failed_message()
    {
        // Shape of an Azure.RequestFailedException message that may surface ACS details.
        const string raw =
            "Service request failed.\nStatus: 401 (Unauthorized)\n" +
            "Connect to endpoint=https://acme.communication.azure.com/;accesskey=Zm9vYmFyc2VjcmV0a2V5==\n" +
            "for sender noreply@acme.example.com";

        var result = ProviderErrorSanitizer.Sanitize(raw);

        Assert.DoesNotContain("Zm9vYmFyc2VjcmV0a2V5", result, StringComparison.Ordinal);
        Assert.DoesNotContain("acme.communication.azure.com", result, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("noreply@acme.example.com", result, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("401 (Unauthorized)", result, StringComparison.Ordinal);
    }

    [Fact]
    public void Sanitize_scrubs_realistic_smtp_auth_failure_message()
    {
        const string raw =
            "535: 5.7.8 Authentication failed for user smtp-user@mail.example.com password=hunter2";

        var result = ProviderErrorSanitizer.Sanitize(raw);

        Assert.DoesNotContain("smtp-user@mail.example.com", result, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("hunter2", result, StringComparison.Ordinal);
        Assert.Contains("password=***", result, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Authentication failed", result, StringComparison.Ordinal);
    }
}
