#!/usr/bin/env python3
"""Git promotion regressions using the shared Issue #622 production fixture."""

from __future__ import annotations

import copy
import hashlib
import json
import shutil
import subprocess
import sys
import tempfile
from pathlib import Path


SCRIPT_DIR = Path(__file__).resolve().parent
VALIDATOR = SCRIPT_DIR / "validate-qualified-git-promotion.py"
PREPARER = SCRIPT_DIR / "prepare-qualification-handoff.py"
FINGERPRINTER = SCRIPT_DIR / "ruleset-fingerprint.py"
QUALIFICATION_FIXTURE_ROOT = SCRIPT_DIR / "fixtures/qualification-handoff/production-shape"
PRODUCTION_QUALIFICATION = QUALIFICATION_FIXTURE_ROOT / "artifact"
EXPECTED_PRODUCER = QUALIFICATION_FIXTURE_ROOT / "expected-producer-identity.json"
COMMIT = "0123456789abcdef0123456789abcdef01234567"
OTHER_COMMIT = "89abcdef0123456789abcdef0123456789abcdef"
RC13_FORK_BASE_SHA = "d6743dabc1813ea428081a49874680263ae54f7f"
OCI_DIGEST = "sha256:" + "a" * 64
RELEASE_EVENT_ID = "4" * 32
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
    "qualificationProducerHeadBranch": "qualification-handoff/v1.3.0-rc2",
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


def base_manifest(fingerprint: str, policy_fingerprint: str) -> dict[str, object]:
    return {
        "schemaVersion": 1,
        "mode": "rehearsal",
        "releaseVersion": "1.3.0",
        "releaseCommitSha": COMMIT,
        "releaseBranch": "release/v1.3.0-rc2",
        **IDS,
        "machineVerdict": "GO_ELIGIBLE",
        "humanDecision": "APPROVE",
        "qualificationApprovalScope": "exact-candidate-qualification",
        "promotionPrNumber": 5040,
        "promotionPrHeadSha": COMMIT,
        "promotionPrHeadRef": "release/v1.3.0-rc2",
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
            "headBranch": "qualification-handoff/v1.3.0-rc2",
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
            "workflowRef": "kooiei-in4a/amane-mailer/.github/workflows/generate-setup-release-candidate.yml@refs/heads/release/v1.3.0-rc2",
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

        production_binding = load_json(PRODUCTION_QUALIFICATION / "binding.json")
        production_handoff = load_json(PRODUCTION_QUALIFICATION / "handoff-manifest.json")
        production_producer = load_json(PRODUCTION_QUALIFICATION / "qualification-producer.json")
        release_prep = copy.deepcopy(manifest)
        release_prep.update(
            {
                "mode": "release",
                "releaseVersion": production_binding["releaseVersion"],
                "releaseCommitSha": production_binding["releaseCommitSha"],
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
            }
        )
        release_prep["releaseBranch"] = "release-prep/v1.3.0-rc13"
        release_prep["promotionPrHeadRef"] = release_prep["releaseBranch"]
        release_prep["promotionPrHeadSha"] = release_prep["releaseCommitSha"]
        release_prep["promotionPrBaseRef"] = "main"
        release_prep["promotionPrBaseSha"] = OTHER_COMMIT
        release_prep["promotionBaseSha"] = OTHER_COMMIT
        release_prep["baseRefTipSha"] = OTHER_COMMIT
        release_prep["rcTipSha"] = release_prep["releaseCommitSha"]
        release_prep["tagName"] = "v1.3.0"
        release_prep["tagTargetSha"] = release_prep["releaseCommitSha"]
        release_prep["expectedRcForkBaseSha"] = RC13_FORK_BASE_SHA
        release_prep["rcForkBaseSha"] = RC13_FORK_BASE_SHA
        release_prep["prePromotionMainDeltaPaths"] = [
            ".github/workflows/promote-qualified-git.yml",
            ".github/workflows/publish-sealed-qualification-handoff.yml",
            "global.json",
            "scripts/validate-qualified-git-promotion-self-test.py",
            "scripts/validate-qualified-git-promotion.py",
        ]
        release_prep["prePromotionMainDeltaPolicy"] = "RELEASE_CONTROL_PLANE_ONLY"
        release_prep["globalJsonMatchesRc13"] = True
        release_prep_provenance = {
            **candidate_provenance,
            "sourceCommitSha": release_prep["releaseCommitSha"],
            "releaseVersion": release_prep["releaseVersion"],
            "workflowRunId": str(release_prep["candidateRunId"]),
            "workflowRunAttempt": str(release_prep["candidateAttempt"]),
            "ociIndexDigest": release_prep["ociIndexDigest"],
            "workflowRef": "kooiei-in4a/amane-mailer/.github/workflows/generate-setup-release-candidate.yml@refs/heads/release-prep/v1.3.0-rc13",
        }
        write_json(candidate / "candidate-provenance.json", release_prep_provenance)
        write_json(
            candidate / "image-identity.json",
            {
                "sourceCommitSha": release_prep["releaseCommitSha"],
                "mailerVersion": release_prep["releaseVersion"],
                "imageDigest": release_prep["ociIndexDigest"],
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
            "production-shaped sealed-only release handoff",
            run_validator(root, release_prep, release_positive),
        )

        for name, path in (
            ("product source historical delta", "src/Amane.Mailer/Program.cs"),
            ("migration historical delta", "migrations/999_bad.sql"),
            ("unexpected control-plane path", ".github/workflows/unexpected.yml"),
        ):
            bad_delta = copy.deepcopy(release_prep)
            bad_delta["prePromotionMainDeltaPaths"] = [path]
            expect_fail(name, run_validator(root, bad_delta, release_positive))

        wrong_fork_base = copy.deepcopy(release_prep)
        wrong_fork_base["rcForkBaseSha"] = OTHER_COMMIT
        expect_fail("wrong RC fork base SHA", run_validator(root, wrong_fork_base, release_positive))

        duplicate_delta = copy.deepcopy(release_prep)
        duplicate_delta["prePromotionMainDeltaPaths"] = [
            ".github/workflows/promote-qualified-git.yml",
            ".github/workflows/promote-qualified-git.yml",
        ]
        expect_fail("duplicate historical delta path", run_validator(root, duplicate_delta, release_positive))

        global_json_mismatch = copy.deepcopy(release_prep)
        global_json_mismatch["globalJsonMatchesRc13"] = False
        expect_fail("global.json mismatch", run_validator(root, global_json_mismatch, release_positive))

        promotion_base_drift = copy.deepcopy(release_prep)
        promotion_base_drift["promotionBaseSha"] = COMMIT
        expect_fail("promotion base consistency drift", run_validator(root, promotion_base_drift, release_positive))

        release_bad_digest = root / "release-bad-digest"
        shutil.copytree(release_positive, release_bad_digest)
        bad_digest_manifest = load_json(release_bad_digest / "handoff-manifest.json")
        bad_digest_manifest["objects"][0]["sha256"] = "f" * 64
        write_json(release_bad_digest / "handoff-manifest.json", bad_digest_manifest)
        expect_fail(
            "release manifest object digest tamper",
            run_validator(root, release_prep, release_bad_digest),
        )

        release_wrong_event = root / "release-wrong-event"
        shutil.copytree(release_positive, release_wrong_event)
        wrong_event_manifest = load_json(release_wrong_event / "handoff-manifest.json")
        wrong_event_manifest["sealedEventId"] = "8" * 32
        write_json(release_wrong_event / "handoff-manifest.json", wrong_event_manifest)
        expect_fail(
            "release sealed event ID mismatch",
            run_validator(root, release_prep, release_wrong_event),
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
            run_validator(root, release_prep, release_wrong_binding),
        )

        release_wrong_candidate_run = root / "release-wrong-candidate-run"
        shutil.copytree(release_positive, release_wrong_candidate_run)
        wrong_run_binding = load_json(release_wrong_candidate_run / "binding.json")
        wrong_run_binding["producerWorkflowRunId"] = str(int(release_prep["candidateRunId"]) + 1)
        write_json(release_wrong_candidate_run / "binding.json", wrong_run_binding)
        refresh_manifest_digests(release_wrong_candidate_run)
        expect_fail(
            "release candidate producer run ID mismatch",
            run_validator(root, release_prep, release_wrong_candidate_run),
        )

        release_wrong_source = root / "release-wrong-source"
        shutil.copytree(release_positive, release_wrong_source)
        wrong_source_binding = load_json(release_wrong_source / "binding.json")
        wrong_source_binding["sourceCommitSha"] = OTHER_COMMIT
        write_json(release_wrong_source / "binding.json", wrong_source_binding)
        refresh_manifest_digests(release_wrong_source)
        expect_fail(
            "release source commit mismatch",
            run_validator(root, release_prep, release_wrong_source),
        )

        release_extra_file = root / "release-extra-file"
        shutil.copytree(release_positive, release_extra_file)
        write_json(release_extra_file / "unexpected.json", {"unexpected": True})
        expect_fail(
            "release unexpected extra sealed file",
            run_validator(root, release_prep, release_extra_file),
        )

        write_json(candidate / "candidate-provenance.json", candidate_provenance)
        write_json(candidate / "image-identity.json", image_identity)

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

        handoff_branch_mismatch = copy.deepcopy(manifest)
        handoff_branch_mismatch["mode"] = "release"
        handoff_branch_mismatch["promotionPrBaseRef"] = "main"
        handoff_branch_mismatch["tagName"] = "v1.3.0"
        handoff_branch_mismatch["qualificationProducerHeadBranch"] = "qualification-handoff/v1.3.0"
        expect_fail("N8 qualification handoff branch mismatch", run_validator(root, handoff_branch_mismatch))

        candidate_provenance_mismatch = copy.deepcopy(manifest)
        write_json(candidate / "candidate-provenance.json", {**candidate_provenance, "workflowRunId": "999999"})
        expect_fail("N9 candidate producer provenance mismatch", run_validator(root, candidate_provenance_mismatch))
        write_json(candidate / "candidate-provenance.json", candidate_provenance)

        invalid_branch = copy.deepcopy(manifest)
        invalid_branch["releaseBranch"] = "release/v1.3.0-rc0"
        invalid_branch["promotionPrHeadRef"] = invalid_branch["releaseBranch"]
        expect_fail("N10 invalid RC branch suffix", run_validator(root, invalid_branch))

        invalid_namespace = copy.deepcopy(manifest)
        invalid_namespace["releaseBranch"] = "release-candidate/v1.3.0-rc2"
        invalid_namespace["promotionPrHeadRef"] = invalid_namespace["releaseBranch"]
        expect_fail("N11 invalid release branch namespace", run_validator(root, invalid_namespace))

        sealed_event_mismatch = copy.deepcopy(manifest)
        sealed_event_mismatch["sealedEventId"] = "8" * 64
        expect_fail("N12 sealedEventId mismatch", run_validator(root, sealed_event_mismatch))

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
    print("productionShapePositive=PASS")
    print("productionNegativeFixtures=PASS")
    print("releasePrepCompatibility=PASS")
    print("negativeQualificationFixture=PASS")
    print("negativeHeadMismatchFixture=PASS")
    print("negativeSignatureFixture=PASS")
    print("sealedHandoffCompatibility=PASS")
    print("additionalNegativeFixtures=PASS")
    print("finalResult=PASS")


if __name__ == "__main__":
    main()

