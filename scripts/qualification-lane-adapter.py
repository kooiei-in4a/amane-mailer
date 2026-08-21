#!/usr/bin/env python3
"""Convert one independently executed non-live lane fixture into evidence.

The fixture is the operation owner.  It emits one value-free observation per
validator field.  This adapter only accepts a bound, owner-matching report and
derives PASS when every registered observation is present and PASS.  It never
accepts an operator supplied result or a generic predicate flag.
"""

from __future__ import annotations

import argparse
import hashlib
import importlib.util
import json
import re
import subprocess
import sys
from pathlib import Path
from typing import Any


HEX64 = re.compile(r"^[0-9a-f]{64}$")
SAFE_ID = re.compile(r"^[A-Za-z0-9][A-Za-z0-9._:/-]{0,127}$")
UTC = re.compile(r"^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}Z$")
ADAPTER_MANIFEST = Path(__file__).with_name("qualification-lane-adapter-manifest.json")
RUNNER_PATH = Path(__file__).with_name("qualification-runner.py")
PRODUCER_PATH = Path(__file__).with_name("qualification-lane-fixture-producer.py")


class AdapterError(Exception):
    """Safe, expected adapter validation failure."""


def load_runner() -> Any:
    spec = importlib.util.spec_from_file_location("qualification_runner_for_lane_adapter", RUNNER_PATH)
    if spec is None or spec.loader is None:
        raise AdapterError("qualification runner could not be loaded")
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


def load_producer() -> Any:
    spec = importlib.util.spec_from_file_location("qualification_lane_fixture_producer_for_adapter", PRODUCER_PATH)
    if spec is None or spec.loader is None:
        raise AdapterError("canonical fixture producer could not be loaded")
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


def read_json(path: Path, label: str) -> Any:
    try:
        value = json.loads(path.read_text(encoding="utf-8"))
    except (OSError, UnicodeError, json.JSONDecodeError) as exc:
        raise AdapterError(f"{label} is not readable JSON") from exc
    return value


def canonical_json(value: Any) -> bytes:
    return json.dumps(value, ensure_ascii=False, sort_keys=True, separators=(",", ":")).encode("utf-8")


def sha(value: Any) -> str:
    return hashlib.sha256(canonical_json(value)).hexdigest()


def require_string(value: Any, label: str) -> str:
    if not isinstance(value, str) or not value:
        raise AdapterError(f"{label} must be a non-empty string")
    return value


def require_safe_id(value: Any, label: str) -> str:
    result = require_string(value, label)
    if not SAFE_ID.fullmatch(result):
        raise AdapterError(f"{label} has an invalid safe identity")
    return result


def require_digest(value: Any, label: str) -> str:
    result = require_string(value, label)
    if not HEX64.fullmatch(result):
        raise AdapterError(f"{label} must be a lowercase SHA-256")
    return result


def load_manifest(runner: Any) -> dict[str, Any]:
    manifest = read_json(ADAPTER_MANIFEST, "adapter manifest")
    if not isinstance(manifest, dict) or manifest.get("schemaVersion") != 2:
        raise AdapterError("adapter manifest schema mismatch")
    if manifest.get("targetGateClass") != "Hard":
        raise AdapterError("adapter manifest must be Hard-only")
    if manifest.get("producerScript") != PRODUCER_PATH.name or manifest.get("producerRevision") != "2" or manifest.get("producerContractVersion") != 2 or manifest.get("fixtureContractVersion") != 1:
        raise AdapterError("canonical producer contract is not fixed")
    lanes = manifest.get("lanes")
    if not isinstance(lanes, list) or not lanes:
        raise AdapterError("adapter manifest lanes are missing")
    result: dict[str, Any] = {}
    for lane in lanes:
        if not isinstance(lane, dict):
            raise AdapterError("adapter manifest lane must be an object")
        scenario = require_safe_id(lane.get("scenarioId"), "manifest scenarioId")
        variant = require_safe_id(lane.get("variantId"), "manifest variantId")
        key = f"{scenario}/{variant}"
        if key in result:
            raise AdapterError("adapter manifest contains a duplicate lane")
        if scenario not in runner.HARD_SCENARIO_VALIDATOR_REGISTRY:
            raise AdapterError(f"manifest scenario has no dedicated validator: {scenario}")
        for field in ("expectedPlatform", "expectedOsFamily", "fixtureCommandId", "producerId", "procedureId"):
            require_safe_id(lane.get(field), f"manifest {key} {field}")
        result[key] = lane
    try:
        load_producer().validate_registry(manifest, runner)
    except Exception as exc:
        if isinstance(exc, AdapterError):
            raise
        raise AdapterError("canonical producer registry is incomplete") from exc
    return result


def load_bound_context(run_root: Path, runner: Any) -> tuple[dict[str, Any], dict[str, Any]]:
    try:
        binding = runner.load_binding(run_root)
        auth = runner.load_authorization(run_root)
    except runner.RunnerError as exc:
        raise AdapterError("qualification binding is invalid") from exc
    return binding, auth


def bound_row(binding: dict[str, Any], scenario: str, variant: str) -> dict[str, Any]:
    matches = [row for row in binding.get("rows", []) if row.get("scenarioId") == scenario and variant in row.get("requiredVariants", [])]
    if len(matches) != 1 or matches[0].get("gateClass") != "Hard":
        raise AdapterError("lane is not one bound Hard variant")
    return matches[0]


def require_commit(value: Any, label: str) -> str:
    result = require_string(value, label)
    if not re.fullmatch(r"[0-9a-f]{40}", result):
        raise AdapterError(f"{label} must be a lowercase Git commit SHA")
    return result


def validate_provenance(provenance: Any, binding: dict[str, Any]) -> None:
    allowed = {"schemaVersion", "sourceCommitSha", "bindingReleaseCommitSha", "sourceCommitIdentityMatch", "trackedTreeClean", "freshBuild"}
    if not isinstance(provenance, dict) or set(provenance) != allowed or provenance.get("schemaVersion") != 1:
        raise AdapterError("fixture binary provenance schema is invalid")
    source_commit = require_commit(provenance.get("sourceCommitSha"), "provenance sourceCommitSha")
    binding_commit = require_commit(provenance.get("bindingReleaseCommitSha"), "provenance bindingReleaseCommitSha")
    expected_commit = require_commit(binding.get("releaseCommitSha"), "binding releaseCommitSha")
    if source_commit != binding_commit or source_commit != expected_commit or provenance.get("sourceCommitIdentityMatch") is not True:
        raise AdapterError("fixture source commit does not match the qualification binding")
    if provenance.get("trackedTreeClean") is not True:
        raise AdapterError("fixture tracked source tree was not clean")
    build = provenance.get("freshBuild")
    build_allowed = {"schemaVersion", "restore", "configuration", "freshBuild", "outputIsolation", "noIncremental", "testAssembly", "productAssembly"}
    if not isinstance(build, dict) or set(build) != build_allowed or build.get("schemaVersion") != 1 or build.get("restore") != "locked" or build.get("configuration") != "Release" or build.get("freshBuild") is not True or build.get("outputIsolation") != "isolated-git-worktree" or build.get("noIncremental") is not True:
        raise AdapterError("fixture fresh-build provenance is invalid")
    for role, file_name in (("testAssembly", "Amane.Mailer.Tests.dll"), ("productAssembly", "Amane.Mailer.dll")):
        assembly = build.get(role)
        if not isinstance(assembly, dict) or set(assembly) != {"fileName", "sha256"} or assembly.get("fileName") != file_name:
            raise AdapterError("fixture executed assembly identity is invalid")
        require_digest(assembly.get("sha256"), f"provenance {role} sha256")


def validate_execution(report: dict[str, Any], lane: dict[str, Any], scenario: str, variant: str) -> None:
    execution = report.get("execution")
    if not isinstance(execution, dict) or set(execution) != {"platform", "osFamily", "runtimeKind", "fixtureCommandId"}:
        raise AdapterError("fixture execution identity is incomplete")
    if execution["platform"] != lane["expectedPlatform"] or execution["osFamily"] != lane["expectedOsFamily"] or execution["fixtureCommandId"] != lane["fixtureCommandId"]:
        raise AdapterError("fixture platform identity does not match the bound variant")
    require_safe_id(execution["runtimeKind"], "fixture runtimeKind")
    require_safe_id(execution["fixtureCommandId"], "fixture fixtureCommandId")
    if report.get("scenarioId") != scenario or report.get("variantId") != variant:
        raise AdapterError("fixture scenario/variant identity mismatch")


def validate_report(report: Any, binding: dict[str, Any], auth: dict[str, Any], lane: dict[str, Any], scenario: str, variant: str, runner: Any) -> dict[str, Any]:
    if not isinstance(report, dict):
        raise AdapterError("fixture report must be an object")
    allowed = {"schemaVersion", "kind", "scenarioId", "variantId", "candidateId", "releaseCommitSha", "bindingId", "qualificationRunId", "executedByRole", "executedByIdentity", "startedAtUtc", "finishedAtUtc", "attestedAtUtc", "execution", "producer", "provenance", "fixtureResult", "checks"}
    if report.get("schemaVersion") != 4 or report.get("kind") != "qualification-lane-fixture-observations" or set(report) != allowed:
        raise AdapterError("fixture report schema or fields are invalid")
    for field in ("scenarioId", "variantId", "candidateId", "releaseCommitSha", "bindingId", "qualificationRunId", "executedByRole", "executedByIdentity"):
        require_string(report.get(field), f"fixture {field}")
    if report["candidateId"] != binding.get("candidateId") or report["releaseCommitSha"] != binding.get("releaseCommitSha") or report["bindingId"] != binding.get("bindingId") or report["qualificationRunId"] != binding.get("qualificationRunId"):
        raise AdapterError("fixture qualification identity mismatch")
    if not all(isinstance(report.get(field), str) and UTC.fullmatch(report[field]) for field in ("startedAtUtc", "finishedAtUtc", "attestedAtUtc")):
        raise AdapterError("fixture timestamps are invalid")
    owner = next((item for item in auth.get("evidenceOwners", []) if item.get("scenarioId") == scenario and item.get("variantId") == variant), None)
    if not isinstance(owner, dict) or report["executedByRole"] != owner.get("ownerRole") or report["executedByIdentity"] != owner.get("ownerIdentity"):
        raise AdapterError("fixture owner does not match authorization")
    validate_execution(report, lane, scenario, variant)
    validate_provenance(report.get("provenance"), binding)
    producer_module = load_producer()
    try:
        expected_procedure = producer_module.procedure_for(lane, scenario)
    except producer_module.ProducerError as exc:
        raise AdapterError("lane has no canonical producer procedure") from exc
    producer = report.get("producer")
    if not isinstance(producer, dict) or set(producer) != {"producerId", "producerRevision", "procedureId", "procedureRevision", "procedureDigestSha256", "fixtureId", "fixtureRevision", "fixtureResultDigestSha256", "fixtureSourceTestId", "exitCode", "result", "passedTestCount", "totalTestCount", "skippedTestCount"}:
        raise AdapterError("canonical producer result is incomplete")
    if not expected_procedure.get("fixtureAvailable") or producer.get("producerId") != expected_procedure["producerId"] or producer.get("producerRevision") != expected_procedure["producerRevision"] or producer.get("procedureId") != expected_procedure["procedureId"] or producer.get("procedureRevision") != expected_procedure["procedureRevision"] or producer.get("procedureDigestSha256") != producer_module.digest(expected_procedure) or producer.get("fixtureId") != expected_procedure["fixture"]["fixtureId"] or producer.get("fixtureRevision") != expected_procedure["fixture"]["fixtureRevision"] or producer.get("fixtureSourceTestId") != expected_procedure["fixture"]["sourceTestId"]:
        raise AdapterError("canonical producer identity or digest mismatch")
    require_digest(producer.get("procedureDigestSha256"), "producer procedureDigestSha256")
    require_digest(producer.get("fixtureResultDigestSha256"), "producer fixtureResultDigestSha256")
    if producer.get("exitCode") != 0 or producer.get("result") != "PASS" or type(producer.get("passedTestCount")) is not int or producer["passedTestCount"] <= 0 or type(producer.get("totalTestCount")) is not int or producer["totalTestCount"] < producer["passedTestCount"] or producer.get("skippedTestCount") != 0:
        raise AdapterError("canonical producer result did not pass its fixed procedure")
    fixture_result = report.get("fixtureResult")
    if producer.get("fixtureResultDigestSha256") != producer_module.digest(fixture_result):
        raise AdapterError("fixture result digest mismatch")
    try:
        fixture_payload = producer_module.validate_fixture_result(fixture_result, expected_procedure, scenario, variant, runner)
    except producer_module.ProducerError as exc:
        raise AdapterError("canonical fixture result was rejected") from exc
    try:
        runner.value_free(report, "$.fixtureReport")
    except runner.RunnerError as exc:
        raise AdapterError("fixture report is not value-free") from exc

    spec = runner.HARD_SCENARIO_VALIDATOR_REGISTRY[scenario]
    expected_fields = set(spec["fields"])
    checks = report.get("checks")
    if not isinstance(checks, list) or len(checks) != len(expected_fields):
        raise AdapterError("fixture must emit exactly one check per validator field")
    payload: dict[str, Any] = {}
    expected_prefix = f"{scenario}/{variant}/"
    for check in checks:
        if not isinstance(check, dict) or set(check) != {"checkId", "result", "proofKind", "sourceTestId", "observedFields"}:
            raise AdapterError("fixture check schema is invalid")
        check_id = require_safe_id(check.get("checkId"), "fixture checkId")
        if not check_id.startswith(expected_prefix):
            raise AdapterError("fixture checkId is not bound to this lane")
        if check.get("result") != "PASS" or check.get("proofKind") != "qualification-integration-observation":
            raise AdapterError("fixture check is not an accepted operation observation")
        if check.get("sourceTestId") != fixture_result.get("sourceTestId"):
            raise AdapterError("fixture check sourceTestId is not the exact fixture test case")
        require_safe_id(check.get("sourceTestId"), "fixture sourceTestId")
        observed = check.get("observedFields")
        if not isinstance(observed, dict) or len(observed) != 1:
            raise AdapterError("each fixture check must observe exactly one field")
        field, value = next(iter(observed.items()))
        if field not in expected_fields or field in payload or check_id != f"{expected_prefix}{field}" or fixture_payload.get(field) != value:
            raise AdapterError("fixture check field mapping is invalid")
        field_type, allowed = spec["fields"][field]
        if type(value) is not field_type or (allowed is not None and value not in allowed):
            raise AdapterError("fixture observation has the wrong validator type/value")
        payload[field] = value
    if set(payload) != expected_fields:
        raise AdapterError("fixture observations do not cover the validator predicate")
    return payload


def build_envelope(report: dict[str, Any], payload: dict[str, Any], binding: dict[str, Any], lane: dict[str, Any], scenario: str, variant: str, runner: Any) -> dict[str, Any]:
    spec = runner.HARD_SCENARIO_VALIDATOR_REGISTRY[scenario]
    try:
        runner.value_free(payload, "$.typePayload")
    except runner.RunnerError as exc:
        raise AdapterError("derived payload is not value-free") from exc
    report_digest = sha(report)
    scan = {"result": "PASS", "scannerId": "qualification-lane-value-free", "scannerVersion": "1", "reportDigestSha256": report_digest}
    identity = {"platform": lane["expectedPlatform"], "fixtureCommandId": lane["fixtureCommandId"], "candidateIdentityMatch": True, "sourceCommitIdentityMatch": True}
    runner.value_free(identity, "$.identity")
    envelope = {
        "schemaVersion": 1, "kind": "release-qualification-evidence", "evidenceType": runner.EVIDENCE_TYPES[scenario][0],
        "evidenceId": sha({"bindingId": binding["bindingId"], "scenarioId": scenario, "variantId": variant, "fixtureReportDigest": report_digest}),
        "candidateId": binding["candidateId"], "sourceCommitSha": binding["releaseCommitSha"], "scenarioId": scenario, "variantId": variant,
        "issueBodySha256": binding["issueBodySha256"], "planRevision": binding["planRevision"], "planCommitSha": binding["planCommitSha"], "planFileSha256": binding["planFileSha256"],
        "bindingId": binding["bindingId"], "qualificationRunId": binding["qualificationRunId"], "attempt": 1, "result": "PASS",
        "startedAtUtc": report["startedAtUtc"], "finishedAtUtc": report["finishedAtUtc"], "executedByRole": report["executedByRole"], "executedByIdentity": report["executedByIdentity"],
        "procedureId": spec["procedureId"], "procedureRevision": spec["procedureRevision"], "runnerClass": "qualification-lane-adapter", "toolVersion": "1", "attestedAtUtc": report["attestedAtUtc"],
        "identity": identity, "prohibitedContentScan": scan, "typePayload": payload,
    }
    if binding.get("scopeId") is not None:
        envelope.update({"scopeId": binding["scopeId"], "scopeVersion": binding["scopeVersion"], "scopeManifestSha256": binding["scopeManifestSha256"]})
    return envelope


def command_run(args: argparse.Namespace) -> int:
    runner = load_runner()
    lanes = load_manifest(runner)
    scenario = require_safe_id(args.scenario_id, "scenario-id")
    variant = require_safe_id(args.variant_id, "variant-id")
    key = f"{scenario}/{variant}"
    lane = lanes.get(key)
    if lane is None:
        raise AdapterError("scenario/variant is not in the non-live Hard adapter manifest")
    run_root = Path(args.run_root).resolve()
    binding, auth = load_bound_context(run_root, runner)
    bound_row(binding, scenario, variant)
    command = [sys.executable, str(PRODUCER_PATH), "run", "--run-root", str(run_root), "--scenario-id", scenario, "--variant-id", variant]
    try:
        result = subprocess.run(command, cwd=RUNNER_PATH.parent.parent, capture_output=True, text=True, encoding="utf-8", errors="replace", timeout=960, check=False)
    except (OSError, subprocess.SubprocessError) as exc:
        raise AdapterError("canonical fixture producer could not be started") from exc
    if result.returncode != 0:
        raise AdapterError("canonical fixture producer did not pass")
    try:
        report = json.loads(result.stdout)
    except (UnicodeError, json.JSONDecodeError) as exc:
        raise AdapterError("canonical fixture producer output is not one JSON report") from exc
    if not isinstance(report, dict) or result.stdout.count("\n") != 1:
        raise AdapterError("canonical fixture producer output is not a single report")
    payload = validate_report(report, binding, auth, lane, scenario, variant, runner)
    envelope = build_envelope(report, payload, binding, lane, scenario, variant, runner)
    try:
        runner.validate_evidence_envelope(envelope, binding, auth, (scenario, variant))
    except runner.RunnerError as exc:
        raise AdapterError("derived evidence was rejected by qualification runner") from exc
    output = json.dumps(envelope, ensure_ascii=False, sort_keys=True, separators=(",", ":")) + "\n"
    if args.output:
        output_path = Path(args.output)
        if output_path.exists():
            raise AdapterError("output already exists; evidence input is write-once")
        output_path.parent.mkdir(parents=True, exist_ok=True)
        output_path.write_text(output, encoding="utf-8", newline="\n")
    else:
        sys.stdout.write(output)
    return 0


def command_manifest(args: argparse.Namespace) -> int:
    runner = load_runner()
    lanes = load_manifest(runner)
    manifest = read_json(ADAPTER_MANIFEST, "adapter manifest")
    procedures = load_producer().validate_registry(manifest, runner)
    available = sum(1 for procedure in procedures if procedure["fixtureAvailable"])
    print(json.dumps({"laneCount": len(lanes), "availableLaneCount": available, "canonicalProducerAvailable": available == len(lanes), "lanes": sorted(lanes)}, sort_keys=True))
    return 0


def build_parser() -> argparse.ArgumentParser:
    parser = argparse.ArgumentParser(description=__doc__)
    sub = parser.add_subparsers(dest="command", required=True)
    run = sub.add_parser("run")
    run.add_argument("--run-root", required=True)
    run.add_argument("--scenario-id", required=True)
    run.add_argument("--variant-id", required=True)
    run.add_argument("--output")
    run.set_defaults(func=command_run)
    manifest = sub.add_parser("manifest")
    manifest.set_defaults(func=command_manifest)
    return parser


def main(argv: list[str] | None = None) -> int:
    try:
        args = build_parser().parse_args(argv)
        return args.func(args)
    except AdapterError as exc:
        print(f"[error] {exc}", file=sys.stderr)
        return 1
    except (OSError, ValueError) as exc:
        print(f"[error] operation failed: {type(exc).__name__}", file=sys.stderr)
        return 1


if __name__ == "__main__":
    raise SystemExit(main())
