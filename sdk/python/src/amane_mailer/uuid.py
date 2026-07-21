"""UUID helpers for mail_request_id generation."""

from __future__ import annotations

import secrets
import uuid


def _format_uuid_bytes(raw: bytes) -> str:
    hex_value = raw.hex()
    return (
        f"{hex_value[0:8]}-{hex_value[8:12]}-{hex_value[12:16]}-"
        f"{hex_value[16:20]}-{hex_value[20:32]}"
    )


def generate_uuid_v7(now_ms: int | None = None) -> str:
    """Generate a UUIDv7 string. Falls back to UUIDv4 outside the 48-bit timestamp range."""
    timestamp_ms = int(now_ms if now_ms is not None else __import__("time").time() * 1000)
    if timestamp_ms < 0 or timestamp_ms >= 0x1000000000000:
        return str(uuid.uuid4())

    timestamp = timestamp_ms
    rand = secrets.token_bytes(10)

    uuid_bytes = bytearray(16)
    uuid_bytes[0] = (timestamp >> 40) & 0xFF
    uuid_bytes[1] = (timestamp >> 32) & 0xFF
    uuid_bytes[2] = (timestamp >> 24) & 0xFF
    uuid_bytes[3] = (timestamp >> 16) & 0xFF
    uuid_bytes[4] = (timestamp >> 8) & 0xFF
    uuid_bytes[5] = timestamp & 0xFF
    uuid_bytes[6] = (rand[0] & 0x0F) | 0x70
    uuid_bytes[7] = rand[1]
    uuid_bytes[8] = (rand[2] & 0x3F) | 0x80
    uuid_bytes[9:] = rand[3:]

    return _format_uuid_bytes(bytes(uuid_bytes))


def generate_mail_request_id() -> str:
    """Preferred mail_request_id generator (UUIDv7 with UUIDv4 fallback)."""
    return generate_uuid_v7()
