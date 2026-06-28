package mailpayloadhash

import (
	"encoding/json"
	"os"
	"path/filepath"
	"runtime"
	"testing"
)

type payloadHashVector struct {
	Name                  string          `json:"name"`
	Input                 json.RawMessage `json:"input"`
	ExpectedCanonicalJSON string          `json:"expected_canonical_json"`
	ExpectedSHA256Hex     string          `json:"expected_sha256_hex"`
}

func TestSharedTestVectorsMatchCanonicalJSONAndHash(t *testing.T) {
	root := repoRoot(t)
	vectorsPath := filepath.Join(
		root,
		"tests",
		"Amane.Mailer.Contracts.Tests",
		"TestVectors",
		"payload-hash-vectors.json",
	)
	data, err := os.ReadFile(vectorsPath)
	if err != nil {
		t.Fatalf("read vectors: %v", err)
	}

	var vectors []payloadHashVector
	if err := json.Unmarshal(data, &vectors); err != nil {
		t.Fatalf("parse vectors: %v", err)
	}

	for _, vector := range vectors {
		t.Run(vector.Name, func(t *testing.T) {
			var input any
			if err := json.Unmarshal(vector.Input, &input); err != nil {
				t.Fatalf("parse input: %v", err)
			}

			actualCanonical, err := Canonicalize(input)
			if err != nil {
				t.Fatalf("canonicalize: %v", err)
			}
			if actualCanonical != vector.ExpectedCanonicalJSON {
				t.Fatalf("canonical mismatch\nexpected: %s\nactual:   %s", vector.ExpectedCanonicalJSON, actualCanonical)
			}

			actualHash, err := ComputeSHA256Hex(input)
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
			var payload map[string]any
			if err := json.Unmarshal(vector.Input, &payload); err != nil {
				t.Fatalf("parse payload map: %v", err)
			}
			for key, value := range payload {
				envelope[key] = value
			}

			deliveryJSON, err := BuildDeliveryPayloadJSON(envelope)
			if err != nil {
				t.Fatalf("delivery json: %v", err)
			}
			if deliveryJSON != vector.ExpectedCanonicalJSON {
				t.Fatalf("delivery json mismatch\nexpected: %s\nactual:   %s", vector.ExpectedCanonicalJSON, deliveryJSON)
			}

			deliveryHash, err := ComputeDeliveryPayloadSHA256Hex(envelope)
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
