#!/usr/bin/env python3
"""Synthetic contract and runner E2E tests for qualification-lane-adapter."""

from __future__ import annotations

import copy
import importlib.util
import json
import tempfile
from argparse import Namespace
from pathlib import Path


ROOT = Path(__file__).resolve().parent
ADAPTER_PATH = ROOT / "qualification-lane-adapter.py"
RUNNER_PATH = ROOT / "qualification-runner.py"


def load(path: Path, name: str):
    spec = importlib.util.spec_from_file_location(name, path)
    assert spec is not None and spec.loader is not None
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


def expect_rejected(label, action, adapter, runner):
    try:
        action()
    except (adapter.AdapterError, runner.RunnerError):
        return
    raise AssertionError(f"negative case was accepted: {label}")


def main() -> int:
    adapter = load(ADAPTER_PATH, "qualification_lane_adapter_self_test")
    runner = load(RUNNER_PATH, "qualification_runner_for_adapter_self_test")
    producer = load(ROOT / "qualification-lane-fixture-producer.py", "qualification_lane_fixture_producer_self_test")
    lanes = adapter.load_manifest(runner)
    assert len(lanes) == 32
    assert "G456-03/acs-staging-nosend" not in lanes
    assert "G456-35/linux-arm64" not in lanes
    assert all(scenario.startswith("G456-") for scenario, _ in (key.split("/", 1) for key in lanes))

    scenario = "G456-15"
    variant = "ci-auto"
    lane = lanes[f"{scenario}/{variant}"]
    procedure = producer.procedure_for(lane, scenario)
    candidate_id = "a" * 64
    binding = {
        "scopeId": runner.V13_SCOPE_ID,
        "scopeVersion": 1,
        "scopeManifestSha256": "b" * 64,
        "candidateId": candidate_id,
        "bindingId": "c" * 64,
        "qualificationRunId": "d" * 64,
        "runAttemptNonce": "adapter-self-test",
        "releaseCommitSha": "e" * 40,
        "issueBodySha256": "f" * 64,
        "planRevision": "1",
        "planCommitSha": "e" * 40,
        "planFileSha256": "0" * 64,
        "rows": [{
            "scenarioId": scenario,
            "requiredVariants": [variant],
            "gateClass": "Hard",
            "predicateSet": "legacy-g456-15",
            "ownerRoleClass": "lane-owner",
        }],
    }
    auth = {
        "candidateId": candidate_id,
        "bindingId": binding["bindingId"],
        "qualificationRunId": binding["qualificationRunId"],
        "qualificationLeadRole": "qualification-lead",
        "qualificationLeadIdentity": "maintainer:adapter-self-test",
        "conditionalApproverRole": "conditional-approver",
        "conditionalApproverIdentity": "maintainer:conditional-self-test",
        "evidenceOwners": [{
            "scenarioId": scenario,
            "variantId": variant,
            "ownerRole": "lane-owner",
            "ownerIdentity": "ci:adapter-self-test",
        }],
    }
    payload = {
        "accessProfile": "managed",
        "usernameRelation": "different-user",
        "credentialRotationAttempt": "rejected",
        "manualExistingAdmin": "rejected",
        "reapplyResult": "rejected",
        "credentialChanged": False,
        "sensitiveOutput": "absent",
    }
    report = {
        "schemaVersion": 2,
        "kind": "qualification-lane-fixture-observations",
        "scenarioId": scenario,
        "variantId": variant,
        "candidateId": candidate_id,
        "releaseCommitSha": binding["releaseCommitSha"],
        "bindingId": binding["bindingId"],
        "qualificationRunId": binding["qualificationRunId"],
        "executedByRole": "lane-owner",
        "executedByIdentity": "ci:adapter-self-test",
        "startedAtUtc": "2026-08-09T00:00:00Z",
        "finishedAtUtc": "2026-08-09T00:00:01Z",
        "attestedAtUtc": "2026-08-09T00:00:01Z",
        "execution": {"platform": "ci-auto", "osFamily": "ci", "runtimeKind": "canonical-dotnet-fixture", "fixtureCommandId": lane["fixtureCommandId"]},
        "producer": {"producerId": procedure["producerId"], "producerRevision": procedure["producerRevision"], "procedureId": procedure["procedureId"], "procedureRevision": procedure["procedureRevision"], "procedureDigestSha256": producer.digest(procedure), "exitCode": 0, "result": "PASS", "passedTestCount": 1, "totalTestCount": 1, "skippedTestCount": 0},
        "checks": [
            {"checkId": f"{scenario}/{variant}/{field}", "result": "PASS", "proofKind": "qualification-integration-observation", "sourceTestId": f"fixture-{field}", "observedFields": {field: value}}
            for field, value in payload.items()
        ],
    }
    derived = adapter.validate_report(report, binding, auth, lane, scenario, variant, runner)
    assert derived == payload
    envelope = adapter.build_envelope(report, derived, binding, lane, scenario, variant, runner)
    runner.validate_evidence_envelope(envelope, binding, auth, (scenario, variant))

    expect_rejected("wrong candidate", lambda: adapter.validate_report({**report, "candidateId": "0" * 64}, binding, auth, lane, scenario, variant, runner), adapter, runner)
    expect_rejected("wrong release commit", lambda: adapter.validate_report({**report, "releaseCommitSha": "0" * 40}, binding, auth, lane, scenario, variant, runner), adapter, runner)
    expect_rejected("wrong binding", lambda: adapter.validate_report({**report, "bindingId": "0" * 64}, binding, auth, lane, scenario, variant, runner), adapter, runner)
    expect_rejected("wrong qualification run", lambda: adapter.validate_report({**report, "qualificationRunId": "0" * 64}, binding, auth, lane, scenario, variant, runner), adapter, runner)
    expect_rejected("wrong scenario", lambda: adapter.validate_report({**report, "scenarioId": "G456-16"}, binding, auth, lane, scenario, variant, runner), adapter, runner)
    expect_rejected("wrong variant", lambda: adapter.validate_report({**report, "variantId": "win-docker"}, binding, auth, lane, scenario, variant, runner), adapter, runner)
    expect_rejected("wrong platform", lambda: adapter.validate_report({**report, "execution": {"platform": "linux-docker", "osFamily": "linux", "runtimeKind": "fixture"}}, binding, auth, lane, scenario, variant, runner), adapter, runner)
    expect_rejected("wrong owner", lambda: adapter.validate_report({**report, "executedByIdentity": "ci:wrong"}, binding, auth, lane, scenario, variant, runner), adapter, runner)
    missing = copy.deepcopy(report)
    missing["checks"] = missing["checks"][:-1]
    expect_rejected("missing observation", lambda: adapter.validate_report(missing, binding, auth, lane, scenario, variant, runner), adapter, runner)
    unexpected = copy.deepcopy(report)
    unexpected["checks"][0]["observedFields"]["operatorForcedPass"] = True
    expect_rejected("unexpected observation", lambda: adapter.validate_report(unexpected, binding, auth, lane, scenario, variant, runner), adapter, runner)
    failed_check = copy.deepcopy(report)
    failed_check["checks"][0]["result"] = "FAIL"
    expect_rejected("fixture failure", lambda: adapter.validate_report(failed_check, binding, auth, lane, scenario, variant, runner), adapter, runner)
    leaky = copy.deepcopy(report)
    leaky["checks"][0]["observedFields"] = {"secret": "synthetic-value"}
    expect_rejected("secret-bearing report", lambda: adapter.validate_report(leaky, binding, auth, lane, scenario, variant, runner), adapter, runner)
    wrong_command = copy.deepcopy(report)
    wrong_command["execution"]["fixtureCommandId"] = "g456-14-win-docker"
    expect_rejected("wrong fixture command", lambda: adapter.validate_report(wrong_command, binding, auth, lane, scenario, variant, runner), adapter, runner)
    tampered_value = copy.deepcopy(report)
    tampered_value["checks"][5]["observedFields"]["credentialChanged"] = True
    tampered_payload = adapter.validate_report(tampered_value, binding, auth, lane, scenario, variant, runner)
    tampered_envelope = adapter.build_envelope(tampered_value, tampered_payload, binding, lane, scenario, variant, runner)
    expect_rejected("tampered predicate value", lambda: runner.validate_evidence_envelope(tampered_envelope, binding, auth, (scenario, variant)), adapter, runner)

    # These cases execute the checked-in producer against real product tests.
    # The binding/auth context is synthetic and in-memory; no qualification run
    # is read or modified.  At least one Docker, local-admin, and integrated
    # procedure must pass before this self-test can report success.
    real_cases = (("G456-02", "linux-docker"), ("G456-07", "admin-local-dev"), ("G456-16", "admin-integrated"))
    real_results = []
    for real_scenario, real_variant in real_cases:
        real_binding = copy.deepcopy(binding)
        real_binding["rows"] = [{
            "scenarioId": real_scenario,
            "requiredVariants": [real_variant],
            "gateClass": "Hard",
            "predicateSet": f"legacy-{real_scenario.lower()}",
            "ownerRoleClass": "lane-owner",
        }]
        real_auth = copy.deepcopy(auth)
        real_auth["evidenceOwners"] = [{
            "scenarioId": real_scenario,
            "variantId": real_variant,
            "ownerRole": "lane-owner",
            "ownerIdentity": f"fixture:{real_scenario.lower()}:{real_variant}",
        }]
        real_lane = lanes[f"{real_scenario}/{real_variant}"]
        real_report = producer.produce_with_context(
            adapter.read_json(adapter.ADAPTER_MANIFEST, "adapter manifest"),
            runner,
            real_lane,
            real_scenario,
            real_variant,
            real_binding,
            real_auth["evidenceOwners"][0],
        )
        real_payload = adapter.validate_report(real_report, real_binding, real_auth, real_lane, real_scenario, real_variant, runner)
        real_envelope = adapter.build_envelope(real_report, real_payload, real_binding, real_lane, real_scenario, real_variant, runner)
        runner.validate_evidence_envelope(real_envelope, real_binding, real_auth, (real_scenario, real_variant))
        real_results.append((real_scenario, real_variant, real_binding, real_auth, real_envelope))
    assert len(real_results) == len(real_cases)

    flow_scenario, flow_variant, flow_binding, flow_auth, flow_envelope = real_results[1]
    with tempfile.TemporaryDirectory(prefix="amane-lane-adapter-self-test-") as temp:
        run_root = Path(temp)
        (run_root / "evidence").mkdir()
        (run_root / "scans").mkdir()
        envelope_path = run_root / "observations.json"
        envelope_path.write_text(json.dumps(flow_envelope, sort_keys=True), encoding="utf-8")
        original_binding = runner.load_binding
        original_auth = runner.load_authorization
        runner.load_binding = lambda _: flow_binding
        runner.load_authorization = lambda _: flow_auth
        try:
            runner.command_evidence(Namespace(
                run_root=str(run_root), evidence_id=flow_envelope["evidenceId"], scenario_id=flow_scenario, variant_id=flow_variant,
                result="PASS", executed_by_role="lane-owner", executed_by_identity=flow_auth["evidenceOwners"][0]["ownerIdentity"], observations=str(envelope_path),
            ))
            runner.command_disposition(Namespace(
                run_root=str(run_root), scenario_id=flow_scenario, variant_id=flow_variant, action="accept",
                target_evidence_id=flow_envelope["evidenceId"], restores_event_id=None, superseded_by_evidence_id=None,
                reason_code="adapter-self-test", approved_by_role="lane-owner", approved_by_identity=flow_auth["evidenceOwners"][0]["ownerIdentity"],
            ))
            active, _, _, _ = runner.replay(run_root)
            assert active[(flow_scenario, flow_variant)] == flow_envelope["evidenceId"]
            assert len(list((run_root / "evidence").glob("*.json"))) == 1
            assert len(list((run_root / "scans").glob("*.json"))) == 1
            assert len(list((run_root / "dispositions").glob("*.json"))) == 1
        finally:
            runner.load_binding = original_binding
            runner.load_authorization = original_auth
    print("[info] qualification-lane-adapter self-test passed")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
