#!/usr/bin/env python3
"""Run and verify a canonical structured qualification fixture.

The producer does not contain predicate observations.  A checked-in
qualification fixture executes the operation, writes a value-free structured
result, and exits non-zero when its operation predicate fails.  This producer
verifies that result against the exact fixture test case, schema, identity,
and registered Hard predicate before handing it to the lane adapter.
"""

from __future__ import annotations

import argparse
import hashlib
import importlib.util
import json
import os
import platform
import re
import shutil
import subprocess
import sys
import tempfile
import xml.etree.ElementTree as ET
from contextlib import contextmanager
from datetime import datetime, timezone
from pathlib import Path
from typing import Any, Iterator


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
SOURCE_COMMIT_PATTERN = r"^[0-9a-f]{40}$"


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
        raise ProducerError("canonical fixture JSON is not readable") from exc


def canonical_json(value: Any) -> bytes:
    return json.dumps(value, ensure_ascii=False, sort_keys=True, separators=(",", ":")).encode("utf-8")


def digest(value: Any) -> str:
    return hashlib.sha256(canonical_json(value)).hexdigest()


def now_utc() -> str:
    return datetime.now(timezone.utc).replace(microsecond=0).isoformat().replace("+00:00", "Z")


def sha256_file(path: Path) -> str:
    hasher = hashlib.sha256()
    try:
        with path.open("rb") as stream:
            for chunk in iter(lambda: stream.read(1024 * 1024), b""):
                hasher.update(chunk)
    except OSError as exc:
        raise ProducerError("fresh build assembly is not readable") from exc
    return hasher.hexdigest()


def git_command(repo_root: Path, arguments: list[str], timeout: int = 30) -> subprocess.CompletedProcess[str]:
    try:
        return subprocess.run(
            ["git", "-C", str(repo_root), *arguments],
            capture_output=True,
            text=True,
            encoding="utf-8",
            errors="replace",
            timeout=timeout,
            check=False,
        )
    except (OSError, subprocess.SubprocessError) as exc:
        raise ProducerError("git source identity could not be checked") from exc


def git_query_at(repo_root: Path, *arguments: str) -> str:
    result = git_command(repo_root, list(arguments))
    if result.returncode != 0:
        raise ProducerError("git source identity could not be checked")
    return result.stdout.strip()


def git_query(*arguments: str) -> str:
    return git_query_at(REPO_ROOT, *arguments)


def git_require_no_changes_at(repo_root: Path, *arguments: str) -> None:
    result = git_command(repo_root, list(arguments))
    if result.returncode == 1:
        raise ProducerError("producer source worktree is dirty")
    if result.returncode != 0:
        raise ProducerError("git source cleanliness could not be checked")


def git_require_no_changes(*arguments: str) -> None:
    git_require_no_changes_at(REPO_ROOT, *arguments)


def verify_source_identity(binding: dict[str, Any]) -> str:
    expected = binding.get("releaseCommitSha")
    if not isinstance(expected, str) or not re.fullmatch(SOURCE_COMMIT_PATTERN, expected):
        raise ProducerError("binding releaseCommitSha is invalid")
    actual = git_query("rev-parse", "HEAD")
    if actual != expected:
        raise ProducerError("producer checkout HEAD does not match binding releaseCommitSha")
    if git_query("status", "--porcelain", "--untracked-files=no"):
        raise ProducerError("producer tracked source worktree is dirty")
    git_require_no_changes("diff", "--quiet")
    git_require_no_changes("diff", "--cached", "--quiet")
    if git_query("ls-files", "--others", "--exclude-standard"):
        raise ProducerError("producer has non-ignored untracked files")
    return actual


def verify_isolated_source_identity(worktree_root: Path, expected: str) -> str:
    actual = git_query_at(worktree_root, "rev-parse", "HEAD")
    if actual != expected:
        raise ProducerError("isolated producer worktree does not match binding releaseCommitSha")
    if git_query_at(worktree_root, "status", "--porcelain", "--untracked-files=no"):
        raise ProducerError("isolated producer tracked source worktree is dirty")
    git_require_no_changes_at(worktree_root, "diff", "--quiet")
    git_require_no_changes_at(worktree_root, "diff", "--cached", "--quiet")
    if git_query_at(worktree_root, "ls-files", "--others", "--exclude-standard"):
        raise ProducerError("isolated producer has non-ignored untracked files")
    return actual


@contextmanager
def isolated_git_worktree(commit: str) -> Iterator[Path]:
    parent = Path(tempfile.mkdtemp(prefix="amane-lane-worktree-"))
    worktree_root = parent / "source"
    registered = False
    cleanup_failure = False
    try:
        result = git_command(
            REPO_ROOT,
            ["worktree", "add", "--detach", str(worktree_root), commit],
            timeout=120,
        )
        if result.returncode != 0:
            raise ProducerError("isolated Git worktree could not be created")
        registered = True
        verify_isolated_source_identity(worktree_root, commit)
        yield worktree_root
    finally:
        if registered:
            try:
                result = git_command(
                    REPO_ROOT,
                    ["worktree", "remove", "--force", str(worktree_root)],
                    timeout=120,
                )
                if result.returncode != 0:
                    cleanup_failure = True
            except ProducerError:
                cleanup_failure = True
            if not cleanup_failure:
                try:
                    if parent.exists():
                        shutil.rmtree(parent)
                except OSError:
                    cleanup_failure = True
        else:
            try:
                if parent.exists():
                    shutil.rmtree(parent)
            except OSError:
                cleanup_failure = True
        if cleanup_failure:
            raise ProducerError("isolated Git worktree cleanup failed")


def run_dotnet(command: list[str], environment: dict[str, str], error: str, cwd: Path) -> None:
    try:
        result = subprocess.run(
            command,
            cwd=cwd,
            env=environment,
            capture_output=True,
            text=True,
            encoding="utf-8",
            errors="replace",
            timeout=1800,
            check=False,
        )
    except (OSError, subprocess.SubprocessError) as exc:
        raise ProducerError(error) from exc
    if result.returncode != 0:
        raise ProducerError(error)


def build_fresh_binary(
    dotnet: str,
    environment: dict[str, str],
    worktree_root: Path,
) -> dict[str, Any]:
    solution_path = worktree_root / "Amane.Mailer.slnx"
    for output_dir in worktree_root.rglob("bin"):
        if output_dir.is_dir() or output_dir.is_symlink():
            raise ProducerError("isolated worktree contains pre-existing build output")
    for output_dir in worktree_root.rglob("obj"):
        if output_dir.is_dir() or output_dir.is_symlink():
            raise ProducerError("isolated worktree contains pre-existing build output")
    run_dotnet(
        [dotnet, "restore", str(solution_path), "--locked-mode", "--nologo", "--verbosity", "minimal"],
        environment,
        "locked restore for the canonical fixture failed",
        worktree_root,
    )
    run_dotnet(
        [dotnet, "build", str(solution_path), "-c", "Release", "--no-restore", "--no-incremental", "--nologo", "--verbosity", "minimal"],
        environment,
        "fresh Release rebuild for the canonical fixture failed",
        worktree_root,
    )
    assemblies: dict[str, dict[str, str]] = {}
    test_output = worktree_root / "tests" / "Amane.Mailer.Tests" / "bin" / "Release" / "net10.0"
    for role, file_name in (("test", "Amane.Mailer.Tests.dll"), ("product", "Amane.Mailer.dll")):
        assembly_path = test_output / file_name
        if not assembly_path.is_file() or assembly_path.is_symlink():
            raise ProducerError("fresh build did not produce the expected canonical assembly")
        assemblies[role] = {"fileName": file_name, "sha256": sha256_file(assembly_path)}
    return {
        "schemaVersion": 1,
        "restore": "locked",
        "configuration": "Release",
        "freshBuild": True,
        "outputIsolation": "isolated-git-worktree",
        "noIncremental": True,
        "testAssembly": assemblies["test"],
        "productAssembly": assemblies["product"],
    }


def require_lane(manifest: dict[str, Any], scenario: str, variant: str) -> dict[str, Any]:
    lanes = manifest.get("lanes")
    if not isinstance(lanes, list):
        raise ProducerError("producer manifest lanes are missing")
    matches = [lane for lane in lanes if isinstance(lane, dict) and lane.get("scenarioId") == scenario and lane.get("variantId") == variant]
    if len(matches) != 1 or scenario not in SAFE_SCENARIOS:
        raise ProducerError("scenario/variant is not a canonical producer lane")
    lane = matches[0]
    for field in ("fixtureCommandId", "producerId", "procedureId", "expectedPlatform", "expectedOsFamily"):
        if not isinstance(lane.get(field), str) or not lane[field]:
            raise ProducerError("canonical producer identity is incomplete")
    return lane


def procedure_for(lane: dict[str, Any], scenario: str) -> dict[str, Any]:
    # Only manifest entries with an exact structured fixture identity are
    # available.  A product-test class without such a producer is deliberately
    # absent and can never be promoted to qualification PASS.
    fixture = None
    fixture_spec = lane.get("canonicalFixture")
    if fixture_spec is not None:
        if not isinstance(fixture_spec, dict) or set(fixture_spec) != {"fixtureId", "fixtureRevision", "sourceTestId", "testSelector"}:
            raise ProducerError("canonical fixture manifest identity is invalid")
        fixture = {
            "fixtureId": fixture_spec["fixtureId"],
            "fixtureRevision": fixture_spec["fixtureRevision"],
            "sourceTestId": fixture_spec["sourceTestId"],
            "testSelector": fixture_spec["testSelector"],
        }
        if not all(isinstance(fixture[field], str) and fixture[field] for field in fixture):
            raise ProducerError("canonical fixture manifest identity is incomplete")
        if fixture["fixtureId"] != lane["fixtureCommandId"] or fixture["testSelector"] != f"FullyQualifiedName={fixture['sourceTestId']}":
            raise ProducerError("canonical fixture manifest identity is not bound to the lane")
    return {
        "producerId": lane["producerId"],
        "producerRevision": "2",
        "procedureId": lane["procedureId"],
        "procedureRevision": "1",
        "executionKind": "structured-dotnet-fixture",
        "platformProbe": {"win-docker": "docker-windows", "linux-docker": "docker-linux", "ci-auto": "ci", "admin-local-dev": "windows", "admin-integrated": "windows", "local-dev": "windows"}.get(lane["variantId"]),
        "testProject": "tests/Amane.Mailer.Tests/Amane.Mailer.Tests.csproj",
        "fixtureAvailable": fixture is not None,
        "fixture": fixture,
    }


def validate_registry(manifest: dict[str, Any], runner: Any) -> list[dict[str, Any]]:
    if manifest.get("schemaVersion") != 2 or manifest.get("producerScript") != Path(__file__).name or manifest.get("producerRevision") != "2" or manifest.get("fixtureContractVersion") != 1:
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
        if procedure["producerId"] != lane.get("producerId") or procedure["procedureId"] != lane.get("procedureId"):
            raise ProducerError("manifest and canonical procedure identity differ")
        if procedure["fixtureAvailable"]:
            fixture = procedure["fixture"]
            if not isinstance(fixture, dict) or not all(isinstance(fixture.get(field), str) and fixture[field] for field in ("fixtureId", "fixtureRevision", "sourceTestId", "testSelector")):
                raise ProducerError("canonical fixture identity is incomplete")
        procedures.append(procedure)
    if len(seen) != 32:
        raise ProducerError("canonical producer registry is incomplete")
    return procedures


def probe_platform(probe: str | None) -> None:
    if probe == "windows":
        if platform.system() != "Windows":
            raise ProducerError("required Windows execution environment is unavailable")
        return
    if probe == "ci":
        if os.environ.get("CI", "").lower() != "true" and os.environ.get("GITHUB_ACTIONS", "").lower() != "true":
            raise ProducerError("required CI execution environment is unavailable")
        return
    if probe not in {"docker-linux", "docker-windows"}:
        raise ProducerError("canonical fixture has no execution environment probe")
    docker = shutil.which("docker")
    if docker is None:
        raise ProducerError("required Docker execution environment is unavailable")
    try:
        result = subprocess.run([docker, "info", "--format", "{{.OSType}}|{{.Architecture}}"], cwd=REPO_ROOT, capture_output=True, text=True, encoding="utf-8", errors="replace", timeout=30, check=False)
    except (OSError, subprocess.SubprocessError) as exc:
        raise ProducerError("Docker execution environment probe failed") from exc
    expected = "linux" if probe == "docker-linux" else "windows"
    if result.returncode != 0 or not result.stdout.strip().lower().startswith(expected + "|"):
        raise ProducerError("Docker OS identity does not match the bound variant")


def parse_trx(path: Path) -> tuple[dict[str, int], set[str]]:
    try:
        root = ET.parse(path).getroot()
    except (OSError, ET.ParseError) as exc:
        raise ProducerError("canonical fixture did not produce a valid TRX") from exc
    counters = next((element for element in root.iter() if element.tag.rsplit("}", 1)[-1] == "Counters"), None)
    if counters is None:
        raise ProducerError("canonical fixture TRX counters are missing")
    def count(name: str) -> int:
        try:
            return int(counters.attrib.get(name, "0"))
        except ValueError as exc:
            raise ProducerError("canonical fixture TRX counters are invalid") from exc
    test_ids: set[str] = set()
    for element in root.iter():
        if element.tag.rsplit("}", 1)[-1] != "UnitTest":
            continue
        method = next((child for child in element if child.tag.rsplit("}", 1)[-1] == "TestMethod"), None)
        if method is not None and method.attrib.get("className") and method.attrib.get("name"):
            test_ids.add(f"{method.attrib['className']}.{method.attrib['name']}")
    return ({name: count(name) for name in ("total", "executed", "passed", "failed", "error", "timeout", "aborted", "inconclusive", "notExecuted")}, test_ids)


def validate_fixture_result(fixture_result: Any, procedure: dict[str, Any], scenario: str, variant: str, runner: Any) -> dict[str, Any]:
    if not procedure.get("fixtureAvailable"):
        raise ProducerError("canonical structured fixture is not available for this lane")
    if not isinstance(fixture_result, dict) or set(fixture_result) != {"schemaVersion", "kind", "fixtureId", "fixtureRevision", "scenarioId", "variantId", "sourceTestId", "result", "operationExitCode", "observations"}:
        raise ProducerError("canonical fixture result schema is invalid")
    fixture = procedure["fixture"]
    if fixture_result.get("schemaVersion") != 1 or fixture_result.get("kind") != "qualification-fixture-result" or fixture_result.get("fixtureId") != fixture["fixtureId"] or fixture_result.get("fixtureRevision") != fixture["fixtureRevision"] or fixture_result.get("scenarioId") != scenario or fixture_result.get("variantId") != variant or fixture_result.get("sourceTestId") != fixture["sourceTestId"] or fixture_result.get("result") != "PASS" or fixture_result.get("operationExitCode") != 0:
        raise ProducerError("canonical fixture result identity or operation result is invalid")
    try:
        runner.value_free(fixture_result, "$.fixtureResult")
    except runner.RunnerError as exc:
        raise ProducerError("canonical fixture result is not value-free") from exc
    observations = fixture_result.get("observations")
    spec = runner.HARD_SCENARIO_VALIDATOR_REGISTRY[scenario]
    if not isinstance(observations, dict) or set(observations) != set(spec["fields"]):
        raise ProducerError("canonical fixture did not observe every validator field")
    try:
        fake_envelope = {"evidenceType": spec["evidenceTypes"][0], "procedureId": spec["procedureId"], "procedureRevision": spec["procedureRevision"], "typePayload": observations, "result": "PASS", "variantId": variant}
        runner.validate_registered_hard_payload(fake_envelope, {"scenarioId": scenario, "predicateSet": spec["predicateSet"]}, spec)
    except runner.RunnerError as exc:
        raise ProducerError("canonical fixture observations do not satisfy the registered predicate") from exc
    return observations


def execute_procedure(
    procedure: dict[str, Any],
    scenario: str,
    variant: str,
    runner: Any,
    binding: dict[str, Any],
) -> tuple[dict[str, Any], dict[str, int], dict[str, Any]]:
    if not procedure.get("fixtureAvailable"):
        raise ProducerError("canonical structured fixture is not available for this lane")
    probe_platform(procedure["platformProbe"])
    dotnet = shutil.which("dotnet")
    if dotnet is None:
        raise ProducerError("dotnet is unavailable for the canonical fixture")
    fixture = procedure["fixture"]
    with tempfile.TemporaryDirectory(prefix="amane-lane-fixture-") as temp:
        source_commit = verify_source_identity(binding)
        with isolated_git_worktree(source_commit) as worktree_root:
            project = worktree_root / procedure["testProject"]
            if not project.is_file() or project.parent.parent != worktree_root / "tests":
                raise ProducerError("canonical fixture test project is unavailable")
            build_info = build_fresh_binary(dotnet, os.environ.copy(), worktree_root)
            if verify_isolated_source_identity(worktree_root, source_commit) != source_commit:
                raise ProducerError("isolated producer source changed during fresh build")
            result_path = Path(temp) / "fixture-result.json"
            trx_path = Path(temp) / "fixture.trx"
            environment = os.environ.copy()
            environment["AMANE_QUALIFICATION_FIXTURE_RESULT_PATH"] = str(result_path)
            environment["AMANE_QUALIFICATION_FIXTURE_SCENARIO"] = scenario
            environment["AMANE_QUALIFICATION_FIXTURE_VARIANT"] = variant
            command = [dotnet, "test", str(project), "-c", "Release", "--no-build", "--no-restore", "--nologo", "--filter", fixture["testSelector"], "--logger", f"trx;LogFileName={trx_path}"]
            try:
                result = subprocess.run(command, cwd=worktree_root, env=environment, capture_output=True, text=True, encoding="utf-8", errors="replace", timeout=900, check=False)
            except (OSError, subprocess.SubprocessError) as exc:
                raise ProducerError("canonical fixture could not be executed") from exc
            if not trx_path.is_file():
                raise ProducerError("canonical fixture did not produce a TRX")
            counters, test_ids = parse_trx(trx_path)
            if result.returncode != 0 or counters["total"] != 1 or counters["executed"] != 1 or counters["passed"] != 1 or counters["failed"] or counters["error"] or counters["timeout"] or counters["aborted"] or counters["inconclusive"] or counters["notExecuted"] or fixture["sourceTestId"] not in test_ids:
                raise ProducerError("canonical fixture test case did not pass exactly")
            fixture_result = read_json(result_path)
            validate_fixture_result(fixture_result, procedure, scenario, variant, runner)
            verify_isolated_source_identity(worktree_root, source_commit)
            return fixture_result, counters, {"sourceCommitSha": source_commit, "build": build_info}


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
    fixture_result, counters, provenance = execute_procedure(procedure, scenario, variant, runner, binding)
    finished = now_utc()
    observations = validate_fixture_result(fixture_result, procedure, scenario, variant, runner)
    checks = [{"checkId": f"{scenario}/{variant}/{field}", "result": "PASS", "proofKind": "qualification-integration-observation", "sourceTestId": fixture_result["sourceTestId"], "observedFields": {field: value}} for field, value in observations.items()]
    report = {
        "schemaVersion": 4, "kind": "qualification-lane-fixture-observations", "scenarioId": scenario, "variantId": variant,
        "candidateId": binding["candidateId"], "releaseCommitSha": binding["releaseCommitSha"], "bindingId": binding["bindingId"], "qualificationRunId": binding["qualificationRunId"],
        "executedByRole": owner["ownerRole"], "executedByIdentity": owner["ownerIdentity"], "startedAtUtc": started, "finishedAtUtc": finished, "attestedAtUtc": finished,
        "execution": {"platform": lane["expectedPlatform"], "osFamily": lane["expectedOsFamily"], "runtimeKind": "structured-dotnet-fixture", "fixtureCommandId": lane["fixtureCommandId"]},
        "producer": {"producerId": procedure["producerId"], "producerRevision": procedure["producerRevision"], "procedureId": procedure["procedureId"], "procedureRevision": procedure["procedureRevision"], "procedureDigestSha256": digest(procedure), "fixtureId": procedure["fixture"]["fixtureId"], "fixtureRevision": procedure["fixture"]["fixtureRevision"], "fixtureResultDigestSha256": digest(fixture_result), "fixtureSourceTestId": fixture_result["sourceTestId"], "exitCode": 0, "result": "PASS", "passedTestCount": counters["passed"], "totalTestCount": counters["total"], "skippedTestCount": counters["notExecuted"]},
        "provenance": {"schemaVersion": 1, "sourceCommitSha": provenance["sourceCommitSha"], "bindingReleaseCommitSha": binding["releaseCommitSha"], "sourceCommitIdentityMatch": provenance["sourceCommitSha"] == binding["releaseCommitSha"], "trackedTreeClean": True, "freshBuild": provenance["build"]},
        "fixtureResult": fixture_result,
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
    available = sum(1 for procedure in procedures if procedure["fixtureAvailable"])
    print(json.dumps({"laneCount": len(procedures), "canonicalProducerAvailable": available == len(procedures), "availableLaneCount": available}, sort_keys=True))
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
