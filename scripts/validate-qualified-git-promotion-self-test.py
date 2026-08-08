#!/usr/bin/env python3
"""Synthetic positive and negative fixtures for Issue #504 preflight."""

from __future__ import annotations

import copy
import hashlib
import json
import subprocess
import sys
import tempfile
from pathlib import Path


SCRIPT_DIR = Path(__file__).resolve().parent
VALIDATOR = SCRIPT_DIR / "validate-qualified-git-promotion.py"
FINGERPRINTER = SCRIPT_DIR / "ruleset-fingerprint.py"
COMMIT = "0123456789abcdef0123456789abcdef01234567"
OTHER_COMMIT = "89abcdef0123456789abcdef0123456789abcdef"
OCI_DIGEST = "sha256:" + "a" * 64
IDS = {
    "candidateRunId": 31203481547,
    "candidateAttempt": 1,
    "candidateId": "1" * 64,
    "bindingId": "2" * 64,
    "qualificationRunId": "3" * 64,
    "sealedEventId": "4" * 64,
    "ociIndexDigest": OCI_DIGEST,
    "qualificationProducerRunId": 456789,
    "qualificationWorkflowRunAttempt": 2,
    "qualificationProducerRepository": "kooiei-in4a/amane-mailer",
    "qualificationProducerWorkflowPath": ".github/workflows/qualify-release.yml",
    "qualificationProducerWorkflowId": 987654,
    "qualificationProducerEvent": "workflow_dispatch",
    "qualificationProducerHeadBranch": "release/v1.3.0-rc",
    "qualificationProducerHeadSha": COMMIT,
}
APP_ID = 24680
CHECKS = [
    {"context": "Restore, build, and test", "integration_id": 15368},
    {"context": "Native AOT publish smoke", "integration_id": 15368},
    {"context": "Docker build smoke", "integration_id": 15368},
]


def write_json(path: Path, value: object) -> None:
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_text(json.dumps(value, indent=2, sort_keys=True) + "\n", encoding="utf-8")


def run_validator(root: Path, manifest: dict[str, object]) -> subprocess.CompletedProcess[str]:
    manifest_path = root / "promotion.json"
    write_json(manifest_path, manifest)
    return subprocess.run(
        [
            sys.executable,
            str(VALIDATOR),
            "--manifest",
            str(manifest_path),
            "--qualification-root",
            str(root / "qualification"),
            "--candidate-root",
            str(root / "candidate"),
        ],
        check=False,
        capture_output=True,
        text=True,
    )


def expect_pass(name: str, result: subprocess.CompletedProcess[str]) -> None:
    if result.returncode != 0:
        raise SystemExit(f"{name} unexpectedly failed: {result.stderr.strip()}")


def expect_fail(name: str, result: subprocess.CompletedProcess[str]) -> None:
    if result.returncode == 0:
        raise SystemExit(f"{name} unexpectedly passed")


def base_manifest(fingerprint: str, policy_fingerprint: str) -> dict[str, object]:
    return {
        "schemaVersion": 1,
        "mode": "rehearsal",
        "releaseVersion": "1.3.0",
        "releaseCommitSha": COMMIT,
        "releaseBranch": "release/v1.3.0-rc",
        **IDS,
        "machineVerdict": "GO_ELIGIBLE",
        "humanDecision": "APPROVE",
        "qualificationApprovalScope": "exact-candidate-qualification",
        "promotionPrNumber": 5040,
        "promotionPrHeadSha": COMMIT,
        "promotionPrHeadRef": "release/v1.3.0-rc",
        "promotionPrBaseRef": "release-rehearsal/504-main-equivalent",
        "promotionPrBaseSha": OTHER_COMMIT,
        "promotionBaseSha": OTHER_COMMIT,
        "baseRefTipSha": OTHER_COMMIT,
        "promotionPrState": "open",
        "promotionPrDraft": False,
        "promotionPrMergeable": True,
        "rcTipSha": COMMIT,
        "tagName": "rehearsal/issue-504/fixture",
        "tagTargetSha": COMMIT,
        "mergeFreezeConfirmation": "CONFIRM_TARGET_MERGE_FREEZE",
        "rulesetFingerprint": fingerprint,
        "expectedRulesetFingerprint": fingerprint,
        "mainRulesetPolicyFingerprint": policy_fingerprint,
        "targetRulesetPolicyFingerprint": policy_fingerprint,
        "rulesetEnforcement": "active",
        "requiredSignatures": True,
        "normalActorBypass": "never",
        "expectedReleaseAppId": APP_ID,
        "rulesetBypassActors": [{"actor_id": APP_ID, "actor_type": "Integration", "bypass_mode": "pull_request"}],
        "repositoryAllowMergeCommit": True,
        "selectedMergeMethod": "merge",
        "rulesetAllowedMergeMethods": ["merge", "rebase", "squash"],
        "rulesetRequiredStatusChecks": CHECKS,
        "observedStatusChecks": [{**item, "conclusion": "success"} for item in CHECKS],
    }


def main() -> None:
    with tempfile.TemporaryDirectory(prefix="qualified-git-promotion-") as temp:
        root = Path(temp)
        qual = root / "qualification"
        candidate = root / "candidate"
        binding = {
            **IDS,
            "releaseCommitSha": COMMIT,
            "releaseVersion": "1.3.0",
        }
        decision = {
            **IDS,
            "sourceCommitSha": COMMIT,
            "releaseVersion": "1.3.0",
            "machineVerdict": "GO_ELIGIBLE",
            "humanDecision": "APPROVE",
            "runSealed": True,
        }
        event = {
            **IDS,
            "sourceCommitSha": COMMIT,
            "releaseVersion": "1.3.0",
            "status": "sealed",
            "runStatusEventSequence": 1,
        }
        producer = {
            "repository": "kooiei-in4a/amane-mailer",
            "workflowPath": ".github/workflows/qualify-release.yml",
            "workflowId": 987654,
            "event": "workflow_dispatch",
            "headBranch": "release/v1.3.0-rc",
            "headSha": COMMIT,
            "runId": 456789,
            "runAttempt": 2,
        }
        candidate_provenance = {
            "schemaVersion": 1,
            "sourceCommitSha": COMMIT,
            "releaseVersion": "1.3.0",
            "workflowRunId": str(IDS["candidateRunId"]),
            "workflowRunAttempt": str(IDS["candidateAttempt"]),
            "workflowRef": "kooiei-in4a/amane-mailer/.github/workflows/generate-setup-release-candidate.yml@refs/heads/release/v1.3.0-rc",
            "ociIndexDigest": OCI_DIGEST,
        }
        image_identity = {
            "sourceCommitSha": COMMIT,
            "mailerVersion": "1.3.0",
            "imageDigest": OCI_DIGEST,
        }
        write_json(qual / "binding.json", binding)
        write_json(qual / "decision" / "go-no-go.json", decision)
        write_json(qual / "run-status-events" / "sealed.json", event)
        write_json(qual / "qualification-producer.json", producer)
        write_json(candidate / "candidate-provenance.json", candidate_provenance)
        write_json(candidate / "image-identity.json", image_identity)

        ruleset = {
            "id": 1,
            "name": "fixture",
            "target": "branch",
            "source_type": "Repository",
            "source": "example/repo",
            "enforcement": "active",
            "conditions": {"ref_name": {"include": ["refs/heads/main"], "exclude": []}},
            "rules": [
                {"type": "required_signatures"},
                {"type": "required_status_checks", "parameters": {"strict_required_status_checks_policy": True, "do_not_enforce_on_create": False, "required_status_checks": list(reversed(CHECKS))}},
                {"type": "pull_request", "parameters": {"allowed_merge_methods": ["squash", "merge", "rebase"]}},
            ],
            "bypass_actors": [{"actor_id": APP_ID, "actor_type": "Integration", "bypass_mode": "pull_request"}],
        }
        write_json(root / "ruleset.json", ruleset)
        write_json(root / "effective.json", list(reversed(ruleset["rules"])))
        fingerprint_output = root / "fingerprint.json"
        fingerprint_result = subprocess.run(
            [sys.executable, str(FINGERPRINTER), "--ruleset", str(root / "ruleset.json"), "--effective-rules", str(root / "effective.json"), "--output", str(fingerprint_output)],
            check=False,
            capture_output=True,
            text=True,
        )
        expect_pass("fingerprint-positive", fingerprint_result)
        fingerprints = json.loads(fingerprint_output.read_text(encoding="utf-8"))
        manifest = base_manifest(fingerprints["fingerprint"], fingerprints["policyFingerprint"])

        expect_pass("positive", run_validator(root, manifest))

        (qual / "qualification-producer.json").unlink()
        expect_pass("existing sealed handoff compatibility", run_validator(root, manifest))
        write_json(qual / "qualification-producer.json", producer)

        no_go = copy.deepcopy(manifest)
        no_go["machineVerdict"] = "NO_GO"
        expect_fail("N1 qualification not approved", run_validator(root, no_go))

        head_mismatch = copy.deepcopy(manifest)
        head_mismatch["promotionPrHeadSha"] = OTHER_COMMIT
        expect_fail("N2 head SHA mismatch", run_validator(root, head_mismatch))

        signatures_disabled = copy.deepcopy(manifest)
        signatures_disabled["requiredSignatures"] = False
        expect_fail("N3 signature requirement failure", run_validator(root, signatures_disabled))

        rc_drift = copy.deepcopy(manifest)
        rc_drift["rcTipSha"] = OTHER_COMMIT
        expect_fail("N4 RC tip drift", run_validator(root, rc_drift))

        qualification_mismatch = copy.deepcopy(manifest)
        qualification_mismatch["qualificationRunId"] = "5" * 64
        expect_fail("N5 qualificationRunId mismatch", run_validator(root, qualification_mismatch))

        ruleset_mismatch = copy.deepcopy(manifest)
        ruleset_mismatch["expectedRulesetFingerprint"] = "6" * 64
        expect_fail("N6 ruleset fingerprint mismatch", run_validator(root, ruleset_mismatch))

        candidate_mismatch = copy.deepcopy(manifest)
        candidate_mismatch["candidateId"] = "7" * 64
        expect_fail("N7 candidateId mismatch", run_validator(root, candidate_mismatch))

        producer_mismatch = copy.deepcopy(manifest)
        producer_mismatch["qualificationProducerWorkflowId"] = 123456
        expect_fail("N8 qualification producer mismatch", run_validator(root, producer_mismatch))

        candidate_provenance_mismatch = copy.deepcopy(manifest)
        write_json(candidate / "candidate-provenance.json", {**candidate_provenance, "workflowRunId": "999999"})
        expect_fail("N9 candidate producer provenance mismatch", run_validator(root, candidate_provenance_mismatch))
        write_json(candidate / "candidate-provenance.json", candidate_provenance)

        sealed_event_mismatch = copy.deepcopy(manifest)
        sealed_event_mismatch["sealedEventId"] = "8" * 64
        expect_fail("N7 sealedEventId mismatch", run_validator(root, sealed_event_mismatch))

        changed_ruleset = copy.deepcopy(ruleset)
        changed_ruleset["bypass_actors"] = []
        write_json(root / "ruleset-changed.json", changed_ruleset)
        changed_output = root / "fingerprint-changed.json"
        changed_result = subprocess.run(
            [sys.executable, str(FINGERPRINTER), "--ruleset", str(root / "ruleset-changed.json"), "--effective-rules", str(root / "effective.json"), "--output", str(changed_output)],
            check=False,
            capture_output=True,
            text=True,
        )
        expect_pass("fingerprint-change", changed_result)
        changed_fingerprints = json.loads(changed_output.read_text(encoding="utf-8"))
        if changed_fingerprints["fingerprint"] == fingerprints["fingerprint"]:
            raise SystemExit("ruleset actor change did not change fingerprint")

    print("[info] qualified Git promotion validator self-test passed")
    print("positiveFixture=PASS")
    print("negativeQualificationFixture=PASS")
    print("negativeHeadMismatchFixture=PASS")
    print("negativeSignatureFixture=PASS")
    print("sealedHandoffCompatibility=PASS")
    print("additionalNegativeFixtures=PASS")
    print("finalResult=PASS")


if __name__ == "__main__":
    main()
