#!/usr/bin/env python3
"""Contract and real structured-fixture E2E tests for the lane adapter."""

from __future__ import annotations

import copy
import importlib.util
import json
import tempfile
from argparse import Namespace
from pathlib import Path


ROOT = Path(__file__).resolve().parent


def load(path: Path, name: str):
    spec = importlib.util.spec_from_file_location(name, path)
    assert spec is not None and spec.loader is not None
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


def expect_rejected(label, action, adapter, runner, producer=None):
    errors = (adapter.AdapterError, runner.RunnerError)
    if producer is not None:
        errors = errors + (producer.ProducerError,)
    try:
        action()
    except errors:
        return
    raise AssertionError(f"negative case was accepted: {label}")


def main() -> int:
    adapter = load(ROOT / "qualification-lane-adapter.py", "qualification_lane_adapter_self_test")
    runner = load(ROOT / "qualification-runner.py", "qualification_runner_for_adapter_self_test")
    producer = load(ROOT / "qualification-lane-fixture-producer.py", "qualification_lane_fixture_producer_self_test")
    lanes = adapter.load_manifest(runner)
    assert len(lanes) == 32
    procedures = producer.validate_registry(adapter.read_json(adapter.ADAPTER_MANIFEST, "adapter manifest"), runner)
    assert len(procedures) == 32
    assert sum(1 for item in procedures if item["fixtureAvailable"]) == 1

    scenario = "G456-07"
    variant = "admin-local-dev"
    lane = lanes[f"{scenario}/{variant}"]
    procedure = producer.procedure_for(lane, scenario)
    candidate_id = "a" * 64
    binding = {
        "scopeId": runner.V13_SCOPE_ID, "scopeVersion": 1, "scopeManifestSha256": "b" * 64,
        "candidateId": candidate_id, "bindingId": "c" * 64, "qualificationRunId": "d" * 64,
        "runAttemptNonce": "adapter-self-test", "releaseCommitSha": "e" * 40,
        "issueBodySha256": "f" * 64, "planRevision": "1", "planCommitSha": "e" * 40,
        "planFileSha256": "0" * 64,
        "rows": [{"scenarioId": scenario, "requiredVariants": [variant], "gateClass": "Hard", "predicateSet": "legacy-g456-07", "ownerRoleClass": "lane-owner"}],
    }
    auth = {
        "candidateId": candidate_id, "bindingId": binding["bindingId"], "qualificationRunId": binding["qualificationRunId"],
        "qualificationLeadRole": "qualification-lead", "qualificationLeadIdentity": "maintainer:adapter-self-test",
        "conditionalApproverRole": "conditional-approver", "conditionalApproverIdentity": "maintainer:conditional-self-test",
        "evidenceOwners": [{"scenarioId": scenario, "variantId": variant, "ownerRole": "lane-owner", "ownerIdentity": "fixture:g456-07:admin-local-dev"}],
    }
    # The baseline report is produced by the exact fixture.  Negative cases
    # below mutate that real result rather than manufacturing PASS observations.
    report = producer.produce_with_context(
        adapter.read_json(adapter.ADAPTER_MANIFEST, "adapter manifest"), runner, lane, scenario, variant,
        binding, auth["evidenceOwners"][0],
    )
    derived = adapter.validate_report(report, binding, auth, lane, scenario, variant, runner)
    envelope = adapter.build_envelope(report, derived, binding, lane, scenario, variant, runner)
    runner.validate_evidence_envelope(envelope, binding, auth, (scenario, variant))

    expect_rejected("wrong candidate", lambda: adapter.validate_report({**report, "candidateId": "0" * 64}, binding, auth, lane, scenario, variant, runner), adapter, runner)
    expect_rejected("wrong fixture digest", lambda: adapter.validate_report({**report, "producer": {**report["producer"], "fixtureResultDigestSha256": "0" * 64}}, binding, auth, lane, scenario, variant, runner), adapter, runner)
    wrong_source = copy.deepcopy(report)
    wrong_source["fixtureResult"]["sourceTestId"] = "not-the-exact-test-case"
    expect_rejected("wrong source test id", lambda: adapter.validate_report(wrong_source, binding, auth, lane, scenario, variant, runner), adapter, runner)
    missing = copy.deepcopy(report)
    missing["fixtureResult"]["observations"].pop("sensitiveOutput")
    expect_rejected("missing fixture observation", lambda: adapter.validate_report(missing, binding, auth, lane, scenario, variant, runner), adapter, runner)
    failed_fixture = copy.deepcopy(report)
    failed_fixture["fixtureResult"]["result"] = "FAIL"
    expect_rejected("failed fixture result", lambda: adapter.validate_report(failed_fixture, binding, auth, lane, scenario, variant, runner), adapter, runner)
    tampered = copy.deepcopy(report)
    tampered["checks"][0]["observedFields"]["accessProfile"] = "unexpected"
    expect_rejected("check differs from fixture result", lambda: adapter.validate_report(tampered, binding, auth, lane, scenario, variant, runner), adapter, runner)

    # The same real fixture result continues through runner evidence, scan,
    # disposition, and replay to prove the active-PASS path.
    runner.validate_evidence_envelope(envelope, binding, auth, (scenario, variant))

    with tempfile.TemporaryDirectory(prefix="amane-lane-adapter-self-test-") as temp:
        run_root = Path(temp)
        (run_root / "evidence").mkdir()
        (run_root / "scans").mkdir()
        envelope_path = run_root / "observations.json"
        envelope_path.write_text(json.dumps(envelope, sort_keys=True), encoding="utf-8")
        original_binding = runner.load_binding
        original_auth = runner.load_authorization
        runner.load_binding = lambda _: binding
        runner.load_authorization = lambda _: auth
        try:
            runner.command_evidence(Namespace(run_root=str(run_root), evidence_id=envelope["evidenceId"], scenario_id=scenario, variant_id=variant, result="PASS", executed_by_role="lane-owner", executed_by_identity=auth["evidenceOwners"][0]["ownerIdentity"], observations=str(envelope_path)))
            runner.command_disposition(Namespace(run_root=str(run_root), scenario_id=scenario, variant_id=variant, action="accept", target_evidence_id=envelope["evidenceId"], restores_event_id=None, superseded_by_evidence_id=None, reason_code="structured-fixture-self-test", approved_by_role="lane-owner", approved_by_identity=auth["evidenceOwners"][0]["ownerIdentity"]))
            active, _, _, _ = runner.replay(run_root)
            assert active[(scenario, variant)] == envelope["evidenceId"]
            assert len(list((run_root / "evidence").glob("*.json"))) == 1
            assert len(list((run_root / "scans").glob("*.json"))) == 1
            assert len(list((run_root / "dispositions").glob("*.json"))) == 1
        finally:
            runner.load_binding = original_binding
            runner.load_authorization = original_auth
    print("[info] qualification-lane-adapter structured-fixture self-test passed")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
