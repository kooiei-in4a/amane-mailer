#!/usr/bin/env python3
"""Verify payload_hash for a Mailer mail request JSON file."""

from __future__ import annotations

import argparse
import json
import re
import sys
from dataclasses import dataclass
from pathlib import Path
from typing import Any

from mail_payload_hash import (
    INCLUDED_FIELDS,
    build_delivery_payload_json,
    compute_delivery_payload_sha256_hex,
)

EXCLUDED_FIELDS = frozenset(
    [
        "tenant_id",
        "mail_request_id",
        "payload_hash",
    ]
)

LOWERCASE_SHA256_HEX_PATTERN = re.compile(r"^[0-9a-f]{64}$")


@dataclass(frozen=True)
class VerifyResult:
    included_fields: tuple[str, ...]
    excluded_fields_present: tuple[str, ...]
    canonical_json: str
    computed_hash: str
    request_hash: str | None
    matches: bool | None


def verify_request_data(request: dict[str, Any]) -> VerifyResult:
    included = tuple(sorted(key for key in request if key in INCLUDED_FIELDS))
    excluded_present = tuple(sorted(key for key in request if key in EXCLUDED_FIELDS))
    canonical_json = build_delivery_payload_json(request)
    computed_hash = compute_delivery_payload_sha256_hex(request)
    request_hash = request.get("payload_hash")
    if request_hash is None:
        matches = None
    else:
        request_hash_text = str(request_hash)
        matches = (
            LOWERCASE_SHA256_HEX_PATTERN.fullmatch(request_hash_text) is not None
            and request_hash_text == computed_hash
        )

    return VerifyResult(
        included_fields=included,
        excluded_fields_present=excluded_present,
        canonical_json=canonical_json,
        computed_hash=computed_hash,
        request_hash=None if request_hash is None else str(request_hash),
        matches=matches,
    )


def format_verify_result(result: VerifyResult) -> str:
    lines = [
        "Included fields (hash input):",
    ]
    if result.included_fields:
        lines.extend(f"  - {field}" for field in result.included_fields)
    else:
        lines.append("  (none)")

    lines.append("")
    lines.append("Excluded from hash (present in request):")
    if result.excluded_fields_present:
        lines.extend(f"  - {field}" for field in result.excluded_fields_present)
    else:
        lines.append("  (none)")

    lines.extend(
        [
            "",
            "Canonical JSON:",
            result.canonical_json,
            "",
            "Computed SHA-256:",
            result.computed_hash,
        ]
    )

    if result.request_hash is None:
        lines.extend(
            [
                "",
                "Request payload_hash:",
                "(missing)",
                "",
                "Result: no payload_hash field to compare",
            ]
        )
    else:
        lines.extend(
            [
                "",
                "Request payload_hash:",
                result.request_hash,
                "",
                f"Result: {'MATCH' if result.matches else 'MISMATCH'}",
            ]
        )

    return "\n".join(lines)


def _parse_object_pairs(pairs: list[tuple[str, Any]]) -> dict[str, Any]:
    keys = [key for key, _ in pairs]
    if len(keys) != len(set(keys)):
        duplicate = next(key for key in keys if keys.count(key) > 1)
        raise ValueError(f"Duplicate JSON property: {duplicate!r}")
    return dict(pairs)


def parse_request_json(raw: str) -> dict[str, Any]:
    parsed = json.loads(raw, object_pairs_hook=_parse_object_pairs)
    if not isinstance(parsed, dict):
        raise ValueError("Mail request JSON must be an object.")
    return parsed


def load_request_json(path: Path) -> dict[str, Any]:
    return parse_request_json(path.read_text(encoding="utf-8"))


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(
        description="Verify payload_hash for a Mailer mail request JSON file.",
    )
    parser.add_argument(
        "request_json",
        type=Path,
        help="Path to the mail request JSON you plan to POST.",
    )
    args = parser.parse_args(argv)

    try:
        request = load_request_json(args.request_json)
        result = verify_request_data(request)
    except (OSError, json.JSONDecodeError, ValueError, TypeError) as error:
        print(f"[error] {error}", file=sys.stderr)
        return 2

    print(format_verify_result(result))

    if result.matches is False:
        return 1
    return 0


if __name__ == "__main__":
    sys.exit(main())
