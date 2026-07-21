from __future__ import annotations

import json
import re
import threading
import unittest
from http.server import BaseHTTPRequestHandler, HTTPServer
from typing import Any

from amane_mailer import (
    MailRequestAcceptanceStatus,
    MailRequestBuilder,
    MailerClient,
    MailerIdempotencyConflictError,
    MailerRetryableError,
    MailerValidationError,
)
from amane_mailer.uuid import generate_uuid_v7
from amane_mailer.validation import MailRequestValidationError


def build_sample_request() -> dict[str, Any]:
    return (
        MailRequestBuilder()
        .tenant_id("00000000-0000-0000-0000-000000000101")
        .source_service("example-service")
        .mail_request_id("00000000-0000-0000-0000-000000000201")
        .purpose("FormResponseNotification")
        .to(email="admin@example.com")
        .subject("New response")
        .text_body("A new response arrived.")
        .build()
    )


class MockHandler(BaseHTTPRequestHandler):
    responses: list[tuple[int, dict[str, Any]]] = []
    call_count = 0

    def do_POST(self) -> None:  # noqa: N802
        type(self).call_count += 1
        status_code, body = type(self).responses.pop(0)
        payload = json.dumps(body).encode("utf-8")
        self.send_response(status_code)
        self.send_header("Content-Type", "application/json")
        self.send_header("Content-Length", str(len(payload)))
        self.end_headers()
        self.wfile.write(payload)

    def log_message(self, format: str, *args: Any) -> None:
        return


class ClientTests(unittest.TestCase):
    def test_builder_computes_payload_hash(self) -> None:
        request = build_sample_request()
        self.assertRegex(request["payload_hash"], r"^[0-9a-f]{64}$")
        self.assertEqual(
            request["payload_hash"],
            "7c6d491cc70ac1b48fcc770d90ff80ae8a13c0e5ed3284fd1de9705d7e801ea9",
        )

    def test_builder_rejects_invalid_source_service(self) -> None:
        with self.assertRaises(MailRequestValidationError):
            (
                MailRequestBuilder()
                .tenant_id("00000000-0000-0000-0000-000000000101")
                .source_service("INVALID")
                .mail_request_id("00000000-0000-0000-0000-000000000201")
                .purpose("FormResponseNotification")
                .to(email="one@example.com")
                .subject("x")
                .text_body("body")
                .build()
            )

    def test_generate_uuid_v7_format(self) -> None:
        value = generate_uuid_v7(1_753_094_400_000)
        self.assertRegex(
            value,
            re.compile(
                r"^[0-9a-f]{8}-[0-9a-f]{4}-7[0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$",
                re.IGNORECASE,
            ),
        )

    def test_client_handles_accepted_and_already_accepted(self) -> None:
        MockHandler.responses = [
            (202, {"mail_request_id": "00000000-0000-0000-0000-000000000201", "status": "accepted"}),
            (
                202,
                {
                    "mail_request_id": "00000000-0000-0000-0000-000000000201",
                    "status": "already_accepted",
                },
            ),
        ]
        MockHandler.call_count = 0

        server = HTTPServer(("127.0.0.1", 0), MockHandler)
        thread = threading.Thread(target=server.serve_forever, daemon=True)
        thread.start()

        try:
            client = MailerClient(
                base_url=f"http://127.0.0.1:{server.server_port}",
                bearer_token="token",
            )
            request = build_sample_request()

            first = client.send_mail(request)
            self.assertEqual(first.status, MailRequestAcceptanceStatus.ACCEPTED)
            self.assertTrue(first.is_first_acceptance)

            second = client.send_mail(request)
            self.assertEqual(second.status, MailRequestAcceptanceStatus.ALREADY_ACCEPTED)
            self.assertTrue(second.is_idempotent_resend)
        finally:
            server.shutdown()
            thread.join(timeout=2)

    def test_client_maps_idempotency_conflict(self) -> None:
        MockHandler.responses = [(409, {"code": "IDEMPOTENCY_CONFLICT"})]
        server = HTTPServer(("127.0.0.1", 0), MockHandler)
        thread = threading.Thread(target=server.serve_forever, daemon=True)
        thread.start()

        try:
            client = MailerClient(
                base_url=f"http://127.0.0.1:{server.server_port}",
                bearer_token="token",
            )
            with self.assertRaises(MailerIdempotencyConflictError):
                client.send_mail(build_sample_request())
        finally:
            server.shutdown()
            thread.join(timeout=2)

    def test_client_maps_validation_error(self) -> None:
        MockHandler.responses = [(422, {"code": "INVALID_PAYLOAD_HASH"})]
        server = HTTPServer(("127.0.0.1", 0), MockHandler)
        thread = threading.Thread(target=server.serve_forever, daemon=True)
        thread.start()

        try:
            client = MailerClient(
                base_url=f"http://127.0.0.1:{server.server_port}",
                bearer_token="token",
            )
            with self.assertRaises(MailerValidationError):
                client.send_mail(build_sample_request())
        finally:
            server.shutdown()
            thread.join(timeout=2)

    def test_client_retries_retryable_503(self) -> None:
        MockHandler.responses = [
            (503, {"code": "MAILER_TEMPORARILY_UNAVAILABLE", "retryable": True}),
            (202, {"mail_request_id": "00000000-0000-0000-0000-000000000201", "status": "accepted"}),
        ]
        MockHandler.call_count = 0

        server = HTTPServer(("127.0.0.1", 0), MockHandler)
        thread = threading.Thread(target=server.serve_forever, daemon=True)
        thread.start()

        try:
            client = MailerClient(
                base_url=f"http://127.0.0.1:{server.server_port}",
                bearer_token="token",
            )
            response = client.send_mail(
                build_sample_request(),
                max_retries=2,
                base_delay_seconds=0.001,
            )
            self.assertEqual(response.status, MailRequestAcceptanceStatus.ACCEPTED)
            self.assertEqual(MockHandler.call_count, 2)
        finally:
            server.shutdown()
            thread.join(timeout=2)

    def test_client_surfaces_retryable_error_after_max_retries(self) -> None:
        MockHandler.responses = [
            (503, {"code": "MAILER_TEMPORARILY_UNAVAILABLE", "retryable": True}),
            (503, {"code": "MAILER_TEMPORARILY_UNAVAILABLE", "retryable": True}),
        ]

        server = HTTPServer(("127.0.0.1", 0), MockHandler)
        thread = threading.Thread(target=server.serve_forever, daemon=True)
        thread.start()

        try:
            client = MailerClient(
                base_url=f"http://127.0.0.1:{server.server_port}",
                bearer_token="token",
            )
            with self.assertRaises(MailerRetryableError):
                client.send_mail(
                    build_sample_request(),
                    max_retries=1,
                    base_delay_seconds=0.001,
                )
        finally:
            server.shutdown()
            thread.join(timeout=2)


if __name__ == "__main__":
    unittest.main()
