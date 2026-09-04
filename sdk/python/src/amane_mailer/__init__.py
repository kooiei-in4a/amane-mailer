"""Official Python Consumer SDK for Amane Mailer."""

from amane_mailer.builder import MailRequestBuilder
from amane_mailer.client import MailerClient
from amane_mailer.errors import (
    MailRequestAcceptedResponse,
    MailRequestAcceptanceStatus,
    MailerError,
    MailerErrorCode,
    MailerIdempotencyConflictError,
    MailerRetryableError,
    MailerValidationError,
    parse_mailer_error,
)
from amane_mailer.uuid import generate_mail_request_id, generate_uuid_v7
from amane_mailer.validation import MailRequestValidationError, validate_mail_request_draft

__all__ = [
    "MailRequestBuilder",
    "MailRequestAcceptedResponse",
    "MailRequestAcceptanceStatus",
    "MailRequestValidationError",
    "MailerClient",
    "MailerError",
    "MailerErrorCode",
    "MailerIdempotencyConflictError",
    "MailerRetryableError",
    "MailerValidationError",
    "generate_mail_request_id",
    "generate_uuid_v7",
    "parse_mailer_error",
    "validate_mail_request_draft",
]
