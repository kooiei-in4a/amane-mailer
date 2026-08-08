#!/usr/bin/env python3
"""Fail-closed preflight for exact-RC PR merge and annotated-tag promotion."""

from __future__ import annotations

import argparse
import json
import re
import sys
from pathlib import Path
from typing import Any, NoReturn


HEX40 = re.compile(r"^[0-9a-f]{40}$")
HEX64 = re.compile(r"^[0-9a-f]{64}$")
VERSION = re.compile(r"^[0-9]+\.[0-9]+\.[0-9]+$")
RELEASE_BRANCH = re.compile(r"^release/v[0-9]+\.[0-9]+\.[0-9]+-rc(?:[1-9][0-9]*)?$")
SHA256_DIGEST = re.compile(r"^sha256:[0-9a-f]{64}$")
CANDIDATE_WORKFLOW_PATH = ".github/workflows/generate-setup-release-candidate.yml"
CANDIDATE_WORKFLOW_ID = 324880172
CANDIDATE_WORKFLOW_EVENT = "workflow_dispatch"
CANDIDATE_REPOSITORY = "kooiei-in4a/amane-mailer"


def fail(field: str, message: str) -> NoReturn:
    print(f"[error] {field}: {message}", file=sys.stderr)
    raise SystemExit(1)


def load_json(path: Path, field: str) -> Any:
    try:
        return json.loads(path.read_text(encoding="utf-8"))
    except (OSError, json.JSONDecodeError):
        fail(field, "missing or invalid JSON")


def exactly_one(paths: list[Path], field: str) -> Path:
    unique = sorted(set(paths))
    if len(unique) != 1:
        fail(field, "exactly one file is required")
    return unique[0]


def require_string(obj: dict[str, Any], field: str, pattern: re.Pattern[str] | None = None) -> str:
    value = obj.get(field)
    if not isinstance(value, str) or not value:
        fail(field, "is required")
    if pattern and not pattern.fullmatch(value):
        fail(field, "invalid format")
    return value


def require_equal(field: str, actual: Any, expected: Any) -> None:
    if actual != expected:
        fail(field, "mismatch")


def source_sha(document: dict[str, Any], field: str) -> str:
    values = [document.get("releaseCommitSha"), document.get("sourceCommitSha")]
    values = [value for value in values if value is not None]
    if not values or any(not isinstance(value, str) for value in values):
        fail(field, "source commit is required")
    if len(set(values)) != 1:
        fail(field, "ambiguous source commit")
    return values[0]


def document_version(document: dict[str, Any]) -> str | None:
    values = [document.get("releaseVersion"), document.get("targetVersion")]
    values = [value for value in values if value is not None]
    if not values:
        return None
    if any(not isinstance(value, str) for value in values) or len(set(values)) != 1:
        fail("qualification.releaseVersion", "ambiguous or invalid")
    return values[0]


def require_int_equal(field: str, actual: Any, expected: int) -> None:
    try:
        value = int(actual)
    except (TypeError, ValueError):
        fail(field, "must be an integer")
    require_equal(field, value, expected)


def validate_candidate_provenance(root: Path, promotion: dict[str, Any]) -> None:
    """Validate the immutable #455 candidate handoff without rebuilding it."""
    if not root.is_dir():
        fail("candidateRoot", "directory is missing")
    provenance_path = exactly_one(list(root.rglob("candidate-provenance.json")), "candidate-provenance.json")
    image_path = exactly_one(list(root.rglob("image-identity.json")), "image-identity.json")
    provenance = load_json(provenance_path, "candidate-provenance.json")
    image = load_json(image_path, "image-identity.json")
    if not isinstance(provenance, dict) or not isinstance(image, dict):
        fail("candidateProvenance", "documents must be objects")

    require_equal("candidateProvenance.schemaVersion", provenance.get("schemaVersion"), 1)
    require_equal("candidateProvenance.sourceCommitSha", provenance.get("sourceCommitSha"), promotion["releaseCommitSha"])
    require_equal("candidateProvenance.releaseVersion", provenance.get("releaseVersion"), promotion["releaseVersion"])
    require_int_equal("candidateProvenance.workflowRunId", provenance.get("workflowRunId"), promotion["candidateRunId"])
    require_int_equal("candidateProvenance.workflowRunAttempt", provenance.get("workflowRunAttempt"), promotion["candidateAttempt"])
    expected_ref = (
        f"{CANDIDATE_REPOSITORY}/{CANDIDATE_WORKFLOW_PATH}"
        f"@refs/heads/{promotion['releaseBranch']}"
    )
    require_equal("candidateProvenance.workflowRef", provenance.get("workflowRef"), expected_ref)
    require_string(provenance, "ociIndexDigest", SHA256_DIGEST)
    require_equal("candidateProvenance.ociIndexDigest", provenance["ociIndexDigest"], promotion["ociIndexDigest"])

    require_equal("imageIdentity.sourceCommitSha", image.get("sourceCommitSha"), promotion["releaseCommitSha"])
    require_equal("imageIdentity.mailerVersion", image.get("mailerVersion"), promotion["releaseVersion"])
    require_equal("imageIdentity.imageDigest", image.get("imageDigest"), promotion["ociIndexDigest"])


def validate_qualification_producer(producer: dict[str, Any], promotion: dict[str, Any]) -> None:
    """Bind the sealed handoff to the exact trusted Actions producer identity."""
    if not isinstance(producer, dict):
        fail("qualificationProducer", "document must be an object")
    for field in (
        "repository",
        "workflowPath",
        "workflowId",
        "event",
        "headBranch",
        "headSha",
        "runId",
        "runAttempt",
    ):
        if field not in producer:
            fail(f"qualificationProducer.{field}", "is required")
    require_equal("qualificationProducer.runId", str(producer["runId"]), str(promotion["qualificationProducerRunId"]))
    require_equal("qualificationProducer.runAttempt", int(producer["runAttempt"]), promotion["qualificationWorkflowRunAttempt"])
    require_equal("qualificationProducer.repository", producer["repository"], promotion["qualificationProducerRepository"])
    require_equal("qualificationProducer.workflowPath", producer["workflowPath"], promotion["qualificationProducerWorkflowPath"])
    require_equal("qualificationProducer.workflowId", int(producer["workflowId"]), promotion["qualificationProducerWorkflowId"])
    require_equal("qualificationProducer.event", producer["event"], promotion["qualificationProducerEvent"])
    require_equal("qualificationProducer.headBranch", producer["headBranch"], promotion["qualificationProducerHeadBranch"])
    require_equal("qualificationProducer.headSha", producer["headSha"], promotion["qualificationProducerHeadSha"])


def validate_qualification(root: Path, promotion: dict[str, Any]) -> None:
    if not root.is_dir():
        fail("qualificationRoot", "directory is missing")
    binding_path = exactly_one(list(root.rglob("binding.json")), "binding.json")
    decision_path = exactly_one(list(root.rglob("go-no-go.json")), "decision/go-no-go.json")
    event_path = exactly_one(list(root.rglob("run-status-events/*.json")), "run-status-events")
    binding = load_json(binding_path, "binding.json")
    decision = load_json(decision_path, "decision/go-no-go.json")
    event = load_json(event_path, "run-status-event")
    if not all(isinstance(item, dict) for item in (binding, decision, event)):
        fail("qualification", "documents must be objects")
    producer_paths = list(root.rglob("qualification-producer.json"))
    if producer_paths:
        producer = load_json(exactly_one(producer_paths, "qualification-producer.json"), "qualification-producer.json")
        validate_qualification_producer(producer, promotion)

    identities = (
        ("candidateRunId", "candidateRunId"),
        ("candidateAttempt", "candidateAttempt"),
        ("candidateId", "candidateId"),
        ("bindingId", "bindingId"),
        ("qualificationRunId", "qualificationRunId"),
    )
    for manifest_field, document_field in identities:
        expected = promotion[manifest_field]
        for prefix, document in (("binding", binding), ("decision", decision), ("event", event)):
            require_equal(f"{prefix}.{document_field}", document.get(document_field), expected)

    commit = promotion["releaseCommitSha"]
    require_equal("binding.sourceCommitSha", source_sha(binding, "binding"), commit)
    require_equal("decision.sourceCommitSha", source_sha(decision, "decision"), commit)
    require_equal("event.sourceCommitSha", source_sha(event, "event"), commit)

    versions = [document_version(item) for item in (binding, decision, event)]
    versions = [value for value in versions if value is not None]
    if not versions:
        fail("qualification.releaseVersion", "is required")
    if any(value != promotion["releaseVersion"] for value in versions):
        fail("qualification.releaseVersion", "mismatch")

    require_equal("decision.machineVerdict", decision.get("machineVerdict"), "GO_ELIGIBLE")
    require_equal("decision.humanDecision", decision.get("humanDecision"), "APPROVE")
    require_equal("decision.runSealed", decision.get("runSealed"), True)
    require_equal("run-status-event.status", event.get("status"), "sealed")
    if event.get("runStatusEventSequence") not in (1, "1"):
        fail("run-status-event.runStatusEventSequence", "must be 1")
    sealed_event_id = event.get("sealedEventId", event.get("eventId"))
    require_equal("run-status-event.sealedEventId", sealed_event_id, promotion["sealedEventId"])


def validate_status_checks(promotion: dict[str, Any]) -> None:
    required = promotion.get("rulesetRequiredStatusChecks")
    observed = promotion.get("observedStatusChecks")
    if not isinstance(required, list) or not required:
        fail("rulesetRequiredStatusChecks", "must be a non-empty array")
    if not isinstance(observed, list):
        fail("observedStatusChecks", "must be an array")
    for check in required:
        if not isinstance(check, dict):
            fail("rulesetRequiredStatusChecks", "contains an invalid entry")
        context = check.get("context")
        integration_id = check.get("integration_id")
        matches = [
            item for item in observed
            if isinstance(item, dict)
            and item.get("context") == context
            and item.get("integration_id") == integration_id
        ]
        if not matches:
            fail("requiredStatusChecks", "missing required context")
        if not any(str(item.get("conclusion", "")).lower() == "success" for item in matches):
            fail("requiredStatusChecks", "required context did not pass")


def validate_manifest(promotion: dict[str, Any]) -> None:
    require_equal("schemaVersion", promotion.get("schemaVersion"), 1)
    mode = require_string(promotion, "mode")
    if mode not in ("rehearsal", "release"):
        fail("mode", "must be rehearsal or release")

    version = require_string(promotion, "releaseVersion", VERSION)
    commit = require_string(promotion, "releaseCommitSha", HEX40)
    release_branch = require_string(promotion, "releaseBranch", RELEASE_BRANCH)
    require_equal(
        "releaseBranch.version",
        release_branch.split("/", 1)[1].split("-rc", 1)[0],
        f"v{version}",
    )
    if mode == "release":
        release_suffix = release_branch[len(f"release/v{version}") :]
        expected_handoff_branch = f"qualification-handoff/v{version}"
        if release_suffix != "-rc":
            expected_handoff_branch += release_suffix
        require_equal(
            "qualificationProducerHeadBranch",
            promotion.get("qualificationProducerHeadBranch"),
            expected_handoff_branch,
        )
    candidate_run_id = promotion.get("candidateRunId")
    if not isinstance(candidate_run_id, int) or candidate_run_id <= 0:
        fail("candidateRunId", "must be a positive integer")
    candidate_attempt = promotion.get("candidateAttempt")
    if not isinstance(candidate_attempt, int) or candidate_attempt <= 0:
        fail("candidateAttempt", "must be a positive integer")
    for field in ("candidateId", "bindingId", "qualificationRunId", "sealedEventId"):
        require_string(promotion, field, HEX64)

    require_equal("machineVerdict", promotion.get("machineVerdict"), "GO_ELIGIBLE")
    require_equal("humanDecision", promotion.get("humanDecision"), "APPROVE")
    require_equal("qualificationApprovalScope", promotion.get("qualificationApprovalScope"), "exact-candidate-qualification")
    require_equal("mergeFreezeConfirmation", promotion.get("mergeFreezeConfirmation"), "CONFIRM_TARGET_MERGE_FREEZE")
    require_equal("rcTipSha", promotion.get("rcTipSha"), commit)
    require_equal("promotionPrHeadSha", promotion.get("promotionPrHeadSha"), commit)
    require_equal("promotionPrHeadRef", promotion.get("promotionPrHeadRef"), promotion["releaseBranch"])
    require_equal("promotionPrBaseSha", promotion.get("promotionPrBaseSha"), promotion.get("promotionBaseSha"))
    require_equal("baseRefTipSha", promotion.get("baseRefTipSha"), promotion.get("promotionBaseSha"))
    require_equal("tagTargetSha", promotion.get("tagTargetSha"), commit)

    if not isinstance(promotion.get("promotionPrNumber"), int) or promotion["promotionPrNumber"] <= 0:
        fail("promotionPrNumber", "must be a positive integer")
    require_equal("promotionPrState", promotion.get("promotionPrState"), "open")
    require_equal("promotionPrDraft", promotion.get("promotionPrDraft"), False)
    require_equal("promotionPrMergeable", promotion.get("promotionPrMergeable"), True)

    if mode == "rehearsal":
        require_equal(
            "promotionPrBaseRef",
            promotion.get("promotionPrBaseRef"),
            "release-rehearsal/504-main-equivalent",
        )
        if not re.fullmatch(r"rehearsal/issue-504/[A-Za-z0-9._-]+", str(promotion.get("tagName", ""))):
            fail("tagName", "must use the rehearsal namespace and safe characters")
    else:
        require_equal("promotionPrBaseRef", promotion.get("promotionPrBaseRef"), "main")
        require_equal("tagName", promotion.get("tagName"), "v" + version)

    require_string(promotion, "rulesetFingerprint", HEX64)
    require_string(promotion, "expectedRulesetFingerprint", HEX64)
    require_string(promotion, "mainRulesetPolicyFingerprint", HEX64)
    require_string(promotion, "targetRulesetPolicyFingerprint", HEX64)
    require_equal("rulesetFingerprint", promotion["rulesetFingerprint"], promotion["expectedRulesetFingerprint"])
    require_equal("rulesetPolicyFingerprint", promotion["targetRulesetPolicyFingerprint"], promotion["mainRulesetPolicyFingerprint"])
    require_equal("rulesetEnforcement", promotion.get("rulesetEnforcement"), "active")
    require_equal("requiredSignatures", promotion.get("requiredSignatures"), True)
    require_equal("normalActorBypass", promotion.get("normalActorBypass"), "never")

    require_string(promotion, "ociIndexDigest", SHA256_DIGEST)
    producer_fields = {
        "qualificationProducerRepository": (str, None),
        "qualificationProducerWorkflowPath": (str, None),
        "qualificationProducerEvent": (str, None),
        "qualificationProducerHeadBranch": (str, None),
        "qualificationProducerHeadSha": (str, HEX40),
    }
    for field, (kind, pattern) in producer_fields.items():
        value = promotion.get(field)
        if not isinstance(value, kind) or not value:
            fail(field, "is required")
        if pattern and not pattern.fullmatch(value):
            fail(field, "invalid format")
    for field in ("qualificationProducerRunId", "qualificationProducerWorkflowId", "qualificationWorkflowRunAttempt"):
        value = promotion.get(field)
        if not isinstance(value, int) or value <= 0:
            fail(field, "must be a positive integer")

    app_id = promotion.get("expectedReleaseAppId")
    if not isinstance(app_id, int) or app_id <= 0:
        fail("expectedReleaseAppId", "must be a positive integer")
    actors = promotion.get("rulesetBypassActors")
    expected_actor = {
        "actor_id": app_id,
        "actor_type": "Integration",
        "bypass_mode": "pull_request",
    }
    require_equal("rulesetBypassActors", actors, [expected_actor])

    require_equal("repositoryAllowMergeCommit", promotion.get("repositoryAllowMergeCommit"), True)
    require_equal("selectedMergeMethod", promotion.get("selectedMergeMethod"), "merge")
    allowed = promotion.get("rulesetAllowedMergeMethods")
    if not isinstance(allowed, list) or "merge" not in allowed:
        fail("rulesetAllowedMergeMethods", "merge is not allowed")
    validate_status_checks(promotion)


def main() -> None:
    parser = argparse.ArgumentParser()
    parser.add_argument("--manifest", required=True, type=Path)
    parser.add_argument("--qualification-root", required=True, type=Path)
    parser.add_argument("--candidate-root", required=True, type=Path)
    args = parser.parse_args()

    promotion = load_json(args.manifest, "promotionManifest")
    if not isinstance(promotion, dict):
        fail("promotionManifest", "must be an object")
    validate_manifest(promotion)
    validate_candidate_provenance(args.candidate_root, promotion)
    validate_qualification(args.qualification_root, promotion)
    print("[info] qualified Git promotion preflight passed")
    print("finalResult=PASS")


if __name__ == "__main__":
    main()
