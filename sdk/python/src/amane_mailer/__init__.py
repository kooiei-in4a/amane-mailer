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
from amane_mailer.payload_hash import (
    build_delivery_payload_json,
    canonicalize,
    compute_delivery_payload_sha256_hex,
    compute_sha256_hex,
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
    "build_delivery_payload_json",
    "canonicalize",
    "compute_delivery_payload_sha256_hex",
    "compute_sha256_hex",
    "generate_mail_request_id",
    "generate_uuid_v7",
    "parse_mailer_error",
    "validate_mail_request_draft",
]
