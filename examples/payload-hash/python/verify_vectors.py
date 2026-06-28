#!/usr/bin/env python3
"""Verify Python payload_hash implementation against official test vectors."""

from __future__ import annotations

import json
import sys
from pathlib import Path

from mail_payload_hash import (
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


def main() -> int:
    vectors = json.loads(VECTORS_PATH.read_text(encoding="utf-8"))
    for vector in vectors:
        name = vector["name"]
        payload = vector["input"]
        expected_canonical = vector["expected_canonical_json"]
        expected_hash = vector["expected_sha256_hex"]

        actual_canonical = canonicalize(payload)
        if actual_canonical != expected_canonical:
            print(f"[FAIL] {name}: canonical JSON mismatch")
            print(f"  expected: {expected_canonical}")
            print(f"  actual:   {actual_canonical}")
            return 1

        actual_hash = compute_sha256_hex(payload)
        if actual_hash != expected_hash:
            print(f"[FAIL] {name}: SHA-256 mismatch")
            print(f"  expected: {expected_hash}")
            print(f"  actual:   {actual_hash}")
            return 1

        envelope_request = {
            "tenant_id": "00000000-0000-0000-0000-000000000101",
            "mail_request_id": "00000000-0000-0000-0000-000000000201",
            "payload_hash": "caller-provided-placeholder",
            **payload,
        }
        delivery_json = build_delivery_payload_json(envelope_request)
        if delivery_json != expected_canonical:
            print(f"[FAIL] {name}: delivery payload JSON mismatch")
            print(f"  expected: {expected_canonical}")
            print(f"  actual:   {delivery_json}")
            return 1

        delivery_hash = compute_delivery_payload_sha256_hex(envelope_request)
        if delivery_hash != expected_hash:
            print(f"[FAIL] {name}: delivery payload hash mismatch")
            print(f"  expected: {expected_hash}")
            print(f"  actual:   {delivery_hash}")
            return 1

    print(f"Python payload_hash examples passed ({len(vectors)} vectors).")
    return 0


if __name__ == "__main__":
    sys.exit(main())
