#!/usr/bin/env python3
"""Regression tests for OCI qualification producer validation and sealed view."""

from __future__ import annotations

import copy
import hashlib
import json
import shutil
import subprocess
import sys
import tempfile
from pathlib import Path
from typing import Any


SCRIPT_DIR = Path(__file__).resolve().parent
PREPARER = SCRIPT_DIR / "prepare-qualification-handoff.py"
SEALED_VALIDATOR = SCRIPT_DIR / "validate-qualification-handoff.sh"
CANDIDATE_ID = "a" * 64
BINDING_ID = "b" * 64
QUALIFICATION_RUN_ID = "c" * 64
EVENT_ID = "d" * 32
RELEASE_COMMIT_SHA = "c5a928eafe0e0f3527ad484993347d5035aa92bc"
OCI_DIGEST = "sha256:" + "e" * 64
AUTHORIZATION_DIGEST = "f" * 64
PRODUCER = {
    "repository": "kooiei-in4a/amane-mailer",
    "workflowPath": ".github/workflows/publish-sealed-qualification-handoff.yml",
    "workflowId": 339000001,
    "event": "workflow_dispatch",
    "headBranch": "qualification-handoff/v1.3.0-rc13",
    "headSha": "fba3f0cc8cbdef60c129dbffa214228f6967d073",
    "runId": 32225560868,
    "runAttempt": 1,
}


def write_json(path: Path, value: Any) -> None:
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_text(json.dumps(value, indent=2, sort_keys=True) + "\n", encoding="utf-8")


def sha256(path: Path) -> str:
    return hashlib.sha256(path.read_bytes()).hexdigest()


def create_fixture(root: Path) -> None:
    identity = {
        "candidateId": CANDIDATE_ID,
        "bindingId": BINDING_ID,
        "qualificationRunId": QUALIFICATION_RUN_ID,
    }
    binding = {
        **identity,
        "authorizationDigestSha256": AUTHORIZATION_DIGEST,
        "releaseCommitSha": RELEASE_COMMIT_SHA,
        "sourceCommitSha": RELEASE_COMMIT_SHA,
        "ociIndexDigest": OCI_DIGEST,
    }
    decision = {
        **identity,
        "authorizationDigestSha256": AUTHORIZATION_DIGEST,
        "sourceCommitSha": RELEASE_COMMIT_SHA,
        "ociIndexDigest": OCI_DIGEST,
        "machineVerdict": "GO_ELIGIBLE",
        "humanDecision": "APPROVE",
        "runSealed": True,
        "issueFreshnessCheck": {"matchedBinding": True},
    }
    event = {
        **identity,
        "eventId": EVENT_ID,
        "status": "sealed",
        "runStatusEventSequence": 1,
        "canonicalization": {"algorithm": "RFC8785-JCS", "version": 1},
        "previousRunStatusEventDigestSha256": None,
        "decisionDigests": {
            "evidenceIndexSha256": "1" * 64,
            "goNoGoSha256": "2" * 64,
            "phase4ManifestSha256": "3" * 64,
        },
    }
    event["eventDigestSha256"] = hashlib.sha256(
        json.dumps(event, ensure_ascii=False, sort_keys=True, separators=(",", ":")).encode("utf-8")
    ).hexdigest()

    write_json(root / "binding.json", binding)
    write_json(root / "decision/go-no-go.json", decision)
    write_json(root / f"run-status-events/{EVENT_ID}.json", event)
    object_paths = (
        "binding.json",
        "decision/go-no-go.json",
        f"run-status-events/{EVENT_ID}.json",
    )
    manifest = {
        "schemaVersion": 1,
        "publicationOnly": True,
        **identity,
        "sealedEventId": EVENT_ID,
        "objects": [{"path": path, "sha256": sha256(root / path)} for path in object_paths],
    }
    write_json(root / "handoff-manifest.json", manifest)
    write_json(root / "qualification-producer.json", PRODUCER)


def run_preparer(artifact: Path, expected: Path, sealed: Path) -> subprocess.CompletedProcess[str]:
    return subprocess.run(
        [
            sys.executable,
            str(PREPARER),
            "--artifact-root",
            str(artifact),
            "--expected-producer-identity",
            str(expected),
            "--sealed-root",
            str(sealed),
        ],
        check=False,
        capture_output=True,
        text=True,
    )


def run_sealed_validator(root: Path) -> subprocess.CompletedProcess[str]:
    return subprocess.run(
        [
            "bash",
            str(SEALED_VALIDATOR),
            "--root",
            str(root),
            "--candidate-id",
            CANDIDATE_ID,
            "--qualification-run-id",
            QUALIFICATION_RUN_ID,
            "--release-commit-sha",
            RELEASE_COMMIT_SHA,
            "--expected-digest",
            OCI_DIGEST,
        ],
        check=False,
        capture_output=True,
        text=True,
    )


def expect_pass(name: str, result: subprocess.CompletedProcess[str]) -> None:
    if result.returncode != 0:
        raise AssertionError(f"{name}: expected PASS\n{result.stdout}\n{result.stderr}")
    print(f"[PASS] {name}")


def expect_fail(name: str, result: subprocess.CompletedProcess[str]) -> None:
    if result.returncode == 0:
        raise AssertionError(f"{name}: expected FAIL\n{result.stdout}")
    print(f"[PASS] {name}")


def snapshot(root: Path) -> dict[str, str]:
    return {
        path.relative_to(root).as_posix(): sha256(path)
        for path in root.rglob("*")
        if path.is_file()
    }


def main() -> None:
    with tempfile.TemporaryDirectory(prefix="qualification-producer-self-test-") as temporary:
        work = Path(temporary)
        artifact = work / "artifact"
        expected = work / "expected-producer.json"
        sealed = work / "sealed"
        create_fixture(artifact)
        write_json(expected, PRODUCER)
        before = snapshot(artifact)

        expect_pass("valid producer metadata prepares sealed-only view", run_preparer(artifact, expected, sealed))
        expect_pass("sealed-only view passes strict validator", run_sealed_validator(sealed))
        if snapshot(artifact) != before:
            raise AssertionError("immutable qualification artifact changed")
        for relative in (
            "handoff-manifest.json",
            "binding.json",
            "decision/go-no-go.json",
            f"run-status-events/{EVENT_ID}.json",
        ):
            if (artifact / relative).read_bytes() != (sealed / relative).read_bytes():
                raise AssertionError(f"sealed view changed bytes: {relative}")
        print("[PASS] immutable artifact unchanged and sealed bytes copied byte-for-byte")

        expect_fail(
            "strict sealed validator still rejects producer metadata as an extra file",
            run_sealed_validator(artifact),
        )

        mismatch = work / "producer-mismatch"
        shutil.copytree(artifact, mismatch)
        mismatched_producer = copy.deepcopy(PRODUCER)
        mismatched_producer["headSha"] = "0" * 40
        write_json(mismatch / "qualification-producer.json", mismatched_producer)
        expect_fail(
            "qualification producer identity mismatch is rejected",
            run_preparer(mismatch, expected, work / "mismatch-sealed"),
        )

        extra = work / "unexpected-extra"
        shutil.copytree(artifact, extra)
        (extra / "unexpected.txt").write_text("must fail\n", encoding="utf-8")
        expect_fail(
            "unexpected qualification artifact file is rejected",
            run_preparer(extra, expected, work / "extra-sealed"),
        )

    print("[info] qualification producer handoff self-test passed")
    print("finalResult=PASS")


if __name__ == "__main__":
    main()
