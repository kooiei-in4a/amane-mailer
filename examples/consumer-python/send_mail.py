#!/usr/bin/env python3
"""POST one mail request to a local Amane Mailer from Python."""

from __future__ import annotations

import argparse
import json
import os
import sys
import uuid
from dataclasses import dataclass
from typing import Any
from urllib.error import HTTPError, URLError
from urllib.parse import urljoin
from urllib.request import Request, urlopen

@dataclass(frozen=True)
class SampleOptions:
    mailer_base_url: str
    mail_service_token: str
    recipient_email: str
    request_id: str
    mutate: bool
    timeout_seconds: float


def parse_args(argv: list[str] | None = None) -> SampleOptions:
    parser = argparse.ArgumentParser(
        description="POST one mail request to a local Amane Mailer.",
    )
    parser.add_argument(
        "--request-id",
        default=None,
        help="mail_request_id to send. Defaults to a new UUID.",
    )
    parser.add_argument(
        "--mutate",
        action="store_true",
        help="Change a delivery field while keeping the same request id.",
    )
    parser.add_argument(
        "--timeout-seconds",
        type=float,
        default=float(os.environ.get("MAILER_TIMEOUT_SECONDS", "10")),
        help="HTTP timeout in seconds. Defaults to MAILER_TIMEOUT_SECONDS or 10.",
    )
    args = parser.parse_args(argv)

    return SampleOptions(
        mailer_base_url=os.environ.get("MAILER_BASE_URL", "http://127.0.0.1:5280/"),
        mail_service_token=os.environ.get("MAILER_API_KEY", ""),
        recipient_email=os.environ.get("MAILER_RECIPIENT_EMAIL", "admin@example.com"),
        request_id=args.request_id or str(uuid.uuid4()),
        mutate=args.mutate,
        timeout_seconds=args.timeout_seconds,
    )


def build_mail_request(options: SampleOptions) -> dict[str, Any]:
    request: dict[str, Any] = {
        "mail_request_id": options.request_id,
        "purpose": "FormResponseNotification",
        "to": [{"email": options.recipient_email}],
        "subject": "New response (edited)" if options.mutate else "New response",
        "text_body": "A new response arrived.",
    }
    return request


def post_mail_request(
    options: SampleOptions,
    mail_request: dict[str, Any],
) -> tuple[int, str]:
    endpoint = urljoin(
        options.mailer_base_url.rstrip("/") + "/",
        "api/mail-requests",
    )
    body = json.dumps(mail_request, separators=(",", ":"), ensure_ascii=False).encode(
        "utf-8",
    )
    request = Request(
        endpoint,
        data=body,
        method="POST",
        headers={
            "Authorization": f"Bearer {options.mail_service_token}",
            "Content-Type": "application/json",
            "Accept": "application/json",
        },
    )

    try:
        with urlopen(request, timeout=options.timeout_seconds) as response:
            return response.status, response.read().decode("utf-8")
    except HTTPError as error:
        return error.code, error.read().decode("utf-8")


def print_result(status_code: int, response_body: str, request_id: str) -> None:
    if status_code == 202:
        response = json.loads(response_body)
        status = response.get("status")
        print(f"HTTP 202 Accepted - status: {status}")

        if status == "accepted":
            print("The Mailer accepted this request for asynchronous delivery.")
        elif status == "already_accepted":
            print("This mail_request_id was already accepted with the same canonical payload;")
            print("the Mailer treated this POST as an idempotent resend.")
        return

    if status_code == 409:
        print(f"HTTP 409 Conflict: {response_body}")
        print()
        print(f"mail_request_id {request_id} was already accepted with a different")
        print("payload. Reusing a mail_request_id after changing subject, body,")
        print("recipients, or metadata returns IDEMPOTENCY_CONFLICT.")
        return

    print(f"HTTP {status_code}: {response_body}")


def main(argv: list[str] | None = None) -> int:
    options = parse_args(argv)
    if not options.mail_service_token:
        print("MAILER_API_KEY must contain a managed API key.", file=sys.stderr)
        return 2
    mail_request = build_mail_request(options)
    endpoint = urljoin(
        options.mailer_base_url.rstrip("/") + "/",
        "api/mail-requests",
    )

    print(f"POST {endpoint}")
    print(f"mail_request_id: {options.request_id}")
    print()

    try:
        status_code, response_body = post_mail_request(options, mail_request)
    except (OSError, URLError) as error:
        print(f"[error] {error}", file=sys.stderr)
        return 2
    except json.JSONDecodeError as error:
        print(f"[error] Invalid JSON response: {error}", file=sys.stderr)
        return 2

    try:
        print_result(status_code, response_body, options.request_id)
    except json.JSONDecodeError as error:
        print(f"[error] Invalid JSON response: {error}", file=sys.stderr)
        return 2

    return 0 if status_code in (202, 409) else 1


if __name__ == "__main__":
    sys.exit(main())
