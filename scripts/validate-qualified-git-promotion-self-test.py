#!/usr/bin/env python3
"""Git promotion regressions using the shared Issue #622 production fixture."""

from __future__ import annotations

import copy
import hashlib
import json
import re
import shutil
import subprocess
import sys
import tempfile
from pathlib import Path


SCRIPT_DIR = Path(__file__).resolve().parent
VALIDATOR = SCRIPT_DIR / "validate-qualified-git-promotion.py"
PREPARER = SCRIPT_DIR / "prepare-qualification-handoff.py"
FINGERPRINTER = SCRIPT_DIR / "ruleset-fingerprint.py"
PROMOTE_WORKFLOW = SCRIPT_DIR.parent / ".github/workflows/promote-qualified-git.yml"
MALFORMED_PY_HEREDOC_TERMINATOR = re.compile(r"^\s*PY\s+\S")
QUALIFICATION_FIXTURE_ROOT = SCRIPT_DIR / "fixtures/qualification-handoff/production-shape"
PRODUCTION_QUALIFICATION = QUALIFICATION_FIXTURE_ROOT / "artifact"
EXPECTED_PRODUCER = QUALIFICATION_FIXTURE_ROOT / "expected-producer-identity.json"
COMMIT = "0123456789abcdef0123456789abcdef01234567"
OTHER_COMMIT = "89abcdef0123456789abcdef0123456789abcdef"
RC13_FORK_BASE_SHA = "d6743dabc1813ea428081a49874680263ae54f7f"
OCI_DIGEST = "sha256:" + "a" * 64
RELEASE_EVENT_ID = "4" * 32
APP_ID = 24680
CHECKS = [
    {"context": "Restore, build, and test", "integration_id": 15368},
    {"context": "Native AOT publish smoke", "integration_id": 15368},
    {"context": "Docker build smoke", "integration_id": 15368},
]


def write_json(path: Path, value: object) -> None:
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_text(json.dumps(value, indent=2, sort_keys=True) + "\n", encoding="utf-8")


def file_sha256(path: Path) -> str:
    return hashlib.sha256(path.read_bytes()).hexdigest()


def load_json(path: Path) -> dict[str, object]:
    value = json.loads(path.read_text(encoding="utf-8"))
    if not isinstance(value, dict):
        raise AssertionError(f"fixture must be a JSON object: {path}")
    return value


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


def refresh_manifest_digests(root: Path) -> None:
    manifest_path = root / "handoff-manifest.json"
    manifest = load_json(manifest_path)
    objects = manifest.get("objects")
    if not isinstance(objects, list):
        raise AssertionError("fixture manifest objects must be an array")
    for entry in objects:
        if not isinstance(entry, dict) or not isinstance(entry.get("path"), str):
            raise AssertionError("fixture manifest object entry is invalid")
        entry["sha256"] = file_sha256(root / entry["path"])
    write_json(manifest_path, manifest)


def refresh_event_digest(event: dict[str, object]) -> None:
    unsigned = {key: value for key, value in event.items() if key != "eventDigestSha256"}
    event["eventDigestSha256"] = hashlib.sha256(
        json.dumps(unsigned, ensure_ascii=False, sort_keys=True, separators=(",", ":")).encode("utf-8")
    ).hexdigest()


def run_validator(
    root: Path,
    manifest: dict[str, object],
    qualification_root: Path | None = None,
) -> subprocess.CompletedProcess[str]:
    manifest_path = root / "promotion.json"
    write_json(manifest_path, manifest)
    return subprocess.run(
        [
            sys.executable,
            str(VALIDATOR),
            "--manifest",
            str(manifest_path),
            "--qualification-root",
            str(qualification_root or root / "qualification"),
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


def release_manifest(fingerprint: str, policy_fingerprint: str) -> dict[str, object]:
    production_binding = load_json(PRODUCTION_QUALIFICATION / "binding.json")
    production_handoff = load_json(PRODUCTION_QUALIFICATION / "handoff-manifest.json")
    production_producer = load_json(PRODUCTION_QUALIFICATION / "qualification-producer.json")
    return {
        "schemaVersion": 1,
        "mode": "release",
        "releaseVersion": production_binding["releaseVersion"],
        "releaseCommitSha": production_binding["releaseCommitSha"],
        "releaseBranch": "release-prep/v1.3.0-rc13",
        "candidateRunId": int(str(production_binding["producerWorkflowRunId"])),
        "candidateAttempt": int(str(production_binding["producerWorkflowRunAttempt"])),
        "candidateId": production_binding["candidateId"],
        "bindingId": production_binding["bindingId"],
        "qualificationRunId": production_binding["qualificationRunId"],
        "sealedEventId": production_handoff["sealedEventId"],
        "ociIndexDigest": production_binding["ociIndexDigest"],
        "qualificationProducerRunId": production_producer["runId"],
        "qualificationWorkflowRunAttempt": production_producer["runAttempt"],
        "qualificationProducerRepository": production_producer["repository"],
        "qualificationProducerWorkflowPath": production_producer["workflowPath"],
        "qualificationProducerWorkflowId": production_producer["workflowId"],
        "qualificationProducerEvent": production_producer["event"],
        "qualificationProducerHeadBranch": production_producer["headBranch"],
        "qualificationProducerHeadSha": production_producer["headSha"],
        "machineVerdict": "GO_ELIGIBLE",
        "humanDecision": "APPROVE",
        "qualificationApprovalScope": "exact-candidate-qualification",
        "promotionPrNumber": 5040,
        "promotionPrHeadSha": production_binding["releaseCommitSha"],
        "promotionPrHeadRef": "release-prep/v1.3.0-rc13",
        "promotionPrBaseRef": "main",
        "promotionPrBaseSha": OTHER_COMMIT,
        "promotionBaseSha": OTHER_COMMIT,
        "baseRefTipSha": OTHER_COMMIT,
        "promotionPrState": "open",
        "promotionPrDraft": False,
        "promotionPrMergeable": True,
        "rcTipSha": production_binding["releaseCommitSha"],
        "tagName": "v1.3.0",
        "tagTargetSha": production_binding["releaseCommitSha"],
        "mergeFreezeConfirmation": "CONFIRM_TARGET_MERGE_FREEZE",
        "rulesetFingerprint": fingerprint,
        "expectedRulesetFingerprint": fingerprint,
        "mainRulesetPolicyFingerprint": policy_fingerprint,
        "targetRulesetPolicyFingerprint": policy_fingerprint,
        "rulesetEnforcement": "active",
        "requiredSignatures": False,
        "normalActorBypass": "never",
        "expectedReleaseAppId": APP_ID,
        "rulesetBypassActors": [{"actor_id": APP_ID, "actor_type": "Integration", "bypass_mode": "pull_request"}],
        "repositoryAllowMergeCommit": True,
        "selectedMergeMethod": "merge",
        "rulesetAllowedMergeMethods": ["merge", "rebase", "squash"],
        "rulesetRequiredStatusChecks": CHECKS,
        "observedStatusChecks": [{**item, "conclusion": "success"} for item in CHECKS],
        "expectedRcForkBaseSha": RC13_FORK_BASE_SHA,
        "rcForkBaseSha": RC13_FORK_BASE_SHA,
        "prePromotionMainDeltaPaths": [
            ".github/workflows/promote-qualified-git.yml",
            ".github/workflows/publish-sealed-qualification-handoff.yml",
            "global.json",
            "scripts/validate-qualified-git-promotion-self-test.py",
            "scripts/validate-qualified-git-promotion.py",
        ],
        "prePromotionMainDeltaPolicy": "RELEASE_CONTROL_PLANE_ONLY",
        "globalJsonMatchesRc13": True,
    }


def assert_promote_workflow_heredoc_terminators() -> None:
    """Reject `PY <shell tokens>` lines that leave a <<'PY' heredoc unclosed."""
    text = PROMOTE_WORKFLOW.read_text(encoding="utf-8")
    for lineno, line in enumerate(text.splitlines(), start=1):
        if MALFORMED_PY_HEREDOC_TERMINATOR.search(line):
            raise SystemExit(
                f"malformed PY heredoc terminator in {PROMOTE_WORKFLOW.name}:{lineno}: {line!r}"
            )


def main() -> None:
    assert_promote_workflow_heredoc_terminators()
    with tempfile.TemporaryDirectory(prefix="qualified-git-promotion-") as temp:
        root = Path(temp)
        candidate = root / "candidate"

        ruleset = {
            "id": 1,
            "name": "fixture",
            "target": "branch",
            "source_type": "Repository",
            "source": "example/repo",
            "enforcement": "active",
            "conditions": {"ref_name": {"include": ["refs/heads/main"], "exclude": []}},
            "rules": [
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
        manifest = release_manifest(fingerprints["fingerprint"], fingerprints["policyFingerprint"])

        write_json(
            candidate / "candidate-provenance.json",
            {
                "schemaVersion": 1,
                "sourceCommitSha": manifest["releaseCommitSha"],
                "releaseVersion": manifest["releaseVersion"],
                "workflowRunId": str(manifest["candidateRunId"]),
                "workflowRunAttempt": str(manifest["candidateAttempt"]),
                "workflowRef": "kooiei-in4a/amane-mailer/.github/workflows/generate-setup-release-candidate.yml@refs/heads/release-prep/v1.3.0-rc13",
                "ociIndexDigest": manifest["ociIndexDigest"],
            },
        )
        write_json(
            candidate / "image-identity.json",
            {
                "sourceCommitSha": manifest["releaseCommitSha"],
                "mailerVersion": manifest["releaseVersion"],
                "imageDigest": manifest["ociIndexDigest"],
            },
        )

        release_artifact = root / "release-artifact"
        release_positive = root / "release-positive"
        shutil.copytree(PRODUCTION_QUALIFICATION, release_artifact)
        expect_pass(
            "shared production artifact preparation",
            run_preparer(release_artifact, release_positive),
        )
        expect_pass(
            "release mode valid fixture",
            run_validator(root, manifest, release_positive),
        )

        signatures_enabled_ruleset = copy.deepcopy(ruleset)
        signatures_enabled_ruleset["rules"].append({"type": "required_signatures"})
        write_json(root / "ruleset-signatures-enabled.json", signatures_enabled_ruleset)
        write_json(
            root / "effective-signatures-enabled.json",
            list(reversed(signatures_enabled_ruleset["rules"])),
        )
        signatures_enabled_output = root / "fingerprint-signatures-enabled.json"
        signatures_enabled_result = subprocess.run(
            [
                sys.executable,
                str(FINGERPRINTER),
                "--ruleset",
                str(root / "ruleset-signatures-enabled.json"),
                "--effective-rules",
                str(root / "effective-signatures-enabled.json"),
                "--output",
                str(signatures_enabled_output),
            ],
            check=False,
            capture_output=True,
            text=True,
        )
        expect_pass("signature-enabled fingerprint", signatures_enabled_result)
        signatures_enabled_fingerprints = json.loads(
            signatures_enabled_output.read_text(encoding="utf-8")
        )
        signatures_enabled_manifest = copy.deepcopy(manifest)
        signatures_enabled_manifest["rulesetFingerprint"] = signatures_enabled_fingerprints["fingerprint"]
        signatures_enabled_manifest["expectedRulesetFingerprint"] = signatures_enabled_fingerprints["fingerprint"]
        signatures_enabled_manifest["mainRulesetPolicyFingerprint"] = signatures_enabled_fingerprints[
            "policyFingerprint"
        ]
        signatures_enabled_manifest["targetRulesetPolicyFingerprint"] = signatures_enabled_fingerprints[
            "policyFingerprint"
        ]
        signatures_enabled_manifest["requiredSignatures"] = True
        expect_pass(
            "signature-enabled approved authority",
            run_validator(root, signatures_enabled_manifest, release_positive),
        )

        signature_rule_drift = copy.deepcopy(manifest)
        signature_rule_drift["requiredSignatures"] = True
        signature_rule_drift["rulesetFingerprint"] = signatures_enabled_fingerprints["fingerprint"]
        signature_rule_drift["targetRulesetPolicyFingerprint"] = signatures_enabled_fingerprints[
            "policyFingerprint"
        ]
        expect_fail(
            "signature rule drift without approved fingerprints",
            run_validator(root, signature_rule_drift, release_positive),
        )

        retired_rehearsal = copy.deepcopy(manifest)
        retired_rehearsal["mode"] = "rehearsal"
        retired_rehearsal["promotionPrBaseRef"] = "release-rehearsal/504-main-equivalent"
        retired_rehearsal["tagName"] = "rehearsal/issue-504/fixture"
        retired_rehearsal["sealedEventId"] = "4" * 64
        expect_fail("rehearsal mode retired", run_validator(root, retired_rehearsal, release_positive))

        for name, path in (
            ("product source historical delta", "src/Amane.Mailer/Program.cs"),
            ("migration historical delta", "migrations/999_bad.sql"),
            ("unexpected control-plane path", ".github/workflows/unexpected.yml"),
        ):
            bad_delta = copy.deepcopy(manifest)
            bad_delta["prePromotionMainDeltaPaths"] = [path]
            expect_fail(name, run_validator(root, bad_delta, release_positive))

        wrong_fork_base = copy.deepcopy(manifest)
        wrong_fork_base["rcForkBaseSha"] = OTHER_COMMIT
        expect_fail("wrong RC fork base SHA", run_validator(root, wrong_fork_base, release_positive))

        duplicate_delta = copy.deepcopy(manifest)
        duplicate_delta["prePromotionMainDeltaPaths"] = [
            ".github/workflows/promote-qualified-git.yml",
            ".github/workflows/promote-qualified-git.yml",
        ]
        expect_fail("duplicate historical delta path", run_validator(root, duplicate_delta, release_positive))

        global_json_mismatch = copy.deepcopy(manifest)
        global_json_mismatch["globalJsonMatchesRc13"] = False
        expect_fail("global.json mismatch", run_validator(root, global_json_mismatch, release_positive))

        promotion_base_drift = copy.deepcopy(manifest)
        promotion_base_drift["promotionBaseSha"] = COMMIT
        expect_fail("wrong promotionBaseSha consistency", run_validator(root, promotion_base_drift, release_positive))

        wrong_release_commit = copy.deepcopy(manifest)
        wrong_release_commit["releaseCommitSha"] = OTHER_COMMIT
        wrong_release_commit["promotionPrHeadSha"] = OTHER_COMMIT
        wrong_release_commit["rcTipSha"] = OTHER_COMMIT
        wrong_release_commit["tagTargetSha"] = OTHER_COMMIT
        expect_fail("wrong releaseCommitSha", run_validator(root, wrong_release_commit, release_positive))

        wrong_base_ref = copy.deepcopy(manifest)
        wrong_base_ref["promotionPrBaseRef"] = "release-rehearsal/504-main-equivalent"
        expect_fail("wrong promotion base / identity", run_validator(root, wrong_base_ref, release_positive))

        release_wrong_event = copy.deepcopy(manifest)
        release_wrong_event["sealedEventId"] = "8" * 32
        expect_fail(
            "release sealed event ID mismatch",
            run_validator(root, release_wrong_event, release_positive),
        )

        release_wrong_binding = root / "release-wrong-binding"
        shutil.copytree(release_positive, release_wrong_binding)
        wrong_binding_id = "8" * 64
        for relative in (
            "binding.json",
            "decision/go-no-go.json",
            f"run-status-events/{RELEASE_EVENT_ID}.json",
        ):
            document = load_json(release_wrong_binding / relative)
            document["bindingId"] = wrong_binding_id
            if relative.startswith("run-status-events/"):
                refresh_event_digest(document)
            write_json(release_wrong_binding / relative, document)
        wrong_binding_manifest = load_json(release_wrong_binding / "handoff-manifest.json")
        wrong_binding_manifest["bindingId"] = wrong_binding_id
        write_json(release_wrong_binding / "handoff-manifest.json", wrong_binding_manifest)
        refresh_manifest_digests(release_wrong_binding)
        expect_fail(
            "release binding ID mismatch",
            run_validator(root, manifest, release_wrong_binding),
        )

        release_wrong_version = root / "release-wrong-version"
        shutil.copytree(release_positive, release_wrong_version)
        wrong_version_binding = load_json(release_wrong_version / "binding.json")
        wrong_version_binding["releaseVersion"] = "9.9.9"
        write_json(release_wrong_version / "binding.json", wrong_version_binding)
        refresh_manifest_digests(release_wrong_version)
        expect_fail(
            "release version mismatch",
            run_validator(root, manifest, release_wrong_version),
        )

        release_wrong_candidate_run = root / "release-wrong-candidate-run"
        shutil.copytree(release_positive, release_wrong_candidate_run)
        wrong_run_binding = load_json(release_wrong_candidate_run / "binding.json")
        wrong_run_binding["producerWorkflowRunId"] = str(int(manifest["candidateRunId"]) + 1)
        write_json(release_wrong_candidate_run / "binding.json", wrong_run_binding)
        refresh_manifest_digests(release_wrong_candidate_run)
        expect_fail(
            "release candidate producer run ID mismatch",
            run_validator(root, manifest, release_wrong_candidate_run),
        )

        release_wrong_candidate_attempt = root / "release-wrong-candidate-attempt"
        shutil.copytree(release_positive, release_wrong_candidate_attempt)
        wrong_attempt_binding = load_json(release_wrong_candidate_attempt / "binding.json")
        wrong_attempt_binding["producerWorkflowRunAttempt"] = str(int(manifest["candidateAttempt"]) + 1)
        write_json(release_wrong_candidate_attempt / "binding.json", wrong_attempt_binding)
        refresh_manifest_digests(release_wrong_candidate_attempt)
        expect_fail(
            "release candidate producer run attempt mismatch",
            run_validator(root, manifest, release_wrong_candidate_attempt),
        )

        no_go = copy.deepcopy(manifest)
        no_go["machineVerdict"] = "NO_GO"
        expect_fail("N1 qualification not approved", run_validator(root, no_go, release_positive))

        head_mismatch = copy.deepcopy(manifest)
        head_mismatch["promotionPrHeadSha"] = OTHER_COMMIT
        expect_fail("N2 head SHA mismatch", run_validator(root, head_mismatch, release_positive))

        malformed_signatures = copy.deepcopy(manifest)
        malformed_signatures["requiredSignatures"] = "false"
        expect_fail("N3 signature state must be boolean", run_validator(root, malformed_signatures, release_positive))

        rc_drift = copy.deepcopy(manifest)
        rc_drift["rcTipSha"] = OTHER_COMMIT
        expect_fail("N4 RC tip drift", run_validator(root, rc_drift, release_positive))

        qualification_mismatch = copy.deepcopy(manifest)
        qualification_mismatch["qualificationRunId"] = "5" * 64
        expect_fail("N5 qualificationRunId mismatch", run_validator(root, qualification_mismatch, release_positive))

        ruleset_mismatch = copy.deepcopy(manifest)
        ruleset_mismatch["expectedRulesetFingerprint"] = "6" * 64
        expect_fail("N6 ruleset fingerprint mismatch", run_validator(root, ruleset_mismatch, release_positive))

        policy_mismatch = copy.deepcopy(manifest)
        policy_mismatch["targetRulesetPolicyFingerprint"] = "9" * 64
        expect_fail("N6 policy fingerprint mismatch", run_validator(root, policy_mismatch, release_positive))

        candidate_mismatch = copy.deepcopy(manifest)
        candidate_mismatch["candidateId"] = "7" * 64
        expect_fail("N7 candidateId mismatch", run_validator(root, candidate_mismatch, release_positive))

        wrong_producer_branch = copy.deepcopy(manifest)
        wrong_producer_branch["qualificationProducerHeadBranch"] = "qualification-handoff/v1.3.0"
        expect_fail("wrong qualification identity", run_validator(root, wrong_producer_branch, release_positive))

        # Keep the original N8 label for the same handoff-branch contract.
        handoff_branch_mismatch = copy.deepcopy(manifest)
        handoff_branch_mismatch["qualificationProducerHeadBranch"] = "main"
        expect_fail("N8 qualification handoff branch mismatch", run_validator(root, handoff_branch_mismatch, release_positive))

        candidate_provenance_mismatch = copy.deepcopy(manifest)
        write_json(
            candidate / "candidate-provenance.json",
            {
                "schemaVersion": 1,
                "sourceCommitSha": manifest["releaseCommitSha"],
                "releaseVersion": manifest["releaseVersion"],
                "workflowRunId": "999999",
                "workflowRunAttempt": str(manifest["candidateAttempt"]),
                "workflowRef": "kooiei-in4a/amane-mailer/.github/workflows/generate-setup-release-candidate.yml@refs/heads/release-prep/v1.3.0-rc13",
                "ociIndexDigest": manifest["ociIndexDigest"],
            },
        )
        expect_fail(
            "N9 candidate producer provenance mismatch",
            run_validator(root, candidate_provenance_mismatch, release_positive),
        )
        write_json(
            candidate / "candidate-provenance.json",
            {
                "schemaVersion": 1,
                "sourceCommitSha": manifest["releaseCommitSha"],
                "releaseVersion": manifest["releaseVersion"],
                "workflowRunId": str(manifest["candidateRunId"]),
                "workflowRunAttempt": str(manifest["candidateAttempt"]),
                "workflowRef": "kooiei-in4a/amane-mailer/.github/workflows/generate-setup-release-candidate.yml@refs/heads/release-prep/v1.3.0-rc13",
                "ociIndexDigest": manifest["ociIndexDigest"],
            },
        )

        invalid_branch = copy.deepcopy(manifest)
        invalid_branch["releaseBranch"] = "release/v1.3.0-rc0"
        invalid_branch["promotionPrHeadRef"] = invalid_branch["releaseBranch"]
        expect_fail("N10 invalid RC branch suffix", run_validator(root, invalid_branch, release_positive))

        invalid_namespace = copy.deepcopy(manifest)
        invalid_namespace["releaseBranch"] = "release-candidate/v1.3.0-rc2"
        invalid_namespace["promotionPrHeadRef"] = invalid_namespace["releaseBranch"]
        expect_fail("N11 invalid release branch namespace", run_validator(root, invalid_namespace, release_positive))

        sealed_event_mismatch = copy.deepcopy(manifest)
        sealed_event_mismatch["sealedEventId"] = "8" * 32
        expect_fail("N12 sealedEventId mismatch", run_validator(root, sealed_event_mismatch, release_positive))

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
    print("promoteWorkflowHeredocTerminators=PASS")
    print("releaseModePositive=PASS")
    print("productionShapePositive=PASS")
    print("productionNegativeFixtures=PASS")
    print("rehearsalModeRetired=PASS")
    print("negativeQualificationFixture=PASS")
    print("negativeHeadMismatchFixture=PASS")
    print("negativeSignatureFixture=PASS")
    print("signatureStateBooleanCompatibility=PASS")
    print("signaturePolicyDriftFixture=PASS")
    print("additionalNegativeFixtures=PASS")
    print("finalResult=PASS")


if __name__ == "__main__":
    main()
