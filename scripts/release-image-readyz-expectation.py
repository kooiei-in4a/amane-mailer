#!/usr/bin/env python3
"""Version-aware /readyz expectation for release-image-build-smoke.

v1 (major < 2): HTTP 200 means the legacy initialized runtime is ready.
v2 (major >= 2): fresh managed-instance smoke expects exact first-run unreadiness:
  HTTP 503, ready=false, reason=uninitialized.

Generic HTTP 503 acceptance is intentionally rejected.
"""

from __future__ import annotations

import argparse
import json
import sys
from typing import Any


def major_version(mailer_version: str) -> int:
    return int(mailer_version.split(".", 1)[0])


def validate_readyz(
    mailer_version: str,
    status_code: int,
    body: dict[str, Any] | None,
) -> None:
    major = major_version(mailer_version)
    if major >= 2:
        if status_code != 503:
            raise SystemExit(
                f"v{major} fresh smoke requires GET /readyz HTTP 503, got {status_code}"
            )
        if body is None:
            raise SystemExit("v2 fresh smoke requires a JSON /readyz body")
        if body.get("ready") is not False:
            raise SystemExit(
                f"v2 fresh smoke requires ready=false, got {body.get('ready')!r}"
            )
        if body.get("reason") != "uninitialized":
            raise SystemExit(
                "v2 fresh smoke requires reason='uninitialized', "
                f"got {body.get('reason')!r}"
            )
        return

    if status_code != 200:
        raise SystemExit(f"v1 smoke requires GET /readyz HTTP 200, got {status_code}")


def validate_healthz(status_code: int, body: dict[str, Any] | None) -> None:
    if status_code != 200:
        raise SystemExit(f"GET /healthz must return HTTP 200, got {status_code}")
    if body is None:
        raise SystemExit("GET /healthz requires a JSON body")
    if body.get("healthy") is not True:
        raise SystemExit(f"GET /healthz requires healthy=true, got {body.get('healthy')!r}")


def _load_json(path: str) -> dict[str, Any]:
    with open(path, encoding="utf-8") as handle:
        raw = json.load(handle)
    if not isinstance(raw, dict):
        raise SystemExit(f"expected JSON object in {path}")
    return raw


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    sub = parser.add_subparsers(dest="command", required=True)

    health = sub.add_parser("validate-healthz")
    health.add_argument("--status-code", type=int, required=True)
    health.add_argument("--body-file", required=True)

    ready = sub.add_parser("validate-readyz")
    ready.add_argument("--mailer-version", required=True)
    ready.add_argument("--status-code", type=int, required=True)
    ready.add_argument("--body-file")

    self_test = sub.add_parser("self-test")

    args = parser.parse_args(argv)

    if args.command == "validate-healthz":
        validate_healthz(args.status_code, _load_json(args.body_file))
        return 0

    if args.command == "validate-readyz":
        body = _load_json(args.body_file) if args.body_file else None
        validate_readyz(args.mailer_version, args.status_code, body)
        return 0

    if args.command == "self-test":
        # v2 exact first-run PASS
        validate_readyz("2.0.0", 503, {"ready": False, "reason": "uninitialized"})
        # v2 wrong reason FAIL
        try:
            validate_readyz("2.0.0", 503, {"ready": False, "reason": "worker-not-ready"})
        except SystemExit:
            pass
        else:
            raise SystemExit("expected FAIL for wrong reason")
        # v2 ready=true with 503 FAIL
        try:
            validate_readyz("2.0.0", 503, {"ready": True, "reason": "uninitialized"})
        except SystemExit:
            pass
        else:
            raise SystemExit("expected FAIL for ready=true")
        # v2 generic 500 FAIL
        try:
            validate_readyz("2.0.0", 500, {"ready": False, "reason": "uninitialized"})
        except SystemExit:
            pass
        else:
            raise SystemExit("expected FAIL for HTTP 500")
        # v2 HTTP 200 FAIL (must not weaken readiness)
        try:
            validate_readyz("2.0.0", 200, {"ready": True})
        except SystemExit:
            pass
        else:
            raise SystemExit("expected FAIL for v2 HTTP 200")
        # v1 preserves HTTP 200 requirement
        validate_readyz("1.3.7", 200, None)
        try:
            validate_readyz("1.3.7", 503, {"ready": False, "reason": "uninitialized"})
        except SystemExit:
            pass
        else:
            raise SystemExit("expected FAIL for v1 HTTP 503")
        validate_healthz(200, {"healthy": True})
        try:
            validate_healthz(200, {"healthy": False})
        except SystemExit:
            pass
        else:
            raise SystemExit("expected FAIL for healthy=false")
        print("release-image-readyz-expectation self-test: PASS")
        return 0

    raise SystemExit(f"unknown command: {args.command}")


if __name__ == "__main__":
    sys.exit(main())
