"""HTTP client for posting mail requests."""

from __future__ import annotations

import json
import time
from typing import Any
from urllib.error import HTTPError, URLError
from urllib.parse import urljoin
from urllib.request import Request, urlopen

from amane_mailer.builder import MailRequestBuilder
from amane_mailer.errors import (
    MailRequestAcceptedResponse,
    MailRequestAcceptanceStatus,
    MailerRetryableError,
    parse_mailer_error,
)


class MailerClient:
    def __init__(
        self,
        *,
        base_url: str,
        bearer_token: str,
        timeout_seconds: float = 10.0,
    ) -> None:
        if not base_url or not bearer_token:
            raise ValueError("base_url and bearer_token are required.")

        self.base_url = base_url
        self.bearer_token = bearer_token
        self.timeout_seconds = timeout_seconds

    def send_mail(
        self,
        mail_request: dict[str, Any],
        *,
        max_retries: int = 3,
        base_delay_seconds: float = 0.2,
    ) -> MailRequestAcceptedResponse:
        for attempt in range(max_retries + 1):
            try:
                return self._post_once(mail_request)
            except MailerRetryableError:
                if attempt >= max_retries:
                    raise
                time.sleep(base_delay_seconds * (2**attempt))

        raise RuntimeError("send_mail exhausted retries without a response.")

    def _post_once(self, mail_request: dict[str, Any]) -> MailRequestAcceptedResponse:
        endpoint = urljoin(self.base_url.rstrip("/") + "/", "internal/mail-requests")
        body = json.dumps(mail_request, separators=(",", ":"), ensure_ascii=False).encode(
            "utf-8",
        )
        request = Request(
            endpoint,
            data=body,
            method="POST",
            headers={
                "Authorization": f"Bearer {self.bearer_token}",
                "Content-Type": "application/json",
                "Accept": "application/json",
            },
        )

        try:
            with urlopen(request, timeout=self.timeout_seconds) as response:
                status_code = response.status
                response_body = response.read().decode("utf-8")
        except HTTPError as error:
            status_code = error.code
            response_body = error.read().decode("utf-8")
        except URLError as error:
            raise MailerRetryableError(str(error)) from error

        if status_code == 202:
            payload = json.loads(response_body)
            status = payload.get("status")
            if status not in (
                MailRequestAcceptanceStatus.ACCEPTED,
                MailRequestAcceptanceStatus.ALREADY_ACCEPTED,
            ):
                raise parse_mailer_error(status_code, response_body)

            return MailRequestAcceptedResponse(
                mail_request_id=payload["mail_request_id"],
                status=status,
            )

        raise parse_mailer_error(status_code, response_body)


__all__ = ["MailRequestBuilder", "MailerClient"]
