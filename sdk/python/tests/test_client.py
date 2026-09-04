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
from amane_mailer.validation import MailRequestValidationError, validate_mail_request_draft


def build_sample_request() -> dict[str, Any]:
    return (
        MailRequestBuilder()
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
    def test_builder_omits_v1_identity_and_payload_hash(self) -> None:
        request = build_sample_request()
        self.assertNotIn("tenant_id", request)
        self.assertNotIn("source_service", request)
        self.assertNotIn("payload_hash", request)

    def test_builder_supports_cc_and_bcc_without_to(self) -> None:
        for role, email, subject, body, expected_hash in (
            (
                "cc",
                "cc@example.com",
                "CC only",
                "CC only body.",
                "22cee63ba2c526ce67078a838d1b9277f2ce089237dcc36ee28c6b4c086d06ac",
            ),
            (
                "bcc",
                "bcc@example.com",
                "BCC only",
                "BCC only body.",
                "b834a8ba190ecb3f2ae6feeff0de486805de4edef8e98c2529b70771d5619d4d",
            ),
        ):
            with self.subTest(role=role):
                builder = (
                    MailRequestBuilder()
                    .mail_request_id("00000000-0000-0000-0000-000000000201")
                    .purpose("FormResponseNotification")
                    .subject(subject)
                    .text_body(body)
                )
                if role == "cc":
                    request = builder.cc(email=email).build()
                else:
                    request = builder.bcc(email=email).build()

                self.assertNotIn("to", request)
                self.assertEqual(request[role][0]["email"], email)
                self.assertNotIn("payload_hash", request)

        request = (
            MailRequestBuilder()
            .mail_request_id("00000000-0000-0000-0000-000000000201")
            .purpose("FormResponseNotification")
            .to(email=None)
            .cc(email="cc@example.com")
            .subject("CC only")
            .text_body("CC only body.")
            .build()
        )
        self.assertIsNone(request["to"])
        self.assertNotIn("payload_hash", request)

    def test_builder_preserves_multiple_recipient_order_and_limits(self) -> None:
        request = (
            MailRequestBuilder()
            .mail_request_id("00000000-0000-0000-0000-000000000201")
            .purpose("FormResponseNotification")
            .to(email="to1@example.com")
            .add_to(email="to2@example.com", display_name="To Two")
            .cc(email="cc1@example.com")
            .bcc(email="bcc1@example.com")
            .add_bcc(email="bcc2@example.com")
            .subject("All roles")
            .text_body("All roles body.")
            .build()
        )

        self.assertEqual(
            request["to"],
            [
                {"email": "to1@example.com"},
                {"email": "to2@example.com", "display_name": "To Two"},
            ],
        )
        self.assertEqual(request["cc"], [{"email": "cc1@example.com"}])
        self.assertEqual(
            request["bcc"],
            [{"email": "bcc1@example.com"}, {"email": "bcc2@example.com"}],
        )
        self.assertNotIn("payload_hash", request)

        maximum = (
            MailRequestBuilder()
            .mail_request_id("00000000-0000-0000-0000-000000000201")
            .purpose("FormResponseNotification")
            .to(email="to0@example.com")
            .cc(email="cc0@example.com")
            .subject("Maximum recipients")
            .text_body("Maximum recipients body.")
        )
        for index in range(1, 10):
            maximum.add_to(email=f"to{index}@example.com")
            maximum.add_cc(email=f"cc{index}@example.com")
        maximum_request = maximum.build()
        self.assertEqual(len(maximum_request["to"]), 10)
        self.assertEqual(len(maximum_request["cc"]), 10)
        self.assertEqual(len(maximum_request["to"]) + len(maximum_request["cc"]), 20)

    def test_builder_omits_scheduled_at_when_unset(self) -> None:
        request = build_sample_request()
        self.assertNotIn("scheduled_at", request)

    def test_validation_allows_empty_to_when_cc_is_present(self) -> None:
        validate_mail_request_draft(
            {
                "mail_request_id": "00000000-0000-0000-0000-000000000201",
                "purpose": "FormResponseNotification",
                "to": [],
                "cc": [{"email": "cc@example.com"}],
                "subject": "CC only",
                "text_body": "CC only body.",
            },
        )

    def test_builder_accepts_scheduled_at_with_z_and_offsets(self) -> None:
        for value in (
            "2026-08-01T09:00:00Z",
            "2026-08-01T18:00:00+09:00",
            "2026-08-01T00:00:00-05:00",
        ):
            with self.subTest(value=value):
                request = (
                    MailRequestBuilder()
                    .mail_request_id("00000000-0000-0000-0000-000000000201")
                    .purpose("FormResponseNotification")
                    .to(email="admin@example.com")
                    .subject("New response")
                    .text_body("A new response arrived.")
                    .scheduled_at(value)
                    .build()
                )
                self.assertEqual(request["scheduled_at"], value)

    def test_builder_rejects_timezone_less_and_invalid_scheduled_at(self) -> None:
        for value in (
            "2026-08-01T09:00:00",
            "2026-08-01 09:00:00",
            "not-a-date",
            "2026-13-45T09:00:00Z",
            "2026-02-30T09:00:00Z",
            "2026-04-31T09:00:00Z",
            "2026-08-01T09:00:00z",
        ):
            with self.subTest(value=value):
                with self.assertRaises(MailRequestValidationError):
                    (
                        MailRequestBuilder()
                        .mail_request_id("00000000-0000-0000-0000-000000000201")
                        .purpose("FormResponseNotification")
                        .to(email="admin@example.com")
                        .subject("New response")
                        .text_body("A new response arrived.")
                        .scheduled_at(value)
                        .build()
                    )

    def test_builder_allows_explicit_null_scheduled_at(self) -> None:
        request = (
            MailRequestBuilder()
            .mail_request_id("00000000-0000-0000-0000-000000000201")
            .purpose("FormResponseNotification")
            .to(email="admin@example.com")
            .subject("New response")
            .text_body("A new response arrived.")
            .scheduled_at(None)
            .build()
        )
        self.assertIn("scheduled_at", request)
        self.assertIsNone(request["scheduled_at"])

    def test_scheduled_at_is_emitted_without_payload_hash(self) -> None:
        base = build_sample_request()
        scheduled = (
            MailRequestBuilder()
            .mail_request_id("00000000-0000-0000-0000-000000000201")
            .purpose("FormResponseNotification")
            .to(email="admin@example.com")
            .subject("New response")
            .text_body("A new response arrived.")
            .scheduled_at("2026-08-01T09:00:00Z")
            .build()
        )
        self.assertNotIn("payload_hash", base)
        self.assertNotIn("payload_hash", scheduled)
        self.assertNotEqual(base.get("scheduled_at"), scheduled["scheduled_at"])

    def test_builder_rejects_invalid_mail_request_id(self) -> None:
        with self.assertRaises(MailRequestValidationError):
            (
                MailRequestBuilder()
                .mail_request_id("not-a-uuid")
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

    def test_client_posts_builder_scheduled_at_field(self) -> None:
        captured: dict[str, Any] = {}

        class CaptureHandler(BaseHTTPRequestHandler):
            def do_POST(self) -> None:  # noqa: N802
                length = int(self.headers.get("Content-Length", "0"))
                body = self.rfile.read(length)
                captured["request"] = json.loads(body.decode("utf-8"))
                payload = json.dumps(
                    {
                        "mail_request_id": "00000000-0000-0000-0000-000000000201",
                        "status": "accepted",
                    }
                ).encode("utf-8")
                self.send_response(202)
                self.send_header("Content-Type", "application/json")
                self.send_header("Content-Length", str(len(payload)))
                self.end_headers()
                self.wfile.write(payload)

            def log_message(self, format: str, *args: Any) -> None:
                return

        server = HTTPServer(("127.0.0.1", 0), CaptureHandler)
        thread = threading.Thread(target=server.serve_forever, daemon=True)
        thread.start()

        try:
            client = MailerClient(
                base_url=f"http://127.0.0.1:{server.server_port}",
                bearer_token="token",
            )
            request = (
                MailRequestBuilder()
                .mail_request_id("00000000-0000-0000-0000-000000000201")
                .purpose("FormResponseNotification")
                .to(email="admin@example.com")
                .subject("New response")
                .text_body("A new response arrived.")
                .scheduled_at("2026-08-01T09:00:00Z")
                .build()
            )
            response = client.send_mail(request)
            self.assertEqual(response.status, MailRequestAcceptanceStatus.ACCEPTED)
            self.assertEqual(captured["request"]["scheduled_at"], "2026-08-01T09:00:00Z")
            self.assertNotIn("payload_hash", captured["request"])
        finally:
            server.shutdown()
            thread.join(timeout=2)

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
        MockHandler.responses = [(422, {"code": "INVALID_REQUEST"})]
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
