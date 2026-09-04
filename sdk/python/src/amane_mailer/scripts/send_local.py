"""Local Mailer smoke script for manual integration checks."""

from __future__ import annotations

import os
import sys

from amane_mailer import MailRequestBuilder, MailerClient


def main() -> int:
    api_key = os.environ.get("MAILER_API_KEY")
    if not api_key:
        print("MAILER_API_KEY must contain a managed API key.", file=sys.stderr)
        return 2

    client = MailerClient(
        base_url=os.environ.get("MAILER_BASE_URL", "http://127.0.0.1:5280"),
        bearer_token=api_key,
    )

    response = client.send_mail(
        MailRequestBuilder()
        .generate_mail_request_id()
        .purpose("FormResponseNotification")
        .to(email=os.environ.get("MAILER_RECIPIENT_EMAIL", "admin@example.com"))
        .subject("SDK smoke test")
        .text_body("Sent from amane-mailer Python SDK.")
        .build()
    )

    print(f"HTTP 202 - status: {response.status}")
    print(f"mail_request_id: {response.mail_request_id}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
