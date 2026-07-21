"""Client-side mail request validation."""

from __future__ import annotations

import re
from typing import Any

UUID_PATTERN = re.compile(
    r"^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$",
    re.IGNORECASE,
)
SOURCE_SERVICE_PATTERN = re.compile(r"^[a-z0-9][a-z0-9_-]{1,62}$")
FORBIDDEN_METADATA_KEY_PATTERN = re.compile(r"token|password|secret|url", re.IGNORECASE)


class MailRequestValidationError(ValueError):
    pass


def _assert_uuid(value: str, field_name: str) -> None:
    if not isinstance(value, str) or not UUID_PATTERN.fullmatch(value):
        raise MailRequestValidationError(f"{field_name} must be a UUID string.")


def _assert_source_service(value: str) -> None:
    if not isinstance(value, str) or not SOURCE_SERVICE_PATTERN.fullmatch(value):
        raise MailRequestValidationError(
            "source_service must match ^[a-z0-9][a-z0-9_-]{1,62}$.",
        )


def _assert_recipient(recipient: dict[str, Any]) -> None:
    email = recipient.get("email") if isinstance(recipient, dict) else None
    if not isinstance(email, str) or not email:
        raise MailRequestValidationError("to[0].email is required.")


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


def validate_mail_request_draft(draft: dict[str, Any]) -> None:
    _assert_uuid(draft["tenant_id"], "tenant_id")
    _assert_uuid(draft["mail_request_id"], "mail_request_id")
    _assert_source_service(draft["source_service"])

    purpose = draft.get("purpose")
    if not isinstance(purpose, str) or not purpose:
        raise MailRequestValidationError("purpose is required.")

    recipients = draft.get("to")
    if not isinstance(recipients, list) or not recipients:
        raise MailRequestValidationError("to must contain at least one recipient.")
    if len(recipients) > 1:
        raise MailRequestValidationError("to must contain at most one recipient.")
    _assert_recipient(recipients[0])

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
