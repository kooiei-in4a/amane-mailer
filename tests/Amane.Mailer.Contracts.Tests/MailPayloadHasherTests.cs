using Amane.Mailer.Contracts.Json;
using Amane.Mailer.Contracts.MailRequests;
using Amane.Mailer.Contracts.Security;
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

    [Fact]
    public async Task Shared_test_vectors_match_canonical_json_and_hash()
    {
        await using var stream = File.OpenRead(Path.Combine(AppContext.BaseDirectory, "TestVectors", "payload-hash-vectors.json"));
        var vectors = await JsonSerializer.DeserializeAsync<IReadOnlyList<PayloadHashVector>>(
            stream,
            options: null,
            TestContext.Current.CancellationToken);
        Assert.NotNull(vectors);

        foreach (var vector in vectors)
        {
            var json = vector.Input.GetRawText();

            Assert.Equal(vector.ExpectedCanonicalJson, MailPayloadHasher.Canonicalize(json));
            Assert.Equal(vector.ExpectedSha256Hex, MailPayloadHasher.ComputeSha256Hex(json));
        }
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

        [JsonPropertyName("expected_canonical_json")]
        public required string ExpectedCanonicalJson { get; init; }

        [JsonPropertyName("expected_sha256_hex")]
        public required string ExpectedSha256Hex { get; init; }
    }
}
