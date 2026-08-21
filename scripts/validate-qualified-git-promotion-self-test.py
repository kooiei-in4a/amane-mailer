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
RC13_SOURCE_SHA = "c5a928eafe0e0f3527ad484993347d5035aa92bc"
RC13_FORK_BASE_SHA = "d6743dabc1813ea428081a49874680263ae54f7f"
OCI_DIGEST = "sha256:" + "a" * 64
RELEASE_EVENT_ID = "4" * 32
AUTHORIZATION_DIGEST = "b" * 64
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


def write_release_qualification(
    root: Path,
    manifest: dict[str, object],
    *,
    binding_id: str | None = None,
    candidate_run_id: int | None = None,
    event_id: str | None = None,
    source_commit_sha: str | None = None,
    corrupt_object_digest: bool = False,
    include_producer: bool = True,
    include_unexpected_file: bool = False,
) -> None:
    actual_binding_id = binding_id or str(manifest["bindingId"])
    actual_candidate_run_id = candidate_run_id or int(manifest["candidateRunId"])
    actual_event_id = event_id or str(manifest["sealedEventId"])
    actual_source_sha = source_commit_sha or str(manifest["releaseCommitSha"])
    identity = {
        "candidateId": manifest["candidateId"],
        "bindingId": actual_binding_id,
        "qualificationRunId": manifest["qualificationRunId"],
    }
    binding = {
        **identity,
        "authorizationDigestSha256": AUTHORIZATION_DIGEST,
        "releaseCommitSha": manifest["releaseCommitSha"],
        "sourceCommitSha": actual_source_sha,
        "releaseVersion": manifest["releaseVersion"],
        "ociIndexDigest": manifest["ociIndexDigest"],
        "producerWorkflowRunId": str(actual_candidate_run_id),
        "producerWorkflowRunAttempt": str(manifest["candidateAttempt"]),
    }
    decision = {
        **identity,
        "authorizationDigestSha256": AUTHORIZATION_DIGEST,
        "sourceCommitSha": manifest["releaseCommitSha"],
        "ociIndexDigest": manifest["ociIndexDigest"],
        "machineVerdict": "GO_ELIGIBLE",
        "humanDecision": "APPROVE",
        "runSealed": True,
    }
    event = {
        **identity,
        "eventId": actual_event_id,
        "status": "sealed",
        "runStatusEventSequence": 1,
        "canonicalization": {"algorithm": "RFC8785-JCS", "version": 1},
        "previousRunStatusEventDigestSha256": None,
        "decisionDigests": {
            "evidenceIndexSha256": "c" * 64,
            "goNoGoSha256": "d" * 64,
            "phase4ManifestSha256": "e" * 64,
        },
    }
    event["eventDigestSha256"] = hashlib.sha256(
        json.dumps(event, ensure_ascii=False, sort_keys=True, separators=(",", ":")).encode("utf-8")
    ).hexdigest()

    event_relative = f"run-status-events/{actual_event_id}.json"
    documents = {
        "binding.json": binding,
        "decision/go-no-go.json": decision,
        event_relative: event,
    }
    for relative, document in documents.items():
        write_json(root / relative, document)

    objects = [
        {"path": relative, "sha256": file_sha256(root / relative)}
        for relative in sorted(documents)
    ]
    if corrupt_object_digest:
        objects[0]["sha256"] = "f" * 64
    write_json(
        root / "handoff-manifest.json",
        {
            "schemaVersion": 1,
            "publicationOnly": True,
            "candidateId": manifest["candidateId"],
            "bindingId": actual_binding_id,
            "qualificationRunId": manifest["qualificationRunId"],
            "sealedEventId": actual_event_id,
            "objects": objects,
        },
    )
    if include_producer:
        write_json(
            root / "qualification-producer.json",
            {
                "repository": manifest["qualificationProducerRepository"],
                "workflowPath": manifest["qualificationProducerWorkflowPath"],
                "workflowId": manifest["qualificationProducerWorkflowId"],
                "event": manifest["qualificationProducerEvent"],
                "headBranch": manifest["qualificationProducerHeadBranch"],
                "headSha": manifest["qualificationProducerHeadSha"],
                "runId": manifest["qualificationProducerRunId"],
                "runAttempt": manifest["qualificationWorkflowRunAttempt"],
            },
        )
    if include_unexpected_file:
        write_json(root / "unexpected.json", {"unexpected": True})


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

        release_prep = copy.deepcopy(manifest)
        release_prep["mode"] = "release"
        release_prep["releaseCommitSha"] = RC13_SOURCE_SHA
        release_prep["releaseBranch"] = "release-prep/v1.3.0-rc13"
        release_prep["promotionPrHeadRef"] = release_prep["releaseBranch"]
        release_prep["promotionPrHeadSha"] = RC13_SOURCE_SHA
        release_prep["promotionPrBaseRef"] = "main"
        release_prep["promotionPrBaseSha"] = OTHER_COMMIT
        release_prep["promotionBaseSha"] = OTHER_COMMIT
        release_prep["baseRefTipSha"] = OTHER_COMMIT
        release_prep["rcTipSha"] = RC13_SOURCE_SHA
        release_prep["tagName"] = "v1.3.0"
        release_prep["tagTargetSha"] = RC13_SOURCE_SHA
        release_prep["sealedEventId"] = RELEASE_EVENT_ID
        release_prep["qualificationProducerHeadBranch"] = "qualification-handoff/v1.3.0-rc13"
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
        release_prep["qualificationProducerWorkflowPath"] = ".github/workflows/publish-sealed-qualification-handoff.yml"
        release_prep["qualificationProducerWorkflowId"] = 329865510
        release_prep_provenance = {
            **candidate_provenance,
            "sourceCommitSha": release_prep["releaseCommitSha"],
            "workflowRef": "kooiei-in4a/amane-mailer/.github/workflows/generate-setup-release-candidate.yml@refs/heads/release-prep/v1.3.0-rc2",
        }
        release_prep_provenance["workflowRef"] = "kooiei-in4a/amane-mailer/.github/workflows/generate-setup-release-candidate.yml@refs/heads/release-prep/v1.3.0-rc13"
        write_json(candidate / "candidate-provenance.json", release_prep_provenance)
        write_json(
            candidate / "image-identity.json",
            {
                "sourceCommitSha": release_prep["releaseCommitSha"],
                "mailerVersion": release_prep["releaseVersion"],
                "imageDigest": release_prep["ociIndexDigest"],
            },
        )
        release_positive = root / "release-positive"
        write_release_qualification(release_positive, release_prep)
        expect_pass(
            "production-shaped release handoff",
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
        write_release_qualification(release_bad_digest, release_prep, corrupt_object_digest=True)
        expect_fail(
            "release manifest object digest tamper",
            run_validator(root, release_prep, release_bad_digest),
        )

        release_wrong_event = root / "release-wrong-event"
        write_release_qualification(release_wrong_event, release_prep, event_id="8" * 32)
        expect_fail(
            "release sealed event ID mismatch",
            run_validator(root, release_prep, release_wrong_event),
        )

        release_wrong_binding = root / "release-wrong-binding"
        write_release_qualification(release_wrong_binding, release_prep, binding_id="8" * 64)
        expect_fail(
            "release binding ID mismatch",
            run_validator(root, release_prep, release_wrong_binding),
        )

        release_wrong_candidate_run = root / "release-wrong-candidate-run"
        write_release_qualification(
            release_wrong_candidate_run,
            release_prep,
            candidate_run_id=int(release_prep["candidateRunId"]) + 1,
        )
        expect_fail(
            "release candidate producer run ID mismatch",
            run_validator(root, release_prep, release_wrong_candidate_run),
        )

        release_wrong_source = root / "release-wrong-source"
        write_release_qualification(release_wrong_source, release_prep, source_commit_sha=OTHER_COMMIT)
        expect_fail(
            "release source commit mismatch",
            run_validator(root, release_prep, release_wrong_source),
        )

        release_extra_file = root / "release-extra-file"
        write_release_qualification(release_extra_file, release_prep, include_unexpected_file=True)
        expect_fail(
            "release unexpected extra sealed file",
            run_validator(root, release_prep, release_extra_file),
        )

        release_missing_producer = root / "release-missing-producer"
        write_release_qualification(release_missing_producer, release_prep, include_producer=False)
        expect_fail(
            "release missing qualification producer",
            run_validator(root, release_prep, release_missing_producer),
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

