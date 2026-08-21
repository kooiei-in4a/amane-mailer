"""Mailer payload_hash helper for Python consumers."""

from __future__ import annotations

import hashlib
import json
import unicodedata
from typing import Any

INCLUDED_FIELDS = frozenset(
    [
        "source_service",
        "purpose",
        "to",
        "cc",
        "bcc",
        "subject",
        "html_body",
        "text_body",
        "reply_to",
        "metadata",
        "attachments",
    ]
)

# attachments is included with a special projection (ADR 0022 D-03): absent or an empty list
# omits the field entirely from the hash document (byte-identical to the pre-attachment hash);
# a non-empty list is re-projected to exactly file_name (NFC), content_type, byte_length,
# content_sha256, and a zero-based order generated from list position -- never the raw
# content_base64 or an unverified declared content_type. Pass the *verified* attachment values
# (post decode/validation), not the raw request body's attachments array.
ATTACHMENTS_FIELD_NAME = "attachments"
RECIPIENT_FIELD_NAMES = ("to", "cc", "bcc")


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


def _utf16_code_units(value: str) -> list[int]:
    encoded = value.encode("utf-16-le")
    return [
        encoded[index] | (encoded[index + 1] << 8)
        for index in range(0, len(encoded), 2)
    ]


def _sort_keys_ordinal(keys: list[str]) -> list[str]:
    return sorted(keys, key=_utf16_code_units)


def _canonicalize_object(value: dict[str, Any]) -> str:
    parts = []
    for key in _sort_keys_ordinal(list(value.keys())):
        parts.append(f"{escape_json_string(key)}:{canonicalize(value[key])}")
    return "{" + ",".join(parts) + "}"


def _project_attachments(attachments: list[dict[str, Any]]) -> list[dict[str, Any]]:
    """Projects verified attachment values to the fixed 5-field hash object (ADR 0022 D-03)."""
    return [
        {
            "file_name": unicodedata.normalize("NFC", attachment["file_name"]),
            "content_type": attachment["content_type"],
            "byte_length": attachment["byte_length"],
            "content_sha256": attachment["content_sha256"],
            "order": order,
        }
        for order, attachment in enumerate(attachments)
    ]


def _project_recipient_role(
    role: list[dict[str, Any]] | None,
) -> list[dict[str, Any]] | None:
    if not role:
        return None
    projected = []
    for recipient in role:
        entry: dict[str, Any] = {"email": recipient["email"].strip()}
        display_name = recipient.get("display_name")
        if display_name is not None and display_name.strip() != "":
            entry["display_name"] = display_name
        projected.append(entry)
    return projected


def build_delivery_payload_json(
    request: dict[str, Any],
    attachments: list[dict[str, Any]] | None = None,
) -> str:
    filtered = {
        key: value
        for key, value in request.items()
        if key in INCLUDED_FIELDS
        and key != ATTACHMENTS_FIELD_NAME
        and key not in RECIPIENT_FIELD_NAMES
    }
    for field_name in RECIPIENT_FIELD_NAMES:
        if field_name in request:
            projected_role = _project_recipient_role(request[field_name])
            if projected_role is not None:
                filtered[field_name] = projected_role
    if attachments:
        filtered[ATTACHMENTS_FIELD_NAME] = _project_attachments(attachments)
    return _canonicalize_object(filtered)


def compute_sha256_hex(json_value: Any) -> str:
    canonical_json = canonicalize(json_value)
    return hashlib.sha256(canonical_json.encode("utf-8")).hexdigest()


def compute_delivery_payload_sha256_hex(
    request: dict[str, Any],
    attachments: list[dict[str, Any]] | None = None,
) -> str:
    delivery_json = build_delivery_payload_json(request, attachments)
    return hashlib.sha256(delivery_json.encode("utf-8")).hexdigest()
