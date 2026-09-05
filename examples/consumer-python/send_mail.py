#!/usr/bin/env python3
"""Official stdlib-only smoke client for the Amane Mailer v2 Consumer API."""

from __future__ import annotations

import argparse
import getpass
import ipaddress
import json
import os
import re
import sys
import time
import uuid
from dataclasses import dataclass
from http import HTTPStatus
from typing import Any
from urllib.error import HTTPError, URLError
from urllib.parse import urljoin, urlsplit, urlunsplit
from urllib.request import HTTPRedirectHandler, Request, build_opener


DEFAULT_BASE_URL = "http://127.0.0.1:5280/"
DEFAULT_PURPOSE = "SmokeTest"
DEFAULT_SUBJECT = "Amane Mailer smoke"
DEFAULT_TEXT_BODY = "Amane Mailer smoke request."
DEFAULT_TIMEOUT_SECONDS = 10.0
DEFAULT_POLL_TIMEOUT_SECONDS = 30.0
DEFAULT_POLL_INTERVAL_SECONDS = 1.0
MAX_RESPONSE_BYTES = 1024 * 1024
SAFE_ERROR_CODE = re.compile(r"^[A-Z][A-Z0-9_]{0,63}$")

NON_TERMINAL_STATUSES = frozenset({"queued", "processing"})
TERMINAL_STATUSES = frozenset(
    {"delivered", "failed", "dead_lettered", "cancelled", "delivery_unknown"},
)


class SmokeClientError(Exception):
    """An expected, safe-to-display smoke-client failure."""


class HttpResponseError(SmokeClientError):
    def __init__(self, status_code: int, code: str) -> None:
        self.status_code = status_code
        self.code = code
        super().__init__(format_http_error(status_code, code))


@dataclass(frozen=True)
class SmokeOptions:
    base_url: str
    recipient_email: str
    subject: str
    text_body: str
    purpose: str
    request_id: str
    timeout_seconds: float
    poll_timeout_seconds: float
    poll_interval_seconds: float


class _NoRedirectHandler(HTTPRedirectHandler):
    def redirect_request(self, *args: Any, **kwargs: Any) -> None:
        raise SmokeClientError("Mailer returned a redirect; refusing to forward the API key.")


HTTP_OPENER = build_opener(_NoRedirectHandler)


def _environment_float(name: str, default: float) -> float:
    raw = os.environ.get(name)
    if raw is None or raw.strip() == "":
        return default
    try:
        return float(raw)
    except ValueError as error:
        raise SmokeClientError(f"{name} must be a number.") from error


def _validate_seconds(value: float, name: str, *, allow_zero: bool) -> float:
    if (
        not isinstance(value, (int, float))
        or value != value
        or value in (float("inf"), float("-inf"))
    ):
        raise SmokeClientError(f"{name} must be a finite number.")
    if value < 0 or (value == 0 and not allow_zero):
        qualifier = "zero or greater" if allow_zero else "greater than zero"
        raise SmokeClientError(f"{name} must be {qualifier}.")
    return float(value)


def normalize_base_url(value: str) -> str:
    """Validate a base URL and remove only its trailing slash.

    Public Consumer API endpoints must use HTTPS. Plain HTTP is intentionally
    limited to loopback so a managed API key cannot be sent to a remote host.
    """

    try:
        parsed = urlsplit(value)
    except ValueError as error:
        raise SmokeClientError("MAILER_BASE_URL is not a valid URL.") from error

    scheme = parsed.scheme.lower()
    if scheme not in {"http", "https"} or not parsed.netloc:
        raise SmokeClientError("MAILER_BASE_URL must use an http or https URL.")
    if parsed.username or parsed.password or parsed.query or parsed.fragment:
        raise SmokeClientError(
            "MAILER_BASE_URL must not contain credentials, a query, or a fragment.",
        )

    # urlsplit().hostname can raise for malformed bracketed IPv6 hosts.
    try:
        hostname = parsed.hostname
        # Force validation of an invalid or out-of-range port before any key
        # is read or request is attempted.
        _ = parsed.port
        if not hostname:
            raise SmokeClientError("MAILER_BASE_URL must contain a host.")
    except ValueError as error:
        raise SmokeClientError("MAILER_BASE_URL is not a valid URL.") from error

    if scheme == "http":
        normalized_hostname = hostname.rstrip(".").lower()
        is_loopback = normalized_hostname == "localhost"
        if not is_loopback:
            try:
                is_loopback = ipaddress.ip_address(hostname).is_loopback
            except ValueError:
                is_loopback = False
        if not is_loopback:
            raise SmokeClientError(
                "MAILER_BASE_URL must use HTTPS; plain HTTP is allowed only for loopback testing."
            )

    safe_url = urlunsplit((scheme, parsed.netloc, parsed.path.rstrip("/"), "", ""))
    return safe_url + "/"


def _parser() -> argparse.ArgumentParser:
    parser = argparse.ArgumentParser(
        description=(
            "Send one explicit v2 mail request and poll its delivery status. "
            "MAILER_API_KEY is read from the environment or an echo-free prompt."
        ),
    )
    parser.add_argument(
        "--recipient",
        "--to",
        dest="recipient_email",
        default=os.environ.get("MAILER_RECIPIENT_EMAIL"),
        help="Recipient email address (or MAILER_RECIPIENT_EMAIL). Required.",
    )
    parser.add_argument(
        "--subject",
        default=os.environ.get("MAILER_SUBJECT", DEFAULT_SUBJECT),
        help="Message subject (or MAILER_SUBJECT).",
    )
    parser.add_argument(
        "--body",
        dest="text_body",
        default=os.environ.get("MAILER_TEXT_BODY", DEFAULT_TEXT_BODY),
        help="Plain-text message body (or MAILER_TEXT_BODY).",
    )
    parser.add_argument(
        "--purpose",
        default=os.environ.get("MAILER_PURPOSE", DEFAULT_PURPOSE),
        help="v2 purpose value (or MAILER_PURPOSE).",
    )
    parser.add_argument(
        "--request-id",
        default=None,
        help="Optional UUID for an idempotency/conflict rehearsal; otherwise a random UUID is generated.",
    )
    parser.add_argument(
        "--base-url",
        default=os.environ.get("MAILER_BASE_URL", DEFAULT_BASE_URL),
        help="Mailer base URL (or MAILER_BASE_URL).",
    )
    parser.add_argument(
        "--timeout-seconds",
        type=float,
        default=_environment_float("MAILER_TIMEOUT_SECONDS", DEFAULT_TIMEOUT_SECONDS),
        help="Per-request HTTP timeout (or MAILER_TIMEOUT_SECONDS).",
    )
    parser.add_argument(
        "--poll-timeout-seconds",
        type=float,
        default=_environment_float(
            "MAILER_POLL_TIMEOUT_SECONDS",
            DEFAULT_POLL_TIMEOUT_SECONDS,
        ),
        help="Maximum status-polling window (or MAILER_POLL_TIMEOUT_SECONDS).",
    )
    parser.add_argument(
        "--poll-interval-seconds",
        type=float,
        default=_environment_float(
            "MAILER_POLL_INTERVAL_SECONDS",
            DEFAULT_POLL_INTERVAL_SECONDS,
        ),
        help="Delay between status polls (or MAILER_POLL_INTERVAL_SECONDS).",
    )
    return parser


def parse_args(argv: list[str] | None = None) -> SmokeOptions:
    parser = _parser()
    args = parser.parse_args(argv)

    if not args.recipient_email or not args.recipient_email.strip():
        parser.error("--recipient or MAILER_RECIPIENT_EMAIL is required.")
    if not args.subject:
        parser.error("--subject must not be empty.")
    if not args.text_body:
        parser.error("--body must not be empty.")
    if not args.purpose:
        parser.error("--purpose must not be empty.")

    request_id = args.request_id or str(uuid.uuid4())
    try:
        request_id = str(uuid.UUID(request_id))
    except (ValueError, AttributeError) as error:
        parser.error("--request-id must be a UUID.")
        raise AssertionError("argparse.error exits") from error

    try:
        base_url = normalize_base_url(args.base_url)
        timeout_seconds = _validate_seconds(
            args.timeout_seconds,
            "--timeout-seconds",
            allow_zero=False,
        )
        poll_timeout_seconds = _validate_seconds(
            args.poll_timeout_seconds,
            "--poll-timeout-seconds",
            allow_zero=True,
        )
        poll_interval_seconds = _validate_seconds(
            args.poll_interval_seconds,
            "--poll-interval-seconds",
            allow_zero=True,
        )
    except SmokeClientError as error:
        parser.error(str(error))
        raise AssertionError("argparse.error exits") from error

    return SmokeOptions(
        base_url=base_url,
        recipient_email=args.recipient_email.strip(),
        subject=args.subject,
        text_body=args.text_body,
        purpose=args.purpose,
        request_id=request_id,
        timeout_seconds=timeout_seconds,
        poll_timeout_seconds=poll_timeout_seconds,
        poll_interval_seconds=poll_interval_seconds,
    )


def read_api_key() -> str:
    """Read the managed key from the environment or an echo-free prompt."""

    api_key = os.environ.get("MAILER_API_KEY", "")
    if api_key:
        return api_key

    if not sys.stdin.isatty() and not sys.stderr.isatty():
        raise SmokeClientError(
            "MAILER_API_KEY is not set; set it for non-interactive use or run from a terminal.",
        )

    try:
        api_key = getpass.getpass("Mailer API key (input hidden): ")
    except (EOFError, KeyboardInterrupt) as error:
        raise SmokeClientError("API key prompt was cancelled.") from error
    if not api_key:
        raise SmokeClientError("A managed API key is required.")
    return api_key


def build_mail_request(options: SmokeOptions) -> dict[str, Any]:
    """Build only the v2 Consumer request fields."""

    return {
        "mail_request_id": options.request_id,
        "purpose": options.purpose,
        "to": [{"email": options.recipient_email}],
        "subject": options.subject,
        "text_body": options.text_body,
    }


def _endpoint(base_url: str, path: str) -> str:
    return urljoin(base_url, path.lstrip("/"))


def _read_body(response: Any) -> str:
    raw_body = response.read(MAX_RESPONSE_BYTES + 1)
    if len(raw_body) > MAX_RESPONSE_BYTES:
        raise SmokeClientError("Mailer returned an oversized response body.")
    return raw_body.decode("utf-8", errors="replace")


def _request(
    *,
    method: str,
    endpoint: str,
    api_key: str,
    body: bytes | None,
    timeout_seconds: float,
) -> tuple[int, str]:
    request = Request(
        endpoint,
        data=body,
        method=method,
        headers={
            "Authorization": f"Bearer {api_key}",
            "Content-Type": "application/json",
            "Accept": "application/json",
        },
    )

    try:
        with HTTP_OPENER.open(request, timeout=timeout_seconds) as response:
            return response.status, _read_body(response)
    except HTTPError as error:
        return error.code, _read_body(error)
    except (OSError, URLError) as error:
        raise SmokeClientError(f"{method} request could not reach Mailer.") from error


def _safe_error_code(response_body: str) -> str:
    try:
        payload = json.loads(response_body)
    except (TypeError, ValueError):
        return "unknown"
    code = payload.get("code") if isinstance(payload, dict) else None
    if isinstance(code, str) and SAFE_ERROR_CODE.fullmatch(code):
        return code
    return "unknown"


def format_http_error(status_code: int, code: str) -> str:
    try:
        phrase = HTTPStatus(status_code).phrase
    except ValueError:
        phrase = "unexpected response"
    return f"HTTP {status_code} {phrase} (code={code})"


def post_mail_request(
    options: SmokeOptions,
    api_key: str,
    mail_request: dict[str, Any],
) -> str:
    body = json.dumps(mail_request, separators=(",", ":"), ensure_ascii=False).encode("utf-8")
    status_code, response_body = _request(
        method="POST",
        endpoint=_endpoint(options.base_url, "api/mail-requests"),
        api_key=api_key,
        body=body,
        timeout_seconds=options.timeout_seconds,
    )
    if status_code != HTTPStatus.ACCEPTED:
        raise HttpResponseError(status_code, _safe_error_code(response_body))

    try:
        payload = json.loads(response_body)
    except (TypeError, ValueError) as error:
        raise SmokeClientError("Mailer returned invalid JSON for the acceptance response.") from error
    if not isinstance(payload, dict):
        raise SmokeClientError("Mailer returned an invalid acceptance response.")

    returned_id = payload.get("mail_request_id")
    if returned_id != options.request_id:
        raise SmokeClientError("Mailer acceptance response returned a different request ID.")
    acceptance_status = payload.get("status")
    if acceptance_status not in {"accepted", "already_accepted"}:
        raise SmokeClientError("Mailer returned an unknown acceptance status.")
    return acceptance_status


def _status_response(response_body: str, request_id: str) -> str:
    try:
        payload = json.loads(response_body)
    except (TypeError, ValueError) as error:
        raise SmokeClientError("Mailer returned invalid JSON for the status response.") from error
    if not isinstance(payload, dict):
        raise SmokeClientError("Mailer returned an invalid status response.")
    if payload.get("mail_request_id") != request_id:
        raise SmokeClientError("Mailer status response returned a different request ID.")
    status = payload.get("status")
    if status not in NON_TERMINAL_STATUSES and status not in TERMINAL_STATUSES:
        raise SmokeClientError("Mailer returned an unknown delivery status.")
    return status


def poll_status(options: SmokeOptions, api_key: str) -> str:
    """Poll only until a contract terminal status or the operator's deadline."""

    endpoint = _endpoint(options.base_url, f"api/mail-requests/{options.request_id}")
    deadline = time.monotonic() + options.poll_timeout_seconds
    last_status = "not queried"

    while True:
        status_code, response_body = _request(
            method="GET",
            endpoint=endpoint,
            api_key=api_key,
            body=None,
            timeout_seconds=options.timeout_seconds,
        )
        if status_code == HTTPStatus.OK:
            last_status = _status_response(response_body, options.request_id)
            print(f"GET /api/mail-requests/{{id}} -> {last_status}")
            if last_status in TERMINAL_STATUSES:
                return last_status
        elif status_code in {HTTPStatus.TOO_MANY_REQUESTS, HTTPStatus.SERVICE_UNAVAILABLE}:
            code = _safe_error_code(response_body)
            print(
                f"GET /api/mail-requests/{{id}} -> {format_http_error(status_code, code)}; retrying within bound",
                file=sys.stderr,
            )
        else:
            raise HttpResponseError(status_code, _safe_error_code(response_body))

        remaining = deadline - time.monotonic()
        if remaining <= 0:
            raise SmokeClientError(
                f"Status polling timed out after {options.poll_timeout_seconds:g}s (last_status={last_status}).",
            )
        time.sleep(min(options.poll_interval_seconds, remaining))


def main(argv: list[str] | None = None) -> int:
    try:
        options = parse_args(argv)
        api_key = read_api_key()
        request = build_mail_request(options)

        print("POST /api/mail-requests")
        acceptance_status = post_mail_request(options, api_key, request)
        print(f"HTTP 202 Accepted - status: {acceptance_status}")
        print(f"mail_request_id: {options.request_id}")

        delivery_status = poll_status(options, api_key)
        if delivery_status == "delivered":
            print("Delivery status: delivered")
            return 0
        print(f"Delivery status: {delivery_status} (not a successful smoke result)", file=sys.stderr)
        return 1
    except HttpResponseError as error:
        print(f"[error] {error}", file=sys.stderr)
        return 1
    except SmokeClientError as error:
        print(f"[error] {error}", file=sys.stderr)
        return 1
    except OSError:
        print("[error] Mailer I/O failed.", file=sys.stderr)
        return 1


if __name__ == "__main__":
    sys.exit(main())
