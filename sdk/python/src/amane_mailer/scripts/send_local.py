"""Local Mailer smoke script for manual integration checks."""

from __future__ import annotations

import os
import sys

from amane_mailer import MailRequestBuilder, MailerClient


def main() -> int:
    client = MailerClient(
        base_url=os.environ.get("MAILER_BASE_URL", "http://127.0.0.1:5280"),
        bearer_token=os.environ.get("MAIL_SERVICE_TOKEN", "local-mail-service-token"),
    )

    response = client.send_mail(
        MailRequestBuilder()
        .tenant_id(os.environ.get("MAILER_TENANT_ID", "00000000-0000-0000-0000-000000000101"))
        .source_service(os.environ.get("MAILER_SOURCE_SERVICE", "example-service"))
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
