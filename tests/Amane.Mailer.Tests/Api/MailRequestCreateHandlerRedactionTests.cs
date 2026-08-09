using Amane.Mailer.Api;

namespace Amane.Mailer.Tests.Api;

/// <summary>
/// Pure unit coverage for <see cref="MailRequestCreateHandler.RedactAttachmentContentBase64"/>
/// (ADR 0022 D-04/D-14): content_base64 must never survive into what gets persisted.
/// </summary>
public sealed class MailRequestCreateHandlerRedactionTests
{
    [Fact]
    public void Redacts_content_base64_from_every_attachment_while_keeping_other_fields()
    {
        const string requestBody = """
            {
              "subject": "Invoice",
              "attachments": [
                {
                  "file_name": "a.pdf",
                  "content_type": "application/pdf",
                  "content_base64": "AAAAAAAAAAAAAAAAAAAA",
                  "content_sha256": "aa",
                  "byte_length": 10
                },
                {
                  "file_name": "b.pdf",
                  "content_type": "application/pdf",
                  "content_base64": "BBBBBBBBBBBBBBBBBBBB",
                  "content_sha256": "bb",
                  "byte_length": 20
                }
              ]
            }
            """;

        var redacted = MailRequestCreateHandler.RedactAttachmentContentBase64(requestBody);

        Assert.DoesNotContain("content_base64", redacted, StringComparison.Ordinal);
        Assert.DoesNotContain("AAAAAAAAAAAAAAAAAAAA", redacted, StringComparison.Ordinal);
        Assert.DoesNotContain("BBBBBBBBBBBBBBBBBBBB", redacted, StringComparison.Ordinal);
        Assert.Contains("a.pdf", redacted, StringComparison.Ordinal);
        Assert.Contains("b.pdf", redacted, StringComparison.Ordinal);
        Assert.Contains("\"content_sha256\":\"aa\"", redacted, StringComparison.Ordinal);
        Assert.Contains("\"byte_length\":20", redacted, StringComparison.Ordinal);
        Assert.Contains("\"subject\":\"Invoice\"", redacted, StringComparison.Ordinal);
    }

    [Fact]
    public void Leaves_a_request_without_attachments_unchanged_in_structure()
    {
        const string requestBody = """{"subject":"No attachments","metadata":{"k":"v"}}""";

        var redacted = MailRequestCreateHandler.RedactAttachmentContentBase64(requestBody);

        Assert.Contains("\"subject\":\"No attachments\"", redacted, StringComparison.Ordinal);
        Assert.Contains("\"metadata\":{\"k\":\"v\"}", redacted, StringComparison.Ordinal);
    }

    [Fact]
    public void Handles_an_empty_attachments_array()
    {
        const string requestBody = """{"subject":"Empty","attachments":[]}""";

        var redacted = MailRequestCreateHandler.RedactAttachmentContentBase64(requestBody);

        Assert.Contains("\"attachments\":[]", redacted, StringComparison.Ordinal);
    }

    [Fact]
    public void Redacts_recipient_email_and_display_name_from_to_cc_and_bcc_snapshots()
    {
        const string requestBody = """
            {
              "to": [{"email":"to-secret@example.com","display_name":"To Secret"}],
              "cc": [{"email":"cc-secret@example.com","display_name":"Cc Secret"}],
              "bcc": [{"email":"bcc-secret@example.com","display_name":"Bcc Secret"}],
              "subject": "Invoice"
            }
            """;

        var redacted = MailRequestCreateHandler.RedactRecipientPii(requestBody);

        Assert.DoesNotContain("to-secret@example.com", redacted, StringComparison.Ordinal);
        Assert.DoesNotContain("cc-secret@example.com", redacted, StringComparison.Ordinal);
        Assert.DoesNotContain("bcc-secret@example.com", redacted, StringComparison.Ordinal);
        Assert.DoesNotContain("To Secret", redacted, StringComparison.Ordinal);
        Assert.DoesNotContain("Cc Secret", redacted, StringComparison.Ordinal);
        Assert.DoesNotContain("Bcc Secret", redacted, StringComparison.Ordinal);
        Assert.Equal(6, redacted.Split("[REDACTED]", StringSplitOptions.None).Length - 1);
        Assert.Contains("\"subject\":\"Invoice\"", redacted, StringComparison.Ordinal);
    }
}
