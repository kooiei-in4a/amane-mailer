#!/usr/bin/env python3
"""No-send contract tests for the official Python smoke client."""

from __future__ import annotations

import json
import os
import subprocess
import sys
import threading
import unittest
import uuid
from collections import deque
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from pathlib import Path
from typing import Any


REPOSITORY_ROOT = Path(__file__).resolve().parents[2]
CLIENT_PATH = REPOSITORY_ROOT / "examples" / "consumer-python" / "send_mail.py"
SECRET = "amk_fixture.secret-must-not-be-printed"
RECIPIENT = "recipient-canary@example.invalid"
SUBJECT = "subject-canary-must-not-be-printed"
TEXT_BODY = "body-canary-must-not-be-printed"


class FixtureState:
    def __init__(self, *, post_status: int = 202, post_code: str = "") -> None:
        self.post_status = post_status
        self.post_code = post_code
        self.statuses: deque[str] = deque()
        self.requests: list[dict[str, Any]] = []


class FixtureHandler(BaseHTTPRequestHandler):
    state: FixtureState

    def _write_json(self, status_code: int, payload: dict[str, Any]) -> None:
        body = json.dumps(payload).encode("utf-8")
        self.send_response(status_code)
        self.send_header("Content-Type", "application/json")
        self.send_header("Content-Length", str(len(body)))
        self.end_headers()
        self.wfile.write(body)

    def do_POST(self) -> None:  # noqa: N802
        length = int(self.headers.get("Content-Length", "0"))
        payload = json.loads(self.rfile.read(length).decode("utf-8"))
        self.state.requests.append(
            {
                "method": "POST",
                "path": self.path,
                "authorization": self.headers.get("Authorization"),
                "content_type": self.headers.get("Content-Type"),
                "payload": payload,
            },
        )
        if self.state.post_status == 202:
            self._write_json(
                202,
                {"mail_request_id": payload["mail_request_id"], "status": "accepted"},
            )
            return
        self._write_json(
            self.state.post_status,
            {
                "code": self.state.post_code,
                "message": f"diagnostic must not include {SECRET}",
            },
        )

    def do_GET(self) -> None:  # noqa: N802
        request_id = self.path.rsplit("/", 1)[-1]
        status = self.state.statuses.popleft() if self.state.statuses else "delivered"
        self.state.requests.append(
            {
                "method": "GET",
                "path": self.path,
                "authorization": self.headers.get("Authorization"),
            },
        )
        self._write_json(
            200,
            {
                "mail_request_id": request_id,
                "status": status,
                "attempt_count": 1,
                "max_attempts": 5,
                "accepted_at": "2026-09-05T00:00:00Z",
            },
        )

    def log_message(self, format: str, *args: Any) -> None:
        return


class FixtureServer:
    def __init__(self, state: FixtureState) -> None:
        handler = type("BoundFixtureHandler", (FixtureHandler,), {"state": state})
        self.server = ThreadingHTTPServer(("127.0.0.1", 0), handler)
        self.thread = threading.Thread(target=self.server.serve_forever, daemon=True)

    @property
    def base_url(self) -> str:
        return f"http://127.0.0.1:{self.server.server_port}/"

    def __enter__(self) -> FixtureServer:
        self.thread.start()
        return self

    def __exit__(self, *_: Any) -> None:
        self.server.shutdown()
        self.server.server_close()
        self.thread.join(timeout=2)


def run_client(base_url: str, *, extra_args: list[str] | None = None) -> subprocess.CompletedProcess[str]:
    environment = os.environ.copy()
    environment.update(
        {
            "MAILER_BASE_URL": base_url,
            "MAILER_API_KEY": SECRET,
            "MAILER_RECIPIENT_EMAIL": RECIPIENT,
            "MAILER_SUBJECT": SUBJECT,
            "MAILER_TEXT_BODY": TEXT_BODY,
            "MAILER_POLL_INTERVAL_SECONDS": "0",
            "MAILER_POLL_TIMEOUT_SECONDS": "2",
        },
    )
    command = [sys.executable, str(CLIENT_PATH)]
    if extra_args:
        command.extend(extra_args)
    return subprocess.run(
        command,
        capture_output=True,
        text=True,
        env=environment,
        timeout=10,
        check=False,
    )


class OfficialPythonSmokeClientTests(unittest.TestCase):
    def test_success_posts_v2_request_and_polls_until_delivered(self) -> None:
        state = FixtureState()
        state.statuses.extend(["queued", "processing", "delivered"])
        with FixtureServer(state) as server:
            result = run_client(server.base_url)

        self.assertEqual(result.returncode, 0, result.stderr)
        self.assertNotIn(SECRET, result.stdout + result.stderr)
        self.assertNotIn(RECIPIENT, result.stdout + result.stderr)
        self.assertNotIn(SUBJECT, result.stdout + result.stderr)
        self.assertNotIn(TEXT_BODY, result.stdout + result.stderr)

        post = next((item for item in state.requests if item["method"] == "POST"), None)
        self.assertIsNotNone(post)
        assert post is not None
        payload = post["payload"]
        self.assertEqual(
            set(payload),
            {"mail_request_id", "purpose", "to", "subject", "text_body"},
        )
        self.assertEqual(payload["to"], [{"email": RECIPIENT}])
        self.assertEqual(post["authorization"], f"Bearer {SECRET}")
        self.assertEqual(post["content_type"], "application/json")

        request_id = payload["mail_request_id"]
        self.assertIsInstance(uuid.UUID(request_id), uuid.UUID)
        gets = [item for item in state.requests if item["method"] == "GET"]
        self.assertEqual(len(gets), 3)
        self.assertTrue(all(item["authorization"] == f"Bearer {SECRET}" for item in gets))
        self.assertTrue(all(item["path"].endswith(request_id) for item in gets))

    def test_polling_timeout_is_bounded(self) -> None:
        state = FixtureState()
        state.statuses.extend(["queued"] * 1000)
        with FixtureServer(state) as server:
            result = run_client(
                server.base_url,
                extra_args=["--poll-timeout-seconds", "0.05", "--poll-interval-seconds", "0.01"],
            )

        self.assertEqual(result.returncode, 1)
        self.assertIn("Status polling timed out", result.stderr)
        self.assertNotIn(SECRET, result.stdout + result.stderr)
        self.assertGreaterEqual(len([item for item in state.requests if item["method"] == "GET"]), 1)
        self.assertLess(len(state.requests), 20)

    def test_http_errors_show_only_safe_status_and_code(self) -> None:
        for status_code, code in ((401, "UNAUTHORIZED"), (409, "IDEMPOTENCY_CONFLICT"), (429, "AUTHENTICATION_RATE_LIMITED")):
            with self.subTest(status_code=status_code):
                state = FixtureState(post_status=status_code, post_code=code)
                with FixtureServer(state) as server:
                    result = run_client(server.base_url)

                self.assertEqual(result.returncode, 1)
                diagnostics = result.stdout + result.stderr
                self.assertIn(str(status_code), diagnostics)
                self.assertIn(code, diagnostics)
                self.assertNotIn(SECRET, diagnostics)
                self.assertNotIn("diagnostic must not include", diagnostics)

    def test_untrusted_error_code_is_not_echoed(self) -> None:
        state = FixtureState(post_status=401, post_code=SECRET)
        with FixtureServer(state) as server:
            result = run_client(server.base_url)

        self.assertEqual(result.returncode, 1)
        diagnostics = result.stdout + result.stderr
        self.assertIn("HTTP 401 Unauthorized (code=unknown)", diagnostics)
        self.assertNotIn(SECRET, diagnostics)

    def test_missing_key_in_noninteractive_mode_fails_without_a_secret_argument(self) -> None:
        environment = os.environ.copy()
        for name in (
            "MAILER_API_KEY",
            "MAILER_RECIPIENT_EMAIL",
            "MAILER_SUBJECT",
            "MAILER_TEXT_BODY",
        ):
            environment.pop(name, None)
        environment["MAILER_BASE_URL"] = "http://127.0.0.1:1/"

        result = subprocess.run(
            [sys.executable, str(CLIENT_PATH), "--help"],
            capture_output=True,
            text=True,
            env=environment,
            timeout=10,
            check=False,
        )
        self.assertEqual(result.returncode, 0)
        self.assertNotIn("--api-key", result.stdout)
        self.assertIn("MAILER_API_KEY", result.stdout)

        result = subprocess.run(
            [
                sys.executable,
                str(CLIENT_PATH),
                "--recipient",
                RECIPIENT,
            ],
            input="",
            capture_output=True,
            text=True,
            env=environment,
            timeout=10,
            check=False,
        )
        self.assertEqual(result.returncode, 1)
        self.assertIn("MAILER_API_KEY", result.stderr)
        self.assertNotIn("Bearer", result.stdout + result.stderr)


if __name__ == "__main__":
    unittest.main(verbosity=2)
