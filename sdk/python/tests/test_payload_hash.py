from __future__ import annotations

import json
import unittest
from pathlib import Path

from amane_mailer.payload_hash import (
    build_delivery_payload_json,
    canonicalize,
    compute_delivery_payload_sha256_hex,
    compute_sha256_hex,
)

ROOT = Path(__file__).resolve().parents[3]
VECTORS_PATH = (
    ROOT
    / "tests"
    / "Amane.Mailer.Contracts.Tests"
    / "TestVectors"
    / "payload-hash-vectors.json"
)


class PayloadHashVectorTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls) -> None:
        cls.vectors = json.loads(VECTORS_PATH.read_text(encoding="utf-8"))

    def test_payload_hash_matches_official_vectors(self) -> None:
        for vector in self.vectors:
            with self.subTest(name=vector["name"]):
                payload = vector["input"]
                expected_canonical = vector["expected_canonical_json"]
                expected_hash = vector["expected_sha256_hex"]

                self.assertEqual(canonicalize(payload), expected_canonical)
                self.assertEqual(compute_sha256_hex(payload), expected_hash)

                envelope_request = {
                    "tenant_id": "00000000-0000-0000-0000-000000000101",
                    "mail_request_id": "00000000-0000-0000-0000-000000000201",
                    "payload_hash": "caller-provided-placeholder",
                    **payload,
                }
                self.assertEqual(
                    build_delivery_payload_json(envelope_request),
                    expected_canonical,
                )
                self.assertEqual(
                    compute_delivery_payload_sha256_hex(envelope_request),
                    expected_hash,
                )


if __name__ == "__main__":
    unittest.main()
