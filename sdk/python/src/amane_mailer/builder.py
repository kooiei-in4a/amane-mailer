"""Mail request builder with payload_hash computation."""

from __future__ import annotations

from copy import deepcopy
from typing import Any

from amane_mailer.payload_hash import compute_delivery_payload_sha256_hex
from amane_mailer.uuid import generate_mail_request_id
from amane_mailer.validation import validate_mail_request_draft


class MailRequestBuilder:
    def __init__(self) -> None:
        self._fields: dict[str, Any] = {}
        self._explicit_nulls: set[str] = set()

    def tenant_id(self, value: str) -> MailRequestBuilder:
        self._fields["tenant_id"] = value
        return self

    def source_service(self, value: str) -> MailRequestBuilder:
        self._fields["source_service"] = value
        return self

    def mail_request_id(self, value: str) -> MailRequestBuilder:
        self._fields["mail_request_id"] = value
        return self

    def generate_mail_request_id(self) -> MailRequestBuilder:
        return self.mail_request_id(generate_mail_request_id())

    def purpose(self, value: str) -> MailRequestBuilder:
        self._fields["purpose"] = value
        return self

    def to(self, *, email: str, display_name: str | None = None) -> MailRequestBuilder:
        recipient: dict[str, Any] = {"email": email}
        if display_name is not None:
            recipient["display_name"] = display_name
        self._fields["to"] = [recipient]
        return self

    def subject(self, value: str) -> MailRequestBuilder:
        self._fields["subject"] = value
        return self

    def html_body(self, value: str | None) -> MailRequestBuilder:
        self._fields["html_body"] = value
        if value is None:
            self._explicit_nulls.add("html_body")
        else:
            self._explicit_nulls.discard("html_body")
        return self

    def text_body(self, value: str | None) -> MailRequestBuilder:
        self._fields["text_body"] = value
        if value is None:
            self._explicit_nulls.add("text_body")
        else:
            self._explicit_nulls.discard("text_body")
        return self

    def reply_to(self, value: str | None) -> MailRequestBuilder:
        self._fields["reply_to"] = value
        if value is None:
            self._explicit_nulls.add("reply_to")
        else:
            self._explicit_nulls.discard("reply_to")
        return self

    def metadata(self, value: dict[str, str] | None) -> MailRequestBuilder:
        self._fields["metadata"] = value
        if value is None:
            self._explicit_nulls.add("metadata")
        else:
            self._explicit_nulls.discard("metadata")
        return self

    def build(self) -> dict[str, Any]:
        draft = deepcopy(self._fields)

        for key in ("html_body", "text_body", "reply_to", "metadata"):
            if key not in draft:
                continue
            if draft[key] is None and key not in self._explicit_nulls:
                del draft[key]

        validate_mail_request_draft(draft)

        hash_input = {**draft, "payload_hash": "placeholder"}
        draft["payload_hash"] = compute_delivery_payload_sha256_hex(hash_input)
        return draft
