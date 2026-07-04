#!/usr/bin/env python3
"""Verify request JSON verifier against official payload_hash test vectors."""

from __future__ import annotations

import json
import sys
from pathlib import Path

from verify_request import format_verify_result, parse_request_json, verify_request_data

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
        expected_hash = vector["expected_sha256_hex"]
        expected_canonical = vector["expected_canonical_json"]

        matching_request = {
            "tenant_id": "00000000-0000-0000-0000-000000000101",
            "mail_request_id": "00000000-0000-0000-0000-000000000201",
            "payload_hash": expected_hash,
            **payload,
        }
        match_result = verify_request_data(matching_request)
        if match_result.canonical_json != expected_canonical:
            print(f"[FAIL] {name}: canonical JSON mismatch in verifier")
            print(f"  expected: {expected_canonical}")
            print(f"  actual:   {match_result.canonical_json}")
            return 1
        if match_result.computed_hash != expected_hash:
            print(f"[FAIL] {name}: computed hash mismatch in verifier")
            print(f"  expected: {expected_hash}")
            print(f"  actual:   {match_result.computed_hash}")
            return 1
        if match_result.matches is not True:
            print(f"[FAIL] {name}: expected MATCH for correct payload_hash")
            print(format_verify_result(match_result))
            return 1

        mismatching_request = dict(matching_request)
        mismatching_request["payload_hash"] = "0" * 64
        mismatch_result = verify_request_data(mismatching_request)
        if mismatch_result.matches is not False:
            print(f"[FAIL] {name}: expected MISMATCH for incorrect payload_hash")
            print(format_verify_result(mismatch_result))
            return 1

        uppercase_request = dict(matching_request)
        uppercase_request["payload_hash"] = expected_hash.upper()
        uppercase_result = verify_request_data(uppercase_request)
        if uppercase_result.matches is not False:
            print(f"[FAIL] {name}: expected MISMATCH for uppercase payload_hash")
            print(format_verify_result(uppercase_result))
            return 1

    duplicate_json = (
        '{'
        '"tenant_id":"00000000-0000-0000-0000-000000000101",'
        '"mail_request_id":"00000000-0000-0000-0000-000000000201",'
        '"source_service":"example-service",'
        '"purpose":"FormResponseNotification",'
        '"to":[{"email":"admin@example.com"}],'
        '"subject":"First subject",'
        '"text_body":"A new response arrived.",'
        f'"payload_hash":"{vectors[0]["expected_sha256_hex"]}",'
        '"subject":"Duplicate subject"'
        '}'
    )
    try:
        parse_request_json(duplicate_json)
    except ValueError as error:
        if "Duplicate JSON property" not in str(error):
            print(f"[FAIL] duplicate property: unexpected error: {error}")
            return 1
    else:
        print("[FAIL] duplicate property: expected input error")
        return 1

    nested_duplicate_json = (
        '{'
        '"tenant_id":"00000000-0000-0000-0000-000000000101",'
        '"mail_request_id":"00000000-0000-0000-0000-000000000201",'
        '"source_service":"example-service",'
        '"purpose":"FormResponseNotification",'
        '"to":[{"email":"admin@example.com"}],'
        '"subject":"New response",'
        '"text_body":"A new response arrived.",'
        '"metadata":{"form_id":"form-001","form_id":"form-002"},'
        f'"payload_hash":"{vectors[0]["expected_sha256_hex"]}"'
        '}'
    )
    try:
        parse_request_json(nested_duplicate_json)
    except ValueError as error:
        if "Duplicate JSON property" not in str(error):
            print(f"[FAIL] nested duplicate property: unexpected error: {error}")
            return 1
    else:
        print("[FAIL] nested duplicate property: expected input error")
        return 1

    unknown_top_level_json = (
        '{'
        '"tenant_id":"00000000-0000-0000-0000-000000000101",'
        '"mail_request_id":"00000000-0000-0000-0000-000000000201",'
        '"source_service":"example-service",'
        '"purpose":"FormResponseNotification",'
        '"to":[{"email":"admin@example.com"}],'
        '"subject":"New response",'
        '"text_body":"A new response arrived.",'
        f'"payload_hash":"{vectors[0]["expected_sha256_hex"]}",'
        '"unexpected":"value"'
        '}'
    )
    try:
        parse_request_json(unknown_top_level_json)
    except ValueError as error:
        if "Unknown request property" not in str(error):
            print(f"[FAIL] unknown top-level property: unexpected error: {error}")
            return 1
    else:
        print("[FAIL] unknown top-level property: expected input error")
        return 1

    unknown_recipient_json = (
        '{'
        '"tenant_id":"00000000-0000-0000-0000-000000000101",'
        '"mail_request_id":"00000000-0000-0000-0000-000000000201",'
        '"source_service":"example-service",'
        '"purpose":"FormResponseNotification",'
        '"to":[{"email":"admin@example.com","unexpected":"value"}],'
        '"subject":"New response",'
        '"text_body":"A new response arrived.",'
        f'"payload_hash":"{vectors[0]["expected_sha256_hex"]}"'
        '}'
    )
    try:
        parse_request_json(unknown_recipient_json)
    except ValueError as error:
        if "Unknown recipient property" not in str(error):
            print(f"[FAIL] unknown recipient property: unexpected error: {error}")
            return 1
    else:
        print("[FAIL] unknown recipient property: expected input error")
        return 1

    print(f"Python payload_hash request verifier passed ({len(vectors)} vectors).")
    return 0


if __name__ == "__main__":
    sys.exit(main())
