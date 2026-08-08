#!/usr/bin/env python3
"""Append-only qualification store runner for Issue #580.

This tool implements the durable-store boundary from Issue #456.  It does not
run product tests, access ACS, or infer a qualification result from CI.  Lane
adapters write value-free evidence through ``evidence`` and the release lead
then performs ``seal``.  Every object is write-once; the sealed run-status
event is the only seal authority.

The store root is deliberately supplied by the maintainer at runtime.  It is
never a repository path and is never uploaded wholesale as a GitHub artifact.
Only the three publication-only handoff files are exported by ``handoff``.
"""

from __future__ import annotations

import argparse
import copy
import hashlib
import json
import os
import re
import shutil
import subprocess
import sys
import unicodedata
import uuid
from datetime import datetime, timezone
from pathlib import Path
from typing import Any, Iterable


HEX64 = re.compile(r"^[0-9a-f]{64}$")
EVENT32 = re.compile(r"^[0-9a-f]{32}$")
SHA40 = re.compile(r"^[0-9a-f]{40}$")
SHA256_DIGEST = re.compile(r"^sha256:[0-9a-f]{64}$")
SAFE_ID = re.compile(r"^[A-Za-z0-9][A-Za-z0-9._-]{0,127}$")
UTC_TIMESTAMP = re.compile(r"^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}Z$")
JCS_VERSION = {"algorithm": "RFC8785-JCS", "version": 1}
ROOT_DIGEST_ALGORITHM = "RFC8785-JCS-sorted-path-sha256/v1"
VARIANT_RULES_VERSION = 4

# Canonical #456 table binding.  Gate labels still come from the frozen Issue
# snapshot, but scenario identity/cardinality must agree with this map so a
# caller cannot omit or reclassify a required lane while binding a run.
CANONICAL_VARIANTS: dict[str, tuple[str, ...]] = {
    **{f"G456-{number:02d}": ("win-docker",) for number in (1,)},
    "G456-02": ("linux-docker",),
    "G456-03": ("acs-staging-nosend",),
    "G456-04": ("acs-staging-real",),
    "G456-05": ("acs-production",),
    "G456-06": ("acs-production-release-ov",),
    "G456-07": ("admin-local-dev",),
    "G456-08": ("admin-prod-https",),
    "G456-09": ("admin-prod-https",),
    "G456-10": ("admin-prod-https",),
    "G456-11": ("local-dev", "proxy-https"),
    "G456-12": ("admin-prod-https",),
    "G456-13": ("win-docker", "linux-docker"),
    "G456-14": ("win-docker", "linux-docker"),
    "G456-15": ("ci-auto",),
    "G456-16": ("ci-auto", "admin-integrated"),
    "G456-17": ("win-docker", "linux-docker"),
    "G456-18": ("win-docker", "linux-docker"),
    "G456-19": ("win-docker", "linux-docker"),
    "G456-20": ("ci-auto",),
    "G456-21": ("ci-auto",),
    "G456-22": ("ci-auto",),
    "G456-23": ("ci-auto",),
    "G456-24": ("ci-auto",),
    "G456-25": ("ci-auto",),
    "G456-26": ("win-docker", "linux-docker"),
    "G456-27": ("ci-auto",),
    "G456-28": ("ci-auto",),
    "G456-29": ("win-docker", "linux-docker"),
    "G456-30": ("ci-auto",),
    "G456-31": ("ci-auto",),
    "G456-32": ("ci-auto",),
    "G456-33": ("win-docker", "linux-docker"),
    "G456-34": ("linux-arm64",),
    "G456-35": ("linux-arm64",),
    "G456-36": ("vps",),
    "G456-37": ("win-docker", "linux-docker"),
    "G456-38": (),
    "G456-39": (),
    "G456-40": (),
    "G456-41": (),
    "G456-42": ("win-docker", "linux-docker"),
    "G456-43": ("win-docker", "linux-docker"),
    "G456-44": ("ci-auto",),
}
CANONICAL_GATES: dict[str, str] = {
    **{f"G456-{number:02d}": "Hard" for number in range(1, 29)},
    "G456-29": "Conditional",
    **{f"G456-{number:02d}": "Hard" for number in range(30, 34)},
    "G456-34": "Conditional",
    "G456-35": "Hard",
    "G456-36": "Conditional",
    "G456-37": "Conditional",
    "G456-38": "Informational",
    "G456-39": "Informational",
    "G456-40": "Informational",
    "G456-41": "Informational",
    "G456-42": "Hard",
    "G456-43": "Hard",
    "G456-44": "Hard",
}
MIGRATION_FULL_INVENTORY = [f"{number:03d}_{name}.sql" for number, name in (
    (1, "initial"),
    (2, "worker_heartbeats"),
    (3, "admin_indexes"),
    (4, "admin_audit_events"),
    (5, "admin_session_and_throttle"),
    (6, "admin_users_and_tenant_scopes"),
    (7, "mail_request_cancelled_status"),
    (8, "delivery_events"),
    (9, "mail_request_scheduled_at"),
    (10, "admin_audit_events_tenant_id"),
    (11, "bounce_ingestion"),
    (12, "provider_event_inbox_details"),
    (13, "provider_queue_dead_letters"),
)]
MIGRATION_POST011 = MIGRATION_FULL_INVENTORY[11:]
MIGRATION_FILE_PATHS = [f"src/Amane.Mailer/Data/Migrations/{name}" for name in MIGRATION_POST011]
COMMON_EVIDENCE_FIELDS = {
    "schemaVersion", "kind", "evidenceType", "evidenceId", "candidateId",
    "sourceCommitSha", "scenarioId", "variantId", "issueBodySha256",
    "planRevision", "planCommitSha", "planFileSha256", "bindingId",
    "qualificationRunId", "attempt", "result", "startedAtUtc", "finishedAtUtc",
    "executedByRole", "executedByIdentity", "procedureId", "procedureRevision",
    "runnerClass", "toolVersion", "attestedAtUtc", "identity",
    "prohibitedContentScan", "typePayload",
}
EVIDENCE_TYPES: dict[str, tuple[str, ...]] = {
    "G456-01": ("manual-smoke", "automated-test"),
    "G456-02": ("manual-smoke", "automated-test"),
    "G456-03": ("staging-acs-verification",),
    "G456-04": ("staging-acs-verification",),
    "G456-05": ("production-acs-send-ready",),
    "G456-06": ("release-production-operational-verification",),
    **{f"G456-{number:02d}": ("qualification-scenario",) for number in range(7, 35)},
    "G456-34": ("linux-arm64-e2e",),
    "G456-35": ("linux-arm64-artifact-smoke",),
    "G456-36": ("vps-verification",),
    "G456-37": ("optional-automation",),
    "G456-38": ("informational",),
    "G456-39": ("informational",),
    "G456-40": ("informational",),
    "G456-41": ("informational",),
    "G456-42": ("db-migration-fresh-apply",),
    "G456-43": ("db-migration-upgrade",),
    "G456-44": ("db-migration-schema-contract",),
}
# These are the only scenario adapters whose predicates are implemented in
# this runner.  Other #456 lanes must register a dedicated validator before a
# PASS can affect the machine verdict; accepting a generic predicateResult
# would turn an unexecuted lane into an arbitrary GO.
IMPLEMENTED_SCENARIO_VALIDATORS = {
    "G456-03", "G456-04", "G456-05", "G456-06",
    "G456-42", "G456-43", "G456-44",
}
RESTRICTED_LANE_OWNER_ROLES = {
    "G456-03": "maintainer-acs-staging",
    "G456-04": "maintainer-acs-staging",
    "G456-05": "maintainer-acs-production",
    "G456-06": "maintainer-acs-production",
    "G456-42": "maintainer-migration",
    "G456-43": "maintainer-migration",
    "G456-44": "maintainer-migration",
}
MIGRATION_SCHEMA_COLUMNS = [
    "id TEXT NOT NULL PRIMARY KEY", "provider TEXT NOT NULL", "queue_message_id TEXT NOT NULL",
    "failure_stage TEXT NOT NULL", "last_error_code TEXT NOT NULL", "dequeue_count INTEGER NOT NULL",
    "created_at TEXT NOT NULL", "updated_at TEXT NOT NULL",
]
MIGRATION_SCHEMA_CONSTRAINTS = [
    "CHECK (failure_stage IN ('decode', 'parse'))", "CHECK (dequeue_count >= 0)",
    "UNIQUE (provider, queue_message_id)",
]
MIGRATION_SCHEMA_INDEXES = ["idx_provider_queue_dead_letters_created ON provider_queue_dead_letters (created_at)"]


class RunnerError(Exception):
    """Expected, field-level validation failure."""


def fail(message: str) -> None:
    raise RunnerError(message)


def utc_now() -> str:
    return datetime.now(timezone.utc).replace(microsecond=0).isoformat().replace("+00:00", "Z")


def reject_float(value: Any, path: str = "$ ") -> None:
    if isinstance(value, float):
        fail(f"{path.strip()}: floating-point values are not supported by the JCS boundary")
    if isinstance(value, dict):
        for key, child in value.items():
            reject_float(child, f"{path}.{key}")
    elif isinstance(value, list):
        for index, child in enumerate(value):
            reject_float(child, f"{path}[{index}]")


def jcs(value: Any) -> bytes:
    """Encode the JSON subset used by the qualification contract.

    Qualification objects contain strings, integers, booleans, null, arrays,
    and objects.  Rejecting floats avoids the platform-dependent number
    formatting corner of RFC 8785 while preserving the contract's exact
    canonicalization for all supported fields.
    """

    reject_float(value)
    if isinstance(value, dict):
        normalized = {}
        for key, child in value.items():
            if not isinstance(key, str):
                fail("JSON object keys must be strings")
            normalized_key = unicodedata.normalize("NFC", key)
            # The persisted qualification schema uses ASCII field names only.
            # Rejecting non-ASCII keys makes Python's ordering equivalent to
            # the RFC8785 UTF-16 ordering for every accepted object and avoids
            # silently producing a different digest on another implementation.
            if any(ord(character) > 0x7F for character in normalized_key):
                fail("JSON object keys must use ASCII schema names")
            if normalized_key in normalized:
                fail("JSON object keys collide after NFC normalization")
            normalized[normalized_key] = child
        value = normalized
    return json.dumps(
        value,
        ensure_ascii=False,
        separators=(",", ":"),
        sort_keys=True,
        allow_nan=False,
    ).encode("utf-8")


def sha_bytes(data: bytes) -> str:
    return hashlib.sha256(data).hexdigest()


def sha_object(value: Any) -> str:
    return sha_bytes(jcs(value))


def read_json(path: Path, field: str) -> Any:
    if not path.is_file() or path.is_symlink():
        fail(f"{field}: missing or symlink")
    try:
        value = json.loads(path.read_text(encoding="utf-8"))
    except Exception:
        fail(f"{field}: invalid JSON")
    return value


def ensure_directory_chain(directory: Path) -> None:
    current = directory
    missing: list[Path] = []
    while not current.exists() and current != current.parent:
        missing.append(current)
        current = current.parent
    if current.exists() and (current.is_symlink() or not current.is_dir()):
        fail("durable store directory boundary is not a real directory")
    for item in reversed(missing):
        item.mkdir()
        if item.is_symlink() or not item.is_dir():
            fail("durable store directory boundary is not a real directory")


def fsync_directory(directory: Path) -> None:
    try:
        descriptor = os.open(directory, os.O_RDONLY)
        try:
            os.fsync(descriptor)
        finally:
            os.close(descriptor)
    except OSError:
        # Windows does not expose directory fsync.  File fsync below remains
        # mandatory; the operational durable-store backend must provide the
        # parent-directory persistence guarantee there.
        pass


def write_once(path: Path, value: Any) -> None:
    if path.exists() or path.is_symlink():
        fail(f"write-once object already exists: {path.as_posix()}")
    ensure_directory_chain(path.parent)
    if path.parent.is_symlink():
        fail("write-once parent directory is a symlink")
    payload = jcs(value) + b"\n"
    with path.open("xb") as handle:
        handle.write(payload)
        handle.flush()
        os.fsync(handle.fileno())
    fsync_directory(path.parent)


def write_bytes_once(path: Path, data: bytes) -> None:
    if path.exists() or path.is_symlink():
        fail(f"write-once object already exists: {path.as_posix()}")
    ensure_directory_chain(path.parent)
    if path.parent.is_symlink():
        fail("write-once parent directory is a symlink")
    with path.open("xb") as handle:
        handle.write(data)
        handle.flush()
        os.fsync(handle.fileno())
    fsync_directory(path.parent)


def file_sha(path: Path) -> str:
    if not path.is_file() or path.is_symlink():
        fail(f"file is missing or symlink: {path}")
    return sha_bytes(path.read_bytes())


def safe_child(root: Path, relative: str) -> Path:
    rel = Path(relative)
    if rel.is_absolute() or "\\" in relative or "\x00" in relative:
        fail("path must be a relative POSIX path")
    if any(part in ("", ".", "..") for part in rel.parts):
        fail("path contains an invalid segment")
    target = (root / rel).resolve()
    root_resolved = root.resolve()
    try:
        target.relative_to(root_resolved)
    except ValueError:
        fail("path escapes store root")
    return target


def require_string(obj: dict[str, Any], key: str, field: str | None = None) -> str:
    value = obj.get(key)
    if not isinstance(value, str) or not value:
        fail(f"{field or key}: required string")
    return value


def require_arg(value: Any, field: str) -> str:
    if not isinstance(value, str) or not value:
        fail(f"{field}: required string")
    return value


def require_value_free_identity(value: Any, field: str) -> str:
    text = require_arg(value, field)
    if "@" in text or "://" in text or "\\" in text or "/" in text:
        fail(f"{field}: identity must be a value-free handle, not an email, URL, or path")
    return text


def require_hex(value: str, field: str) -> str:
    if not HEX64.fullmatch(value):
        fail(f"{field}: expected lowercase 64-hex")
    return value


def require_event_id(value: Any, field: str) -> str:
    if not isinstance(value, str) or not EVENT32.fullmatch(value):
        fail(f"{field}: expected lowercase 32-hex event id")
    return value


def require_commit(value: str, field: str) -> str:
    if not SHA40.fullmatch(value):
        fail(f"{field}: expected lowercase 40-hex")
    return value


def require_digest(value: str, field: str) -> str:
    if not SHA256_DIGEST.fullmatch(value):
        fail(f"{field}: expected sha256:<64 lowercase hex>")
    return value


def git_output(repo_root: Path, *arguments: str) -> str:
    try:
        result = subprocess.run(["git", "-C", str(repo_root), *arguments], check=True, capture_output=True, text=True)
    except (OSError, subprocess.CalledProcessError):
        fail("git provenance query failed")
    return result.stdout.strip()


def copy_tree_file(src: Path, dst: Path) -> None:
    if not src.is_file() or src.is_symlink():
        fail(f"candidate file missing or symlink: {src.name}")
    write_bytes_once(dst, src.read_bytes())


def candidate_documents(candidate_root: Path) -> tuple[dict[str, Any], dict[str, Any], dict[str, str]]:
    provenance = read_json(candidate_root / "candidate-provenance.json", "candidate-provenance.json")
    identity = read_json(candidate_root / "image-identity.json", "image-identity.json")
    if not isinstance(provenance, dict) or not isinstance(identity, dict):
        fail("candidate documents must be JSON objects")
    source = require_commit(require_string(provenance, "sourceCommitSha"), "sourceCommitSha")
    run_id = provenance.get("workflowRunId")
    if not ((isinstance(run_id, int) and run_id > 0) or (isinstance(run_id, str) and run_id.isdigit() and int(run_id) > 0)):
        fail("workflowRunId: expected positive integer")
    attempt = provenance.get("workflowRunAttempt")
    if not ((isinstance(attempt, int) and attempt == 1) or (isinstance(attempt, str) and attempt == "1")):
        fail("workflowRunAttempt: candidate attempt must be 1")
    workflow_ref = require_string(provenance, "workflowRef")
    if any(character in workflow_ref for character in ("\x00", "\r", "\n")):
        fail("workflowRef: invalid control character")
    platforms = identity.get("platforms")
    if not isinstance(platforms, list) or not platforms or any(not isinstance(platform, str) or not platform for platform in platforms):
        fail("image-identity.platforms: non-empty string array required")
    oci = require_digest(require_string(provenance, "ociIndexDigest"), "ociIndexDigest")
    if identity.get("sourceCommitSha") != source or identity.get("imageDigest") != oci:
        fail("image-identity does not match candidate provenance")
    archives = provenance.get("archives")
    if not isinstance(archives, list) or not archives:
        fail("archives: non-empty array required")
    checksums: dict[str, str] = {}
    sums_path = candidate_root / "CANDIDATE-SHA256SUMS"
    if not sums_path.is_file() or sums_path.is_symlink():
        fail("CANDIDATE-SHA256SUMS: required regular file")
    sums: dict[str, str] = {}
    for line_number, line in enumerate(sums_path.read_text(encoding="utf-8").splitlines(), 1):
        if not line.strip():
            continue
        match = re.fullmatch(r"([0-9a-f]{64})  ([A-Za-z0-9._-]+)", line)
        if match is None or match.group(2) in sums:
            fail(f"CANDIDATE-SHA256SUMS:{line_number}: invalid or duplicate entry")
        sums[match.group(2)] = match.group(1)
    for archive in archives:
        if not isinstance(archive, dict):
            fail("archives: invalid entry")
        rid = require_string(archive, "targetRid")
        name = require_string(archive, "archiveFileName")
        require_string(archive, "artifactName")
        if archive.get("smokeResult") != "passed":
            fail(f"archives[{rid}].smokeResult: candidate smoke must be passed")
        if "payloadTreeSha256" not in archive or (archive.get("payloadTreeSha256") is not None and not HEX64.fullmatch(str(archive.get("payloadTreeSha256")))):
            fail(f"archives[{rid}].payloadTreeSha256: invalid")
        digest = require_digest(require_string(archive, "archiveSha256"), f"archives[{rid}].archiveSha256")
        if not SAFE_ID.fullmatch(rid) or Path(name).name != name:
            fail(f"archives[{rid}]: unsafe identity")
        path = candidate_root / name
        raw_digest = digest.removeprefix("sha256:")
        if file_sha(path) != raw_digest or sums.get(name) != raw_digest:
            fail(f"archives[{rid}]: archive digest mismatch")
        checksums[rid] = digest
    if len(checksums) != len(archives):
        fail("archives: duplicate targetRid")
    if set(sums) != {require_string(archive, "archiveFileName") for archive in archives}:
        fail("CANDIDATE-SHA256SUMS: entries do not exactly match provenance archives")
    return provenance, identity, checksums


def candidate_id(provenance: dict[str, Any], archive_digests: dict[str, str]) -> str:
    parts = [
        require_string(provenance, "sourceCommitSha"),
        str(provenance["workflowRunId"]),
        str(provenance["workflowRunAttempt"]),
        require_string(provenance, "ociIndexDigest"),
        *[archive_digests[rid] for rid in sorted(archive_digests)],
    ]
    return sha_bytes("|".join(parts).encode("utf-8"))


def binding_id_for(binding: dict[str, Any], authorization: dict[str, Any]) -> str:
    authorization_seed = sha_object({
        "qualificationLeadRole": authorization.get("qualificationLeadRole"),
        "qualificationLeadIdentity": authorization.get("qualificationLeadIdentity"),
        "conditionalApproverRole": authorization.get("conditionalApproverRole"),
        "conditionalApproverIdentity": authorization.get("conditionalApproverIdentity"),
        "evidenceOwners": authorization.get("evidenceOwners"),
    })
    preimage = "|".join([
        require_string(binding, "candidateId"),
        require_string(binding, "issueBodySha256"),
        require_string(binding, "planCommitSha"),
        require_string(binding, "planFileSha256"),
        str(binding.get("variantRulesVersion")),
        sha_object(binding.get("rows")),
        authorization_seed,
        require_commit(require_string(binding, "releaseCommitSha"), "releaseCommitSha"),
        require_digest(require_string(binding, "ociIndexDigest"), "ociIndexDigest"),
        require_string(binding, "producerWorkflowRef"),
        str(binding.get("producerWorkflowRunId")),
        str(binding.get("producerWorkflowRunAttempt")),
        require_hex(require_string(binding, "candidateProvenanceSha256"), "candidateProvenanceSha256"),
        require_hex(require_string(binding, "candidateImageIdentitySha256"), "candidateImageIdentitySha256"),
        require_hex(require_string(binding, "candidatePhase1ManifestSha256"), "candidatePhase1ManifestSha256"),
        require_hex(require_string(binding, "candidateArchivesDigestSha256"), "candidateArchivesDigestSha256"),
        require_hex(require_string(binding, "migrationPinDigestSha256"), "migrationPinDigestSha256"),
        require_hex(require_string(binding, "migrationInventoryDigestSha256"), "migrationInventoryDigestSha256"),
    ])
    return sha_bytes(preimage.encode("utf-8"))


def load_binding(run_root: Path) -> dict[str, Any]:
    binding = read_json(run_root / "binding.json", "binding.json")
    if not isinstance(binding, dict):
        fail("binding.json: object required")
    for field in ("candidateId", "bindingId", "qualificationRunId"):
        require_hex(require_string(binding, field), f"binding.{field}")
    nonce = require_string(binding, "runAttemptNonce")
    if sha_bytes((binding["bindingId"] + "|" + nonce).encode("utf-8")) != run_root.name:
        fail("qualificationRunId/runAttemptNonce binding mismatch")
    if binding.get("issueNumber") != 456 or binding.get("planRevision") != "12" or binding.get("variantRulesVersion") != VARIANT_RULES_VERSION:
        fail("binding canonical plan/issue identity mismatch")
    bind_rows({"rows": binding.get("rows"), "number": 456})
    if not isinstance(binding.get("migrationFileDigests"), list) or binding.get("migrationPinDigestSha256") is None or binding.get("migrationInventoryDigestSha256") is None:
        fail("binding migration PIN fields are missing")
    phase2 = read_json(run_root / "phase-manifests/phase-2.json", "phase-2.json")
    if any(phase2.get(field) != binding.get(field) for field in ("candidateId", "bindingId", "qualificationRunId", "runAttemptNonce", "releaseCommitSha", "producerWorkflowRef", "producerWorkflowRunId", "producerWorkflowRunAttempt", "candidateProvenanceSha256", "candidateImageIdentitySha256", "candidatePhase1ManifestSha256", "candidateArchivesDigestSha256")) or phase2.get("authorizationDigestSha256") != binding.get("authorizationDigestSha256"):
        fail("phase-2 manifest identity mismatch")
    auth = read_json(run_root / "authorization.json", "authorization.json")
    if not isinstance(auth, dict) or any(auth.get(field) != binding.get(field) for field in ("candidateId", "bindingId", "qualificationRunId")) or sha_object(auth) != binding.get("authorizationDigestSha256"):
        fail("authorization snapshot digest/identity mismatch")
    if binding["bindingId"] != binding_id_for(binding, auth):
        fail("bindingId recomputation mismatch")
    saved_pin = load_migration_pin(run_root / "migration-pin.json")
    if saved_pin["releaseCommitSha"] != binding.get("releaseCommitSha") or saved_pin["migrationPinDigestSha256"] != binding.get("migrationPinDigestSha256") or saved_pin["migrationInventoryDigestSha256"] != binding.get("migrationInventoryDigestSha256") or saved_pin["migrationFileDigests"] != binding.get("migrationFileDigests"):
        fail("saved migration PIN does not match binding")
    if phase2.get("migrationPinDigestSha256") != saved_pin["migrationPinDigestSha256"] or phase2.get("migrationInventoryDigestSha256") != saved_pin["migrationInventoryDigestSha256"] or phase2.get("migrationFileDigests") != saved_pin["migrationFileDigests"] or phase2.get("releaseCommitSha") != saved_pin["releaseCommitSha"]:
        fail("phase-2 migration PIN identity mismatch")
    candidate_root = run_root.parent.parent / "candidates" / binding["candidateId"] / "intake"
    provenance, identity, archive_digests = candidate_documents(candidate_root)
    if candidate_id(provenance, archive_digests) != binding.get("candidateId"):
        fail("candidateId recomputation mismatch")
    if provenance.get("sourceCommitSha") != binding.get("releaseCommitSha") or provenance.get("ociIndexDigest") != binding.get("ociIndexDigest") or identity.get("sourceCommitSha") != binding.get("releaseCommitSha") or identity.get("imageDigest") != binding.get("ociIndexDigest"):
        fail("candidate intake identity does not match binding")
    if binding.get("producerWorkflowRef") != provenance.get("workflowRef") or binding.get("producerWorkflowRunId") != provenance.get("workflowRunId") or binding.get("producerWorkflowRunAttempt") != provenance.get("workflowRunAttempt"):
        fail("candidate producer identity does not match binding")
    if binding.get("candidateProvenanceSha256") != file_sha(candidate_root / "candidate-provenance.json") or binding.get("candidateImageIdentitySha256") != file_sha(candidate_root / "image-identity.json") or binding.get("candidatePhase1ManifestSha256") != file_sha(candidate_root / "phase-1.json") or binding.get("candidateArchivesDigestSha256") != sha_object({"archives": provenance["archives"], "archiveDigests": archive_digests}):
        fail("candidate provenance/archive digest does not match binding")
    if binding.get("sourceCommitSha") != binding.get("releaseCommitSha") or not SHA40.fullmatch(str(binding.get("sourceCommitSha", ""))) or not SHA256_DIGEST.fullmatch(str(binding.get("ociIndexDigest", ""))):
        fail("binding source/OCI identity is invalid")
    phase1 = read_json(candidate_root / "phase-1.json", "phase-1.json")
    objects = [{"path": p.relative_to(candidate_root.parent).as_posix(), "sha256": file_sha(p)} for p in sorted(candidate_root.rglob("*")) if p.is_file() and p.name != "phase-1.json"]
    if phase1.get("candidateId") != binding.get("candidateId") or phase1.get("sourceCommitSha") != binding.get("releaseCommitSha") or phase1.get("ociIndexDigest") != binding.get("ociIndexDigest") or phase1.get("workflowRunId") != provenance.get("workflowRunId") or phase1.get("workflowRunAttempt") != provenance.get("workflowRunAttempt") or phase1.get("workflowRef") != provenance.get("workflowRef") or phase1.get("objects") != objects:
        fail("phase-1 object inventory mismatch")
    return binding


def load_authorization(run_root: Path) -> dict[str, Any]:
    auth = read_json(run_root / "authorization.json", "authorization.json")
    if not isinstance(auth, dict):
        fail("authorization.json: object required")
    binding = read_json(run_root / "binding.json", "binding.json")
    if not isinstance(binding, dict) or any(auth.get(field) != binding.get(field) for field in ("candidateId", "bindingId", "qualificationRunId")):
        fail("authorization identity mismatch")
    if not all(isinstance(auth.get(field), str) and auth[field] for field in ("qualificationLeadRole", "qualificationLeadIdentity", "conditionalApproverRole", "conditionalApproverIdentity")):
        fail("authorization actor fields are required")
    return auth


def run_status_events(run_root: Path) -> list[Path]:
    directory = run_root / "run-status-events"
    if not directory.is_dir():
        return []
    return sorted(p for p in directory.glob("*.json") if p.is_file() and not p.is_symlink())


def ensure_unsealed(run_root: Path) -> None:
    events = run_status_events(run_root)
    if events:
        fail("qualification run is terminal and cannot be modified")


def allowed_keys(binding: dict[str, Any]) -> set[tuple[str, str]]:
    result: set[tuple[str, str]] = set()
    for row in binding.get("rows", []):
        scenario = require_string(row, "scenarioId")
        variants = row.get("requiredVariants")
        if not isinstance(variants, list):
            fail(f"binding.rows[{scenario}].requiredVariants: array required")
        for variant in variants:
            if not isinstance(variant, str) or not variant:
                fail(f"binding.rows[{scenario}].requiredVariants: invalid variant")
            result.add((scenario, variant))
    for entry in binding.get("optionalEvidenceKeys", []):
        result.add((require_string(entry, "scenarioId"), require_string(entry, "variantId")))
    return result


def evidence_paths(run_root: Path) -> list[Path]:
    directory = run_root / "evidence"
    return sorted(directory.glob("*.json")) if directory.is_dir() else []


def disposition_paths(run_root: Path) -> list[Path]:
    directory = run_root / "dispositions"
    return sorted(directory.glob("*.json")) if directory.is_dir() else []


def exception_paths(run_root: Path) -> list[Path]:
    directory = run_root / "exceptions"
    return sorted(directory.glob("*.json")) if directory.is_dir() else []


def exception_disposition_paths(run_root: Path) -> list[Path]:
    directory = run_root / "exception-dispositions"
    return sorted(directory.glob("*.json")) if directory.is_dir() else []


def load_evidence(run_root: Path) -> dict[str, dict[str, Any]]:
    result: dict[str, dict[str, Any]] = {}
    binding = load_binding(run_root)
    auth = load_authorization(run_root)
    for path in evidence_paths(run_root):
        value = read_json(path, f"evidence/{path.name}")
        if not isinstance(value, dict):
            fail(f"evidence/{path.name}: object required")
        evidence_id = require_hex(require_string(value, "evidenceId"), f"evidence/{path.name}.evidenceId")
        if evidence_id in result:
            fail("duplicate evidenceId")
        validate_evidence_envelope(value, binding, auth, (require_string(value, "scenarioId"), require_string(value, "variantId")))
        if path.stem != evidence_id:
            fail(f"evidence/{path.name}: filename must equal evidenceId")
        result[evidence_id] = value
    return result


def load_dispositions(run_root: Path) -> list[tuple[Path, dict[str, Any]]]:
    result = []
    for path in disposition_paths(run_root):
        value = read_json(path, f"dispositions/{path.name}")
        if not isinstance(value, dict) or path.stem != require_event_id(value.get("eventId"), f"dispositions/{path.name}.eventId"):
            fail(f"dispositions/{path.name}: event filename/id mismatch")
        result.append((path, value))
    return sorted(result, key=lambda item: (item[1].get("eventSequence", -1), item[0].name))


def load_exceptions(run_root: Path) -> dict[str, dict[str, Any]]:
    result: dict[str, dict[str, Any]] = {}
    binding = load_binding(run_root)
    auth = load_authorization(run_root)
    rows = {row["scenarioId"]: row for row in binding.get("rows", [])}
    for path in exception_paths(run_root):
        value = read_json(path, f"exceptions/{path.name}")
        if not isinstance(value, dict) or value.get("schemaVersion") != 1 or path.stem != str(value.get("exceptionId", "")):
            fail(f"exceptions/{path.name}: schema or filename mismatch")
        exception_id = require_hex(require_string(value, "exceptionId"), f"exceptions/{path.name}.exceptionId")
        if exception_id in result:
            fail("duplicate exceptionId")
        scenario = require_string(value, "scenarioId")
        variant = require_string(value, "variantId")
        row = rows.get(scenario)
        if row is None or row.get("gateClass") != "Conditional":
            fail(f"exceptions/{path.name}: only Conditional keys may have exceptions")
        if scenario in {"G456-42", "G456-43", "G456-44"}:
            fail(f"exceptions/{path.name}: migration rows cannot use exceptions")
        if variant not in row.get("requiredVariants", []):
            fail(f"exceptions/{path.name}: variant is not required for the scenario")
        if value.get("qualificationRunId") != binding.get("qualificationRunId") or value.get("bindingId") != binding.get("bindingId") or value.get("candidateId") != binding.get("candidateId"):
            fail(f"exceptions/{path.name}: identity mismatch")
        if value.get("issueBodySha256") != binding.get("issueBodySha256") or value.get("planCommitSha") != binding.get("planCommitSha") or value.get("planFileSha256") != binding.get("planFileSha256"):
            fail(f"exceptions/{path.name}: issue/plan identity mismatch")
        if not all(isinstance(value.get(field), str) and value[field] for field in ("reasonNotExecutable", "alternateVerification", "residualRisk", "impactScope", "createdAtUtc")):
            fail(f"exceptions/{path.name}: required exception fields missing")
        owner = owner_for(auth, (scenario, variant))
        if value.get("createdByRole") != owner["ownerRole"] or value.get("createdByIdentity") != owner["ownerIdentity"]:
            fail(f"exceptions/{path.name}: creator mismatch")
        for field in ("reasonNotExecutable", "alternateVerification", "residualRisk", "impactScope"):
            value_free(value[field], f"$.{field}")
        result[exception_id] = value
    return result


def load_exception_dispositions(run_root: Path) -> list[tuple[Path, dict[str, Any]]]:
    result = []
    for path in exception_disposition_paths(run_root):
        value = read_json(path, f"exception-dispositions/{path.name}")
        if not isinstance(value, dict) or path.stem != require_event_id(value.get("eventId"), f"exception-dispositions/{path.name}.eventId"):
            fail(f"exception-dispositions/{path.name}: event filename/id mismatch")
        result.append((path, value))
    return sorted(result, key=lambda item: (item[1].get("exceptionEventSequence", -1), item[0].name))


def verify_exception_disposition_digest(event: dict[str, Any]) -> str:
    digest = require_hex(require_string(event, "eventDigestSha256"), "exception disposition eventDigestSha256")
    without = dict(event)
    without.pop("eventDigestSha256", None)
    if sha_object(without) != digest:
        fail("exception disposition event digest mismatch")
    if event.get("canonicalization") != JCS_VERSION:
        fail("exception disposition canonicalization mismatch")
    return digest


def replay_exceptions(run_root: Path) -> tuple[dict[tuple[str, str], str | None], dict[str, dict[str, Any]], int, str | None]:
    binding = load_binding(run_root)
    auth = load_authorization(run_root)
    exceptions = load_exceptions(run_root)
    active: dict[tuple[str, str], str | None] = {}
    history: dict[str, tuple[tuple[str, str], str | None, set[str]]] = {}
    last_digest: str | None = None
    last_sequence = 0
    for path, event in load_exception_dispositions(run_root):
        sequence = event.get("exceptionEventSequence")
        if not isinstance(sequence, int) or sequence != last_sequence + 1:
            fail(f"exception-dispositions/{path.name}: event sequence is not contiguous")
        if event.get("qualificationRunId") != binding.get("qualificationRunId") or event.get("bindingId") != binding.get("bindingId") or event.get("candidateId") != binding.get("candidateId"):
            fail(f"exception-dispositions/{path.name}: identity mismatch")
        if event.get("previousExceptionEventDigestSha256") != last_digest:
            fail(f"exception-dispositions/{path.name}: hash chain mismatch")
        digest = verify_exception_disposition_digest(event)
        if event.get("approvedByRole") != auth.get("conditionalApproverRole") or event.get("approvedByIdentity") != auth.get("conditionalApproverIdentity"):
            fail(f"exception-dispositions/{path.name}: approver mismatch")
        scenario = require_string(event, "scenarioId")
        variant = require_string(event, "variantId")
        key = (scenario, variant)
        action = event.get("action")
        target = event.get("targetExceptionId")
        replacement = event.get("supersededByExceptionId")
        if action == "approve":
            if not isinstance(target, str) or target not in exceptions or (exceptions[target].get("scenarioId"), exceptions[target].get("variantId")) != key or active.get(key) is not None or replacement is not None or event.get("restoresExceptionEventId") is not None:
                fail(f"exception-dispositions/{path.name}: invalid approve transition")
            if event.get("targetExceptionSha256") != file_sha(run_root / "exceptions" / f"{target}.json"):
                fail(f"exception-dispositions/{path.name}: target exception digest mismatch")
            active[key] = target
        elif action == "supersede":
            if not isinstance(target, str) or not isinstance(replacement, str) or target == replacement or active.get(key) != target or replacement not in exceptions or (exceptions[target].get("scenarioId"), exceptions[target].get("variantId")) != key or (exceptions[replacement].get("scenarioId"), exceptions[replacement].get("variantId")) != key or event.get("restoresExceptionEventId") is not None:
                fail(f"exception-dispositions/{path.name}: invalid supersede transition")
            if event.get("targetExceptionSha256") != file_sha(run_root / "exceptions" / f"{target}.json") or event.get("supersededByExceptionSha256") != file_sha(run_root / "exceptions" / f"{replacement}.json"):
                fail(f"exception-dispositions/{path.name}: exception digest mismatch")
            active[key] = replacement
        elif action == "revoke":
            if not isinstance(target, str) or target not in exceptions or (exceptions[target].get("scenarioId"), exceptions[target].get("variantId")) != key or active.get(key) != target or replacement is not None or event.get("restoresExceptionEventId") is not None:
                fail(f"exception-dispositions/{path.name}: invalid revoke transition")
            if event.get("targetExceptionSha256") != file_sha(run_root / "exceptions" / f"{target}.json"):
                fail(f"exception-dispositions/{path.name}: target exception digest mismatch")
            if active.get(key) == target:
                active[key] = None
        elif action == "restore":
            restore_id = event.get("restoresExceptionEventId")
            if target is not None or replacement is not None or not isinstance(restore_id, str) or restore_id not in history or history[restore_id][0] != key:
                fail(f"exception-dispositions/{path.name}: invalid restore transition")
            active[key] = history[restore_id][1]
        else:
            fail(f"exception-dispositions/{path.name}: unsupported action")
        history[event["eventId"]] = (key, active.get(key), set())
        last_sequence = sequence
        last_digest = digest
    return active, exceptions, last_sequence, last_digest


def verify_disposition_digest(event: dict[str, Any]) -> str:
    digest = require_hex(require_string(event, "eventDigestSha256"), "eventDigestSha256")
    without = dict(event)
    without.pop("eventDigestSha256", None)
    if sha_object(without) != digest:
        fail("disposition event digest mismatch")
    if event.get("canonicalization") != JCS_VERSION:
        fail("disposition canonicalization mismatch")
    return digest


def replay(run_root: Path) -> tuple[dict[tuple[str, str], str | None], dict[str, dict[str, Any]], int, str | None]:
    binding = load_binding(run_root)
    auth = load_authorization(run_root)
    evidence = load_evidence(run_root)
    active: dict[tuple[str, str], str | None] = {}
    invalidated: set[str] = set()
    history: dict[str, tuple[tuple[str, str], str | None, set[str]]] = {}
    last_digest: str | None = None
    last_sequence = 0
    for path, event in load_dispositions(run_root):
        sequence = event.get("eventSequence")
        if not isinstance(sequence, int) or sequence != last_sequence + 1:
            fail(f"dispositions/{path.name}: eventSequence is not contiguous")
        if event.get("qualificationRunId") != binding.get("qualificationRunId") or event.get("bindingId") != binding.get("bindingId") or event.get("candidateId") != binding.get("candidateId"):
            fail(f"dispositions/{path.name}: identity mismatch")
        if event.get("previousEventDigestSha256") != last_digest:
            fail(f"dispositions/{path.name}: hash chain mismatch")
        digest = verify_disposition_digest(event)
        scenario = require_string(event, "scenarioId")
        variant = require_string(event, "variantId")
        key = (scenario, variant)
        allowed = allowed_keys(binding)
        if key not in allowed:
            fail(f"dispositions/{path.name}: unknown evidence key")
        action = event.get("action")
        target = event.get("targetEvidenceId")
        incoming = event.get("supersededByEvidenceId")
        restore_id = event.get("restoresEventId")
        owner = owner_for(auth, key)
        previous_active = active.get(key)
        if action == "accept":
            if not isinstance(target, str) or target not in evidence or active.get(key) is not None or incoming is not None or restore_id is not None:
                fail(f"dispositions/{path.name}: invalid accept transition")
            if evidence[target].get("scenarioId") != scenario or evidence[target].get("variantId") != variant:
                fail(f"dispositions/{path.name}: target key mismatch")
            if target in invalidated:
                fail(f"dispositions/{path.name}: invalidated evidence cannot be accepted")
            incoming_id = target
            active[key] = target
        elif action == "supersede":
            if not isinstance(target, str) or not isinstance(incoming, str) or target == incoming or restore_id is not None:
                fail(f"dispositions/{path.name}: supersede IDs required")
            if active.get(key) != target or incoming not in evidence or incoming in invalidated:
                fail(f"dispositions/{path.name}: invalid supersede transition")
            if evidence[incoming].get("scenarioId") != scenario or evidence[incoming].get("variantId") != variant:
                fail(f"dispositions/{path.name}: replacement key mismatch")
            incoming_id = incoming
            active[key] = incoming
        elif action == "invalidate":
            if not isinstance(target, str) or target not in evidence or incoming is not None or restore_id is not None:
                fail(f"dispositions/{path.name}: invalidation target missing")
            if (evidence[target].get("scenarioId"), evidence[target].get("variantId")) != key:
                fail(f"dispositions/{path.name}: invalidation target key mismatch")
            invalidated.add(target)
            if active.get(key) == target:
                active[key] = None
        elif action == "restore":
            if target is not None or incoming is not None or not isinstance(restore_id, str) or restore_id not in history or history[restore_id][0] != key:
                fail(f"dispositions/{path.name}: invalid restore transition")
            invalidated.difference_update({evidence_id for evidence_id in invalidated if evidence[evidence_id].get("scenarioId") == scenario and evidence[evidence_id].get("variantId") == variant})
            active[key] = history[restore_id][1]
            invalidated.update(history[restore_id][2])
        else:
            fail(f"dispositions/{path.name}: unsupported action")
        if action in {"accept", "supersede"}:
            incoming_evidence = evidence[incoming_id]
            if incoming_evidence.get("executedByRole") != owner["ownerRole"] or incoming_evidence.get("executedByIdentity") != owner["ownerIdentity"]:
                fail(f"dispositions/{path.name}: evidence owner mismatch")
            expected_role = owner["ownerRole"]
            expected_identity = owner["ownerIdentity"]
            prior = evidence.get(previous_active) if previous_active else None
            if prior is not None and prior.get("result") == "FAIL" and incoming_evidence.get("result") == "PASS":
                expected_role = auth["qualificationLeadRole"]
                expected_identity = auth["qualificationLeadIdentity"]
            if event.get("approvedByRole") != expected_role or event.get("approvedByIdentity") != expected_identity:
                fail(f"dispositions/{path.name}: approver mismatch")
        elif event.get("approvedByRole") != auth["qualificationLeadRole"] or event.get("approvedByIdentity") != auth["qualificationLeadIdentity"]:
            fail(f"dispositions/{path.name}: lead approver mismatch")
        history[event["eventId"]] = (key, active.get(key), {evidence_id for evidence_id in invalidated if evidence[evidence_id].get("scenarioId") == scenario and evidence[evidence_id].get("variantId") == variant})
        last_sequence = sequence
        last_digest = digest
    return active, evidence, last_sequence, last_digest


def object_inventory(run_root: Path) -> list[dict[str, str]]:
    entries: list[dict[str, str]] = []
    for path in sorted(run_root.rglob("*")):
        if not path.is_file() or path.is_symlink():
            continue
        relative = path.relative_to(run_root).as_posix()
        if relative.startswith("run-status-events/") or relative == "phase-manifests/phase-4.json":
            continue
        if ".." in Path(relative).parts or "\\" in relative or "\x00" in relative:
            fail("invalid relative path in run object inventory")
        entries.append({"path": relative, "sha256": file_sha(path)})
    return entries


def object_root(entries: Iterable[dict[str, str]]) -> str:
    normalized = sorted(({"path": e["path"], "sha256": e["sha256"]} for e in entries), key=lambda e: e["path"].encode("utf-8"))
    return sha_bytes(jcs(normalized))


def bind_rows(snapshot: dict[str, Any]) -> list[dict[str, Any]]:
    rows = snapshot.get("rows")
    if not isinstance(rows, list) or not rows:
        fail("issue snapshot rows: non-empty array required")
    result = []
    seen: set[str] = set()
    for index, row in enumerate(rows):
        if not isinstance(row, dict):
            fail(f"issue snapshot rows[{index}]: object required")
        scenario = require_string(row, "scenarioId")
        if scenario not in CANONICAL_VARIANTS or scenario != f"G456-{index + 1:02d}":
            fail(f"issue snapshot rows[{index}].scenarioId: canonical #456 order required")
        if scenario in seen:
            fail(f"issue snapshot rows[{index}]: duplicate scenarioId")
        seen.add(scenario)
        gate = require_string(row, "gateClass")
        if gate != CANONICAL_GATES[scenario]:
            fail(f"issue snapshot rows[{scenario}].gateClass: canonical gate mismatch")
        variants = row.get("requiredVariants", [])
        if not isinstance(variants, list) or any(not isinstance(v, str) or not v for v in variants):
            fail(f"issue snapshot rows[{scenario}].requiredVariants: invalid")
        if len(set(variants)) != len(variants) or tuple(variants) != CANONICAL_VARIANTS[scenario]:
            fail(f"issue snapshot rows[{scenario}].requiredVariants: canonical variant binding mismatch")
        if row.get("rowIndex", index) != index:
            fail(f"issue snapshot rows[{scenario}].rowIndex: expected {index}")
        result.append({
            "rowIndex": index,
            "scenarioId": scenario,
            "scenarioText": require_string(row, "scenarioText"),
            "environmentText": require_string(row, "environmentText"),
            "gateClass": gate,
            "scenarioTextSha256": sha_bytes(require_string(row, "scenarioText").encode("utf-8")),
            "environmentTextSha256": sha_bytes(require_string(row, "environmentText").encode("utf-8")),
            "requiredVariants": variants,
            "informationalNotRequired": gate == "Informational",
        })
    if len(result) != len(CANONICAL_VARIANTS) or seen != set(CANONICAL_VARIANTS):
        fail("issue snapshot rows: complete canonical G456-01..44 table required")
    return result


def command_intake(args: argparse.Namespace) -> None:
    candidate_root = Path(args.candidate_root).resolve()
    store = Path(args.store_root).resolve()
    if not candidate_root.is_dir():
        fail("candidate root is missing")
    provenance, identity, archive_digests = candidate_documents(candidate_root)
    source = require_commit(args.release_commit_sha, "release-commit-sha")
    expected_digest = require_digest(args.expected_oci_digest, "expected-oci-digest")
    expected_workflow_ref = require_arg(args.expected_workflow_ref, "expected-workflow-ref")
    if provenance.get("workflowRef") != expected_workflow_ref:
        fail("candidate workflowRef does not match the trusted producer input")
    if provenance["sourceCommitSha"] != source or identity.get("sourceCommitSha") != source:
        fail("candidate sourceCommitSha does not match exact release commit")
    if provenance["ociIndexDigest"] != expected_digest or identity.get("imageDigest") != expected_digest:
        fail("candidate OCI digest does not match expected digest")
    cid = candidate_id(provenance, archive_digests)
    candidate_store = store / "candidates" / cid
    if candidate_store.exists():
        fail("candidateId already exists; intake is write-once")
    intake = candidate_store / "intake"
    for name in ("candidate-provenance.json", "image-identity.json", "CANDIDATE-SHA256SUMS", "CANDIDATE-HANDOFF.md"):
        copy_tree_file(candidate_root / name, intake / name)
    for archive in provenance["archives"]:
        copy_tree_file(candidate_root / archive["archiveFileName"], intake / archive["archiveFileName"])
    manifest = {
        "schemaVersion": 1,
        "candidateId": cid,
        "sourceCommitSha": source,
        "ociIndexDigest": expected_digest,
        "workflowRunId": provenance["workflowRunId"],
        "workflowRunAttempt": provenance["workflowRunAttempt"],
        "workflowRef": provenance["workflowRef"],
        "objects": [{"path": p.relative_to(candidate_store).as_posix(), "sha256": file_sha(p)} for p in sorted(intake.rglob("*")) if p.is_file()],
        "createdAtUtc": utc_now(),
    }
    write_once(intake / "phase-1.json", manifest)
    lifecycle = {
        "eventId": uuid.uuid4().hex,
        "candidateId": cid,
        "status": "active",
        "sourceCommitSha": source,
        "createdAtUtc": utc_now(),
    }
    write_once(candidate_store / "lifecycle-events" / f"{lifecycle['eventId']}.json", lifecycle)
    print(json.dumps({"candidateId": cid, "sourceCommitSha": source, "ociIndexDigest": expected_digest}, sort_keys=True))


def load_owner_map(path: Path) -> list[dict[str, str]]:
    value = read_json(path, "evidence owners")
    if not isinstance(value, list):
        fail("evidence owners: array required")
    result = []
    seen: set[tuple[str, str]] = set()
    for entry in value:
        if not isinstance(entry, dict):
            fail("evidence owners: invalid entry")
        item = {
            "scenarioId": require_string(entry, "scenarioId"),
            "variantId": require_string(entry, "variantId"),
            "ownerRole": require_value_free_identity(entry.get("ownerRole"), "ownerRole"),
            "ownerIdentity": require_value_free_identity(entry.get("ownerIdentity"), "ownerIdentity"),
        }
        required_role = RESTRICTED_LANE_OWNER_ROLES.get(item["scenarioId"])
        if required_role is not None and item["ownerRole"] != required_role:
            fail(f"evidence owners: {item['scenarioId']} requires role {required_role}")
        key = (item["scenarioId"], item["variantId"])
        if key in seen:
            fail("evidence owners: duplicate scenario/variant key")
        seen.add(key)
        result.append(item)
    return result


def load_migration_pin(path: Path) -> dict[str, Any]:
    pin = read_json(path, "migration pin")
    if not isinstance(pin, dict):
        fail("migration pin: object required")
    without = pin.get("migrationPinWithoutDigest")
    if not isinstance(without, dict):
        fail("migrationPinWithoutDigest: object required")
    if without.get("schemaVersion") != 1:
        fail("migrationPinWithoutDigest.schemaVersion: expected 1")
    require_commit(require_string(without, "releaseCommitSha"), "migrationPinWithoutDigest.releaseCommitSha")
    if without.get("inventoryAlgorithm") != "RFC8785-JCS-runner-order-migration-inventory-sha256/v1":
        fail("migrationPinWithoutDigest.inventoryAlgorithm: unsupported algorithm")
    if "evidenceDigestSha256" in without:
        fail("migrationPinWithoutDigest: evidenceDigestSha256 is forbidden")
    pin_digest = require_hex(require_string(pin, "migrationPinDigestSha256"), "migrationPinDigestSha256")
    inventory_digest = require_hex(require_string(pin, "migrationInventoryDigestSha256"), "migrationInventoryDigestSha256")
    if sha_object(without) != pin_digest:
        fail("migrationPinDigestSha256: canonical digest mismatch")
    if without.get("inventoryDigestSha256") != inventory_digest:
        fail("migrationInventoryDigestSha256: must match migrationPinWithoutDigest.inventoryDigestSha256")
    files = without.get("files")
    expected_files = {
        "src/Amane.Mailer/Data/Migrations/012_provider_event_inbox_details.sql",
        "src/Amane.Mailer/Data/Migrations/013_provider_queue_dead_letters.sql",
    }
    if not isinstance(files, list) or {entry.get("path") for entry in files if isinstance(entry, dict)} != expected_files or len(files) != len(expected_files):
        fail("migrationPinWithoutDigest.files: exact frozen 012/013 inventory required")
    normalized_files = []
    for entry in files:
        if not isinstance(entry, dict):
            fail("migration pin file entry: object required")
        path_value = require_string(entry, "path")
        if path_value.startswith("/") or "\\" in path_value or ".." in Path(path_value).parts:
            fail("migration pin file path: unsafe")
        normalized_files.append({
            "path": path_value,
            "sha256": require_hex(require_string(entry, "sha256"), "migration pin file sha256"),
            "gitBlobSha": require_commit(require_string(entry, "gitBlobSha"), "migration pin file gitBlobSha"),
        })
    if normalized_files != sorted(normalized_files, key=lambda item: item["path"]):
        fail("migration pin files: must be ordinal path sorted")
    return {
        "releaseCommitSha": without["releaseCommitSha"],
        "inventoryDigestSha256": without["inventoryDigestSha256"],
        "migrationPinDigestSha256": pin_digest,
        "migrationInventoryDigestSha256": inventory_digest,
        "migrationFileDigests": normalized_files,
    }


def verify_migration_pin_tree(repo_root: Path, release_commit_sha: str, migration_pin: dict[str, Any]) -> None:
    if not repo_root.is_dir():
        fail("repo-root: directory is missing")
    if git_output(repo_root, "rev-parse", release_commit_sha) != release_commit_sha:
        fail("repo-root does not contain the exact release commit")
    tree_paths = git_output(repo_root, "ls-tree", "-r", "--name-only", release_commit_sha, "--", "src/Amane.Mailer/Data/Migrations").splitlines()
    full_inventory = [path for path in tree_paths if path.endswith(".sql")]
    expected_inventory = [f"src/Amane.Mailer/Data/Migrations/{name}" for name in MIGRATION_FULL_INVENTORY]
    if full_inventory != expected_inventory:
        fail("migration tree inventory does not match the frozen runner-order inventory")
    inventory_document = {"schemaVersion": 1, "releaseCommitSha": release_commit_sha, "runnerOrderPaths": full_inventory}
    if sha_object(inventory_document) != migration_pin["inventoryDigestSha256"]:
        fail("migration inventory digest does not match the release tree")
    for entry in migration_pin["migrationFileDigests"]:
        path = entry["path"]
        try:
            blob = subprocess.run(["git", "-C", str(repo_root), "show", f"{release_commit_sha}:{path}"], check=True, capture_output=True).stdout
        except (OSError, subprocess.CalledProcessError):
            fail("migration file is missing from the release tree")
        if sha_bytes(blob) != entry["sha256"] or git_output(repo_root, "rev-parse", f"{release_commit_sha}:{path}") != entry["gitBlobSha"]:
            fail(f"migration file digest mismatch: {path}")


def command_bind(args: argparse.Namespace) -> None:
    store = Path(args.store_root).resolve()
    candidate_store = store / "candidates" / require_hex(args.candidate_id, "candidate-id")
    intake_manifest = read_json(candidate_store / "intake" / "phase-1.json", "phase-1.json")
    if intake_manifest.get("candidateId") != args.candidate_id:
        fail("phase-1 candidateId does not match bind input")
    snapshot = read_json(Path(args.issue_snapshot), "issue snapshot")
    if snapshot.get("number") != 456:
        fail("issue snapshot number: expected 456")
    rows = bind_rows(snapshot)
    body = require_string(snapshot, "body")
    issue_body_sha = sha_bytes(body.encode("utf-8"))
    plan_path = Path(args.plan_file).resolve()
    plan_sha = file_sha(plan_path)
    plan_commit = require_commit(args.plan_commit_sha, "plan-commit-sha")
    migration_pin = load_migration_pin(Path(args.migration_pin))
    if migration_pin["releaseCommitSha"] != intake_manifest["sourceCommitSha"]:
        fail("migration pin releaseCommitSha does not match candidate sourceCommitSha")
    verify_migration_pin_tree(Path(args.repo_root).resolve(), intake_manifest["sourceCommitSha"], migration_pin)
    candidate_intake = candidate_store / "intake"
    provenance, identity, archive_digests = candidate_documents(candidate_intake)
    if candidate_id(provenance, archive_digests) != args.candidate_id:
        fail("candidate intake candidateId recomputation mismatch")
    candidate_provenance_sha = file_sha(candidate_intake / "candidate-provenance.json")
    candidate_identity_sha = file_sha(candidate_intake / "image-identity.json")
    candidate_phase1_sha = file_sha(candidate_intake / "phase-1.json")
    candidate_archives_sha = sha_object({"archives": provenance["archives"], "archiveDigests": archive_digests})
    nonce = require_value_free_identity(args.run_attempt_nonce, "run-attempt-nonce")
    owners = load_owner_map(Path(args.evidence_owners))
    optional = [{"scenarioId": "G456-38", "variantId": "nas"}, {"scenarioId": "G456-39", "variantId": "macos"}, {"scenarioId": "G456-40", "variantId": "mode5-manual"}, {"scenarioId": "G456-41", "variantId": "external-secret-manager-docs"}]
    required_keys = {(r["scenarioId"], v) for r in rows for v in r["requiredVariants"]}
    required_keys.update((e["scenarioId"], e["variantId"]) for e in optional)
    owner_keys = {(e["scenarioId"], e["variantId"]) for e in owners}
    if required_keys != owner_keys:
        fail("evidence owners must cover every required and optional key exactly once")
    qualification_lead_role = require_value_free_identity(args.qualification_lead_role, "qualification-lead-role")
    qualification_lead_identity = require_value_free_identity(args.qualification_lead_identity, "qualification-lead-identity")
    conditional_role = require_value_free_identity(args.conditional_approver_role, "conditional-approver-role")
    conditional_identity = require_value_free_identity(args.conditional_approver_identity, "conditional-approver-identity")
    binding_material = {
        "candidateId": args.candidate_id,
        "issueBodySha256": issue_body_sha,
        "planCommitSha": plan_commit,
        "planFileSha256": plan_sha,
        "variantRulesVersion": VARIANT_RULES_VERSION,
        "migrationPinDigestSha256": migration_pin["migrationPinDigestSha256"],
        "migrationInventoryDigestSha256": migration_pin["migrationInventoryDigestSha256"],
        "releaseCommitSha": intake_manifest["sourceCommitSha"],
        "ociIndexDigest": intake_manifest["ociIndexDigest"],
        "producerWorkflowRef": provenance["workflowRef"],
        "producerWorkflowRunId": provenance["workflowRunId"],
        "producerWorkflowRunAttempt": provenance["workflowRunAttempt"],
        "candidateProvenanceSha256": candidate_provenance_sha,
        "candidateImageIdentitySha256": candidate_identity_sha,
        "candidatePhase1ManifestSha256": candidate_phase1_sha,
        "candidateArchivesDigestSha256": candidate_archives_sha,
        "rows": rows,
    }
    authorization_seed = {
        "qualificationLeadRole": qualification_lead_role,
        "qualificationLeadIdentity": qualification_lead_identity,
        "conditionalApproverRole": conditional_role,
        "conditionalApproverIdentity": conditional_identity,
        "evidenceOwners": owners,
    }
    binding_id = binding_id_for(binding_material, authorization_seed)
    run_id = sha_bytes((binding_id + "|" + nonce).encode("utf-8"))
    run_root = store / "runs" / run_id
    if run_root.exists():
        fail("qualificationRunId already exists; bind is write-once")
    created = utc_now()
    authorization = {
        "schemaVersion": 1,
        "qualificationRunId": run_id,
        "bindingId": binding_id,
        "candidateId": args.candidate_id,
        "qualificationLeadRole": qualification_lead_role,
        "qualificationLeadIdentity": qualification_lead_identity,
        "conditionalApproverRole": conditional_role,
        "conditionalApproverIdentity": conditional_identity,
        "evidenceOwners": owners,
        "createdAtUtc": created,
    }
    binding = {
        "schemaVersion": 1,
        "bindingId": binding_id,
        "qualificationRunId": run_id,
        "runAttemptNonce": nonce,
        "candidateId": args.candidate_id,
        "planRevision": "12",
        "planCommitSha": plan_commit,
        "planFileSha256": plan_sha,
        "variantRulesVersion": VARIANT_RULES_VERSION,
        "issueNumber": 456,
        "issueUpdatedAt": require_string(snapshot, "updatedAt"),
        "issueBodySha256": issue_body_sha,
        "fetchedAtUtc": created,
        "sourceCommitSha": intake_manifest["sourceCommitSha"],
        "releaseCommitSha": intake_manifest["sourceCommitSha"],
        "ociIndexDigest": intake_manifest["ociIndexDigest"],
        "producerWorkflowRef": provenance["workflowRef"],
        "producerWorkflowRunId": provenance["workflowRunId"],
        "producerWorkflowRunAttempt": provenance["workflowRunAttempt"],
        "candidateProvenanceSha256": candidate_provenance_sha,
        "candidateImageIdentitySha256": candidate_identity_sha,
        "candidatePhase1ManifestSha256": candidate_phase1_sha,
        "candidateArchivesDigestSha256": candidate_archives_sha,
        "migrationPinDigestSha256": migration_pin["migrationPinDigestSha256"],
        "migrationInventoryDigestSha256": migration_pin["migrationInventoryDigestSha256"],
        "migrationFileDigests": migration_pin["migrationFileDigests"],
        "rows": rows,
        "optionalEvidenceKeys": optional,
        "authorizationDigestSha256": sha_object(authorization),
    }
    write_once(run_root / "authorization.json", authorization)
    write_once(run_root / "binding.json", binding)
    write_once(run_root / "migration-pin.json", read_json(Path(args.migration_pin), "migration pin"))
    write_once(run_root / "docs-extract" / "issue-metadata.json", {"issueNumber": 456, "updatedAt": snapshot["updatedAt"], "bodySha256": issue_body_sha})
    write_once(run_root / "docs-extract" / "plan-metadata.json", {"planCommitSha": plan_commit, "planFileSha256": plan_sha, "planRevision": "12"})
    phase2 = {
        "schemaVersion": 1,
        "phase": 2,
        "candidateId": args.candidate_id,
        "bindingId": binding_id,
        "qualificationRunId": run_id,
        "runAttemptNonce": nonce,
        "authorizationDigestSha256": sha_object(authorization),
        "migrationPinDigestSha256": migration_pin["migrationPinDigestSha256"],
        "migrationInventoryDigestSha256": migration_pin["migrationInventoryDigestSha256"],
        "migrationFileDigests": migration_pin["migrationFileDigests"],
        "releaseCommitSha": migration_pin["releaseCommitSha"],
        "producerWorkflowRef": provenance["workflowRef"],
        "producerWorkflowRunId": provenance["workflowRunId"],
        "producerWorkflowRunAttempt": provenance["workflowRunAttempt"],
        "candidateProvenanceSha256": candidate_provenance_sha,
        "candidateImageIdentitySha256": candidate_identity_sha,
        "candidatePhase1ManifestSha256": candidate_phase1_sha,
        "candidateArchivesDigestSha256": candidate_archives_sha,
        "createdAtUtc": created,
    }
    write_once(run_root / "phase-manifests" / "phase-2.json", phase2)
    print(json.dumps({"candidateId": args.candidate_id, "bindingId": binding_id, "qualificationRunId": run_id}, sort_keys=True))


def value_free(value: Any, path: str = "$ ") -> None:
    forbidden_key = re.compile(r"(password|secret|token|connection|string|private.?key|recipient|sender|subject|body|raw.?error|bearer|cookie)", re.I)
    if isinstance(value, dict):
        for key, child in value.items():
            if forbidden_key.search(str(key)):
                fail(f"value-free evidence contains forbidden field: {path}.{key}")
            value_free(child, f"{path}.{key}")
    elif isinstance(value, list):
        for index, child in enumerate(value):
            value_free(child, f"{path}[{index}]")
    elif isinstance(value, str):
        if "@" in value or "-----BEGIN" in value or "://" in value:
            fail(f"value-free evidence contains secret/PII-like value at {path}")


def validate_evidence_envelope(envelope: dict[str, Any], binding: dict[str, Any], auth: dict[str, Any], key: tuple[str, str]) -> dict[str, Any]:
    missing = sorted(COMMON_EVIDENCE_FIELDS - envelope.keys())
    if missing:
        fail(f"evidence envelope missing fields: {','.join(missing)}")
    unknown = sorted(set(envelope) - COMMON_EVIDENCE_FIELDS)
    if unknown:
        fail(f"evidence envelope contains unknown fields: {','.join(unknown)}")
    if envelope.get("schemaVersion") != 1 or envelope.get("kind") != "release-qualification-evidence":
        fail("evidence envelope schema/kind mismatch")
    evidence_id = require_hex(envelope.get("evidenceId"), "evidenceId")
    if envelope.get("candidateId") != binding.get("candidateId") or envelope.get("bindingId") != binding.get("bindingId") or envelope.get("qualificationRunId") != binding.get("qualificationRunId"):
        fail("evidence envelope qualification identity mismatch")
    if envelope.get("sourceCommitSha") != binding.get("releaseCommitSha") or envelope.get("issueBodySha256") != binding.get("issueBodySha256"):
        fail("evidence envelope source/issue identity mismatch")
    if envelope.get("planRevision") != "12" or envelope.get("planCommitSha") != binding.get("planCommitSha") or envelope.get("planFileSha256") != binding.get("planFileSha256"):
        fail("evidence envelope plan identity mismatch")
    if envelope.get("scenarioId") != key[0] or envelope.get("variantId") != key[1]:
        fail("evidence envelope scenario/variant mismatch")
    if envelope.get("attempt") != 1:
        fail("evidence envelope attempt must be 1")
    result = envelope.get("result")
    if result not in {"PASS", "FAIL", "NOT_RUN", "EXCEPTION"}:
        fail("evidence envelope result is invalid")
    owner = owner_for(auth, key)
    if envelope.get("executedByRole") != owner["ownerRole"] or envelope.get("executedByIdentity") != owner["ownerIdentity"]:
        fail("evidence envelope executed actor mismatch")
    if not all(isinstance(envelope.get(field), str) and envelope[field] for field in ("startedAtUtc", "finishedAtUtc", "procedureId", "procedureRevision", "runnerClass", "toolVersion", "attestedAtUtc")):
        fail("evidence envelope procedure/timestamp fields are required")
    if not all(UTC_TIMESTAMP.fullmatch(envelope[field]) for field in ("startedAtUtc", "finishedAtUtc", "attestedAtUtc")):
        fail("evidence envelope timestamps must be UTC RFC3339 seconds")
    value_free({field: envelope[field] for field in ("procedureId", "procedureRevision", "runnerClass", "toolVersion")}, "$.metadata")
    if not isinstance(envelope.get("identity"), dict):
        fail("evidence envelope identity must be an object")
    scan = envelope.get("prohibitedContentScan")
    if not isinstance(scan, dict) or scan.get("result") != "PASS" or not all(isinstance(scan.get(field), str) and scan[field] for field in ("scannerId", "scannerVersion", "reportDigestSha256")):
        fail("evidence envelope prohibitedContentScan is invalid")
    value_free({"scannerId": scan["scannerId"], "scannerVersion": scan["scannerVersion"]}, "$.prohibitedContentScan")
    require_hex(scan["reportDigestSha256"], "prohibitedContentScan.reportDigestSha256")
    if not isinstance(envelope.get("typePayload"), dict):
        fail("evidence envelope typePayload must be an object")
    value_free(envelope["identity"], "$.identity")
    value_free(envelope["typePayload"], "$.typePayload")
    row = next(row for row in binding["rows"] if row["scenarioId"] == key[0])
    if result == "EXCEPTION" or (row["gateClass"] == "Hard" and result == "EXCEPTION"):
        fail("EXCEPTION evidence result is reserved for approved Conditional exception flow")
    validate_type_payload(envelope, binding, row)
    return envelope


def require_payload_fields(payload: dict[str, Any], fields: Iterable[str], scenario: str) -> None:
    missing = [field for field in fields if field not in payload]
    if missing:
        fail(f"{scenario}: typePayload missing fields: {','.join(missing)}")


def validate_migration_payload(envelope: dict[str, Any], binding: dict[str, Any], scenario: str, payload: dict[str, Any]) -> None:
    common = {"migrationDecision", "migrationInventory", "expectedFullMigrationInventory", "expectedThrough011", "expectedPost011Inventory", "migrationDirectoryInventoryBefore", "migrationDirectoryInventoryDigestSha256", "migrationFileDigests", "outcome", "preApplyAppliedMigrations", "preApplyPendingMigrations", "postApplyAppliedMigrations", "postApplyPendingMigrations", "lastAppliedBefore", "lastAppliedAfter"}
    extra = {"addedColumnsByMigration", "createdTableByMigration", "piiValueCanaryResult", "contractResult"} if scenario == "G456-44" else set()
    if set(payload) - common - extra:
        fail(f"{scenario}: unknown migration typePayload field")
    require_payload_fields(payload, common, scenario)
    if payload["migrationDecision"] != "INCLUDE" or payload["migrationInventory"] != MIGRATION_POST011 or payload["expectedFullMigrationInventory"] != MIGRATION_FULL_INVENTORY or payload["expectedThrough011"] != MIGRATION_FULL_INVENTORY[:11] or payload["expectedPost011Inventory"] != MIGRATION_POST011:
        fail(f"{scenario}: migration inventory contract mismatch")
    if payload["migrationDirectoryInventoryBefore"] != MIGRATION_FULL_INVENTORY or payload["migrationDirectoryInventoryDigestSha256"] != binding["migrationInventoryDigestSha256"] or payload["migrationFileDigests"] != binding["migrationFileDigests"]:
        fail(f"{scenario}: migration PIN evidence mismatch")
    if scenario == "G456-42":
        success = payload["outcome"] == "applied" and payload["preApplyAppliedMigrations"] == [] and payload["preApplyPendingMigrations"] == MIGRATION_FULL_INVENTORY and payload["postApplyAppliedMigrations"] == MIGRATION_FULL_INVENTORY and payload["postApplyPendingMigrations"] == [] and payload["lastAppliedBefore"] is None and payload["lastAppliedAfter"] == MIGRATION_POST011[-1]
        if envelope["result"] == "PASS" and not success:
            fail("G456-42: PASS requires exact fresh-apply history")
        if envelope["result"] == "FAIL" and success:
            fail("G456-42: FAIL cannot carry a fully successful history")
    elif scenario == "G456-43":
        success = payload["outcome"] == "upgraded" and payload["preApplyAppliedMigrations"] == MIGRATION_FULL_INVENTORY[:11] and payload["preApplyPendingMigrations"] == MIGRATION_POST011 and payload["postApplyAppliedMigrations"] == MIGRATION_FULL_INVENTORY and payload["postApplyPendingMigrations"] == [] and payload["lastAppliedBefore"] == MIGRATION_FULL_INVENTORY[10] and payload["lastAppliedAfter"] == MIGRATION_POST011[-1]
        if envelope["result"] == "PASS" and not success:
            fail("G456-43: PASS requires exact upgrade history")
        if envelope["result"] == "FAIL" and success:
            fail("G456-43: FAIL cannot carry a fully successful history")
    else:
        require_payload_fields(payload, ("addedColumnsByMigration", "createdTableByMigration", "piiValueCanaryResult", "contractResult"), scenario)
        added = payload["addedColumnsByMigration"]
        created = payload["createdTableByMigration"]
        schema_success = added == {"012": ["status_message TEXT NULL", "occurred_at TEXT NULL"]} and isinstance(created, dict) and created.get("013", {}).get("table") == "provider_queue_dead_letters" and created.get("013", {}).get("columns") == MIGRATION_SCHEMA_COLUMNS and created.get("013", {}).get("constraints") == MIGRATION_SCHEMA_CONSTRAINTS and created.get("013", {}).get("indexes") == MIGRATION_SCHEMA_INDEXES and payload["piiValueCanaryResult"] in {"pass", "fail"} and payload["contractResult"] in {"pass", "fail"}
        if not schema_success:
            fail("G456-44: schema/canary contract mismatch")
        success = schema_success and payload["piiValueCanaryResult"] == "pass" and payload["contractResult"] == "pass"
        if envelope["result"] == "PASS" and not success:
            fail("G456-44: PASS requires contract and PII canary PASS")
        if envelope["result"] == "FAIL" and success:
            fail("G456-44: FAIL cannot carry a fully successful schema result")


def validate_type_payload(envelope: dict[str, Any], binding: dict[str, Any], row: dict[str, Any]) -> None:
    scenario = row["scenarioId"]
    evidence_type = envelope.get("evidenceType")
    if evidence_type not in EVIDENCE_TYPES.get(scenario, ()):
        fail(f"{scenario}: evidenceType is not allowed")
    payload = envelope["typePayload"]
    if scenario in {"G456-42", "G456-43", "G456-44"}:
        validate_migration_payload(envelope, binding, scenario, payload)
        return
    required: dict[str, tuple[str, ...]] = {
        "G456-03": ("acsEnvironment", "liveSending", "sendKind", "mailSendAttempted", "testBypassUsed", "outcome", "mailboxConfirmation"),
        "G456-04": ("acsEnvironment", "sendKind", "mailSendAttempted", "testBypassUsed", "outcome", "restrictedOpsRecordId"),
        "G456-05": ("acsEnvironment", "liveSending", "sendKind", "mailSendAttempted", "testBypassUsed", "effectiveFingerprintMatch", "bundleIntegrityMatched", "doctorOrReadinessSummary", "mailboxConfirmation"),
        "G456-06": ("acsEnvironment", "mailPath", "testBypassUsed", "sendCompletedValueFree", "distinctFromSendReadyEvidenceId", "tenantStatusExportForbidden", "restrictedOpsRecordId"),
    }
    allowed: dict[str, set[str]] = {
        "G456-03": set(required["G456-03"]) | {"normalMailerPath", "restrictedOpsRecordId"},
        "G456-04": set(required["G456-04"]) | {"mailboxConfirmation"},
        "G456-05": set(required["G456-05"]),
        "G456-06": set(required["G456-06"]),
    }
    if scenario in allowed and set(payload) - allowed[scenario]:
        fail(f"{scenario}: unknown typePayload field")
    require_payload_fields(payload, required.get(scenario, ()), scenario)
    if scenario == "G456-03" and (payload["acsEnvironment"] != "Staging" or payload["liveSending"] is not False or payload["sendKind"] != "none" or payload["mailSendAttempted"] is not False or payload["testBypassUsed"] is not False or payload["mailboxConfirmation"] != "not-required" or ("normalMailerPath" in payload and not isinstance(payload["normalMailerPath"], bool)) or ("restrictedOpsRecordId" in payload and (not isinstance(payload["restrictedOpsRecordId"], str) or not payload["restrictedOpsRecordId"])) or (envelope["result"] == "PASS" and payload["outcome"] != "configuration-applied") or (envelope["result"] == "FAIL" and payload["outcome"] not in {"rejected", "failed"})):
        fail("G456-03: staging no-send predicate mismatch")
    if scenario == "G456-04" and (payload["acsEnvironment"] != "Staging" or payload["sendKind"] != "typed-fixed-synthetic" or payload["mailSendAttempted"] is not True or payload["testBypassUsed"] is not False or ("mailboxConfirmation" in payload and payload["mailboxConfirmation"] not in {"not-run", "observed-value-free"}) or not isinstance(payload["restrictedOpsRecordId"], str) or not payload["restrictedOpsRecordId"] or (envelope["result"] == "PASS" and payload["outcome"] != "completed") or (envelope["result"] == "FAIL" and payload["outcome"] == "completed")):
        fail("G456-04: staging verification predicate mismatch")
    if scenario == "G456-05" and (payload["acsEnvironment"] != "Production" or payload["liveSending"] is not True or payload["sendKind"] != "none-for-send-ready-assert" or payload["mailSendAttempted"] is not False or payload["testBypassUsed"] is not False or payload["mailboxConfirmation"] != "not-required-for-send-ready" or (envelope["result"] == "PASS" and (payload["doctorOrReadinessSummary"] != "pass" or payload["effectiveFingerprintMatch"] is not True or payload["bundleIntegrityMatched"] is not True)) or (envelope["result"] == "FAIL" and payload["doctorOrReadinessSummary"] == "pass" and payload["effectiveFingerprintMatch"] is True and payload["bundleIntegrityMatched"] is True)):
        fail("G456-05: production send-ready predicate mismatch")
    if scenario == "G456-06" and (payload["acsEnvironment"] != "Production" or payload["mailPath"] != "normal-mailer" or payload["testBypassUsed"] is not False or payload["tenantStatusExportForbidden"] is not True or not isinstance(payload["restrictedOpsRecordId"], str) or not payload["restrictedOpsRecordId"] or (envelope["result"] == "PASS" and payload["sendCompletedValueFree"] is not True) or (envelope["result"] == "FAIL" and payload["sendCompletedValueFree"] is True)):
        fail("G456-06: release OV predicate mismatch")
    if scenario not in {"G456-03", "G456-04", "G456-05", "G456-06", "G456-42", "G456-43", "G456-44"}:
        if set(payload) != {"predicateResult"}:
            fail(f"{scenario}: generic typePayload must contain only predicateResult")
        if envelope["result"] == "PASS" and scenario not in IMPLEMENTED_SCENARIO_VALIDATORS:
            fail(f"{scenario}: scenario-specific validator is not registered")
        if payload.get("predicateResult") not in {"PASS", "FAIL"} or payload["predicateResult"] != ("PASS" if envelope["result"] == "PASS" else "FAIL"):
            fail(f"{scenario}: predicateResult must agree with evidence result")


def validate_release_ov_reference(evidence: dict[str, dict[str, Any]], active: dict[tuple[str, str], str | None], evidence_id: str) -> None:
    payload = evidence[evidence_id]["typePayload"]
    reference_id = payload.get("distinctFromSendReadyEvidenceId")
    expected_id = active.get(("G456-05", "acs-production"))
    if not isinstance(reference_id, str) or reference_id != expected_id or reference_id == evidence_id:
        fail("G456-06: distinctFromSendReadyEvidenceId must reference active G456-05 evidence")
    reference = evidence.get(reference_id)
    if reference is None or reference.get("qualificationRunId") != evidence[evidence_id].get("qualificationRunId") or reference.get("bindingId") != evidence[evidence_id].get("bindingId") or reference.get("scenarioId") != "G456-05" or reference.get("variantId") != "acs-production" or reference.get("result") != "PASS":
        fail("G456-06: send-ready reference identity/result mismatch")


def derive_aggregation(binding: dict[str, Any], active: dict[tuple[str, str], str | None], active_exceptions: dict[tuple[str, str], str | None], evidence: dict[str, dict[str, Any]]) -> tuple[list[dict[str, Any]], bool]:
    scenario_index: list[dict[str, Any]] = []
    machine_go = True
    for row in binding["rows"]:
        scenario = row["scenarioId"]
        variant_results = []
        for variant in row["requiredVariants"]:
            evidence_id = active.get((scenario, variant))
            exception_id = active_exceptions.get((scenario, variant))
            if evidence_id is not None and exception_id is not None:
                fail(f"{scenario}/{variant}: active evidence and exception cannot coexist")
            if scenario in {"G456-42", "G456-43", "G456-44"} and exception_id is not None:
                fail(f"{scenario}/{variant}: Hard migration rows cannot use exceptions")
            if scenario == "G456-06" and evidence_id is not None and evidence[evidence_id]["result"] == "PASS":
                validate_release_ov_reference(evidence, active, evidence_id)
            result = evidence[evidence_id]["result"] if evidence_id else ("EXCEPTION" if exception_id else "NOT_RUN")
            variant_results.append({"variantId": variant, "result": result, "evidenceId": evidence_id, "exceptionId": exception_id, "required": True})
            if row["gateClass"] == "Hard" and result != "PASS":
                machine_go = False
            if row["gateClass"] == "Conditional" and result not in {"PASS", "EXCEPTION"}:
                machine_go = False
        scenario_index.append({"scenarioId": scenario, "gateClass": row["gateClass"], "variants": variant_results, "scenarioResult": "NOT_CONFIRMED" if not variant_results and row["gateClass"] == "Informational" else ("PASS" if all(v["result"] in {"PASS", "EXCEPTION"} for v in variant_results) else "INCOMPLETE")})
    return scenario_index, machine_go


def command_evidence(args: argparse.Namespace) -> None:
    run_root = Path(args.run_root).resolve()
    ensure_unsealed(run_root)
    binding = load_binding(run_root)
    auth = load_authorization(run_root)
    evidence_id = require_hex(args.evidence_id, "evidence-id")
    key = (require_arg(args.scenario_id, "scenario-id"), require_arg(args.variant_id, "variant-id"))
    if key not in allowed_keys(binding):
        fail("evidence key is not bound")
    if not args.observations:
        fail("full common evidence envelope is required via --observations")
    evidence = read_json(Path(args.observations), "evidence envelope")
    if not isinstance(evidence, dict):
        fail("evidence envelope must be an object")
    validate_evidence_envelope(evidence, binding, auth, key)
    if evidence.get("evidenceId") != evidence_id or evidence.get("result") != args.result or evidence.get("executedByRole") != args.executed_by_role or evidence.get("executedByIdentity") != args.executed_by_identity:
        fail("CLI evidence identity/result/actor does not match envelope")
    write_once(run_root / "evidence" / f"{evidence_id}.json", evidence)
    print(json.dumps({"evidenceId": evidence_id, "scenarioId": key[0], "variantId": key[1], "result": evidence["result"]}, sort_keys=True))


def owner_for(auth: dict[str, Any], key: tuple[str, str]) -> dict[str, str]:
    matches = [e for e in auth.get("evidenceOwners", []) if (e.get("scenarioId"), e.get("variantId")) == key]
    if len(matches) != 1:
        fail("authorization owner is missing or duplicated")
    return matches[0]


def actor_matches(event: dict[str, Any], role: str, identity: str) -> bool:
    return event.get("approvedByRole") == role and event.get("approvedByIdentity") == identity


def command_disposition(args: argparse.Namespace) -> None:
    run_root = Path(args.run_root).resolve()
    ensure_unsealed(run_root)
    binding = load_binding(run_root)
    auth = load_authorization(run_root)
    active, evidence, last_sequence, last_digest = replay(run_root)
    scenario = require_arg(args.scenario_id, "scenario-id")
    variant = require_arg(args.variant_id, "variant-id")
    key = (scenario, variant)
    action = args.action
    target = None
    restore_id = None
    if action == "restore":
        if args.target_evidence_id or args.superseded_by_evidence_id:
            fail("restore cannot include target or replacement evidence")
        restore_id = require_event_id(args.restores_event_id, "restores-event-id")
        restore_event = next((event for _, event in load_dispositions(run_root) if event.get("eventId") == restore_id), None)
        if restore_event is None or (restore_event.get("scenarioId"), restore_event.get("variantId")) != key:
            fail("restore event does not match scenario/variant")
    else:
        if args.restores_event_id:
            fail("non-restore disposition cannot include restores-event-id")
        target = require_hex(args.target_evidence_id, "target-evidence-id")
        if target not in evidence or (evidence[target].get("scenarioId"), evidence[target].get("variantId")) != key:
            fail("target evidence does not match scenario/variant")
    incoming = args.superseded_by_evidence_id
    if action == "accept" and active.get(key) is not None:
        fail("accept is only valid when the key has no active evidence")
    if action == "supersede":
        if active.get(key) != target or not incoming:
            fail("supersede requires the active target and replacement evidence")
        incoming = require_hex(incoming, "superseded-by-evidence-id")
        if incoming not in evidence or (evidence[incoming].get("scenarioId"), evidence[incoming].get("variantId")) != key:
            fail("replacement evidence does not match scenario/variant")
    elif args.superseded_by_evidence_id:
        fail("only supersede may include a replacement evidence id")
    if action not in {"accept", "supersede", "invalidate", "restore"}:
        fail("unsupported disposition action")
    if action in {"invalidate", "restore"}:
        role = auth["qualificationLeadRole"]
        identity = auth["qualificationLeadIdentity"]
    else:
        owner = owner_for(auth, key)
        role = owner["ownerRole"]
        identity = owner["ownerIdentity"]
        incoming_id = incoming if action == "supersede" else target
        if evidence[incoming_id].get("result") == "PASS" and active.get(key) is not None and evidence[active[key]].get("result") == "FAIL":
            role = auth["qualificationLeadRole"]
            identity = auth["qualificationLeadIdentity"]
    approved_role = require_value_free_identity(args.approved_by_role, "approved-by-role")
    approved_identity = require_value_free_identity(args.approved_by_identity, "approved-by-identity")
    if not actor_matches({"approvedByRole": approved_role, "approvedByIdentity": approved_identity}, role, identity):
        fail("disposition actor is not authorized for this transition")
    event: dict[str, Any] = {
        "eventId": uuid.uuid4().hex,
        "qualificationRunId": binding["qualificationRunId"],
        "bindingId": binding["bindingId"],
        "candidateId": binding["candidateId"],
        "eventSequence": last_sequence + 1,
        "previousEventDigestSha256": last_digest,
        "canonicalization": JCS_VERSION,
        "scenarioId": scenario,
        "variantId": variant,
        "action": action,
        "reasonCode": require_value_free_identity(args.reason_code, "reason-code"),
        "approvedByRole": approved_role,
        "approvedByIdentity": approved_identity,
        "approvedAtUtc": utc_now(),
    }
    if target is not None:
        event["targetEvidenceId"] = target
    if action == "supersede":
        event["supersededByEvidenceId"] = incoming
    if restore_id is not None:
        event["restoresEventId"] = restore_id
    event["eventDigestSha256"] = sha_object(event)
    write_once(run_root / "dispositions" / f"{event['eventId']}.json", event)
    print(json.dumps({"eventId": event["eventId"], "eventSequence": event["eventSequence"]}, sort_keys=True))


def command_exception(args: argparse.Namespace) -> None:
    run_root = Path(args.run_root).resolve()
    ensure_unsealed(run_root)
    binding = load_binding(run_root)
    auth = load_authorization(run_root)
    scenario = require_arg(args.scenario_id, "scenario-id")
    variant = require_arg(args.variant_id, "variant-id")
    row = next((row for row in binding.get("rows", []) if row.get("scenarioId") == scenario), None)
    if row is None or row.get("gateClass") != "Conditional" or variant not in row.get("requiredVariants", []):
        fail("exception key must be a required Conditional variant")
    if scenario in {"G456-42", "G456-43", "G456-44"}:
        fail("migration rows cannot use exceptions")
    owner = owner_for(auth, (scenario, variant))
    created_by_role = require_value_free_identity(args.created_by_role, "created-by-role")
    created_by_identity = require_value_free_identity(args.created_by_identity, "created-by-identity")
    if created_by_role != owner["ownerRole"] or created_by_identity != owner["ownerIdentity"]:
        fail("exception creator is not the evidence owner")
    exception_id = require_hex(args.exception_id, "exception-id")
    exception = {
        "schemaVersion": 1,
        "exceptionId": exception_id,
        "qualificationRunId": binding["qualificationRunId"],
        "bindingId": binding["bindingId"],
        "candidateId": binding["candidateId"],
        "issueBodySha256": binding["issueBodySha256"],
        "planCommitSha": binding["planCommitSha"],
        "planFileSha256": binding["planFileSha256"],
        "scenarioId": scenario,
        "variantId": variant,
        "createdByRole": created_by_role,
        "createdByIdentity": created_by_identity,
        "reasonNotExecutable": require_arg(args.reason_not_executable, "reason-not-executable"),
        "alternateVerification": require_arg(args.alternate_verification, "alternate-verification"),
        "residualRisk": require_arg(args.residual_risk, "residual-risk"),
        "impactScope": require_arg(args.impact_scope, "impact-scope"),
        "createdAtUtc": utc_now(),
    }
    value_free(exception["reasonNotExecutable"], "reasonNotExecutable")
    value_free(exception["alternateVerification"], "alternateVerification")
    value_free(exception["residualRisk"], "residualRisk")
    value_free(exception["impactScope"], "impactScope")
    write_once(run_root / "exceptions" / f"{exception_id}.json", exception)
    print(json.dumps({"exceptionId": exception_id, "scenarioId": scenario, "variantId": variant}, sort_keys=True))


def command_exception_disposition(args: argparse.Namespace) -> None:
    run_root = Path(args.run_root).resolve()
    ensure_unsealed(run_root)
    binding = load_binding(run_root)
    auth = load_authorization(run_root)
    active, exceptions, last_sequence, last_digest = replay_exceptions(run_root)
    scenario = require_arg(args.scenario_id, "scenario-id")
    variant = require_arg(args.variant_id, "variant-id")
    key = (scenario, variant)
    action = args.action
    target = None
    restore_id = None
    if action == "restore":
        if args.target_exception_id or args.superseded_by_exception_id:
            fail("restore cannot include target or replacement exception")
        restore_id = require_event_id(args.restores_exception_event_id, "restores-exception-event-id")
        restore_event = next((event for _, event in load_exception_dispositions(run_root) if event.get("eventId") == restore_id), None)
        if restore_event is None or (restore_event.get("scenarioId"), restore_event.get("variantId")) != key:
            fail("restore event does not match scenario/variant")
    else:
        if args.restores_exception_event_id:
            fail("non-restore exception disposition cannot include restores-exception-event-id")
        target = require_hex(args.target_exception_id, "target-exception-id")
        if target not in exceptions or (exceptions[target].get("scenarioId"), exceptions[target].get("variantId")) != key:
            fail("target exception does not match scenario/variant")
    replacement = args.superseded_by_exception_id
    if action == "approve" and active.get(key) is not None:
        fail("approve is only valid when no exception is active")
    if action == "supersede":
        if active.get(key) != target or not replacement:
            fail("supersede requires the active target and replacement exception")
        replacement = require_hex(replacement, "superseded-by-exception-id")
        if replacement not in exceptions or (exceptions[replacement].get("scenarioId"), exceptions[replacement].get("variantId")) != key:
            fail("replacement exception does not match scenario/variant")
    elif args.superseded_by_exception_id:
        fail("only supersede may include a replacement exception id")
    if action not in {"approve", "supersede", "revoke", "restore"}:
        fail("unsupported exception disposition action")
    approved_role = require_value_free_identity(args.approved_by_role, "approved-by-role")
    approved_identity = require_value_free_identity(args.approved_by_identity, "approved-by-identity")
    if approved_role != auth["conditionalApproverRole"] or approved_identity != auth["conditionalApproverIdentity"]:
        fail("exception disposition actor is not the conditional approver")
    event: dict[str, Any] = {
        "eventId": uuid.uuid4().hex,
        "qualificationRunId": binding["qualificationRunId"],
        "bindingId": binding["bindingId"],
        "candidateId": binding["candidateId"],
        "exceptionEventSequence": last_sequence + 1,
        "previousExceptionEventDigestSha256": last_digest,
        "canonicalization": JCS_VERSION,
        "scenarioId": scenario,
        "variantId": variant,
        "action": action,
        "reasonCode": require_value_free_identity(args.reason_code, "reason-code"),
        "approvedByRole": approved_role,
        "approvedByIdentity": approved_identity,
        "approvedAtUtc": utc_now(),
    }
    if target is not None:
        event["targetExceptionId"] = target
        event["targetExceptionSha256"] = file_sha(run_root / "exceptions" / f"{target}.json")
    if action == "supersede":
        event["supersededByExceptionId"] = replacement
        event["supersededByExceptionSha256"] = file_sha(run_root / "exceptions" / f"{replacement}.json")
    if restore_id is not None:
        event["restoresExceptionEventId"] = restore_id
    event["eventDigestSha256"] = sha_object(event)
    write_once(run_root / "exception-dispositions" / f"{event['eventId']}.json", event)
    print(json.dumps({"eventId": event["eventId"], "exceptionEventSequence": event["exceptionEventSequence"]}, sort_keys=True))


def current_state(run_root: Path) -> tuple[dict[tuple[str, str], str | None], dict[str, dict[str, Any]], int, str | None]:
    return replay(run_root)


def command_seal(args: argparse.Namespace) -> None:
    run_root = Path(args.run_root).resolve()
    ensure_unsealed(run_root)
    binding = load_binding(run_root)
    auth = load_authorization(run_root)
    migration_pin = load_migration_pin(run_root / "migration-pin.json")
    verify_migration_pin_tree(Path(args.repo_root).resolve(), binding["releaseCommitSha"], migration_pin)
    active, evidence, last_sequence, last_digest = current_state(run_root)
    active_exceptions, exceptions, exception_last_sequence, exception_last_digest = replay_exceptions(run_root)
    current_issue = Path(args.current_issue_snapshot).resolve()
    snapshot = read_json(current_issue, "current issue snapshot")
    current_sha = sha_bytes(require_string(snapshot, "body").encode("utf-8"))
    if current_sha != binding["issueBodySha256"]:
        fail("issue freshness mismatch; create a new binding and qualificationRunId")
    scenario_index, machine_go = derive_aggregation(binding, active, active_exceptions, evidence)
    machine_verdict = "GO_ELIGIBLE" if machine_go else "NO_GO"
    human = args.human_decision
    if human not in {"APPROVE", "REJECT", "NOT_DECIDED"}:
        fail("human-decision must be APPROVE, REJECT, or NOT_DECIDED")
    if machine_verdict == "GO_ELIGIBLE" and human != "APPROVE":
        fail("GO_ELIGIBLE requires humanDecision=APPROVE")
    if machine_verdict == "NO_GO" and human == "APPROVE":
        fail("NO_GO cannot be human-approved")
    approved_role = require_value_free_identity(args.approved_by_role, "approved-by-role")
    approved_identity = require_value_free_identity(args.approved_by_identity, "approved-by-identity")
    if approved_role != auth["qualificationLeadRole"] or approved_identity != auth["qualificationLeadIdentity"]:
        fail("seal approver does not match authorization snapshot")
    evidence_index = {
        "schemaVersion": 1,
        "qualificationRunId": binding["qualificationRunId"],
        "bindingId": binding["bindingId"],
        "candidateId": binding["candidateId"],
        "issueBodySha256": binding["issueBodySha256"],
        "entries": [{"scenarioId": s, "variantId": v, "evidenceId": active.get((s, v)), "exceptionId": active_exceptions.get((s, v)), "result": evidence[active[(s, v)]]["result"] if active.get((s, v)) else ("EXCEPTION" if active_exceptions.get((s, v)) else "NOT_RUN")} for s, v in sorted(allowed_keys(binding))],
        "createdAtUtc": utc_now(),
    }
    write_once(run_root / "decision" / "evidence-index.json", evidence_index)
    evidence_index_file_sha = file_sha(run_root / "decision/evidence-index.json")
    go_no_go = {
        "schemaVersion": 1,
        "candidateId": binding["candidateId"],
        "bindingId": binding["bindingId"],
        "qualificationRunId": binding["qualificationRunId"],
        "sourceCommitSha": binding["releaseCommitSha"],
        "ociIndexDigest": binding["ociIndexDigest"],
        "authorizationDigestSha256": binding["authorizationDigestSha256"],
        "evidenceIndexDigestSha256": evidence_index_file_sha,
        "machineVerdict": machine_verdict,
        "humanDecision": human,
        "runSealed": True,
        "issueFreshnessCheck": {"matchedBinding": True, "currentIssueBodySha256": current_sha},
        "scenarioIndex": scenario_index,
        "createdAtUtc": utc_now(),
    }
    write_once(run_root / "decision" / "go-no-go.json", go_no_go)
    write_once(run_root / "indexes" / "evidence-index-v1.json", evidence_index)
    phase3_index_sha = file_sha(run_root / "indexes/evidence-index-v1.json")
    write_once(run_root / "phase-manifests" / "phase-3-v1.json", {"schemaVersion": 1, "phase": 3, "qualificationRunId": binding["qualificationRunId"], "latestEvidenceIndexSha256": phase3_index_sha, "createdAtUtc": utc_now()})
    inventory = object_inventory(run_root)
    phase4 = {
        "schemaVersion": 1,
        "qualificationRunId": binding["qualificationRunId"],
        "bindingId": binding["bindingId"],
        "candidateId": binding["candidateId"],
        "createdAtUtc": utc_now(),
        "sealedObjectInventory": inventory,
        "finalRunState": {
            "evidenceObjectCount": len(evidence_paths(run_root)),
            "evidenceRootSha256": object_root([e for e in inventory if e["path"].startswith("evidence/")]),
            "dispositionLastSequence": last_sequence,
            "dispositionLastDigestSha256": last_digest,
            "exceptionObjectCount": len(exception_paths(run_root)),
            "exceptionRootSha256": object_root([e for e in inventory if e["path"].startswith("exceptions/")]),
            "exceptionDispositionLastSequence": exception_last_sequence,
            "exceptionDispositionLastDigestSha256": exception_last_digest,
            "scanObjectCount": 0,
            "scanRootSha256": object_root([]),
            "phase3LatestIndexSha256": phase3_index_sha,
            "finalEvidenceIndexSha256": evidence_index_file_sha,
            "goNoGoSha256": file_sha(run_root / "decision/go-no-go.json"),
        },
        "decisionObjectPaths": {"evidenceIndex": "decision/evidence-index.json", "goNoGo": "decision/go-no-go.json"},
        "rootDigestAlgorithm": ROOT_DIGEST_ALGORITHM,
    }
    write_once(run_root / "phase-manifests" / "phase-4.json", phase4)
    phase4_sha = file_sha(run_root / "phase-manifests" / "phase-4.json")
    event_id = uuid.uuid4().hex
    event = {
        "eventId": event_id,
        "qualificationRunId": binding["qualificationRunId"],
        "bindingId": binding["bindingId"],
        "candidateId": binding["candidateId"],
        "runStatusEventSequence": 1,
        "previousRunStatusEventDigestSha256": None,
        "canonicalization": JCS_VERSION,
        "status": "sealed",
        "sealedAtUtc": utc_now(),
        "decisionDigests": {"evidenceIndexSha256": file_sha(run_root / "decision/evidence-index.json"), "goNoGoSha256": file_sha(run_root / "decision/go-no-go.json"), "phase4ManifestSha256": phase4_sha},
        "approvedByRole": approved_role,
        "approvedByIdentity": approved_identity,
        "approvedAtUtc": utc_now(),
    }
    event["eventDigestSha256"] = sha_object(event)
    write_once(run_root / "run-status-events" / f"{event_id}.json", event)
    print(json.dumps({"qualificationRunId": binding["qualificationRunId"], "machineVerdict": machine_verdict, "humanDecision": human, "sealedEventId": event_id}, sort_keys=True))


def command_verify(args: argparse.Namespace) -> None:
    run_root = Path(args.run_root).resolve()
    binding = load_binding(run_root)
    auth = load_authorization(run_root)
    migration_pin = load_migration_pin(run_root / "migration-pin.json")
    verify_migration_pin_tree(Path(args.repo_root).resolve(), binding["releaseCommitSha"], migration_pin)
    decision = read_json(run_root / "decision/go-no-go.json", "decision/go-no-go.json")
    phase4 = read_json(run_root / "phase-manifests/phase-4.json", "phase-manifests/phase-4.json")
    events = run_status_events(run_root)
    if len(events) != 1:
        fail("sealed run requires exactly one run-status event")
    event = read_json(events[0], "sealed run-status event")
    if not isinstance(event, dict) or events[0].stem != require_event_id(event.get("eventId"), "sealed run-status event.eventId"):
        fail("sealed run-status event filename/id mismatch")
    if event.get("status") != "sealed" or event.get("runStatusEventSequence") != 1:
        fail("run-status event is not a terminal sealed event")
    if event.get("previousRunStatusEventDigestSha256") is not None:
        fail("sealed run-status event must be the first event")
    if event.get("canonicalization") != JCS_VERSION:
        fail("sealed run-status event canonicalization mismatch")
    for field in ("sealedAtUtc", "approvedAtUtc"):
        if not isinstance(event.get(field), str) or not UTC_TIMESTAMP.fullmatch(event[field]):
            fail(f"sealed run-status event {field} must be UTC RFC3339 seconds")
    for field in ("qualificationRunId", "bindingId", "candidateId"):
        if event.get(field) != binding.get(field):
            fail(f"sealed run-status event {field} mismatch")
    if event.get("approvedByRole") != auth.get("qualificationLeadRole") or event.get("approvedByIdentity") != auth.get("qualificationLeadIdentity"):
        fail("sealed run-status event approver mismatch")
    event_digest = require_hex(require_string(event, "eventDigestSha256"), "eventDigestSha256")
    if sha_object({k: v for k, v in event.items() if k != "eventDigestSha256"}) != event_digest:
        fail("sealed event digest mismatch")
    digests = event.get("decisionDigests")
    if not isinstance(digests, dict) or set(digests) != {"evidenceIndexSha256", "goNoGoSha256", "phase4ManifestSha256"} or any(not isinstance(value, str) or not HEX64.fullmatch(value) for value in digests.values()):
        fail("sealed decision digests are invalid")
    if digests.get("evidenceIndexSha256") != file_sha(run_root / "decision/evidence-index.json") or digests.get("goNoGoSha256") != file_sha(run_root / "decision/go-no-go.json") or digests.get("phase4ManifestSha256") != file_sha(run_root / "phase-manifests/phase-4.json"):
        fail("sealed decision digest mismatch")
    inventory = phase4.get("sealedObjectInventory")
    if not isinstance(inventory, list):
        fail("phase-4 sealedObjectInventory missing")
    for field in ("qualificationRunId", "bindingId", "candidateId"):
        if phase4.get(field) != binding.get(field):
            fail(f"phase-4 {field} mismatch")
    listed = {entry.get("path") for entry in inventory}
    if len(listed) != len(inventory) or any(not isinstance(entry, dict) or not isinstance(entry.get("path"), str) or not HEX64.fullmatch(str(entry.get("sha256", ""))) for entry in inventory):
        fail("phase-4 sealedObjectInventory entry is invalid")
    actual = {entry["path"] for entry in object_inventory(run_root)}
    if listed != actual:
        fail("sealed object inventory mismatch")
    for entry in inventory:
        path = safe_child(run_root, entry["path"])
        if file_sha(path) != entry.get("sha256"):
            fail(f"sealed object digest mismatch: {entry['path']}")
    if phase4.get("rootDigestAlgorithm") != ROOT_DIGEST_ALGORITHM:
        fail("phase-4 root digest algorithm mismatch")
    final_state = phase4.get("finalRunState") or {}
    active, evidence, disposition_sequence, disposition_digest = replay(run_root)
    active_exceptions, exceptions, exception_sequence, exception_digest = replay_exceptions(run_root)
    expected_scenario_index, expected_machine_go = derive_aggregation(binding, active, active_exceptions, evidence)
    expected_machine_verdict = "GO_ELIGIBLE" if expected_machine_go else "NO_GO"
    if decision.get("machineVerdict") != expected_machine_verdict or decision.get("scenarioIndex") != expected_scenario_index:
        fail("go/no-go aggregation does not match replayed active state")
    expected_entries = [{"scenarioId": scenario, "variantId": variant, "evidenceId": active.get((scenario, variant)), "exceptionId": active_exceptions.get((scenario, variant)), "result": evidence[active[(scenario, variant)]]["result"] if active.get((scenario, variant)) else ("EXCEPTION" if active_exceptions.get((scenario, variant)) else "NOT_RUN")} for scenario, variant in sorted(allowed_keys(binding))]
    evidence_index = read_json(run_root / "decision/evidence-index.json", "decision/evidence-index.json")
    if evidence_index.get("entries") != expected_entries or evidence_index.get("candidateId") != binding.get("candidateId") or evidence_index.get("bindingId") != binding.get("bindingId") or evidence_index.get("qualificationRunId") != binding.get("qualificationRunId"):
        fail("evidence index does not match replayed active state")
    if final_state.get("evidenceObjectCount") != len(evidence_paths(run_root)) or final_state.get("evidenceRootSha256") != object_root([entry for entry in inventory if entry["path"].startswith("evidence/")]):
        fail("phase-4 evidence high-water mark mismatch")
    if final_state.get("dispositionLastSequence") != disposition_sequence or final_state.get("dispositionLastDigestSha256") != disposition_digest:
        fail("phase-4 disposition high-water mark mismatch")
    if final_state.get("exceptionObjectCount") != len(exception_paths(run_root)) or final_state.get("exceptionRootSha256") != object_root([entry for entry in inventory if entry["path"].startswith("exceptions/")]):
        fail("phase-4 exception high-water mark mismatch")
    if final_state.get("exceptionDispositionLastSequence") != exception_sequence or final_state.get("exceptionDispositionLastDigestSha256") != exception_digest:
        fail("phase-4 exception disposition high-water mark mismatch")
    if final_state.get("scanObjectCount") != len([entry for entry in inventory if entry["path"].startswith("scans/")]) or final_state.get("scanRootSha256") != object_root([entry for entry in inventory if entry["path"].startswith("scans/")]):
        fail("phase-4 scan high-water mark mismatch")
    if final_state.get("finalEvidenceIndexSha256") != file_sha(run_root / "decision/evidence-index.json"):
        fail("phase-4 final evidence index mismatch")
    if final_state.get("goNoGoSha256") != file_sha(run_root / "decision/go-no-go.json"):
        fail("phase-4 go/no-go mismatch")
    if decision.get("runSealed") is not True:
        fail("go/no-go runSealed must be true")
    for field in ("qualificationRunId", "bindingId", "candidateId"):
        if decision.get(field) != binding.get(field):
            fail(f"go/no-go {field} mismatch")
    if decision.get("sourceCommitSha") != binding.get("releaseCommitSha") or decision.get("ociIndexDigest") != binding.get("ociIndexDigest"):
        fail("go/no-go release identity mismatch")
    print(json.dumps({"qualificationRunId": binding["qualificationRunId"], "candidateId": binding["candidateId"], "sealedEventId": event["eventId"], "machineVerdict": decision.get("machineVerdict"), "humanDecision": decision.get("humanDecision")}, sort_keys=True))


def command_abandon(args: argparse.Namespace) -> None:
    run_root = Path(args.run_root).resolve()
    ensure_unsealed(run_root)
    binding = load_binding(run_root)
    auth = load_authorization(run_root)
    reason = require_value_free_identity(args.reason_code, "reason-code")
    event_id = uuid.uuid4().hex
    event = {
        "eventId": event_id,
        "qualificationRunId": binding["qualificationRunId"],
        "bindingId": binding["bindingId"],
        "candidateId": binding["candidateId"],
        "runStatusEventSequence": 1,
        "previousRunStatusEventDigestSha256": None,
        "canonicalization": JCS_VERSION,
        "status": "abandoned-phase4-incomplete" if args.phase4_incomplete else "abandoned-other",
        "reasonCode": reason,
        "approvedByRole": auth["qualificationLeadRole"],
        "approvedByIdentity": auth["qualificationLeadIdentity"],
        "approvedAtUtc": utc_now(),
    }
    event["eventDigestSha256"] = sha_object(event)
    write_once(run_root / "run-status-events" / f"{event_id}.json", event)
    print(json.dumps({"qualificationRunId": binding["qualificationRunId"], "status": event["status"], "eventId": event_id}, sort_keys=True))


def command_handoff(args: argparse.Namespace) -> None:
    run_root = Path(args.run_root).resolve()
    command_verify(argparse.Namespace(run_root=str(run_root), repo_root=args.repo_root))
    decision = read_json(run_root / "decision/go-no-go.json", "decision/go-no-go.json")
    if decision.get("machineVerdict") != "GO_ELIGIBLE" or decision.get("humanDecision") != "APPROVE":
        fail("only an approved GO_ELIGIBLE run may produce a qualification handoff")
    output = Path(args.output_root).resolve()
    if output.exists() and any(output.iterdir()):
        fail("handoff output must be empty")
    output.mkdir(parents=True, exist_ok=True)
    binding = load_binding(run_root)
    event_path = run_status_events(run_root)[0]
    for source, target in ((run_root / "binding.json", output / "binding.json"), (run_root / "decision/go-no-go.json", output / "decision/go-no-go.json"), (event_path, output / "run-status-events" / event_path.name)):
        target.parent.mkdir(parents=True, exist_ok=True)
        write_bytes_once(target, source.read_bytes())
    manifest = {"schemaVersion": 1, "publicationOnly": True, "candidateId": binding["candidateId"], "bindingId": binding["bindingId"], "qualificationRunId": binding["qualificationRunId"], "sealedEventId": read_json(event_path, "sealed event")["eventId"], "objects": [{"path": p.relative_to(output).as_posix(), "sha256": file_sha(p)} for p in sorted(output.rglob("*.json"))]}
    write_once(output / "handoff-manifest.json", manifest)
    print(json.dumps({"qualificationRunId": binding["qualificationRunId"], "publicationOnly": True}, sort_keys=True))


def build_parser() -> argparse.ArgumentParser:
    parser = argparse.ArgumentParser(description=__doc__)
    sub = parser.add_subparsers(dest="command", required=True)
    intake = sub.add_parser("intake")
    intake.add_argument("--candidate-root", required=True)
    intake.add_argument("--store-root", required=True)
    intake.add_argument("--release-commit-sha", required=True)
    intake.add_argument("--expected-oci-digest", required=True)
    intake.add_argument("--expected-workflow-ref", required=True)
    intake.set_defaults(func=command_intake)
    bind = sub.add_parser("bind")
    bind.add_argument("--store-root", required=True)
    bind.add_argument("--candidate-id", required=True)
    bind.add_argument("--issue-snapshot", required=True)
    bind.add_argument("--plan-file", required=True)
    bind.add_argument("--plan-commit-sha", required=True)
    bind.add_argument("--repo-root", required=True)
    bind.add_argument("--migration-pin", required=True)
    bind.add_argument("--run-attempt-nonce", required=True)
    bind.add_argument("--evidence-owners", required=True)
    bind.add_argument("--qualification-lead-role", required=True)
    bind.add_argument("--qualification-lead-identity", required=True)
    bind.add_argument("--conditional-approver-role", required=True)
    bind.add_argument("--conditional-approver-identity", required=True)
    bind.set_defaults(func=command_bind)
    evidence = sub.add_parser("evidence")
    evidence.add_argument("--run-root", required=True)
    evidence.add_argument("--evidence-id", required=True)
    evidence.add_argument("--scenario-id", required=True)
    evidence.add_argument("--variant-id", required=True)
    evidence.add_argument("--result", required=True)
    evidence.add_argument("--executed-by-role", required=True)
    evidence.add_argument("--executed-by-identity", required=True)
    evidence.add_argument("--observations")
    evidence.set_defaults(func=command_evidence)
    disposition = sub.add_parser("disposition")
    disposition.add_argument("--run-root", required=True)
    disposition.add_argument("--scenario-id", required=True)
    disposition.add_argument("--variant-id", required=True)
    disposition.add_argument("--action", required=True)
    disposition.add_argument("--target-evidence-id")
    disposition.add_argument("--restores-event-id")
    disposition.add_argument("--superseded-by-evidence-id")
    disposition.add_argument("--reason-code", required=True)
    disposition.add_argument("--approved-by-role", required=True)
    disposition.add_argument("--approved-by-identity", required=True)
    disposition.set_defaults(func=command_disposition)
    exception = sub.add_parser("exception")
    exception.add_argument("--run-root", required=True)
    exception.add_argument("--exception-id", required=True)
    exception.add_argument("--scenario-id", required=True)
    exception.add_argument("--variant-id", required=True)
    exception.add_argument("--reason-not-executable", required=True)
    exception.add_argument("--alternate-verification", required=True)
    exception.add_argument("--residual-risk", required=True)
    exception.add_argument("--impact-scope", required=True)
    exception.add_argument("--created-by-role", required=True)
    exception.add_argument("--created-by-identity", required=True)
    exception.set_defaults(func=command_exception)
    exception_disposition = sub.add_parser("exception-disposition")
    exception_disposition.add_argument("--run-root", required=True)
    exception_disposition.add_argument("--scenario-id", required=True)
    exception_disposition.add_argument("--variant-id", required=True)
    exception_disposition.add_argument("--action", required=True)
    exception_disposition.add_argument("--target-exception-id")
    exception_disposition.add_argument("--restores-exception-event-id")
    exception_disposition.add_argument("--superseded-by-exception-id")
    exception_disposition.add_argument("--reason-code", required=True)
    exception_disposition.add_argument("--approved-by-role", required=True)
    exception_disposition.add_argument("--approved-by-identity", required=True)
    exception_disposition.set_defaults(func=command_exception_disposition)
    seal = sub.add_parser("seal")
    seal.add_argument("--run-root", required=True)
    seal.add_argument("--current-issue-snapshot", required=True)
    seal.add_argument("--repo-root", required=True)
    seal.add_argument("--human-decision", required=True)
    seal.add_argument("--approved-by-role", required=True)
    seal.add_argument("--approved-by-identity", required=True)
    seal.set_defaults(func=command_seal)
    verify = sub.add_parser("verify")
    verify.add_argument("--run-root", required=True)
    verify.add_argument("--repo-root", required=True)
    verify.set_defaults(func=command_verify)
    handoff = sub.add_parser("handoff")
    handoff.add_argument("--run-root", required=True)
    handoff.add_argument("--output-root", required=True)
    handoff.add_argument("--repo-root", required=True)
    handoff.set_defaults(func=command_handoff)
    abandon = sub.add_parser("abandon")
    abandon.add_argument("--run-root", required=True)
    abandon.add_argument("--reason-code", required=True)
    abandon.add_argument("--phase4-incomplete", action="store_true")
    abandon.set_defaults(func=command_abandon)
    return parser


def main(argv: list[str] | None = None) -> int:
    try:
        args = build_parser().parse_args(argv)
        args.func(args)
        return 0
    except RunnerError as exc:
        print(f"[error] {exc}", file=sys.stderr)
        return 1
    except (OSError, json.JSONDecodeError) as exc:
        print(f"[error] operation failed: {type(exc).__name__}", file=sys.stderr)
        return 1


if __name__ == "__main__":
    raise SystemExit(main())
