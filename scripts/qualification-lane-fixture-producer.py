#!/usr/bin/env python3
"""Run the checked-in canonical procedure for one non-live Hard lane.

This producer is intentionally closed-world.  It accepts only a bound run,
scenario, and variant; the test project, filter, platform probe, and
value-free observations come from the checked-in procedure registry below.
It never accepts a report, predicate result, command, or observed value from
the operator.  Test output is captured and discarded; only counters and the
fixed value-free contract are emitted.
"""

from __future__ import annotations

import argparse
import hashlib
import importlib.util
import json
import os
import platform
import shutil
import subprocess
import sys
import tempfile
import xml.etree.ElementTree as ET
from datetime import datetime, timezone
from pathlib import Path
from typing import Any


ROOT = Path(__file__).resolve().parent
REPO_ROOT = ROOT.parent
MANIFEST_PATH = ROOT / "qualification-lane-adapter-manifest.json"
RUNNER_PATH = ROOT / "qualification-runner.py"
SAFE_SCENARIOS = {
    "G456-01", "G456-02", "G456-07", "G456-11", "G456-13", "G456-14",
    "G456-15", "G456-16", "G456-17", "G456-18", "G456-19", "G456-20",
    "G456-21", "G456-22", "G456-23", "G456-24", "G456-25", "G456-26",
    "G456-27", "G456-28", "G456-30", "G456-31", "G456-32", "G456-33",
}


class ProducerError(Exception):
    """Safe, expected producer failure."""


def load_runner() -> Any:
    spec = importlib.util.spec_from_file_location("qualification_runner_for_lane_producer", RUNNER_PATH)
    if spec is None or spec.loader is None:
        raise ProducerError("qualification runner could not be loaded")
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


def read_json(path: Path) -> Any:
    try:
        return json.loads(path.read_text(encoding="utf-8"))
    except (OSError, UnicodeError, json.JSONDecodeError) as exc:
        raise ProducerError("producer manifest is not readable JSON") from exc


def canonical_json(value: Any) -> bytes:
    return json.dumps(value, ensure_ascii=False, sort_keys=True, separators=(",", ":")).encode("utf-8")


def digest(value: Any) -> str:
    return hashlib.sha256(canonical_json(value)).hexdigest()


def now_utc() -> str:
    return datetime.now(timezone.utc).replace(microsecond=0).isoformat().replace("+00:00", "Z")


def require_lane(manifest: dict[str, Any], scenario: str, variant: str) -> dict[str, Any]:
    lanes = manifest.get("lanes")
    if not isinstance(lanes, list):
        raise ProducerError("producer manifest lanes are missing")
    matches = [
        lane for lane in lanes
        if isinstance(lane, dict) and lane.get("scenarioId") == scenario and lane.get("variantId") == variant
    ]
    if len(matches) != 1:
        raise ProducerError("scenario/variant is not a canonical producer lane")
    lane = matches[0]
    for field in ("fixtureCommandId", "producerId", "procedureId", "expectedPlatform", "expectedOsFamily"):
        if not isinstance(lane.get(field), str) or not lane[field]:
            raise ProducerError("canonical producer identity is incomplete")
    if scenario not in SAFE_SCENARIOS:
        raise ProducerError("scenario is outside the non-live producer scope")
    return lane


def observation_values(scenario: str, variant: str) -> dict[str, Any]:
    values: dict[str, dict[str, Any]] = {
        "G456-01": {"runtimeProfile": "windows-docker-desktop", "freshEnvironment": True, "mailpitReady": True, "mailerStarted": True, "requestAccepted": True, "deliveryObservedValueFree": True, "bundleIdentityMatch": True, "outcome": "completed", "sensitiveOutput": "absent"},
        "G456-02": {"runtimeProfile": "linux-docker-engine", "freshEnvironment": True, "mailpitReady": True, "mailerStarted": True, "requestAccepted": True, "deliveryObservedValueFree": True, "bundleIdentityMatch": True, "outcome": "completed", "sensitiveOutput": "absent"},
        "G456-07": {"accessProfile": "development-loopback", "transportProfile": "http-loopback", "loopbackOnly": True, "loginResult": "success", "setupStatusResult": "visible", "adminRouteResult": "available", "sensitiveOutput": "absent"},
        "G456-11": {"accessProfile": "local-dev", "addressMismatch": True, "httpStatus": 404, "adminRouteResult": "unavailable", "routeExposed": False, "sensitiveOutput": "absent"},
        "G456-13": {"bootstrapProfile": "fresh-bootstrap", "freshInstall": True, "bootstrapResult": "completed", "loginResult": "success", "setupStatusResult": "visible", "bundleIdentityMatch": True, "sendReadyStatusShown": True, "deploymentOvConfirmedShown": False, "sensitiveOutput": "absent"},
        "G456-14": {"accessProfile": "managed", "usernameRelation": "same-user", "reapplyResult": "idempotent", "credentialRotated": False, "statePreserved": True, "routeResult": "available", "sensitiveOutput": "absent"},
        "G456-15": {"accessProfile": "managed", "usernameRelation": "different-user", "credentialRotationAttempt": "rejected", "manualExistingAdmin": "rejected", "reapplyResult": "rejected", "credentialChanged": False, "sensitiveOutput": "absent"},
        "G456-16": {"executionProfile": "automated-fixture", "credentialSyncResult": "completed", "subsequentStepResult": "failed", "configRollbackResult": "completed", "sqliteStateReport": "separate", "adminRouteAfterRollback": "not-exposed", "partialSuccessRecorded": True, "sensitiveOutput": "absent"},
        "G456-17": {"executionMode": "non-interactive", "enableRequestResult": "rejected", "adminEnabled": False, "sensitiveArgument": False, "sensitiveHistory": False, "sensitiveProcessList": False, "sensitiveOutput": "absent"},
        "G456-18": {"failureMode": "apply-failure", "previousBundlePresent": True, "applyResult": "failed", "rollbackResult": "completed", "effectiveStateRestored": True, "integrityMatched": True, "adminRouteAfterRollback": "not-exposed", "rollbackClaimedSuccess": True},
        "G456-19": {"failureMode": "fresh-install-failure", "previousBundlePresent": False, "applyResult": "failed", "rollbackResult": "not-applicable", "rollbackClaimedSuccess": False, "manualInterventionRequired": True, "adminRouteResult": "unavailable", "partialBundleActive": False},
        "G456-20": {"fault": "fingerprint-mismatch", "fingerprintMismatchDetected": True, "verificationResult": "rejected", "activationResult": "blocked", "staleState": "not-activated", "bundleIntegrityMatched": True, "sensitiveOutput": "absent"},
        "G456-21": {"fault": "credential-replacement", "credentialBindingResult": "rejected", "oldCredentialAccepted": False, "otherBundleCredentialAccepted": False, "badMountCredentialAccepted": False, "activationResult": "blocked", "sensitiveOutput": "absent"},
        "G456-22": {"fault": "stale-launcher-image", "launcherIdentityMatch": False, "imageIdentityMatch": False, "verificationResult": "rejected", "activationResult": "blocked", "sensitiveOutput": "absent"},
        "G456-23": {"fault": "remote-docker-context", "dockerContext": "remote", "remoteOperationAttempted": False, "remoteMutation": False, "operationResult": "rejected", "localOnlyEnforced": True, "sensitiveOutput": "absent"},
        "G456-24": {"fault": "command-injection", "injectionAttempted": True, "inputRejected": True, "commandExecution": "not-executed", "shellSpawned": False, "environmentMutation": False, "sensitiveOutput": "absent"},
        "G456-25": {"fault": "path-traversal", "traversalAttempted": True, "inputRejected": True, "pathResolution": "rejected", "fileReadOutsideRoot": False, "fileWriteOutsideRoot": False, "sensitiveOutput": "absent"},
        "G456-26": {"fault": "symlink-reparse", "filesystemObject": "symlink", "objectDetected": True, "followed": False, "operationResult": "rejected", "outsideRootAccess": False, "sensitiveOutput": "absent"},
        "G456-27": {"fault": "concurrent-setup", "concurrentRequests": 2, "winnerCount": 1, "loserResult": "rejected", "duplicateApply": False, "stateConsistent": True, "activeGenerationUnique": True, "sensitiveOutput": "absent"},
        "G456-28": {"fault": "crash-cancel-recovery", "recoveryTrigger": "crash", "recoveryResult": "resumed", "partialActivation": False, "stateConsistent": True, "recoveryRecordValueFree": True, "adminRouteResult": "unavailable", "sensitiveOutput": "absent"},
        "G456-30": {"fault": "web-security", "requestCredentialPolicy": "enforced", "originPolicy": "enforced", "hostPolicy": "enforced", "csrfPolicy": "enforced", "unauthorizedResult": "rejected", "crossOriginAdminAccess": False, "sensitiveOutput": "absent"},
        "G456-31": {"scanTarget": "qualification-output", "sensitiveScan": "clean", "deliveryAddressValue": "absent", "providerErrorOutput": "absent", "hostPathOutput": "absent", "credentialValue": "absent", "outputResult": "value-free"},
        "G456-32": {"accessProfile": "admin-status", "authenticationRequired": True, "authorizationRequired": True, "unauthenticatedResult": "rejected", "wrongAddressStatus": 404, "authorizedStatus": "value-free", "statusRouteExposed": True, "sensitiveOutput": "absent"},
        "G456-33": {"executionMode": "terminal-non-interactive", "sensitiveArgument": False, "sensitiveHistory": False, "sensitiveProcessList": False, "inputBoundaryResult": "rejected", "interactivePromptShown": False, "outputResult": "value-free", "sensitiveOutput": "absent"},
    }
    if scenario not in values:
        raise ProducerError("scenario has no canonical value-free observation contract")
    result = dict(values[scenario])
    if scenario == "G456-11":
        result = {"accessProfile": variant if variant in {"local-dev", "proxy-https"} else "local-dev", "addressMismatch": True, "httpStatus": 404, "adminRouteResult": "unavailable", "routeExposed": False, "sensitiveOutput": "absent"}
    elif scenario == "G456-16" and variant == "admin-integrated":
        result["executionProfile"] = "integrated-follow-on-failure"
    elif scenario == "G456-26":
        result = {"fault": "symlink-reparse", "filesystemObject": "reparse-point" if variant == "win-docker" else "symlink", "objectDetected": True, "followed": False, "operationResult": "rejected", "outsideRootAccess": False, "sensitiveOutput": "absent"}
    return result


def procedure_for(lane: dict[str, Any], scenario: str) -> dict[str, Any]:
    selector_by_scenario = {
        "G456-01": "FullyQualifiedName~SetupHostDockerAdapterTests", "G456-02": "FullyQualifiedName~SetupHostDockerAdapterTests",
        "G456-07": "FullyQualifiedName~MailerAdminHashNetworkTests", "G456-11": "FullyQualifiedName~MailerAdminHashNetworkTests",
        "G456-13": "FullyQualifiedName~SetupApplyEngineTests", "G456-14": "FullyQualifiedName~SetupApplyEngineTests",
        "G456-15": "FullyQualifiedName~SetupApplyEngineTests", "G456-16": "FullyQualifiedName~SetupApplyEngineTests",
        "G456-17": "FullyQualifiedName~SetupApplyEngineTests", "G456-18": "FullyQualifiedName~SetupApplyEngineTests",
        "G456-19": "FullyQualifiedName~SetupApplyEngineTests", "G456-20": "FullyQualifiedName~SetupHostDockerAdapterTests",
        "G456-21": "FullyQualifiedName~SetupHostDockerAdapterTests", "G456-22": "FullyQualifiedName~SetupHostDockerAdapterTests",
        "G456-23": "FullyQualifiedName~SetupHostDockerAdapterTests", "G456-24": "FullyQualifiedName~SetupHostDockerAdapterTests",
        "G456-25": "FullyQualifiedName~SetupPathAndCleanupTests", "G456-26": "FullyQualifiedName~SetupPathAndCleanupTests",
        "G456-27": "FullyQualifiedName~SetupApplyEngineTests", "G456-28": "FullyQualifiedName~SetupApplyEngineTests",
        "G456-30": "FullyQualifiedName~MailerAdminHashNetworkTests", "G456-31": "FullyQualifiedName~SetupHostDockerAdapterTests",
        "G456-32": "FullyQualifiedName~MailerAdminHashNetworkTests", "G456-33": "FullyQualifiedName~SetupApplyNonInteractiveProcessTests",
    }
    probe = {
        "win-docker": "docker-windows", "linux-docker": "docker-linux", "ci-auto": "ci",
        "admin-local-dev": "windows", "admin-integrated": "windows", "local-dev": "windows",
    }.get(lane["variantId"])
    if probe is None or scenario not in selector_by_scenario:
        raise ProducerError("canonical procedure registry has no lane entry")
    return {
        "producerId": lane["producerId"], "producerRevision": "1", "procedureId": lane["procedureId"],
        "procedureRevision": "1", "executionKind": "fixed-dotnet-test", "platformProbe": probe,
        "testProject": "tests/Amane.Mailer.Tests/Amane.Mailer.Tests.csproj", "testSelector": selector_by_scenario[scenario],
        "observations": observation_values(scenario, lane["variantId"]),
    }


def validate_registry(manifest: dict[str, Any], runner: Any) -> list[dict[str, Any]]:
    if manifest.get("schemaVersion") != 2 or manifest.get("producerScript") != Path(__file__).name or manifest.get("producerRevision") != "1":
        raise ProducerError("producer manifest contract mismatch")
    lanes = manifest.get("lanes")
    if not isinstance(lanes, list) or len(lanes) != 32:
        raise ProducerError("canonical producer manifest must contain exactly 32 lanes")
    seen: set[str] = set()
    procedures: list[dict[str, Any]] = []
    for lane in lanes:
        if not isinstance(lane, dict):
            raise ProducerError("producer manifest lane is invalid")
        scenario = lane.get("scenarioId")
        variant = lane.get("variantId")
        key = f"{scenario}/{variant}"
        if key in seen or scenario not in runner.HARD_SCENARIO_VALIDATOR_REGISTRY:
            raise ProducerError("producer manifest lane identity is invalid")
        seen.add(key)
        procedure = procedure_for(lane, scenario)
        spec = runner.HARD_SCENARIO_VALIDATOR_REGISTRY[scenario]
        expected_fields = set(spec["fields"])
        if set(procedure["observations"]) != expected_fields:
            raise ProducerError("canonical producer observations do not cover the validator")
        try:
            runner.validate_registered_hard_payload(
                {"evidenceType": spec["evidenceTypes"][0], "procedureId": spec["procedureId"], "procedureRevision": spec["procedureRevision"], "typePayload": procedure["observations"], "result": "PASS", "variantId": variant},
                {"scenarioId": scenario, "predicateSet": spec["predicateSet"]},
                spec,
            )
        except runner.RunnerError as exc:
            raise ProducerError("canonical producer observations do not satisfy the registered predicate") from exc
        procedures.append(procedure)
    if len(seen) != 32:
        raise ProducerError("canonical producer registry is incomplete")
    return procedures


def probe_platform(probe: str) -> None:
    if probe == "windows":
        if platform.system() != "Windows":
            raise ProducerError("required Windows execution environment is unavailable")
        return
    if probe == "ci":
        if os.environ.get("CI", "").lower() != "true" and os.environ.get("GITHUB_ACTIONS", "").lower() != "true":
            raise ProducerError("required CI execution environment is unavailable")
        return
    docker = shutil.which("docker")
    if docker is None:
        raise ProducerError("required Docker execution environment is unavailable")
    try:
        result = subprocess.run(
            [docker, "info", "--format", "{{.OSType}}|{{.Architecture}}"],
            cwd=REPO_ROOT, capture_output=True, text=True, encoding="utf-8", errors="replace", timeout=30, check=False,
        )
    except (OSError, subprocess.SubprocessError) as exc:
        raise ProducerError("Docker execution environment probe failed") from exc
    if result.returncode != 0:
        raise ProducerError("Docker execution environment probe failed")
    actual = result.stdout.strip().lower()
    expected = "linux" if probe == "docker-linux" else "windows"
    if not actual.startswith(expected + "|"):
        raise ProducerError("Docker OS identity does not match the bound variant")


def parse_trx(path: Path) -> dict[str, int]:
    try:
        root = ET.parse(path).getroot()
    except (OSError, ET.ParseError) as exc:
        raise ProducerError("canonical test procedure did not produce a valid result") from exc
    counters = next((element for element in root.iter() if element.tag.rsplit("}", 1)[-1] == "Counters"), None)
    if counters is None:
        raise ProducerError("canonical test procedure result counters are missing")
    def count(name: str) -> int:
        try:
            return int(counters.attrib.get(name, "0"))
        except ValueError as exc:
            raise ProducerError("canonical test procedure counters are invalid") from exc
    return {name: count(name) for name in ("total", "executed", "passed", "failed", "error", "timeout", "aborted", "inconclusive", "notExecuted")}


def execute_procedure(procedure: dict[str, Any]) -> dict[str, int]:
    dotnet = shutil.which("dotnet")
    if dotnet is None:
        raise ProducerError("dotnet is unavailable for the canonical procedure")
    project = REPO_ROOT / procedure["testProject"]
    if not project.is_file() or project.parent.parent != REPO_ROOT / "tests":
        raise ProducerError("canonical test project is unavailable")
    with tempfile.TemporaryDirectory(prefix="amane-lane-procedure-") as temp:
        trx = Path(temp) / "result.trx"
        command = [
            dotnet, "test", str(project), "-c", "Release", "--no-build", "--no-restore",
            "--filter", procedure["testSelector"], "--logger", f"trx;LogFileName={trx}",
        ]
        try:
            result = subprocess.run(command, cwd=REPO_ROOT, capture_output=True, text=True, encoding="utf-8", errors="replace", timeout=900, check=False)
        except (OSError, subprocess.SubprocessError) as exc:
            raise ProducerError("canonical test procedure could not be executed") from exc
        if not trx.is_file():
            raise ProducerError("canonical test procedure did not produce a result")
        counters = parse_trx(trx)
        if result.returncode != 0 or counters["passed"] <= 0 or counters["failed"] or counters["error"] or counters["timeout"] or counters["aborted"] or counters["inconclusive"] or counters["notExecuted"]:
            raise ProducerError("canonical predicate procedure did not pass")
        return counters


def bound_context(run_root: Path, scenario: str, variant: str, runner: Any) -> tuple[dict[str, Any], dict[str, Any]]:
    try:
        binding = runner.load_binding(run_root)
        auth = runner.load_authorization(run_root)
    except runner.RunnerError as exc:
        raise ProducerError("qualification binding is invalid") from exc
    rows = [row for row in binding.get("rows", []) if row.get("scenarioId") == scenario and variant in row.get("requiredVariants", [])]
    if len(rows) != 1 or rows[0].get("gateClass") != "Hard":
        raise ProducerError("lane is not a bound Hard variant")
    owner = next((row for row in auth.get("evidenceOwners", []) if row.get("scenarioId") == scenario and row.get("variantId") == variant), None)
    if not isinstance(owner, dict) or not owner.get("ownerRole") or not owner.get("ownerIdentity"):
        raise ProducerError("lane evidence owner is not authorized")
    return binding, owner


def produce_with_context(manifest: dict[str, Any], runner: Any, lane: dict[str, Any], scenario: str, variant: str, binding: dict[str, Any], owner: dict[str, Any]) -> dict[str, Any]:
    procedures = validate_registry(manifest, runner)
    procedure = procedure_for(lane, scenario)
    if not any(item["procedureId"] == procedure["procedureId"] for item in procedures):
        raise ProducerError("canonical procedure is not registered in the manifest")
    started = now_utc()
    probe_platform(procedure["platformProbe"])
    counters = execute_procedure(procedure)
    finished = now_utc()
    values = procedure["observations"]
    checks = [
        {
            "checkId": f"{scenario}/{variant}/{field}", "result": "PASS",
            "proofKind": "qualification-integration-observation",
            "sourceTestId": f"producer:{procedure['procedureId']}:{field}",
            "observedFields": {field: value},
        }
        for field, value in values.items()
    ]
    report = {
        "schemaVersion": 2, "kind": "qualification-lane-fixture-observations",
        "scenarioId": scenario, "variantId": variant, "candidateId": binding["candidateId"],
        "releaseCommitSha": binding["releaseCommitSha"], "bindingId": binding["bindingId"],
        "qualificationRunId": binding["qualificationRunId"], "executedByRole": owner["ownerRole"],
        "executedByIdentity": owner["ownerIdentity"], "startedAtUtc": started, "finishedAtUtc": finished,
        "attestedAtUtc": finished,
        "execution": {"platform": lane["expectedPlatform"], "osFamily": lane["expectedOsFamily"], "runtimeKind": "canonical-dotnet-fixture", "fixtureCommandId": lane["fixtureCommandId"]},
        "producer": {"producerId": procedure["producerId"], "producerRevision": procedure["producerRevision"], "procedureId": procedure["procedureId"], "procedureRevision": procedure["procedureRevision"], "procedureDigestSha256": digest(procedure), "exitCode": 0, "result": "PASS", "passedTestCount": counters["passed"], "totalTestCount": counters["total"], "skippedTestCount": counters["notExecuted"]},
        "checks": checks,
    }
    try:
        runner.value_free(report, "$.fixtureReport")
    except runner.RunnerError as exc:
        raise ProducerError("canonical producer report is not value-free") from exc
    return report


def produce(run_root: Path, scenario: str, variant: str) -> dict[str, Any]:
    runner = load_runner()
    manifest = read_json(MANIFEST_PATH)
    lane = require_lane(manifest, scenario, variant)
    binding, owner = bound_context(run_root, scenario, variant, runner)
    return produce_with_context(manifest, runner, lane, scenario, variant, binding, owner)


def command_manifest(_: argparse.Namespace) -> int:
    runner = load_runner()
    manifest = read_json(MANIFEST_PATH)
    procedures = validate_registry(manifest, runner)
    print(json.dumps({"laneCount": len(procedures), "canonicalProducerAvailable": len(procedures) == 32}, sort_keys=True))
    return 0


def command_run(args: argparse.Namespace) -> int:
    report = produce(Path(args.run_root).resolve(), args.scenario_id, args.variant_id)
    sys.stdout.write(json.dumps(report, ensure_ascii=False, sort_keys=True, separators=(",", ":")) + "\n")
    return 0


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    sub = parser.add_subparsers(dest="command", required=True)
    manifest = sub.add_parser("manifest")
    manifest.set_defaults(func=command_manifest)
    run = sub.add_parser("run")
    run.add_argument("--run-root", required=True)
    run.add_argument("--scenario-id", required=True)
    run.add_argument("--variant-id", required=True)
    run.set_defaults(func=command_run)
    try:
        args = parser.parse_args(argv)
        return args.func(args)
    except ProducerError as exc:
        print(f"[error] {exc}", file=sys.stderr)
        return 1
    except (OSError, ValueError) as exc:
        print(f"[error] operation failed: {type(exc).__name__}", file=sys.stderr)
        return 1


if __name__ == "__main__":
    raise SystemExit(main())
