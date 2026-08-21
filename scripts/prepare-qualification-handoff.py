#!/usr/bin/env python3
"""Validate qualification producer identity and create a sealed-only view."""

from __future__ import annotations

import argparse
import hashlib
import json
import re
import shutil
import sys
from pathlib import Path
from typing import Any, NoReturn


PRODUCER_FIELDS = (
    "repository",
    "workflowPath",
    "workflowId",
    "event",
    "headBranch",
    "headSha",
    "runId",
    "runAttempt",
)
INTEGER_FIELDS = frozenset({"workflowId", "runId", "runAttempt"})
HEX40 = re.compile(r"^[0-9a-f]{40}$")


def fail(field: str, message: str) -> NoReturn:
    print(f"[error] {field}: {message}", file=sys.stderr)
    raise SystemExit(1)


def load_object(path: Path, field: str) -> dict[str, Any]:
    try:
        value = json.loads(path.read_text(encoding="utf-8"))
    except (OSError, json.JSONDecodeError):
        fail(field, "missing or invalid JSON")
    if not isinstance(value, dict):
        fail(field, "document must be an object")
    return value


def positive_integer(value: Any, field: str) -> int:
    if isinstance(value, bool):
        fail(field, "must be a positive integer")
    if isinstance(value, int) and value > 0:
        return value
    if isinstance(value, str) and re.fullmatch(r"[1-9][0-9]*", value):
        return int(value)
    fail(field, "must be a positive integer")


def validate_expected_identity(expected: dict[str, Any]) -> None:
    if set(expected) != set(PRODUCER_FIELDS):
        fail("expectedProducerIdentity", "must contain exactly the eight producer identity fields")
    for field in PRODUCER_FIELDS:
        value = expected[field]
        if field in INTEGER_FIELDS:
            positive_integer(value, f"expectedProducerIdentity.{field}")
        elif not isinstance(value, str) or not value:
            fail(f"expectedProducerIdentity.{field}", "must be a non-empty string")
    if not HEX40.fullmatch(expected["headSha"]):
        fail("expectedProducerIdentity.headSha", "must be 40 lowercase hex")


def validate_producer(producer: dict[str, Any], expected: dict[str, Any]) -> None:
    for field in PRODUCER_FIELDS:
        if field not in producer:
            fail(f"qualificationProducer.{field}", "is required")
        if field in INTEGER_FIELDS:
            actual = positive_integer(producer[field], f"qualificationProducer.{field}")
            wanted = positive_integer(expected[field], f"expectedProducerIdentity.{field}")
        else:
            actual = producer[field]
            wanted = expected[field]
        if actual != wanted:
            fail(f"qualificationProducer.{field}", "mismatch")


def qualification_files(root: Path) -> tuple[set[str], str]:
    if not root.is_dir():
        fail("qualificationRoot", "directory is missing")
    all_paths: set[str] = set()
    for path in root.rglob("*"):
        if path.is_symlink():
            fail("qualificationRoot", "symlink entries are forbidden")
        if path.is_file():
            all_paths.add(path.relative_to(root).as_posix())

    event_paths = sorted(root.glob("run-status-events/*.json"))
    if len(event_paths) != 1:
        fail("run-status-events", "exactly one JSON event is required")
    event_relative = event_paths[0].relative_to(root).as_posix()
    expected_paths = {
        "handoff-manifest.json",
        "binding.json",
        "decision/go-no-go.json",
        "qualification-producer.json",
        event_relative,
    }
    if all_paths != expected_paths:
        fail("qualificationRoot.files", "contains an unexpected or missing file")
    return all_paths, event_relative


def copy_sealed_view(artifact_root: Path, sealed_root: Path, event_relative: str) -> None:
    if sealed_root.is_symlink():
        fail("sealedRoot", "must not be a symlink")
    if sealed_root.exists():
        if not sealed_root.is_dir() or any(sealed_root.iterdir()):
            fail("sealedRoot", "must be an empty directory")
    else:
        sealed_root.mkdir(parents=True)

    try:
        sealed_root.resolve().relative_to(artifact_root.resolve())
    except ValueError:
        pass
    else:
        fail("sealedRoot", "must be outside the immutable artifact root")

    sealed_paths = (
        "handoff-manifest.json",
        "binding.json",
        "decision/go-no-go.json",
        event_relative,
    )
    for relative in sealed_paths:
        source = artifact_root / relative
        destination = sealed_root / relative
        destination.parent.mkdir(parents=True, exist_ok=True)
        shutil.copyfile(source, destination)
        source_digest = hashlib.sha256(source.read_bytes()).digest()
        destination_digest = hashlib.sha256(destination.read_bytes()).digest()
        if source_digest != destination_digest:
            fail(f"sealedView.{relative}", "byte-for-byte copy verification failed")


def prepare(artifact_root: Path, expected_identity_path: Path, sealed_root: Path) -> None:
    _, event_relative = qualification_files(artifact_root)
    expected = load_object(expected_identity_path, "expectedProducerIdentity")
    validate_expected_identity(expected)
    producer = load_object(artifact_root / "qualification-producer.json", "qualification-producer.json")
    validate_producer(producer, expected)
    copy_sealed_view(artifact_root, sealed_root, event_relative)


def main() -> None:
    parser = argparse.ArgumentParser()
    parser.add_argument("--artifact-root", required=True, type=Path)
    parser.add_argument("--expected-producer-identity", required=True, type=Path)
    parser.add_argument("--sealed-root", required=True, type=Path)
    args = parser.parse_args()
    prepare(args.artifact_root, args.expected_producer_identity, args.sealed_root)
    print("[info] qualification producer identity validated")
    print("[info] sealed-only qualification validation view prepared")


if __name__ == "__main__":
    main()
