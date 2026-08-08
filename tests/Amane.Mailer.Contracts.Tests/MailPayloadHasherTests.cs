using Amane.Mailer.Contracts.Json;
using Amane.Mailer.Contracts.MailRequests;
using Amane.Mailer.Contracts.Security;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Amane.Mailer.Contracts.Tests;

public sealed class MailPayloadHasherTests
{
    [Fact]
    public void Canonicalize_sorts_object_properties_recursively()
    {
        var json = """
            {
              "z": true,
              "a": {
                "b": "second",
                "a": "first"
              },
              "items": [
                { "name": "b", "value": 2 },
                { "value": 1, "name": "a" }
              ]
            }
            """;

        var canonicalJson = MailPayloadHasher.Canonicalize(json);

        Assert.Equal(
            """{"a":{"a":"first","b":"second"},"items":[{"name":"b","value":2},{"name":"a","value":1}],"z":true}""",
            canonicalJson);
    }

    [Fact]
    public void ComputeSha256Hex_returns_same_hash_for_equivalent_payload_order()
    {
        const string first = """
            {
              "source_service": "example-service",
              "purpose": "FormResponseNotification",
              "subject": "New response",
              "to": [
                { "email": "admin@example.com" }
              ],
              "text_body": "A new response arrived."
            }
            """;
        const string second = """
            {
              "text_body": "A new response arrived.",
              "to": [
                { "email": "admin@example.com" }
              ],
              "subject": "New response",
              "purpose": "FormResponseNotification",
              "source_service": "example-service"
            }
            """;

        var hash = MailPayloadHasher.ComputeSha256Hex(first);

        Assert.Equal(hash, MailPayloadHasher.ComputeSha256Hex(second));
        Assert.Equal("7c6d491cc70ac1b48fcc770d90ff80ae8a13c0e5ed3284fd1de9705d7e801ea9", hash);
    }

    /// <summary>
    /// Baseline vectors: the pre-ADR-0023 single-To/attachment fixture set that existing
    /// Python/TypeScript SDKs (<c>sdk/python</c>, <c>sdk/typescript</c>) already implement and
    /// verify against. Kept byte-identical to the pre-#540 fixture so those SDK test suites
    /// (which read this exact file) do not need cc/bcc/trim support to stay green.
    /// </summary>
    [Fact]
    public async Task Shared_test_vectors_match_canonical_json_and_hash()
    {
        var vectors = await LoadVectorsAsync("payload-hash-vectors.json");
        AssertVectorsMatch(vectors);
    }

    /// <summary>
    /// ADR 0023 recipient (to/cc/bcc) conformance vectors, kept in a separate fixture so the
    /// existing Python/TypeScript SDK test suites -- which only implement the baseline
    /// single-To contract until issue #542 lands -- are not broken by these. The .NET Contracts
    /// layer and the language-independent <c>examples/payload-hash/{python,javascript,go}</c>
    /// reference verifiers (this PR's scope) validate both files.
    /// </summary>
    [Fact]
    public async Task Recipient_v1_3_test_vectors_match_canonical_json_and_hash()
    {
        var vectors = await LoadVectorsAsync("payload-hash-recipient-v1.3-vectors.json");
        AssertVectorsMatch(vectors);
    }

    /// <summary>
    /// Guards against the same vector name being reused across the two fixture files, which
    /// would make it ambiguous which file a name refers to in test failures, issue references,
    /// or the non-.NET reference verifiers that load both.
    /// </summary>
    [Fact]
    public async Task Baseline_and_recipient_v1_3_vectors_do_not_share_names()
    {
        var baselineNames = (await LoadVectorsAsync("payload-hash-vectors.json"))
            .Select(vector => vector.Name)
            .ToHashSet(StringComparer.Ordinal);
        var v13Names = (await LoadVectorsAsync("payload-hash-recipient-v1.3-vectors.json"))
            .Select(vector => vector.Name)
            .ToHashSet(StringComparer.Ordinal);

        Assert.Empty(baselineNames.Intersect(v13Names));
    }

    private static async Task<IReadOnlyList<PayloadHashVector>> LoadVectorsAsync(string fileName)
    {
        await using var stream = File.OpenRead(Path.Combine(AppContext.BaseDirectory, "TestVectors", fileName));
        var vectors = await JsonSerializer.DeserializeAsync<IReadOnlyList<PayloadHashVector>>(
            stream,
            options: null,
            TestContext.Current.CancellationToken);
        Assert.NotNull(vectors);
        return vectors;
    }

    private static void AssertVectorsMatch(IReadOnlyList<PayloadHashVector> vectors)
    {
        foreach (var vector in vectors)
        {
            var json = vector.Input.GetRawText();
            var attachments = vector.Attachments?
                .Select(attachment => new MailAttachmentHashInput(
                    attachment.FileName,
                    attachment.ContentType,
                    attachment.ByteLength,
                    attachment.ContentSha256))
                .ToArray();

            Assert.Equal(
                vector.ExpectedCanonicalJson,
                MailPayloadHasher.BuildDeliveryPayloadJson(json, attachments));
            Assert.Equal(
                vector.ExpectedSha256Hex,
                MailPayloadHasher.ComputeDeliveryPayloadSha256Hex(json, attachments));
        }
    }

    [Fact]
    public void BuildDeliveryPayloadJson_omits_attachments_for_null_and_empty_alike()
    {
        const string requestJson = """
            {
              "source_service": "example-service",
              "purpose": "FormResponseNotification",
              "subject": "New response",
              "to": [
                { "email": "admin@example.com" }
              ],
              "text_body": "A new response arrived."
            }
            """;

        var withoutAttachmentsList = MailPayloadHasher.BuildDeliveryPayloadJson(requestJson, null);
        var withEmptyAttachmentsList = MailPayloadHasher.BuildDeliveryPayloadJson(
            requestJson,
            Array.Empty<MailAttachmentHashInput>());

        Assert.Equal(withoutAttachmentsList, withEmptyAttachmentsList);
        Assert.DoesNotContain("attachments", withoutAttachmentsList, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildDeliveryPayloadJson_projects_only_the_five_canonical_attachment_fields()
    {
        const string requestJson = """
            {
              "source_service": "example-service",
              "purpose": "FormResponseNotification",
              "subject": "Invoice",
              "to": [
                { "email": "admin@example.com" }
              ],
              "text_body": "See attached.",
              "attachments": [
                {
                  "file_name": "invoice.pdf",
                  "content_type": "application/octet-stream",
                  "content_base64": "AAAA",
                  "content_sha256": "0000000000000000000000000000000000000000000000000000000000000",
                  "byte_length": 999
                }
              ]
            }
            """;

        var attachments = new[]
        {
            new MailAttachmentHashInput(
                "invoice.pdf",
                "application/pdf",
                4,
                "deadbeefdeadbeefdeadbeefdeadbeefdeadbeefdeadbeefdeadbeefdeadbeef"),
        };

        var json = MailPayloadHasher.BuildDeliveryPayloadJson(requestJson, attachments);

        // The verified projection (application/pdf, byte_length 4) wins; the raw JSON's
        // Consumer-declared content_type/byte_length and content_base64 are never used.
        Assert.Contains("\"content_type\":\"application/pdf\"", json, StringComparison.Ordinal);
        Assert.Contains("\"byte_length\":4", json, StringComparison.Ordinal);
        Assert.DoesNotContain("application/octet-stream", json, StringComparison.Ordinal);
        Assert.DoesNotContain("999", json, StringComparison.Ordinal);
        Assert.DoesNotContain("content_base64", json, StringComparison.Ordinal);
        Assert.DoesNotContain("AAAA", json, StringComparison.Ordinal);
        Assert.Contains("\"order\":0", json, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildDeliveryPayloadJson_excludes_routing_envelope_fields()
    {
        const string fullRequest = """
            {
              "tenant_id": "00000000-0000-0000-0000-000000000101",
              "mail_request_id": "00000000-0000-0000-0000-000000000201",
              "payload_hash": "caller-provided-placeholder",
              "source_service": "example-service",
              "purpose": "FormResponseNotification",
              "subject": "New response",
              "to": [
                { "email": "admin@example.com" }
              ],
              "text_body": "A new response arrived."
            }
            """;

        var deliveryPayloadJson = MailPayloadHasher.BuildDeliveryPayloadJson(fullRequest);

        Assert.Equal(
            """{"purpose":"FormResponseNotification","source_service":"example-service","subject":"New response","text_body":"A new response arrived.","to":[{"email":"admin@example.com"}]}""",
            deliveryPayloadJson);
        Assert.Equal(
            "7c6d491cc70ac1b48fcc770d90ff80ae8a13c0e5ed3284fd1de9705d7e801ea9",
            MailPayloadHasher.ComputeDeliveryPayloadSha256Hex(fullRequest));
    }

    [Fact]
    public void BuildDeliveryPayloadJson_preserves_explicit_null_but_not_omitted_fields()
    {
        const string omittedReplyTo = """
            {
              "source_service": "example-service",
              "purpose": "FormResponseNotification",
              "subject": "New response",
              "to": [
                { "email": "admin@example.com" }
              ],
              "text_body": "A new response arrived."
            }
            """;
        const string explicitNullReplyTo = """
            {
              "source_service": "example-service",
              "purpose": "FormResponseNotification",
              "subject": "New response",
              "to": [
                { "email": "admin@example.com" }
              ],
              "text_body": "A new response arrived.",
              "reply_to": null
            }
            """;

        var omitted = MailPayloadHasher.BuildDeliveryPayloadJson(omittedReplyTo);
        var explicitNull = MailPayloadHasher.BuildDeliveryPayloadJson(explicitNullReplyTo);

        Assert.DoesNotContain("reply_to", omitted, StringComparison.Ordinal);
        Assert.Contains("\"reply_to\":null", explicitNull, StringComparison.Ordinal);
        Assert.NotEqual(
            MailPayloadHasher.ComputeDeliveryPayloadSha256Hex(omittedReplyTo),
            MailPayloadHasher.ComputeDeliveryPayloadSha256Hex(explicitNullReplyTo));
    }

    [Fact]
    public void BuildDeliveryPayloadJson_from_app_constructed_dto_uses_delivery_fields_only()
    {
        var request = new MailRequestCreateRequest
        {
            TenantId = Guid.Parse("00000000-0000-0000-0000-000000000101"),
            MailRequestId = Guid.Parse("00000000-0000-0000-0000-000000000201"),
            PayloadHash = "caller-provided-placeholder",
            SourceService = "example-service",
            Purpose = "FormResponseNotification",
            Subject = "New response",
            To =
            [
                new MailRecipientDto
                {
                    Email = "admin@example.com",
                },
            ],
            TextBody = "A new response arrived.",
        };

        Assert.Equal(
            "7c6d491cc70ac1b48fcc770d90ff80ae8a13c0e5ed3284fd1de9705d7e801ea9",
            MailPayloadHasher.ComputeDeliveryPayloadSha256Hex(request));
    }

    [Fact]
    public void Openapi_example_payload_hash_matches_documented_value()
    {
        var request = new MailRequestCreateRequest
        {
            TenantId = Guid.Parse("00000000-0000-0000-0000-000000000301"),
            MailRequestId = Guid.Parse("018f7c2a-0000-7000-8000-000000000000"),
            PayloadHash = "caller-provided-placeholder",
            SourceService = "example-service",
            Purpose = "FormResponseNotification",
            Subject = "お問い合わせを受け付けました",
            To =
            [
                new MailRecipientDto
                {
                    Email = "user@example.com",
                    DisplayName = "山田太郎",
                },
            ],
            TextBody = "ご回答ありがとうございました。",
            HtmlBody = "<p>ご回答ありがとうございました。</p>",
            Metadata = new Dictionary<string, string>
            {
                ["form_id"] = "42",
            },
        };

        Assert.Equal(
            "9c24a8154fa03970c9a6512e680af20e2d64fa5555849b80525215a74388b8fe",
            MailPayloadHasher.ComputeDeliveryPayloadSha256Hex(request));
    }

    [Fact]
    public void Dto_and_raw_request_json_overloads_return_same_delivery_payload_hash()
    {
        var request = new MailRequestCreateRequest
        {
            TenantId = Guid.Parse("00000000-0000-0000-0000-000000000101"),
            MailRequestId = Guid.Parse("00000000-0000-0000-0000-000000000201"),
            PayloadHash = "caller-provided-placeholder",
            SourceService = "example-service",
            Purpose = "FormResponseNotification",
            Subject = "New response",
            To =
            [
                new MailRecipientDto
                {
                    Email = "admin@example.com",
                    DisplayName = "Admin",
                },
            ],
            HtmlBody = "<p>A new response arrived.</p>",
            TextBody = "A new response arrived.",
            Metadata = new Dictionary<string, string>
            {
                ["form_id"] = "form-123",
            },
        };

        var requestJson = JsonSerializer.Serialize(
            request,
            MailerContractsJsonContext.Default.MailRequestCreateRequest);

        Assert.Equal(
            MailPayloadHasher.ComputeDeliveryPayloadSha256Hex(request),
            MailPayloadHasher.ComputeDeliveryPayloadSha256Hex(requestJson));
    }

    private sealed record PayloadHashVector
    {
        [JsonPropertyName("name")]
        public required string Name { get; init; }

        [JsonPropertyName("input")]
        public required JsonElement Input { get; init; }

        /// <summary>
        /// Verified attachment values to project into the hash (ADR 0022 D-03). Absent for the
        /// pre-attachment vectors (no attachments-property call site at all); an explicit empty
        /// array is a distinct fixture case that must still omit "attachments" from the hash.
        /// </summary>
        [JsonPropertyName("attachments")]
        public IReadOnlyList<PayloadHashVectorAttachment>? Attachments { get; init; }

        [JsonPropertyName("expected_canonical_json")]
        public required string ExpectedCanonicalJson { get; init; }

        [JsonPropertyName("expected_sha256_hex")]
        public required string ExpectedSha256Hex { get; init; }
    }

    private sealed record PayloadHashVectorAttachment
    {
        [JsonPropertyName("file_name")]
        public required string FileName { get; init; }

        [JsonPropertyName("content_type")]
        public required string ContentType { get; init; }

        [JsonPropertyName("byte_length")]
        public required long ByteLength { get; init; }

        [JsonPropertyName("content_sha256")]
        public required string ContentSha256 { get; init; }
    }
}
