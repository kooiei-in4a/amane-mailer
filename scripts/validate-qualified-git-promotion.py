#!/usr/bin/env python3
"""Fail-closed preflight for exact-RC PR merge and annotated-tag promotion."""

from __future__ import annotations

import argparse
import hashlib
import json
import re
import sys
from pathlib import Path
from typing import Any, NoReturn


HEX40 = re.compile(r"^[0-9a-f]{40}$")
HEX64 = re.compile(r"^[0-9a-f]{64}$")
HEX32 = re.compile(r"^[0-9a-f]{32}$")
VERSION = re.compile(r"^[0-9]+\.[0-9]+\.[0-9]+$")
RELEASE_BRANCH = re.compile(r"^(?:release|release-prep)/v[0-9]+\.[0-9]+\.[0-9]+-rc(?:[1-9][0-9]*)?$")
SHA256_DIGEST = re.compile(r"^sha256:[0-9a-f]{64}$")
CANDIDATE_WORKFLOW_PATH = ".github/workflows/generate-setup-release-candidate.yml"
CANDIDATE_WORKFLOW_ID = 324880172
CANDIDATE_WORKFLOW_EVENT = "workflow_dispatch"
CANDIDATE_REPOSITORY = "kooiei-in4a/amane-mailer"
RC13_SOURCE_SHA = "c5a928eafe0e0f3527ad484993347d5035aa92bc"
RC13_FORK_BASE_SHA = "d6743dabc1813ea428081a49874680263ae54f7f"
RC13_PROMOTION_BASE_SHA = "f3606f7b69c629473789f7df101cbd945f614cb9"
RELEASE_CONTROL_PLANE_ONLY_PATHS = frozenset(
    {
        ".github/workflows/promote-qualified-git.yml",
        ".github/workflows/publish-sealed-qualification-handoff.yml",
        "scripts/validate-qualified-git-promotion.py",
        "scripts/validate-qualified-git-promotion-self-test.py",
        "docs/ops/qualified-git-promotion.md",
        "global.json",
    }
)


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
    if isinstance(actual, bool):
        fail(field, "must be an integer")
    if isinstance(actual, int):
        value = actual
    elif isinstance(actual, str) and re.fullmatch(r"[1-9][0-9]*", actual):
        value = int(actual)
    else:
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
    require_int_equal("qualificationProducer.runId", producer["runId"], promotion["qualificationProducerRunId"])
    require_int_equal("qualificationProducer.runAttempt", producer["runAttempt"], promotion["qualificationWorkflowRunAttempt"])
    require_equal("qualificationProducer.repository", producer["repository"], promotion["qualificationProducerRepository"])
    require_equal("qualificationProducer.workflowPath", producer["workflowPath"], promotion["qualificationProducerWorkflowPath"])
    require_int_equal("qualificationProducer.workflowId", producer["workflowId"], promotion["qualificationProducerWorkflowId"])
    require_equal("qualificationProducer.event", producer["event"], promotion["qualificationProducerEvent"])
    require_equal("qualificationProducer.headBranch", producer["headBranch"], promotion["qualificationProducerHeadBranch"])
    require_equal("qualificationProducer.headSha", producer["headSha"], promotion["qualificationProducerHeadSha"])


def validate_rehearsal_qualification(root: Path, promotion: dict[str, Any]) -> None:
    """Preserve the synthetic Issue #504 rehearsal fixture contract."""
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


def validate_release_qualification(root: Path, promotion: dict[str, Any]) -> None:
    """Validate the publication-only production handoff without mutating evidence."""
    manifest_path = root / "handoff-manifest.json"
    producer_path = root / "qualification-producer.json"
    manifest = load_json(manifest_path, "handoff-manifest.json")
    producer = load_json(producer_path, "qualification-producer.json")
    if not isinstance(manifest, dict):
        fail("handoff-manifest.json", "document must be an object")

    require_equal("handoff-manifest.schemaVersion", manifest.get("schemaVersion"), 1)
    require_equal("handoff-manifest.publicationOnly", manifest.get("publicationOnly"), True)
    for field in ("candidateId", "bindingId", "qualificationRunId", "sealedEventId"):
        require_equal(f"handoff-manifest.{field}", manifest.get(field), promotion[field])

    object_entries = manifest.get("objects")
    if not isinstance(object_entries, list) or len(object_entries) != 3:
        fail("handoff-manifest.objects", "exactly three sealed objects are required")
    object_map: dict[str, str] = {}
    for entry in object_entries:
        if not isinstance(entry, dict) or set(entry) != {"path", "sha256"}:
            fail("handoff-manifest.objects", "entries must contain only path and sha256")
        object_path = entry.get("path")
        digest = entry.get("sha256")
        if (
            not isinstance(object_path, str)
            or not object_path
            or Path(object_path).is_absolute()
            or ".." in Path(object_path).parts
            or "\\" in object_path
        ):
            fail("handoff-manifest.objects.path", "unsafe path")
        if object_path in object_map or not isinstance(digest, str) or not HEX64.fullmatch(digest):
            fail("handoff-manifest.objects.sha256", "invalid or duplicate entry")
        object_map[object_path] = digest

    event_paths = list(root.glob("run-status-events/*.json"))
    event_path = exactly_one(event_paths, "run-status-events")
    event_relative = event_path.relative_to(root).as_posix()
    expected_objects = {
        "binding.json",
        "decision/go-no-go.json",
        event_relative,
    }
    require_equal("handoff-manifest.objects", set(object_map), expected_objects)

    all_paths: set[str] = set()
    for path in root.rglob("*"):
        if path.is_symlink():
            fail("qualificationRoot", "symlink entries are forbidden")
        if path.is_file():
            all_paths.add(path.relative_to(root).as_posix())
    require_equal(
        "qualificationRoot.files",
        all_paths,
        expected_objects | {"handoff-manifest.json", "qualification-producer.json"},
    )
    for object_path, expected_digest in object_map.items():
        actual_digest = hashlib.sha256((root / object_path).read_bytes()).hexdigest()
        require_equal(f"handoff-manifest.objects.{object_path}", actual_digest, expected_digest)

    binding = load_json(root / "binding.json", "binding.json")
    decision = load_json(root / "decision/go-no-go.json", "decision/go-no-go.json")
    event = load_json(event_path, "run-status-event")
    if not all(isinstance(item, dict) for item in (binding, decision, event)):
        fail("qualification", "sealed documents must be objects")

    for field in ("candidateId", "bindingId", "qualificationRunId"):
        expected = promotion[field]
        for prefix, document in (("binding", binding), ("decision", decision), ("event", event)):
            require_equal(f"{prefix}.{field}", document.get(field), expected)

    commit = promotion["releaseCommitSha"]
    require_equal("binding.releaseCommitSha", binding.get("releaseCommitSha"), commit)
    require_equal("binding.sourceCommitSha", binding.get("sourceCommitSha"), commit)
    require_equal("binding.releaseVersion", binding.get("releaseVersion"), promotion["releaseVersion"])
    require_equal("binding.ociIndexDigest", binding.get("ociIndexDigest"), promotion["ociIndexDigest"])
    require_int_equal("binding.producerWorkflowRunId", binding.get("producerWorkflowRunId"), promotion["candidateRunId"])
    require_int_equal(
        "binding.producerWorkflowRunAttempt",
        binding.get("producerWorkflowRunAttempt"),
        promotion["candidateAttempt"],
    )

    require_equal("decision.sourceCommitSha", decision.get("sourceCommitSha"), commit)
    require_equal("decision.ociIndexDigest", decision.get("ociIndexDigest"), promotion["ociIndexDigest"])
    require_equal("decision.machineVerdict", decision.get("machineVerdict"), "GO_ELIGIBLE")
    require_equal("decision.humanDecision", decision.get("humanDecision"), "APPROVE")
    require_equal("decision.runSealed", decision.get("runSealed"), True)

    require_equal("run-status-event.eventId", event.get("eventId"), promotion["sealedEventId"])
    require_equal("run-status-event.filename", event_path.stem, event.get("eventId"))
    require_equal("handoff-manifest.sealedEventId", manifest.get("sealedEventId"), event.get("eventId"))
    require_equal("run-status-event.status", event.get("status"), "sealed")
    if event.get("runStatusEventSequence") not in (1, "1"):
        fail("run-status-event.runStatusEventSequence", "must be 1")
    require_equal(
        "run-status-event.canonicalization",
        event.get("canonicalization"),
        {"algorithm": "RFC8785-JCS", "version": 1},
    )
    require_equal(
        "run-status-event.previousRunStatusEventDigestSha256",
        event.get("previousRunStatusEventDigestSha256"),
        None,
    )
    event_digest = event.get("eventDigestSha256")
    unsigned_event = {key: value for key, value in event.items() if key != "eventDigestSha256"}
    calculated_event_digest = hashlib.sha256(
        json.dumps(unsigned_event, ensure_ascii=False, sort_keys=True, separators=(",", ":")).encode("utf-8")
    ).hexdigest()
    if not isinstance(event_digest, str) or not HEX64.fullmatch(event_digest):
        fail("run-status-event.eventDigestSha256", "invalid format")
    require_equal("run-status-event.eventDigestSha256", calculated_event_digest, event_digest)

    decision_digests = event.get("decisionDigests")
    if (
        not isinstance(decision_digests, dict)
        or set(decision_digests) != {"evidenceIndexSha256", "goNoGoSha256", "phase4ManifestSha256"}
        or any(not isinstance(value, str) or not HEX64.fullmatch(value) for value in decision_digests.values())
    ):
        fail("run-status-event.decisionDigests", "invalid digest set")
    require_equal(
        "decision.authorizationDigestSha256",
        decision.get("authorizationDigestSha256"),
        binding.get("authorizationDigestSha256"),
    )
    issue_check = decision.get("issueFreshnessCheck") or {}
    if issue_check and issue_check.get("matchedBinding") is not True:
        fail("decision.issueFreshnessCheck.matchedBinding", "must be true")

    validate_qualification_producer(producer, promotion)


def validate_qualification(root: Path, promotion: dict[str, Any]) -> None:
    if not root.is_dir():
        fail("qualificationRoot", "directory is missing")
    if promotion["mode"] == "release":
        validate_release_qualification(root, promotion)
    else:
        validate_rehearsal_qualification(root, promotion)


def validate_pre_promotion_main_delta(promotion: dict[str, Any]) -> None:
    """Allow only the machine-verified RC13 release-control-plane delta."""
    if promotion["mode"] != "release":
        return

    expected_base = require_string(promotion, "expectedRcForkBaseSha", HEX40)
    actual_base = require_string(promotion, "rcForkBaseSha", HEX40)
    if promotion["releaseCommitSha"] == RC13_SOURCE_SHA:
        require_equal("expectedRcForkBaseSha", expected_base, RC13_FORK_BASE_SHA)
    require_equal("rcForkBaseSha", actual_base, expected_base)
    require_equal(
        "prePromotionMainDeltaPolicy",
        promotion.get("prePromotionMainDeltaPolicy"),
        "RELEASE_CONTROL_PLANE_ONLY",
    )

    paths = promotion.get("prePromotionMainDeltaPaths")
    if not isinstance(paths, list):
        fail("prePromotionMainDeltaPaths", "must be an array")
    seen: set[str] = set()
    for path in paths:
        if not isinstance(path, str) or not path:
            fail("prePromotionMainDeltaPaths", "contains an invalid path")
        pure = Path(path)
        if pure.is_absolute() or ".." in pure.parts or "\\" in path:
            fail("prePromotionMainDeltaPaths", "contains an unsafe path")
        if path in seen:
            fail("prePromotionMainDeltaPaths", "contains a duplicate path")
        seen.add(path)
        if path not in RELEASE_CONTROL_PLANE_ONLY_PATHS:
            fail("prePromotionMainDeltaPaths", "contains an unexpected path")

    require_equal("globalJsonMatchesRc13", promotion.get("globalJsonMatchesRc13"), True)


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
    release_ref = release_branch.split("/", 1)[1]
    require_equal(
        "releaseBranch.version",
        release_ref.split("-rc", 1)[0],
        f"v{version}",
    )
    if mode == "release":
        release_suffix = release_ref[len(f"v{version}") :]
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
    for field in ("candidateId", "bindingId", "qualificationRunId"):
        require_string(promotion, field, HEX64)
    require_string(promotion, "sealedEventId", HEX32 if mode == "release" else HEX64)

    require_equal("machineVerdict", promotion.get("machineVerdict"), "GO_ELIGIBLE")
    require_equal("humanDecision", promotion.get("humanDecision"), "APPROVE")
    require_equal("qualificationApprovalScope", promotion.get("qualificationApprovalScope"), "exact-candidate-qualification")
    require_equal("mergeFreezeConfirmation", promotion.get("mergeFreezeConfirmation"), "CONFIRM_TARGET_MERGE_FREEZE")
    require_equal("rcTipSha", promotion.get("rcTipSha"), commit)
    require_equal("promotionPrHeadSha", promotion.get("promotionPrHeadSha"), commit)
    require_equal("promotionPrHeadRef", promotion.get("promotionPrHeadRef"), promotion["releaseBranch"])
    promotion_base_sha = require_string(promotion, "promotionBaseSha", HEX40)
    require_equal("promotionPrBaseSha", promotion.get("promotionPrBaseSha"), promotion_base_sha)
    require_equal("baseRefTipSha", promotion.get("baseRefTipSha"), promotion_base_sha)
    if commit == RC13_SOURCE_SHA:
        require_equal("promotionBaseSha", promotion_base_sha, RC13_PROMOTION_BASE_SHA)
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
    validate_pre_promotion_main_delta(promotion)


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

