"""Mailer payload_hash helper for Python consumers."""

from __future__ import annotations

import hashlib
import json
from typing import Any

INCLUDED_FIELDS = frozenset(
    [
        "source_service",
        "purpose",
        "to",
        "subject",
        "html_body",
        "text_body",
        "reply_to",
        "metadata",
    ]
)


def escape_json_string(value: str) -> str:
    parts = ['"']
    for character in value:
        code = ord(character)
        if character == '"':
            parts.append('\\"')
        elif character == "\\":
            parts.append("\\\\")
        elif character == "\b":
            parts.append("\\b")
        elif character == "\f":
            parts.append("\\f")
        elif character == "\n":
            parts.append("\\n")
        elif character == "\r":
            parts.append("\\r")
        elif character == "\t":
            parts.append("\\t")
        elif code < 0x20:
            parts.append(f"\\u{code:04x}")
        else:
            parts.append(character)
    parts.append('"')
    return "".join(parts)


def canonicalize(value: Any) -> str:
    if value is None:
        return "null"
    if isinstance(value, bool):
        return "true" if value else "false"
    if isinstance(value, str):
        return escape_json_string(value)
    if isinstance(value, int) and not isinstance(value, bool):
        return str(value)
    if isinstance(value, float):
        return format(value, "g")
    if isinstance(value, list):
        return "[" + ",".join(canonicalize(item) for item in value) + "]"
    if isinstance(value, dict):
        return _canonicalize_object(value)
    raise TypeError(f"Unsupported JSON value type: {type(value)!r}")


def _canonicalize_object(value: dict[str, Any]) -> str:
    parts = []
    for key in sorted(value.keys()):
        parts.append(f"{escape_json_string(key)}:{canonicalize(value[key])}")
    return "{" + ",".join(parts) + "}"


def build_delivery_payload_json(request: dict[str, Any]) -> str:
    filtered = {key: value for key, value in request.items() if key in INCLUDED_FIELDS}
    return _canonicalize_object(filtered)


def compute_sha256_hex(json_value: Any) -> str:
    canonical_json = canonicalize(json_value)
    return hashlib.sha256(canonical_json.encode("utf-8")).hexdigest()


def compute_delivery_payload_sha256_hex(request: dict[str, Any]) -> str:
    delivery_json = build_delivery_payload_json(request)
    return compute_sha256_hex(json.loads(delivery_json))
