#!/usr/bin/env python3
"""Materialize one formal G583 qualification evidence envelope.

This is the narrow RC12 integration bridge between the reviewed G583 Core/V1/V2
pre-qualification observations and the existing qualification runner.  It does
not change G456 semantics and it does not append evidence by itself.
"""

from __future__ import annotations

import argparse
import hashlib
import importlib.util
import json
import subprocess
import sys
from datetime import datetime, timezone
from pathlib import Path
from typing import Any

ROOT = Path(__file__).resolve().parent.parent
SCRIPTS = ROOT / "scripts"
RUNNER_PATH = SCRIPTS / "qualification-runner.py"
CORE_PATH = SCRIPTS / "qualification-g583-dispatch.py"
MIGRATION_ADAPTER_PATH = SCRIPTS / "qualification-g583-migration-docker-adapter.py"
MIGRATION_PRODUCER_PATH = SCRIPTS / "qualification-g583-migration-fixture-producer.py"
MIG03_ADAPTER_PATH = SCRIPTS / "qualification-g583-mig03-ci-auto-adapter.py"
MIG03_PRODUCER_PATH = SCRIPTS / "qualification-g583-mig03-fixture-producer.py"

PLATFORM_CONTRACT = "g583-s5a-platform-v1"
MIG03_CONTRACT = "g583-mig03-ci-auto-v1"
EXPECTED_ROUTES = {
    ("G583-MIG-01", "win-docker", PLATFORM_CONTRACT),
    ("G583-MIG-01", "linux-docker", PLATFORM_CONTRACT),
    ("G583-MIG-02", "win-docker", PLATFORM_CONTRACT),
    ("G583-MIG-02", "linux-docker", PLATFORM_CONTRACT),
    ("G583-MIG-03", "ci-auto", MIG03_CONTRACT),
}


class FormalAdapterError(Exception):
    """Expected fail-closed formal-route rejection."""


def fail(message: str) -> None:
    raise FormalAdapterError(message)


def load_module(name: str, path: Path) -> Any:
    spec = importlib.util.spec_from_file_location(name, path)
    if spec is None or spec.loader is None:
        fail(f"{name} could not be loaded")
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


def runner_module() -> Any:
    return load_module("qualification_runner_for_g583_formal_adapter", RUNNER_PATH)


def core_module() -> Any:
    return load_module("qualification_g583_core_for_formal_adapter", CORE_PATH)


def migration_adapter_module() -> Any:
    return load_module("qualification_g583_migration_adapter_for_formal_adapter", MIGRATION_ADAPTER_PATH)


def mig03_adapter_module() -> Any:
    return load_module("qualification_g583_mig03_adapter_for_formal_adapter", MIG03_ADAPTER_PATH)


def read_json(path: Path, label: str) -> Any:
    if not path.is_file() or path.is_symlink():
        fail(f"{label} is missing or a symlink")
    try:
        return json.loads(path.read_text(encoding="utf-8"))
    except (OSError, UnicodeError, json.JSONDecodeError) as exc:
        raise FormalAdapterError(f"{label} is not valid JSON") from exc


def canonical_json(value: Any) -> bytes:
    return json.dumps(value, ensure_ascii=False, sort_keys=True, separators=(",", ":")).encode("utf-8")


def sha(value: Any) -> str:
    return hashlib.sha256(canonical_json(value)).hexdigest()


def utc_now() -> str:
    return datetime.now(timezone.utc).replace(microsecond=0).isoformat().replace("+00:00", "Z")


def require_object(value: Any, label: str) -> dict[str, Any]:
    if not isinstance(value, dict):
        fail(f"{label} must be an object")
    return value


def bound_row(binding: dict[str, Any], scenario: str, variant: str) -> dict[str, Any]:
    matches = [
        row for row in binding.get("rows", [])
        if row.get("scenarioId") == scenario and variant in row.get("requiredVariants", [])
    ]
    if len(matches) != 1 or matches[0].get("gateClass") != "Hard":
        fail("G583 route is not one bound Hard qualification variant")
    return matches[0]


def owner_for(auth: dict[str, Any], scenario: str, variant: str) -> dict[str, str]:
    matches = [
        owner for owner in auth.get("evidenceOwners", [])
        if owner.get("scenarioId") == scenario and owner.get("variantId") == variant
    ]
    if len(matches) != 1:
        fail("G583 evidence owner is missing or duplicated")
    owner = matches[0]
    if owner.get("ownerRole") != "maintainer-migration":
        fail("G583 evidence owner role must be maintainer-migration")
    if not isinstance(owner.get("ownerIdentity"), str) or not owner["ownerIdentity"]:
        fail("G583 evidence owner identity is missing")
    return {"ownerRole": owner["ownerRole"], "ownerIdentity": owner["ownerIdentity"]}


def validate_route(scenario: str, variant: str) -> dict[str, Any]:
    contract = MIG03_CONTRACT if scenario == "G583-MIG-03" else PLATFORM_CONTRACT
    key = (scenario, variant, contract)
    if key not in EXPECTED_ROUTES:
        fail("unknown G583 formal route")
    core = core_module()
    manifest = core.load_manifest()
    if {
        tuple(item[field] for field in ("scenarioId", "variantId", "contractVersion"))
        for item in manifest["registrations"]
    } != EXPECTED_ROUTES:
        fail("G583 Core route authority is not the exact five-route set")
    return core.resolve_dispatch(manifest, scenario, variant, contract)


def load_context(run_root: Path) -> tuple[Any, dict[str, Any], dict[str, Any]]:
    runner = runner_module()
    try:
        binding = runner.load_binding(run_root)
        auth = runner.load_authorization(run_root)
    except runner.RunnerError as exc:
        raise FormalAdapterError("qualification binding/authorization is invalid") from exc
    if binding.get("scopeId") != runner.V13_SCOPE_ID:
        fail("G583 formal evidence requires the v1.3 qualification scope")
    return runner, binding, auth


def run_json(command: list[str], label: str, *, allowed_environment_not_run: bool = False) -> dict[str, Any]:
    try:
        completed = subprocess.run(
            command,
            cwd=ROOT,
            capture_output=True,
            text=True,
            encoding="utf-8",
            errors="replace",
            check=False,
            timeout=1_900,
        )
    except (OSError, subprocess.SubprocessError) as exc:
        raise FormalAdapterError(f"{label} could not be executed") from exc
    if not completed.stdout.endswith("\n") or completed.stdout.count("\n") != 1:
        fail(f"{label} did not emit exactly one JSON line")
    try:
        output = json.loads(completed.stdout)
    except (UnicodeError, json.JSONDecodeError) as exc:
        raise FormalAdapterError(f"{label} did not emit one JSON result") from exc
    if allowed_environment_not_run and completed.returncode == 2 and isinstance(output, dict) and output.get("result") == "NOT_RUN_ENVIRONMENT":
        fail(f"{label} environment unavailable: {output.get('reasonCode', 'unknown')}")
    if completed.returncode != 0:
        fail(f"{label} did not PASS")
    return require_object(output, f"{label} output")


def require_authority_matches_binding(authority: dict[str, Any], binding: dict[str, Any]) -> None:
    for field in ("candidateId", "releaseCommitSha", "ociIndexDigest"):
        if authority.get(field) != binding.get(field):
            fail(f"artifact authority {field} does not match qualification binding")


def common_migration_payload(binding: dict[str, Any]) -> dict[str, Any]:
    baseline = binding["migrationBaselineInventory"]
    delta = binding["migrationDeltaInventory"]
    full = binding["migrationFullInventory"]
    return {
        "migrationDecision": "INCLUDE",
        "baselineInventory": baseline,
        "deltaInventory": delta,
        "fullInventory": full,
        "expectedFullMigrationInventory": full,
        "migrationDirectoryInventoryBefore": full,
        "migrationDirectoryInventoryDigestSha256": binding["migrationFullInventoryDigestSha256"],
        "migrationDeltaInventoryDigestSha256": binding["migrationDeltaInventoryDigestSha256"],
        "migrationFileDigests": binding["migrationFullFileDigests"],
    }


def migration_payload(binding: dict[str, Any], scenario: str, observations: dict[str, Any]) -> dict[str, Any]:
    baseline = binding["migrationBaselineInventory"]
    delta = binding["migrationDeltaInventory"]
    full = binding["migrationFullInventory"]
    migration = require_object(observations.get("migration"), "MIG01/MIG02 migration observations")
    expected_before = [] if scenario == "G583-MIG-01" else baseline
    expected_applied = full if scenario == "G583-MIG-01" else delta
    if (
        migration.get("beforeInventory") != expected_before
        or migration.get("appliedInventory") != expected_applied
        or migration.get("finalInventory") != full
        or migration.get("lastApplied") != delta[-1]
        or migration.get("pendingMigrations") != []
        or migration.get("checksumVerification") != "PASS"
    ):
        fail("MIG01/MIG02 prequalification observations do not match the bound migration state")
    payload = common_migration_payload(binding)
    payload.update({
        "outcome": "applied" if scenario == "G583-MIG-01" else "upgraded",
        "preApplyAppliedMigrations": expected_before,
        "preApplyPendingMigrations": full if scenario == "G583-MIG-01" else delta,
        "postApplyAppliedMigrations": full,
        "postApplyPendingMigrations": [],
        "lastAppliedBefore": None if scenario == "G583-MIG-01" else baseline[-1],
        "lastAppliedAfter": delta[-1],
    })
    return payload


def mig03_payload(binding: dict[str, Any], observations: dict[str, Any]) -> dict[str, Any]:
    migration = require_object(observations.get("migration"), "MIG03 migration observations")
    full = binding["migrationFullInventory"]
    delta = binding["migrationDeltaInventory"]
    if (
        observations.get("qualificationExecuted") is not False
        or migration.get("migrationDecision") != "INCLUDE"
        or migration.get("baselineInventory") != binding["migrationBaselineInventory"]
        or migration.get("deltaInventory") != delta
        or migration.get("fullInventory") != full
        or migration.get("baselineInventoryDigestSha256") != binding["migrationBaselineInventoryDigestSha256"]
        or migration.get("deltaInventoryDigestSha256") != binding["migrationDeltaInventoryDigestSha256"]
        or migration.get("fullInventoryDigestSha256") != binding["migrationFullInventoryDigestSha256"]
        or migration.get("migrationInventoryDigestSha256") != binding["migrationFullInventoryDigestSha256"]
        or migration.get("schemaAllowlistVersion") != binding["migrationSchemaAllowlistVersion"]
        or migration.get("schemaAllowlistSha256") != binding["migrationSchemaAllowlistSha256"]
    ):
        fail("MIG03 prequalification observations do not match the bound schema authority")
    fixture = require_object(observations.get("fixture"), "MIG03 fixture identity")
    payload = common_migration_payload(binding)
    payload.update({
        "outcome": "schema-checked",
        "preApplyAppliedMigrations": [],
        "preApplyPendingMigrations": full,
        "postApplyAppliedMigrations": full,
        "postApplyPendingMigrations": [],
        "lastAppliedBefore": None,
        "lastAppliedAfter": delta[-1],
        "schemaContractResult": "pass",
        "piiValueCanaryResult": "pass",
        "schemaAllowlistVersion": binding["migrationSchemaAllowlistVersion"],
        "schemaAllowlistSha256": binding["migrationSchemaAllowlistSha256"],
    })
    if not isinstance(fixture.get("fixtureResultDigestSha256"), str):
        fail("MIG03 fixture result digest is missing")
    return payload


def build_envelope(
    runner: Any,
    binding: dict[str, Any],
    owner: dict[str, str],
    scenario: str,
    variant: str,
    payload: dict[str, Any],
    report: dict[str, Any],
    started_at: str,
    finished_at: str,
) -> dict[str, Any]:
    report_digest = sha(report)
    if scenario == "G583-MIG-03":
        procedure_id = "g583-mig03-ci-auto-schema-contract"
        identity = {
            "executionMode": "ci-auto",
            "candidateIdentityMatch": True,
            "sourceCommitIdentityMatch": True,
        }
    else:
        procedure_id = "g583-s5a-migration-docker"
        identity = {
            "platformContractResult": report.get("platformContractResult"),
            "artifactIdentityResult": report.get("artifactIdentityResult"),
            "selectedManifestDigest": report.get("selectedManifestDigest"),
            "candidateIdentityMatch": True,
            "sourceCommitIdentityMatch": True,
        }
    runner.value_free(identity, "$.identity")
    envelope = {
        "schemaVersion": 1,
        "kind": "release-qualification-evidence",
        "evidenceType": runner.EVIDENCE_TYPES[scenario][0],
        "evidenceId": sha({
            "bindingId": binding["bindingId"],
            "scenarioId": scenario,
            "variantId": variant,
            "reportDigestSha256": report_digest,
        }),
        "candidateId": binding["candidateId"],
        "sourceCommitSha": binding["releaseCommitSha"],
        "scenarioId": scenario,
        "variantId": variant,
        "issueBodySha256": binding["issueBodySha256"],
        "planRevision": binding["planRevision"],
        "planCommitSha": binding["planCommitSha"],
        "planFileSha256": binding["planFileSha256"],
        "bindingId": binding["bindingId"],
        "qualificationRunId": binding["qualificationRunId"],
        "attempt": 1,
        "result": "PASS",
        "startedAtUtc": started_at,
        "finishedAtUtc": finished_at,
        "executedByRole": owner["ownerRole"],
        "executedByIdentity": owner["ownerIdentity"],
        "procedureId": procedure_id,
        "procedureRevision": "1",
        "runnerClass": "qualification-g583-formal-adapter",
        "toolVersion": "1",
        "attestedAtUtc": finished_at,
        "identity": identity,
        "prohibitedContentScan": {
            "result": "PASS",
            "scannerId": "qualification-g583-value-free",
            "scannerVersion": "1",
            "reportDigestSha256": report_digest,
        },
        "typePayload": payload,
        "scopeId": binding["scopeId"],
        "scopeVersion": binding["scopeVersion"],
        "scopeManifestSha256": binding["scopeManifestSha256"],
    }
    return envelope


def authoritative_validate(runner: Any, binding: dict[str, Any], auth: dict[str, Any], envelope: dict[str, Any], scenario: str, variant: str) -> None:
    try:
        runner.validate_evidence_envelope(envelope, binding, auth, (scenario, variant))
    except runner.RunnerError as exc:
        raise FormalAdapterError("derived G583 evidence was rejected by the qualification runner") from exc


def command_run(args: argparse.Namespace) -> int:
    scenario = args.scenario_id
    variant = args.variant_id
    validate_route(scenario, variant)
    run_root = Path(args.run_root).resolve()
    runner, binding, auth = load_context(run_root)
    bound_row(binding, scenario, variant)
    owner = owner_for(auth, scenario, variant)
    started_at = utc_now()

    if scenario in {"G583-MIG-01", "G583-MIG-02"}:
        if not args.artifact_authority:
            fail("MIG01/MIG02 requires --artifact-authority")
        authority = require_object(read_json(Path(args.artifact_authority), "artifact authority"), "artifact authority")
        require_authority_matches_binding(authority, binding)
        fixture = run_json(
            [
                sys.executable,
                str(MIGRATION_PRODUCER_PATH),
                "run",
                "--scenario-id",
                scenario,
                "--variant-id",
                variant,
                "--artifact-authority",
                str(Path(args.artifact_authority).resolve()),
            ],
            "G583 migration Docker fixture",
            allowed_environment_not_run=True,
        )
        adapter = migration_adapter_module()
        try:
            report = adapter.validate_and_build_observations(fixture, authority)
        except adapter.AdapterError as exc:
            raise FormalAdapterError("G583 migration adapter rejected its fixture") from exc
        payload = migration_payload(binding, scenario, report)
    else:
        if args.artifact_authority:
            fail("MIG03 must not use --artifact-authority")
        adapter = mig03_adapter_module()
        if args.mig03_observations:
            report = require_object(
                read_json(Path(args.mig03_observations).resolve(), "MIG03 bound observations"),
                "MIG03 bound observations",
            )
            artifact_identity = require_object(report.get("artifactIdentity"), "MIG03 artifactIdentity")
            if (
                report.get("scenarioId") != scenario
                or report.get("variantId") != variant
                or report.get("laneVariant") != variant
                or report.get("contractVersion") != MIG03_CONTRACT
                or report.get("ownerRole") != owner["ownerRole"]
                or report.get("qualificationExecuted") is not False
                or artifact_identity.get("candidateId") != binding["candidateId"]
                or artifact_identity.get("releaseCommitSha") != binding["releaseCommitSha"]
                or artifact_identity.get("ociIndexDigest") != binding["ociIndexDigest"]
            ):
                fail("MIG03 staged observations do not match the local qualification binding")
        else:
            fixture = run_json(
                [
                    sys.executable,
                    str(MIG03_PRODUCER_PATH),
                    "run",
                    "--source-commit",
                    binding["releaseCommitSha"],
                ],
                "G583 MIG03 fixture",
            )
            mig03_input = {
                "scenarioId": scenario,
                "variantId": variant,
                "laneVariant": variant,
                "contractVersion": MIG03_CONTRACT,
                "ownerRole": owner["ownerRole"],
                "artifactIdentity": {
                    "candidateId": binding["candidateId"],
                    "releaseCommitSha": binding["releaseCommitSha"],
                    "ociIndexDigest": binding["ociIndexDigest"],
                },
                "binding": {
                    field: binding[field]
                    for field in adapter.BINDING_FIELDS
                },
                "migrationPin": read_json(run_root / "migration-pin.json", "migration PIN"),
                "fixtureResult": fixture,
            }
            try:
                report = adapter.build_observations(mig03_input)
            except adapter.AdapterError as exc:
                raise FormalAdapterError("G583 MIG03 adapter rejected its bound fixture") from exc
        payload = mig03_payload(binding, report)

    finished_at = utc_now()
    envelope = build_envelope(runner, binding, owner, scenario, variant, payload, report, started_at, finished_at)
    authoritative_validate(runner, binding, auth, envelope, scenario, variant)
    output = json.dumps(envelope, ensure_ascii=False, sort_keys=True, separators=(",", ":")) + "\n"
    if args.output:
        path = Path(args.output)
        if path.exists() or path.is_symlink():
            fail("output already exists; evidence envelope is write-once")
        path.parent.mkdir(parents=True, exist_ok=True)
        path.write_text(output, encoding="utf-8", newline="\n")
    else:
        sys.stdout.write(output)
    return 0


def command_self_test(_: argparse.Namespace) -> int:
    core = core_module()
    manifest = core.load_manifest()
    routes = {
        tuple(item[field] for field in ("scenarioId", "variantId", "contractVersion"))
        for item in manifest["registrations"]
    }
    if routes != EXPECTED_ROUTES or len(manifest["registrations"]) != 5:
        raise AssertionError("formal adapter route authority is not exactly five routes")

    migration_adapter = migration_adapter_module()
    migration_manifest = migration_adapter.load_manifest()
    authority = migration_adapter.valid_authority()
    synthetic_binding = {
        "migrationBaselineInventory": [item["fileName"] for item in migration_manifest["baselineAuthority"]["migrationInventory"]],
        "migrationDeltaInventory": [item["fileName"] for item in migration_manifest["candidateDeltaInventory"]],
    }
    synthetic_binding["migrationFullInventory"] = synthetic_binding["migrationBaselineInventory"] + synthetic_binding["migrationDeltaInventory"]
    synthetic_binding.update({
        "migrationFullInventoryDigestSha256": "a" * 64,
        "migrationDeltaInventoryDigestSha256": "b" * 64,
        "migrationFullFileDigests": migration_manifest["baselineAuthority"]["migrationInventory"] + migration_manifest["candidateDeltaInventory"],
    })
    for scenario, variant in (
        ("G583-MIG-01", "win-docker"),
        ("G583-MIG-01", "linux-docker"),
        ("G583-MIG-02", "win-docker"),
        ("G583-MIG-02", "linux-docker"),
    ):
        fixture = migration_adapter.valid_report(migration_manifest, scenario, variant)
        observations = migration_adapter.validate_and_build_observations(fixture, authority)
        payload = migration_payload(synthetic_binding, scenario, observations)
        if payload["postApplyAppliedMigrations"] != synthetic_binding["migrationFullInventory"]:
            raise AssertionError("MIG01/MIG02 formal payload conversion failed")

    mig03_adapter = mig03_adapter_module()
    mig03_input = mig03_adapter.self_test_input()
    observations = mig03_adapter.build_observations(mig03_input)
    mig03_binding = dict(mig03_input["binding"])
    payload = mig03_payload(mig03_binding, observations)
    if payload["outcome"] != "schema-checked" or payload["schemaContractResult"] != "pass":
        raise AssertionError("MIG03 formal payload conversion failed")

    print(json.dumps({"result": "PASS", "routeCount": 5}, sort_keys=True))
    return 0


def build_parser() -> argparse.ArgumentParser:
    parser = argparse.ArgumentParser(description=__doc__)
    sub = parser.add_subparsers(dest="command", required=True)
    run = sub.add_parser("run")
    run.add_argument("--run-root", required=True)
    run.add_argument("--scenario-id", required=True)
    run.add_argument("--variant-id", required=True)
    run.add_argument("--artifact-authority")
    run.add_argument("--mig03-observations")
    run.add_argument("--output")
    run.set_defaults(func=command_run)
    self_test = sub.add_parser("self-test")
    self_test.set_defaults(func=command_self_test)
    return parser


def main(argv: list[str] | None = None) -> int:
    try:
        args = build_parser().parse_args(argv)
        return args.func(args)
    except FormalAdapterError as exc:
        print(f"[error] {exc}", file=sys.stderr)
        return 1
    except (OSError, ValueError, KeyError, TypeError) as exc:
        print(f"[error] operation failed: {type(exc).__name__}", file=sys.stderr)
        return 1


if __name__ == "__main__":
    raise SystemExit(main())
