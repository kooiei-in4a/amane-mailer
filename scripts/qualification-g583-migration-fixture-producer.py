#!/usr/bin/env python3
"""Run one G583 MIG01/MIG02 Docker fixture without creating qualification evidence.

The producer materializes only a value-free fixture input, runs the exact
checked-in .NET fixture, and returns its structured report.  A missing Docker
environment is represented as ``NOT_RUN_ENVIRONMENT``; it is never converted
to a PASS or a formal qualification result.
"""

from __future__ import annotations

import argparse
import importlib.util
import json
import os
import shutil
import subprocess
import sys
import tempfile
from pathlib import Path
from typing import Any


ROOT = Path(__file__).resolve().parent.parent
SCRIPTS = ROOT / "scripts"
ADAPTER_PATH = SCRIPTS / "qualification-g583-migration-docker-adapter.py"
MANIFEST_PATH = SCRIPTS / "qualification-g583-migration-adapter-manifest.json"
TEST_PROJECT = ROOT / "tests" / "Amane.Mailer.Tests" / "Amane.Mailer.Tests.csproj"


class ProducerError(Exception):
    """Expected, value-free producer rejection."""


def fail(message: str) -> None:
    raise ProducerError(message)


def load_adapter() -> Any:
    spec = importlib.util.spec_from_file_location("qualification_g583_migration_adapter_for_producer", ADAPTER_PATH)
    if spec is None or spec.loader is None:
        fail("migration Docker adapter could not be loaded")
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


def read_json(path: Path, label: str) -> Any:
    if not path.is_file() or path.is_symlink():
        fail(f"{label}: missing or symlink")
    try:
        return json.loads(path.read_text(encoding="utf-8"))
    except (OSError, UnicodeError, json.JSONDecodeError) as exc:
        raise ProducerError(f"{label}: invalid JSON") from exc


def write_json(path: Path, value: Any) -> None:
    path.write_text(json.dumps(value, sort_keys=True, separators=(",", ":")) + "\n", encoding="utf-8", newline="\n")


def fixture_input(
    adapter: Any,
    manifest: dict[str, Any],
    scenario_id: Any,
    variant_id: Any,
    authority_raw: Any,
) -> tuple[dict[str, Any], dict[str, str], dict[str, Any]]:
    authority = adapter.validate_authority(authority_raw)
    route = adapter.route_for(manifest, scenario_id, variant_id, "g583-s5a-platform-v1")
    candidate_image = authority.get("candidateImageReference")
    if not isinstance(candidate_image, str):
        fail("artifactAuthority.candidateImageReference is required to execute a Docker fixture")
    baseline_image = authority.get("baselineImageReference")
    if route["migrationMode"] == "upgrade" and not isinstance(baseline_image, str):
        fail("artifactAuthority.baselineImageReference is required for the MIG02 v1.2 baseline")
    base = manifest["baselineAuthority"]["migrationInventory"]
    delta = manifest["candidateDeltaInventory"]
    full = base + delta
    result = {
        "schemaVersion": 1,
        "scenarioId": route["scenarioId"],
        "variantId": route["variantId"],
        "contractVersion": route["contractVersion"],
        "fixtureId": route["fixtureId"],
        "fixtureRevision": "1",
        "migrationMode": route["migrationMode"],
        "candidate": {
            "candidateId": authority["candidateId"],
            "releaseCommitSha": authority["releaseCommitSha"],
            "ociIndexDigest": authority["ociIndexDigest"],
            "selectedManifests": authority["selectedManifests"],
            "imageReference": candidate_image,
        },
        "baseline": {
            "releaseTag": manifest["baselineAuthority"]["releaseTag"],
            "releaseCommitSha": manifest["baselineAuthority"]["releaseCommitSha"],
            "ociIndexDigest": manifest["baselineAuthority"]["ociIndexDigest"],
            "imageReference": baseline_image,
            "inventory": base,
        } if route["migrationMode"] == "upgrade" else None,
        "candidateDeltaInventory": delta,
        "candidateFullInventory": full,
    }
    return result, route, authority


def environment_status(reason_code: str, scenario_id: str, variant_id: str) -> dict[str, str | int]:
    return {
        "schemaVersion": 1,
        "kind": "g583-migration-fixture-producer-status",
        "scenarioId": scenario_id,
        "variantId": variant_id,
        "result": "NOT_RUN_ENVIRONMENT",
        "reasonCode": reason_code,
    }


def run_fixture(adapter: Any, fixture: dict[str, Any], route: dict[str, str], authority: dict[str, Any]) -> tuple[int, dict[str, Any]]:
    if shutil.which("docker") is None:
        return 2, environment_status("docker-cli-unavailable", route["scenarioId"], route["variantId"])
    if shutil.which("dotnet") is None:
        return 2, environment_status("dotnet-sdk-unavailable", route["scenarioId"], route["variantId"])
    if not TEST_PROJECT.is_file():
        fail("G583 migration fixture test project is unavailable")
    with tempfile.TemporaryDirectory(prefix="amane-g583-migration-fixture-") as temp:
        root = Path(temp)
        input_path = root / "fixture-input.json"
        result_path = root / "fixture-result.json"
        write_json(input_path, fixture)
        environment = os.environ.copy()
        environment["AMANE_G583_MIGRATION_FIXTURE_INPUT_PATH"] = str(input_path)
        environment["AMANE_G583_MIGRATION_FIXTURE_RESULT_PATH"] = str(result_path)
        command = [
            shutil.which("dotnet") or "dotnet", "test", str(TEST_PROJECT), "-c", "Release", "--no-restore", "--nologo",
            "--filter", f"FullyQualifiedName={route['sourceTestId']}",
        ]
        try:
            completed = subprocess.run(
                command,
                cwd=ROOT,
                env=environment,
                capture_output=True,
                text=True,
                encoding="utf-8",
                errors="replace",
                timeout=1_800,
                check=False,
            )
        except (OSError, subprocess.SubprocessError) as exc:
            raise ProducerError("Docker migration fixture could not be executed") from exc
        if not result_path.is_file():
            return 2, environment_status("docker-fixture-skipped-or-no-result", route["scenarioId"], route["variantId"])
        report = read_json(result_path, "fixture result")
        if completed.returncode != 0:
            return 1, {
                "schemaVersion": 1,
                "kind": "g583-migration-fixture-producer-status",
                "scenarioId": route["scenarioId"],
                "variantId": route["variantId"],
                "result": "FAIL",
                "reasonCode": "docker-fixture-failed",
            }
        try:
            adapter.validate_fixture_report(report, authority, adapter.load_manifest())
        except adapter.AdapterError as exc:
            raise ProducerError("Docker fixture report was rejected by the fail-closed adapter") from exc
        return 0, report


def self_test() -> int:
    adapter = load_adapter()
    manifest = adapter.load_manifest(MANIFEST_PATH)
    authority = adapter.valid_authority()
    for scenario, variant in (("G583-MIG-01", "win-docker"), ("G583-MIG-01", "linux-docker"), ("G583-MIG-02", "win-docker"), ("G583-MIG-02", "linux-docker")):
        input_document, route, normalized_authority = fixture_input(adapter, manifest, scenario, variant, authority)
        if input_document["fixtureId"] != route["fixtureId"] or input_document["candidate"]["candidateId"] != normalized_authority["candidateId"]:
            raise AssertionError("fixture input identity did not bind to its route")
        if (scenario == "G583-MIG-02") != (input_document["baseline"] is not None):
            raise AssertionError("MIG02 baseline materialization was incorrect")
    wrong_baseline = dict(authority)
    wrong_baseline["baselineImageReference"] = "example.invalid/amane@sha256:" + "0" * 64
    try:
        fixture_input(adapter, manifest, "G583-MIG-02", "win-docker", wrong_baseline)
    except adapter.AdapterError:
        pass
    else:
        raise AssertionError("wrong v1.2 baseline image authority was accepted")
    no_candidate = dict(authority)
    no_candidate.pop("candidateImageReference")
    try:
        fixture_input(adapter, manifest, "G583-MIG-01", "win-docker", no_candidate)
    except ProducerError:
        pass
    else:
        raise AssertionError("un-pinned candidate fixture input was accepted")
    print("[info] G583 MIG01/MIG02 migration fixture producer self-test passed")
    return 0


def command_run(args: argparse.Namespace) -> int:
    adapter = load_adapter()
    manifest = adapter.load_manifest(Path(args.manifest))
    authority_raw = read_json(Path(args.artifact_authority), "artifact authority")
    fixture, route, authority = fixture_input(adapter, manifest, args.scenario_id, args.variant_id, authority_raw)
    exit_code, output = run_fixture(adapter, fixture, route, authority)
    sys.stdout.write(json.dumps(output, sort_keys=True, separators=(",", ":")) + "\n")
    return exit_code


def command_manifest(args: argparse.Namespace) -> int:
    adapter = load_adapter()
    manifest = adapter.load_manifest(Path(args.manifest))
    print(json.dumps({"fixtureContractVersion": manifest["fixtureContractVersion"], "routeCount": len(manifest["routes"]), "result": "PASS"}, sort_keys=True))
    return 0


def build_parser() -> argparse.ArgumentParser:
    parser = argparse.ArgumentParser(description=__doc__)
    sub = parser.add_subparsers(dest="command", required=True)
    run = sub.add_parser("run")
    run.add_argument("--scenario-id", required=True)
    run.add_argument("--variant-id", required=True)
    run.add_argument("--artifact-authority", required=True)
    run.add_argument("--manifest", default=str(MANIFEST_PATH))
    run.set_defaults(func=command_run)
    manifest = sub.add_parser("manifest")
    manifest.add_argument("--manifest", default=str(MANIFEST_PATH))
    manifest.set_defaults(func=command_manifest)
    self_test_parser = sub.add_parser("self-test")
    self_test_parser.set_defaults(func=lambda _: self_test())
    return parser


def main(argv: list[str] | None = None) -> int:
    try:
        args = build_parser().parse_args(argv)
        return args.func(args)
    except ProducerError as exc:
        print(f"[error] {exc}", file=sys.stderr)
        return 1
    except (OSError, ValueError) as exc:
        print(f"[error] operation failed: {type(exc).__name__}", file=sys.stderr)
        return 1


if __name__ == "__main__":
    raise SystemExit(main())
