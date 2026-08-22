#!/usr/bin/env python3
"""Shared production-shape qualification artifact contract regressions."""

from __future__ import annotations

import hashlib
import json
import os
import shutil
import subprocess
import sys
import tempfile
from pathlib import Path
from typing import Any


SCRIPT_DIR = Path(__file__).resolve().parent
PREPARER = SCRIPT_DIR / "prepare-qualification-handoff.py"
SEALED_VALIDATOR = SCRIPT_DIR / "validate-qualification-handoff.sh"
FIXTURE_ROOT = SCRIPT_DIR / "fixtures/qualification-handoff"
PRODUCTION_FIXTURE = FIXTURE_ROOT / "production-shape/artifact"
EXPECTED_PRODUCER = FIXTURE_ROOT / "production-shape/expected-producer-identity.json"
WRONG_HEAD_PRODUCER = FIXTURE_ROOT / "negative/wrong-producer-head-sha.json"
BASH = os.environ.get("AMANE_BASH", "bash")


def load_json(path: Path) -> dict[str, Any]:
    value = json.loads(path.read_text(encoding="utf-8"))
    if not isinstance(value, dict):
        raise AssertionError(f"fixture must be a JSON object: {path}")
    return value


def write_json(path: Path, value: Any) -> None:
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_text(json.dumps(value, indent=2, sort_keys=True) + "\n", encoding="utf-8")


def sha256(path: Path) -> str:
    return hashlib.sha256(path.read_bytes()).hexdigest()


def copy_artifact(destination: Path) -> Path:
    shutil.copytree(PRODUCTION_FIXTURE, destination)
    return destination


def run_preparer(artifact: Path, sealed: Path) -> subprocess.CompletedProcess[str]:
    return subprocess.run(
        [
            sys.executable,
            str(PREPARER),
            "--artifact-root",
            str(artifact),
            "--expected-producer-identity",
            str(EXPECTED_PRODUCER),
            "--sealed-root",
            str(sealed),
        ],
        check=False,
        capture_output=True,
        text=True,
    )


def run_sealed_validator(root: Path) -> subprocess.CompletedProcess[str]:
    binding = load_json(PRODUCTION_FIXTURE / "binding.json")
    return subprocess.run(
        [
            BASH,
            str(SEALED_VALIDATOR),
            "--root",
            str(root),
            "--candidate-id",
            str(binding["candidateId"]),
            "--qualification-run-id",
            str(binding["qualificationRunId"]),
            "--release-commit-sha",
            str(binding["releaseCommitSha"]),
            "--expected-digest",
            str(binding["ociIndexDigest"]),
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
        if path.is_file() and not path.is_symlink()
    }


def producer_mismatch(work: Path, name: str, field: str, value: Any) -> None:
    artifact = copy_artifact(work / name)
    producer_path = artifact / "qualification-producer.json"
    producer = load_json(producer_path)
    producer[field] = value
    write_json(producer_path, producer)
    expect_fail(
        f"wrong producer {name.replace('-', ' ')} is rejected",
        run_preparer(artifact, work / f"{name}-sealed"),
    )


def main() -> None:
    with tempfile.TemporaryDirectory(prefix="shared-qualification-handoff-") as temporary:
        work = Path(temporary)
        artifact = copy_artifact(work / "artifact")
        sealed = work / "sealed"
        before = snapshot(artifact)

        expect_pass("production-shape artifact prepares sealed-only view", run_preparer(artifact, sealed))
        expect_pass("sealed-only view passes unchanged strict validator", run_sealed_validator(sealed))
        if snapshot(artifact) != before:
            raise AssertionError("immutable qualification artifact changed")

        manifest = load_json(artifact / "handoff-manifest.json")
        sealed_paths = ["handoff-manifest.json", *(str(entry["path"]) for entry in manifest["objects"])]
        for relative in sealed_paths:
            if (artifact / relative).read_bytes() != (sealed / relative).read_bytes():
                raise AssertionError(f"sealed view changed bytes: {relative}")
        print("[PASS] immutable artifact unchanged and sealed bytes copied byte-for-byte")

        expect_fail(
            "strict sealed validator rejects producer metadata as an extra file",
            run_sealed_validator(artifact),
        )

        missing_producer = copy_artifact(work / "missing-producer")
        (missing_producer / "qualification-producer.json").unlink()
        expect_fail(
            "missing producer metadata is rejected",
            run_preparer(missing_producer, work / "missing-producer-sealed"),
        )

        expected = load_json(EXPECTED_PRODUCER)
        wrong_head = load_json(WRONG_HEAD_PRODUCER)
        producer_mismatch(work, "repository", "repository", "example.invalid/amane-mailer")
        producer_mismatch(work, "workflow-path", "workflowPath", ".github/workflows/untrusted.yml")
        producer_mismatch(work, "workflow-id", "workflowId", int(expected["workflowId"]) + 1)
        producer_mismatch(work, "head-sha", "headSha", wrong_head["headSha"])
        producer_mismatch(work, "run-id", "runId", int(expected["runId"]) + 1)
        producer_mismatch(work, "run-attempt", "runAttempt", int(expected["runAttempt"]) + 1)

        extra_producer_field = copy_artifact(work / "extra-producer-field")
        producer_path = extra_producer_field / "qualification-producer.json"
        producer = load_json(producer_path)
        producer["unexpected"] = "forbidden"
        write_json(producer_path, producer)
        expect_fail(
            "unexpected producer metadata field is rejected",
            run_preparer(extra_producer_field, work / "extra-producer-field-sealed"),
        )

        unexpected_file = copy_artifact(work / "unexpected-file")
        (unexpected_file / "unexpected.txt").write_text("must fail\n", encoding="utf-8")
        expect_fail(
            "unexpected qualification artifact file is rejected",
            run_preparer(unexpected_file, work / "unexpected-file-sealed"),
        )

        missing_sealed_file = copy_artifact(work / "missing-sealed-file")
        (missing_sealed_file / "binding.json").unlink()
        expect_fail(
            "missing sealed file is rejected",
            run_preparer(missing_sealed_file, work / "missing-sealed-file-view"),
        )

        nested_layout = work / "nested-layout"
        copy_artifact(nested_layout / "artifact-wrapper")
        expect_fail(
            "nested artifact layout is rejected",
            run_preparer(nested_layout, work / "nested-layout-sealed"),
        )

        symlink_artifact = copy_artifact(work / "symlink")
        try:
            (symlink_artifact / "unexpected-link.json").symlink_to(symlink_artifact / "binding.json")
        except OSError:
            if os.name != "nt":
                raise
            print("[SKIP] symlink creation is unavailable on this Windows host; CI covers rejection")
        else:
            expect_fail(
                "symlink entry is rejected",
                run_preparer(symlink_artifact, work / "symlink-sealed"),
            )

        mutated_artifact = copy_artifact(work / "sealed-mutation")
        binding_path = mutated_artifact / "binding.json"
        binding = load_json(binding_path)
        binding["sourceCommitSha"] = "0" * 40
        write_json(binding_path, binding)
        mutated_sealed = work / "sealed-mutation-view"
        expect_pass(
            "shared preparation preserves mutated sealed bytes for downstream validation",
            run_preparer(mutated_artifact, mutated_sealed),
        )
        expect_fail(
            "unchanged strict validator rejects sealed document mutation",
            run_sealed_validator(mutated_sealed),
        )

        inside_artifact = copy_artifact(work / "inside-artifact")
        inside_before = snapshot(inside_artifact)
        expect_fail(
            "sealed view inside immutable artifact is rejected",
            run_preparer(inside_artifact, inside_artifact / "sealed-view"),
        )
        if snapshot(inside_artifact) != inside_before or (inside_artifact / "sealed-view").exists():
            raise AssertionError("rejected sealed view mutated immutable artifact")
        print("[PASS] rejected output path leaves immutable artifact unchanged")

    print("[info] shared qualification handoff self-test passed")
    print("finalResult=PASS")


if __name__ == "__main__":
    main()
