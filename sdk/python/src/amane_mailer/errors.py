"""Typed Mailer API errors and success responses."""

from __future__ import annotations

from dataclasses import dataclass
from typing import Any


class MailerErrorCode:
    INVALID_REQUEST = "INVALID_REQUEST"
    REQUEST_TOO_LARGE = "REQUEST_TOO_LARGE"
    SOURCE_SERVICE_NOT_ALLOWED = "SOURCE_SERVICE_NOT_ALLOWED"
    TOO_MANY_RECIPIENTS = "TOO_MANY_RECIPIENTS"
    IDEMPOTENCY_CONFLICT = "IDEMPOTENCY_CONFLICT"
    INVALID_PAYLOAD_HASH = "INVALID_PAYLOAD_HASH"
    INVALID_METADATA = "INVALID_METADATA"
    UNAUTHORIZED_TENANT = "UNAUTHORIZED_TENANT"
    MAILER_TEMPORARILY_UNAVAILABLE = "MAILER_TEMPORARILY_UNAVAILABLE"
    NOT_FOUND = "NOT_FOUND"


class MailRequestAcceptanceStatus:
    ACCEPTED = "accepted"
    ALREADY_ACCEPTED = "already_accepted"


class MailerError(Exception):
    def __init__(
        self,
        message: str,
        *,
        status_code: int | None = None,
        code: str | None = None,
        retryable: bool = False,
        body: Any = None,
    ) -> None:
        super().__init__(message)
        self.status_code = status_code
        self.code = code
        self.retryable = retryable
        self.body = body


class MailerValidationError(MailerError):
    pass


class MailerIdempotencyConflictError(MailerError):
    pass


class MailerRetryableError(MailerError):
    def __init__(
        self,
        message: str,
        *,
        status_code: int | None = None,
        code: str | None = None,
        body: Any = None,
    ) -> None:
        super().__init__(
            message,
            status_code=status_code,
            code=code,
            retryable=True,
            body=body,
        )


@dataclass(frozen=True)
class MailRequestAcceptedResponse:
    mail_request_id: str
    status: str

    @property
    def is_first_acceptance(self) -> bool:
        return self.status == MailRequestAcceptanceStatus.ACCEPTED

    @property
    def is_idempotent_resend(self) -> bool:
        return self.status == MailRequestAcceptanceStatus.ALREADY_ACCEPTED


def parse_mailer_error(status_code: int, body_text: str) -> MailerError:
    try:
        body = __import__("json").loads(body_text)
    except ValueError:
        return MailerError(
            f"Mailer returned HTTP {status_code}.",
            status_code=status_code,
            body=body_text,
        )

    code = body.get("code")
    message = body.get("message") or f"Mailer returned {code or status_code}."
    retryable = body.get("retryable") is True

    if status_code == 409 and code == MailerErrorCode.IDEMPOTENCY_CONFLICT:
        return MailerIdempotencyConflictError(
            message,
            status_code=status_code,
            code=code,
            body=body,
        )

    if status_code == 422:
        return MailerValidationError(
            message,
            status_code=status_code,
            code=code,
            body=body,
        )

    if status_code == 503 or retryable:
        return MailerRetryableError(
            message,
            status_code=status_code,
            code=code,
            body=body,
        )

    return MailerError(
        message,
        status_code=status_code,
        code=code,
        retryable=retryable,
        body=body,
    )
