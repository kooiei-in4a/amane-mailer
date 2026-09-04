"""Client-side mail request validation."""

from __future__ import annotations

import re
from datetime import datetime
from typing import Any

UUID_PATTERN = re.compile(
    r"^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$",
    re.IGNORECASE,
)
FORBIDDEN_METADATA_KEY_PATTERN = re.compile(r"token|password|secret|url", re.IGNORECASE)
SCHEDULED_AT_OFFSET_PATTERN = re.compile(r"(Z|[+-]\d{2}:\d{2})$")

# JSON field inventory for MailRequestCreateRequest. Parsed by
# scripts/check-mail-request-field-inventory.mjs — keep in sync with Contracts.
MAIL_REQUEST_JSON_FIELDS = frozenset(
    {
        "mail_request_id",
        "purpose",
        "to",
        "cc",
        "bcc",
        "subject",
        "html_body",
        "text_body",
        "reply_to",
        "metadata",
        "scheduled_at",
        "attachments",
    }
)

# ADR 0022 D-01 fixed MVP limit. Mailer is the authority; this is a client-side fail-fast check
# only, not a substitute for server-side validation.
MAX_ATTACHMENTS = 5


class MailRequestValidationError(ValueError):
    pass


def _assert_uuid(value: str, field_name: str) -> None:
    if not isinstance(value, str) or not UUID_PATTERN.fullmatch(value):
        raise MailRequestValidationError(f"{field_name} must be a UUID string.")


def _assert_recipient(recipient: dict[str, Any], field_name: str, index: int) -> None:
    email = recipient.get("email") if isinstance(recipient, dict) else None
    if not isinstance(email, str) or not email:
        raise MailRequestValidationError(f"{field_name}[{index}].email is required.")


def _assert_recipient_roles(draft: dict[str, Any]) -> None:
    recipient_count = 0
    for field_name in ("to", "cc", "bcc"):
        role = draft.get(field_name)
        if role is None:
            continue
        if not isinstance(role, list):
            raise MailRequestValidationError(f"{field_name} must be an array when provided.")
        if len(role) > 10:
            raise MailRequestValidationError(
                f"{field_name} must contain at most 10 recipients.",
            )
        recipient_count += len(role)
        for index, recipient in enumerate(role):
            _assert_recipient(recipient, field_name, index)

    if recipient_count == 0:
        raise MailRequestValidationError("At least one recipient in to, cc, or bcc is required.")
    if recipient_count > 20:
        raise MailRequestValidationError(
            "to, cc, and bcc must contain at most 20 recipients combined.",
        )


def _assert_metadata(metadata: dict[str, str] | None) -> None:
    if metadata is None:
        return
    if not isinstance(metadata, dict):
        raise MailRequestValidationError("metadata must be an object when provided.")

    for key, value in metadata.items():
        if FORBIDDEN_METADATA_KEY_PATTERN.search(key):
            raise MailRequestValidationError(
                f'metadata key "{key}" is rejected (token/password/secret/url).',
            )
        if not isinstance(value, str):
            raise MailRequestValidationError("metadata values must be strings.")


def _assert_scheduled_at(value: Any) -> None:
    if value is None:
        return
    if not isinstance(value, str):
        raise MailRequestValidationError(
            "scheduled_at must be an ISO-8601 date-time string or null.",
        )
    if not SCHEDULED_AT_OFFSET_PATTERN.search(value):
        raise MailRequestValidationError(
            "scheduled_at must include a timezone offset or Z.",
        )
    try:
        datetime.fromisoformat(value)
    except ValueError as exc:
        raise MailRequestValidationError(
            "scheduled_at must be a valid ISO-8601 date-time with timezone offset or Z.",
        ) from exc


def _assert_attachments(attachments: list[dict[str, Any]] | None) -> None:
    if attachments is None:
        return
    if not isinstance(attachments, list):
        raise MailRequestValidationError("attachments must be an array when provided.")
    if len(attachments) > MAX_ATTACHMENTS:
        raise MailRequestValidationError(
            f"attachments must contain at most {MAX_ATTACHMENTS} entries.",
        )
    for index, attachment in enumerate(attachments):
        if not isinstance(attachment, dict):
            raise MailRequestValidationError(f"attachments[{index}] must be an object.")
        for field in ("file_name", "content_type", "content_base64", "content_sha256"):
            if not isinstance(attachment.get(field), str) or not attachment[field]:
                raise MailRequestValidationError(f"attachments[{index}].{field} is required.")
        if not isinstance(attachment.get("byte_length"), int) or attachment["byte_length"] < 0:
            raise MailRequestValidationError(
                f"attachments[{index}].byte_length must be a non-negative integer.",
            )


def validate_mail_request_draft(draft: dict[str, Any]) -> None:
    _assert_uuid(draft["mail_request_id"], "mail_request_id")

    purpose = draft.get("purpose")
    if not isinstance(purpose, str) or not purpose:
        raise MailRequestValidationError("purpose is required.")

    _assert_recipient_roles(draft)

    subject = draft.get("subject")
    if not isinstance(subject, str) or not subject:
        raise MailRequestValidationError("subject is required.")

    html_body = draft.get("html_body")
    text_body = draft.get("text_body")
    has_html = html_body not in (None, "")
    has_text = text_body not in (None, "")
    if not has_html and not has_text:
        raise MailRequestValidationError("At least one of html_body or text_body is required.")

    _assert_metadata(draft.get("metadata"))
    if "scheduled_at" in draft:
        _assert_scheduled_at(draft["scheduled_at"])
    if "attachments" in draft:
        _assert_attachments(draft["attachments"])
