package mailpayloadhash

import (
	"encoding/json"
	"os"
	"path/filepath"
	"runtime"
	"testing"
)

type attachmentVectorEntry struct {
	FileName      string `json:"file_name"`
	ContentType   string `json:"content_type"`
	ByteLength    int64  `json:"byte_length"`
	ContentSHA256 string `json:"content_sha256"`
}

type payloadHashVector struct {
	Name                  string                   `json:"name"`
	Input                 json.RawMessage          `json:"input"`
	Attachments           []attachmentVectorEntry  `json:"attachments"`
	ExpectedCanonicalJSON string                   `json:"expected_canonical_json"`
	ExpectedSHA256Hex     string                   `json:"expected_sha256_hex"`
}

func toAttachments(entries []attachmentVectorEntry) []Attachment {
	attachments := make([]Attachment, 0, len(entries))
	for _, entry := range entries {
		attachments = append(attachments, Attachment{
			FileName:      entry.FileName,
			ContentType:   entry.ContentType,
			ByteLength:    entry.ByteLength,
			ContentSHA256: entry.ContentSHA256,
		})
	}
	return attachments
}

// loadVectors reads one payload_hash test vector fixture file.
//
// Baseline (payload-hash-vectors.json): the pre-ADR-0023 single-To/attachment fixture, also
// read by the Python/TypeScript SDK test suites (sdk/python, sdk/typescript), which do not yet
// implement cc/bcc (issue #542). Recipient v1.3 (payload-hash-recipient-v1.3-vectors.json):
// ADR 0023 to/cc/bcc conformance vectors, verified here and by the .NET Contracts layer, but
// intentionally NOT read by the SDK test suites until #542 lands.
func loadVectors(t *testing.T, root, fileName string) []payloadHashVector {
	t.Helper()
	vectorsPath := filepath.Join(
		root,
		"tests",
		"Amane.Mailer.Contracts.Tests",
		"TestVectors",
		fileName,
	)
	data, err := os.ReadFile(vectorsPath)
	if err != nil {
		t.Fatalf("read vectors: %v", err)
	}

	var vectors []payloadHashVector
	if err := json.Unmarshal(data, &vectors); err != nil {
		t.Fatalf("parse vectors: %v", err)
	}
	return vectors
}

func TestSharedTestVectorsMatchCanonicalJSONAndHash(t *testing.T) {
	root := repoRoot(t)
	vectors := append(
		loadVectors(t, root, "payload-hash-vectors.json"),
		loadVectors(t, root, "payload-hash-recipient-v1.3-vectors.json")...,
	)

	for _, vector := range vectors {
		t.Run(vector.Name, func(t *testing.T) {
			var payload map[string]any
			if err := json.Unmarshal(vector.Input, &payload); err != nil {
				t.Fatalf("parse payload map: %v", err)
			}
			attachments := toAttachments(vector.Attachments)

			actualCanonical, err := BuildDeliveryPayloadJSONWithAttachments(payload, attachments)
			if err != nil {
				t.Fatalf("canonicalize: %v", err)
			}
			if actualCanonical != vector.ExpectedCanonicalJSON {
				t.Fatalf("canonical mismatch\nexpected: %s\nactual:   %s", vector.ExpectedCanonicalJSON, actualCanonical)
			}

			actualHash, err := ComputeDeliveryPayloadSHA256HexWithAttachments(payload, attachments)
			if err != nil {
				t.Fatalf("hash: %v", err)
			}
			if actualHash != vector.ExpectedSHA256Hex {
				t.Fatalf("hash mismatch\nexpected: %s\nactual:   %s", vector.ExpectedSHA256Hex, actualHash)
			}

			envelope := map[string]any{
				"tenant_id":       "00000000-0000-0000-0000-000000000101",
				"mail_request_id": "00000000-0000-0000-0000-000000000201",
				"payload_hash":    "caller-provided-placeholder",
			}
			for key, value := range payload {
				envelope[key] = value
			}

			deliveryJSON, err := BuildDeliveryPayloadJSONWithAttachments(envelope, attachments)
			if err != nil {
				t.Fatalf("delivery json: %v", err)
			}
			if deliveryJSON != vector.ExpectedCanonicalJSON {
				t.Fatalf("delivery json mismatch\nexpected: %s\nactual:   %s", vector.ExpectedCanonicalJSON, deliveryJSON)
			}

			deliveryHash, err := ComputeDeliveryPayloadSHA256HexWithAttachments(envelope, attachments)
			if err != nil {
				t.Fatalf("delivery hash: %v", err)
			}
			if deliveryHash != vector.ExpectedSHA256Hex {
				t.Fatalf("delivery hash mismatch\nexpected: %s\nactual:   %s", vector.ExpectedSHA256Hex, deliveryHash)
			}
		})
	}
}

func repoRoot(t *testing.T) string {
	t.Helper()
	_, file, _, ok := runtime.Caller(0)
	if !ok {
		t.Fatal("runtime caller failed")
	}
	return filepath.Clean(filepath.Join(filepath.Dir(file), "..", "..", ".."))
}
