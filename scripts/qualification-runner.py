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
import io
import json
import os
import re
import secrets
import shutil
import subprocess
import sys
import tarfile
import unicodedata
import uuid
import zipfile
from datetime import datetime, timezone
from pathlib import Path
from typing import Any, Iterable


HEX64 = re.compile(r"^[0-9a-f]{64}$")
EVENT32 = re.compile(r"^[0-9a-f]{32}$")
SHA40 = re.compile(r"^[0-9a-f]{40}$")
SHA256_DIGEST = re.compile(r"^sha256:[0-9a-f]{64}$")
SAFE_ID = re.compile(r"^[A-Za-z0-9][A-Za-z0-9._-]{0,127}$")
UTC_TIMESTAMP = re.compile(r"^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}Z$")
RELEASE_VERSION = re.compile(r"^\d+\.\d+\.\d+$")
ARCHIVE_RIDS = {"win-x64", "linux-x64", "linux-arm64"}
OCI_PLATFORMS = {"linux/amd64", "linux/arm64"}
JCS_VERSION = {"algorithm": "RFC8785-JCS", "version": 1}
ROOT_DIGEST_ALGORITHM = "RFC8785-JCS-sorted-path-sha256/v1"
RUN_LIFECYCLE_VERSION = 2
RUN_READY_FILE = "run-ready.json"
VARIANT_RULES_VERSION = 4
SCOPE_MANIFEST_SCHEMA_VERSION = 1
LEGACY_SCOPE_ID = "v1.2.0-issue-456"
V13_SCOPE_ID = "v1.3.0-rc-qualification"
V13_SCOPE_VERSION = 1
V13_AUTHORITY_ISSUE = 583
V13_MIGRATION_FULL_INVENTORY = [
    "001_initial.sql", "002_worker_heartbeats.sql", "003_admin_indexes.sql",
    "004_admin_audit_events.sql", "005_admin_session_and_throttle.sql",
    "006_admin_users_and_tenant_scopes.sql", "007_mail_request_cancelled_status.sql",
    "008_delivery_events.sql", "009_mail_request_scheduled_at.sql",
    "010_admin_audit_events_tenant_id.sql", "011_bounce_ingestion.sql",
    "012_provider_event_inbox_details.sql", "013_provider_queue_dead_letters.sql",
    "014_mail_request_delivery_unknown_status.sql",
    "015_attachment_spool_and_submission_evidence.sql",
    "016_recipient_persistence_and_plain_submission_evidence.sql",
    "017_recipient_delivery_events.sql", "018_admin_user_capabilities.sql",
]
V13_MIGRATION_BASELINE = V13_MIGRATION_FULL_INVENTORY[:13]
V13_MIGRATION_DELTA = V13_MIGRATION_FULL_INVENTORY[13:]
V13_MIGRATION_SCENARIOS = {"G583-MIG-01", "G583-MIG-02", "G583-MIG-03"}

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
    "G583-MIG-01": ("db-migration-fresh-apply",),
    "G583-MIG-02": ("db-migration-upgrade",),
    "G583-MIG-03": ("db-migration-schema-contract",),
}
# These are the only scenario adapters whose predicates are implemented in
# this runner.  Other #456 lanes must register a dedicated validator before a
# PASS can affect the machine verdict; accepting a generic predicateResult
# would turn an unexecuted lane into an arbitrary GO.
IMPLEMENTED_SCENARIO_VALIDATORS = {
    "G456-03", "G456-04", "G456-05", "G456-06",
    "G456-42", "G456-43", "G456-44",
    "G583-MIG-01", "G583-MIG-02", "G583-MIG-03",
}
RESTRICTED_LANE_OWNER_ROLES = {
    "G456-03": "maintainer-acs-staging",
    "G456-04": "maintainer-acs-staging",
    "G456-05": "maintainer-acs-production",
    "G456-06": "maintainer-acs-production",
    "G456-42": "maintainer-migration",
    "G456-43": "maintainer-migration",
    "G456-44": "maintainer-migration",
    "G583-MIG-01": "maintainer-migration",
    "G583-MIG-02": "maintainer-migration",
    "G583-MIG-03": "maintainer-migration",
}
SCOPE_OWNER_CLASSES = {
    **{f"G456-{n:02d}": ("maintainer-acs-staging" if n in {3, 4} else "maintainer-acs-production" if n in {5, 6} else "lane-owner") for n in range(1, 42)},
    "G583-MIG-01": "maintainer-migration",
    "G583-MIG-02": "maintainer-migration",
    "G583-MIG-03": "maintainer-migration",
}
SCOPE_PREDICATE_SETS = {
    **{f"G456-{n:02d}": f"legacy-g456-{n:02d}" for n in range(1, 42)},
    "G583-MIG-01": "v1.3-migration-fresh",
    "G583-MIG-02": "v1.3-migration-upgrade",
    "G583-MIG-03": "v1.3-migration-schema-contract",
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


def require_scope_version(value: Any, field: str) -> int:
    """Scope/predicate versions are numeric, never string aliases."""
    if not isinstance(value, int) or isinstance(value, bool) or value < 1:
        fail(f"{field}: positive integer required")
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


def verify_plan_source(repo_root: Path, plan_path: Path, plan_commit: str, plan_sha: str) -> str:
    repo_root = repo_root.resolve()
    plan_path = plan_path.resolve()
    try:
        relative = plan_path.relative_to(repo_root).as_posix()
    except ValueError:
        fail("plan-file must be inside repo-root")
    if not relative or relative.startswith("../"):
        fail("plan-file path is invalid")
    blob_sha = git_output(repo_root, "rev-parse", f"{plan_commit}:{relative}")
    if not SHA40.fullmatch(blob_sha) or sha_bytes(subprocess.run(["git", "-C", str(repo_root), "show", f"{plan_commit}:{relative}"], check=True, capture_output=True).stdout) != plan_sha:
        fail("plan-file bytes do not match plan-commit-sha")
    if git_output(repo_root, "status", "--porcelain", "--", relative):
        fail("plan-file worktree is dirty")
    return relative


def copy_tree_file(src: Path, dst: Path) -> None:
    if not src.is_file() or src.is_symlink():
        fail(f"candidate file missing or symlink: {src.name}")
    write_bytes_once(dst, src.read_bytes())


def archive_manifest(path: Path) -> dict[str, Any]:
    """Read the single embedded release-bundle-manifest without extracting bytes."""
    try:
        if path.suffix.lower() == ".zip":
            with zipfile.ZipFile(path) as archive:
                names = [name for name in archive.namelist() if Path(name).name == "release-bundle-manifest.json"]
                if len(names) != 1:
                    fail(f"{path.name}: exactly one release-bundle-manifest.json is required")
                info = archive.getinfo(names[0])
                if info.is_dir() or info.filename != names[0]:
                    fail(f"{path.name}: release-bundle-manifest.json entry is invalid")
                return json.loads(archive.read(info).decode("utf-8"))
        with tarfile.open(path, "r:*") as archive:
            members = [member for member in archive.getmembers() if Path(member.name).name == "release-bundle-manifest.json"]
            if len(members) != 1 or not members[0].isreg():
                fail(f"{path.name}: exactly one regular release-bundle-manifest.json is required")
            payload = archive.extractfile(members[0])
            if payload is None:
                fail(f"{path.name}: release-bundle-manifest.json cannot be read")
            return json.loads(payload.read().decode("utf-8"))
    except RunnerError:
        raise
    except (OSError, ValueError, KeyError, tarfile.TarError, zipfile.BadZipFile, json.JSONDecodeError) as exc:
        fail(f"{path.name}: release-bundle-manifest is invalid ({type(exc).__name__})")


def archive_member(path: Path, basename: str) -> bytes:
    try:
        if path.suffix.lower() == ".zip":
            with zipfile.ZipFile(path) as archive:
                names = [name for name in archive.namelist() if Path(name).name == basename]
                if len(names) != 1:
                    fail(f"{path.name}: exactly one {basename} is required")
                return archive.read(names[0])
        with tarfile.open(path, "r:*") as archive:
            members = [member for member in archive.getmembers() if Path(member.name).name == basename]
            if len(members) != 1 or not members[0].isreg():
                fail(f"{path.name}: exactly one regular {basename} is required")
            payload = archive.extractfile(members[0])
            if payload is None:
                fail(f"{path.name}: {basename} cannot be read")
            return payload.read()
    except RunnerError:
        raise
    except (OSError, tarfile.TarError, zipfile.BadZipFile) as exc:
        fail(f"{path.name}: {basename} is invalid ({type(exc).__name__})")


def readme_mapping_from_archives(
    candidate_root: Path,
    archive_records: Iterable[dict[str, Any]],
) -> tuple[dict[str, dict[str, str]], dict[str, bytes]]:
    mapping: dict[str, dict[str, str]] = {}
    payloads: dict[str, bytes] = {}
    for archive_record in archive_records:
        rid = require_string(archive_record, "targetRid")
        if rid not in ARCHIVE_RIDS:
            fail(f"candidate README-SETUP.md has unexpected targetRid: {rid}")
        if rid in mapping:
            fail(f"candidate README-SETUP.md has duplicate targetRid: {rid}")
        archive_name = require_string(archive_record, "archiveFileName")
        archive_path = candidate_root / archive_name
        if not archive_path.is_file() or archive_path.is_symlink():
            fail(f"candidate README archive is missing: {archive_name}")
        archive_digest = require_digest(require_string(archive_record, "archiveSha256"), f"archives[{rid}].archiveSha256")
        if archive_digest != "sha256:" + file_sha(archive_path):
            fail(f"candidate README archive digest mismatch: {archive_name}")
        manifest = archive_manifest(archive_path)
        if manifest.get("targetRid") != rid:
            fail(f"candidate README archive manifest targetRid mismatch: {archive_name}")
        payload = archive_member(archive_path, "README-SETUP.md")
        mapping[rid] = {
            "archiveFileName": archive_name,
            "archiveSha256": archive_digest,
            "targetRid": rid,
            "manifestTargetRid": require_string(manifest, "targetRid"),
            "sha256": sha_bytes(payload),
        }
        payloads[rid] = payload
    if set(mapping) != ARCHIVE_RIDS:
        missing = sorted(ARCHIVE_RIDS - set(mapping))
        unexpected = sorted(set(mapping) - ARCHIVE_RIDS)
        fail(f"candidate README-SETUP.md RID set mismatch (missing={missing}, unexpected={unexpected})")
    return {rid: mapping[rid] for rid in sorted(mapping)}, {rid: payloads[rid] for rid in sorted(payloads)}


def validate_rid_readme_binding(
    docs: Any,
    run_root: Path,
    archive_records: Iterable[dict[str, Any]],
) -> None:
    if not isinstance(docs, dict) or "candidateReadmeSetupSha256" in docs:
        fail("v1.3 docs must use candidateReadmeSetupByRid")
    mapping = docs.get("candidateReadmeSetupByRid")
    if not isinstance(mapping, dict) or set(mapping) != ARCHIVE_RIDS:
        fail("v1.3 candidate README mapping must contain the exact RID set")
    mapping_digest = docs.get("candidateReadmeSetupByRidSha256")
    if not isinstance(mapping_digest, str) or not HEX64.fullmatch(mapping_digest) or sha_object(mapping) != mapping_digest:
        fail("v1.3 candidate README mapping digest mismatch")
    expected_archives = {}
    for archive_record in archive_records:
        rid = require_string(archive_record, "targetRid")
        if rid in expected_archives:
            fail(f"candidate archive provenance has duplicate targetRid: {rid}")
        expected_archives[rid] = {
            "archiveFileName": require_string(archive_record, "archiveFileName"),
            "archiveSha256": require_digest(require_string(archive_record, "archiveSha256"), f"archives[{rid}].archiveSha256"),
            "targetRid": rid,
            "manifestTargetRid": rid,
        }
    if set(expected_archives) != ARCHIVE_RIDS:
        fail("candidate archive provenance RID set does not match README mapping")
    extract_dir = run_root / "docs-extract" / "candidate-readme-setup"
    if not extract_dir.is_dir() or extract_dir.is_symlink():
        fail("docs-extract candidate README directory is missing or unsafe")
    expected_extract_names = {f"{rid}.md" for rid in ARCHIVE_RIDS}
    try:
        extract_entries = list(extract_dir.iterdir())
    except OSError as exc:
        fail(f"docs-extract candidate README directory is unreadable ({type(exc).__name__})")
    actual_extract_names = set()
    for entry in extract_entries:
        if entry.is_symlink() or not entry.is_file():
            fail(f"docs-extract candidate README entry is unsafe: {entry.name}")
        actual_extract_names.add(entry.name)
    if actual_extract_names != expected_extract_names:
        fail("docs-extract candidate README directory must contain the exact RID set")
    for rid in sorted(ARCHIVE_RIDS):
        entry = mapping.get(rid)
        if not isinstance(entry, dict) or any(not isinstance(entry.get(field), str) for field in ("archiveFileName", "archiveSha256", "targetRid", "manifestTargetRid", "sha256")):
            fail(f"candidate README mapping entry is invalid: {rid}")
        for field in ("archiveFileName", "archiveSha256", "targetRid", "manifestTargetRid"):
            if entry[field] != expected_archives[rid][field]:
                fail(f"candidate README mapping archive identity mismatch: {rid}.{field}")
        if not HEX64.fullmatch(entry["sha256"]):
            fail(f"candidate README mapping digest is invalid: {rid}")
        path = run_root / "docs-extract" / "candidate-readme-setup" / f"{rid}.md"
        if file_sha(path) != entry["sha256"]:
            fail(f"docs-extract candidate README digest mismatch: {rid}")


def docs_from_release_tree(
    repo_root: Path,
    release_commit: str,
    candidate_root: Path,
    archive_paths: Iterable[Path],
    rid_specific: bool = False,
) -> tuple[dict[str, Any], dict[str, bytes]]:
    paths = {
        "setupGuideJa": "docs/ops/setup-guide.md",
        "setupGuideEn": "docs/ops/setup-guide.en.md",
        "setupReleaseBundleJa": "docs/ops/setup-release-bundle.md",
        "setupReleaseBundleEn": "docs/ops/setup-release-bundle.en.md",
        "readmeJa": "README.md",
        "readmeEn": "README.en.md",
    }
    extracted: dict[str, bytes] = {}
    for key, relative in paths.items():
        try:
            payload = subprocess.run(["git", "-C", str(repo_root), "show", f"{release_commit}:{relative}"], check=True, capture_output=True).stdout
        except (OSError, subprocess.CalledProcessError):
            fail(f"docs extract missing from release tree: {relative}")
        extracted[key] = payload
    archive_paths = list(archive_paths)
    if rid_specific:
        archive_records = []
        for archive in archive_paths:
            manifest = archive_manifest(archive)
            archive_records.append({
                "targetRid": manifest.get("targetRid"),
                "archiveFileName": archive.name,
                "archiveSha256": "sha256:" + file_sha(archive),
            })
        mapping, candidate_setup = readme_mapping_from_archives(candidate_root, archive_records)
        extracted.update({f"candidateReadmeSetup:{rid}": payload for rid, payload in candidate_setup.items()})
        digests = {f"{key}Sha256": sha_bytes(payload) for key, payload in extracted.items()}
        digests.update({
            "candidateReadmeSetupByRid": mapping,
            "candidateReadmeSetupByRidSha256": sha_object(mapping),
            "extractionMethod": "git-archive-exact-source-plus-qualified-archive",
            "sourceCommitSha": release_commit,
        })
        return digests, extracted
    candidate_setup: list[bytes] = []
    for archive in archive_paths:
        try:
            candidate_setup.append(archive_member(archive, "README-SETUP.md"))
        except RunnerError as exc:
            if "exactly one README-SETUP.md" in str(exc):
                continue
            raise
    if not candidate_setup or len({sha_bytes(payload) for payload in candidate_setup}) != 1:
        fail("candidate README-SETUP.md must be present and identical across host archives")
    extracted["candidateReadmeSetup"] = candidate_setup[0]
    digests = {f"{key}Sha256": sha_bytes(payload) for key, payload in extracted.items()}
    digests.update({"extractionMethod": "git-archive-exact-source-plus-qualified-archive", "sourceCommitSha": release_commit})
    return digests, extracted


def validate_oci_layout(layout_root: Path, expected_digest: str, expected_release_version: str, expected_source_commit: str) -> str:
    """Validate the candidate OCI layout using the repository's Buildx contract.

    ``oci-index.digest`` is the Buildx image-index descriptor digest, not the
    SHA-256 of the layout entrypoint ``index.json``.  The entrypoint must have
    one root descriptor whose blob is an image index; the complete descriptor
    graph is then walked and every referenced blob is hash-checked.
    """
    layout_root = layout_root.resolve()
    if not layout_root.is_dir() or any(path.is_symlink() for path in layout_root.rglob("*")):
        fail("OCI layout must be a regular, symlink-free directory")
    marker = read_json(layout_root / "oci-layout", "oci-layout")
    index_path = layout_root / "index.json"
    index = read_json(index_path, "OCI index.json")
    if marker.get("imageLayoutVersion") != "1.0.0" or not isinstance(index, dict) or index.get("schemaVersion") != 2:
        fail("OCI layout/index schema mismatch")
    blob_root = layout_root / "blobs" / "sha256"
    if not blob_root.is_dir() or any(path.is_symlink() or not path.is_file() for path in blob_root.rglob("*")):
        fail("OCI blob store must be a regular, symlink-free directory")

    referenced: set[str] = set()
    platforms: set[str] = set()

    def load_blob(digest: str, label: str) -> bytes:
        if not isinstance(digest, str) or not SHA256_DIGEST.fullmatch(digest):
            fail(f"{label}: invalid descriptor digest")
        blob_path = blob_root / digest.removeprefix("sha256:")
        if not blob_path.is_file() or blob_path.is_symlink():
            fail(f"{label}: referenced blob is missing")
        payload = blob_path.read_bytes()
        if sha_bytes(payload) != digest.removeprefix("sha256:"):
            fail(f"{label}: referenced blob digest mismatch")
        referenced.add(digest.removeprefix("sha256:"))
        return payload

    def descriptor_payload(descriptor: dict[str, Any], label: str) -> tuple[str, bytes]:
        if not isinstance(descriptor, dict):
            fail(f"{label}: descriptor must be an object")
        digest = descriptor.get("digest")
        payload = load_blob(digest, label)
        size = descriptor.get("size")
        if size is not None and (not isinstance(size, int) or size != len(payload)):
            fail(f"{label}: descriptor size mismatch")
        media_type = descriptor.get("mediaType")
        if not isinstance(media_type, str) or not media_type:
            fail(f"{label}: descriptor mediaType is required")
        return media_type, payload

    index_media_types = {"application/vnd.oci.image.index.v1+json", "application/vnd.docker.distribution.manifest.list.v2+json"}
    manifest_media_types = {"application/vnd.oci.image.manifest.v1+json", "application/vnd.docker.distribution.manifest.v2+json"}

    def walk_index(document: dict[str, Any], label: str) -> None:
        if document.get("schemaVersion") != 2 or not isinstance(document.get("manifests"), list) or not document["manifests"]:
            fail(f"{label}: image index schema/manifests mismatch")
        for index, descriptor in enumerate(document["manifests"]):
            media_type, payload = descriptor_payload(descriptor, f"{label}.manifests[{index}]")
            platform = descriptor.get("platform") or {}
            if media_type in index_media_types:
                if platform:
                    fail(f"{label}.manifests[{index}]: nested image index must not have a platform")
                try:
                    nested = json.loads(payload.decode("utf-8"))
                except (UnicodeDecodeError, json.JSONDecodeError):
                    fail(f"{label}.manifests[{index}]: nested image index is invalid")
                walk_index(nested, f"{label}.manifests[{index}]")
                continue
            if media_type not in manifest_media_types:
                fail(f"{label}.manifests[{index}]: unsupported descriptor mediaType")
            os_name, architecture = platform.get("os"), platform.get("architecture")
            platform_name = f"{os_name}/{architecture}" if isinstance(os_name, str) and isinstance(architecture, str) else ""
            if platform_name not in {"linux/amd64", "linux/arm64"} or platform_name in platforms:
                fail(f"{label}.manifests[{index}]: platform set mismatch")
            platforms.add(platform_name)
            try:
                manifest = json.loads(payload.decode("utf-8"))
            except (UnicodeDecodeError, json.JSONDecodeError):
                fail(f"{label}.manifests[{index}]: image manifest is invalid")
            if manifest.get("schemaVersion") != 2 or not isinstance(manifest.get("config"), dict):
                fail(f"{label}.manifests[{index}]: image manifest schema mismatch")
            config_media, config_payload = descriptor_payload(manifest["config"], f"{label}.manifests[{index}].config")
            if config_media not in {"application/vnd.oci.image.config.v1+json", "application/vnd.docker.container.image.v1+json"}:
                fail(f"{label}.manifests[{index}].config: unsupported mediaType")
            try:
                config = json.loads(config_payload.decode("utf-8"))
            except (UnicodeDecodeError, json.JSONDecodeError):
                fail(f"{label}.manifests[{index}].config: JSON is invalid")
            labels = ((config.get("config") or {}).get("Labels") or {})
            if labels.get("org.opencontainers.image.version") != expected_release_version or labels.get("org.opencontainers.image.revision") != expected_source_commit:
                fail(f"{label}.manifests[{index}].config: source/version labels mismatch")
            layers = manifest.get("layers")
            if not isinstance(layers, list):
                fail(f"{label}.manifests[{index}]: layers must be an array")
            for layer_index, layer in enumerate(layers):
                descriptor_payload(layer, f"{label}.manifests[{index}].layers[{layer_index}]")

    root_manifests = index.get("manifests")
    if not isinstance(root_manifests, list) or len(root_manifests) != 1:
        fail("OCI index must contain exactly one Buildx root descriptor")
    root = root_manifests[0]
    if root.get("digest") != expected_digest or root.get("mediaType") not in index_media_types or root.get("platform"):
        fail("OCI root descriptor does not match expected image-index digest")
    root_media, root_payload = descriptor_payload(root, "OCI root descriptor")
    try:
        nested_index = json.loads(root_payload.decode("utf-8"))
    except (UnicodeDecodeError, json.JSONDecodeError):
        fail("OCI root image-index blob is invalid")
    if root_media not in index_media_types:
        fail("OCI root descriptor must reference an image index")
    walk_index(nested_index, "OCI root image-index")
    if platforms != {"linux/amd64", "linux/arm64"}:
        fail("OCI required platform set mismatch")
    digest_files = [path for path in (layout_root / "oci-index.digest", layout_root.parent / "oci-index.digest") if path.is_file()]
    if not digest_files or any(path.is_symlink() or path.read_text(encoding="utf-8").strip() != expected_digest for path in digest_files):
        fail("OCI index digest attestation is missing or mismatched")
    for blob_path in blob_root.iterdir():
        if blob_path.name not in referenced:
            fail("OCI layout contains an unreferenced blob")
    return sha_bytes(index_path.read_bytes())


def validate_archive_manifest(manifest: dict[str, Any], archive: dict[str, Any], source: str, release_version: str, oci: str) -> None:
    if not isinstance(manifest, dict) or manifest.get("schemaVersion") != 1 or manifest.get("packagingKind") != "setup-release-candidate":
        fail(f"archives[{archive['targetRid']}]: release-bundle-manifest schema/kind mismatch")
    rid = archive["targetRid"]
    required_equal = {
        "sourceCommitSha": source,
        "mailerVersion": release_version,
        "setupLauncherVersion": release_version,
        "hostRid": rid,
        "targetRid": rid,
        "imageDigest": oci,
        "ociIndexDigest": oci,
    }
    for field, expected in required_equal.items():
        if manifest.get(field) != expected:
            fail(f"archives[{rid}]: release-bundle-manifest.{field} mismatch")
    for field in ("artifactId", "platform", "architecture", "mailpitImageReference", "artifactFileName"):
        if not isinstance(manifest.get(field), str) or not manifest[field]:
            fail(f"archives[{rid}]: release-bundle-manifest.{field} is required")
    for field in ("composeSha256", "composeImageDigestSha256", "composeRecordedMetadataSha256", "composeMailpitSha256", "payloadTreeSha256"):
        if not isinstance(manifest.get(field), str) or not SHA256_DIGEST.fullmatch(manifest[field]):
            fail(f"archives[{rid}]: release-bundle-manifest.{field} must be sha256 digest")
    if archive.get("payloadTreeSha256") is not None and manifest.get("payloadTreeSha256") != archive["payloadTreeSha256"]:
        fail(f"archives[{rid}]: payloadTreeSha256 mismatch")


def candidate_documents(candidate_root: Path) -> tuple[dict[str, Any], dict[str, Any], dict[str, str]]:
    provenance = read_json(candidate_root / "candidate-provenance.json", "candidate-provenance.json")
    identity = read_json(candidate_root / "image-identity.json", "image-identity.json")
    if not isinstance(provenance, dict) or not isinstance(identity, dict):
        fail("candidate documents must be JSON objects")
    allowed_provenance = {"schemaVersion", "sourceCommitSha", "releaseVersion", "workflowRunId", "workflowRunAttempt", "workflowRef", "imageRepository", "imageTag", "ociIndexDigest", "ociPlatforms", "mailpitImageReference", "dotnetSdkVersion", "archives", "notes"}
    allowed_identity = {"imageRepository", "imageTag", "imageDigest", "sourceCommitSha", "mailerVersion", "platforms"}
    if set(provenance) - allowed_provenance or set(identity) - allowed_identity:
        fail("candidate provenance contains unknown fields")
    if provenance.get("schemaVersion") != 1:
        fail("candidate-provenance.schemaVersion must be 1")
    release_version = provenance.get("releaseVersion")
    if not isinstance(release_version, str) or not RELEASE_VERSION.fullmatch(release_version):
        fail("candidate-provenance.releaseVersion must be a stable semantic version")
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
    oci_platforms = provenance.get("ociPlatforms")
    if not isinstance(oci_platforms, list) or len(oci_platforms) != len(OCI_PLATFORMS) or set(oci_platforms) != OCI_PLATFORMS:
        fail("candidate-provenance.ociPlatforms must be the exact OCI platform set")
    if not isinstance(platforms, list) or len(platforms) != len(OCI_PLATFORMS) or set(platforms) != OCI_PLATFORMS:
        fail("image-identity.platforms: non-empty string array required")
    oci = require_digest(require_string(provenance, "ociIndexDigest"), "ociIndexDigest")
    if identity.get("sourceCommitSha") != source or identity.get("imageDigest") != oci or identity.get("mailerVersion") != release_version:
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
        if archive.get("mailerVersion") != release_version or archive.get("setupLauncherVersion") != release_version:
            fail(f"archives[{rid}]: archive version does not match releaseVersion")
        if "payloadTreeSha256" not in archive or (archive.get("payloadTreeSha256") is not None and not SHA256_DIGEST.fullmatch(str(archive.get("payloadTreeSha256")))):
            fail(f"archives[{rid}].payloadTreeSha256: invalid")
        digest = require_digest(require_string(archive, "archiveSha256"), f"archives[{rid}].archiveSha256")
        if not SAFE_ID.fullmatch(rid) or Path(name).name != name:
            fail(f"archives[{rid}]: unsafe identity")
        path = candidate_root / name
        raw_digest = digest.removeprefix("sha256:")
        if file_sha(path) != raw_digest or sums.get(name) != raw_digest:
            fail(f"archives[{rid}]: archive digest mismatch")
        validate_archive_manifest(archive_manifest(path), archive, source, release_version, oci)
        checksums[rid] = digest
    if len(checksums) != len(archives) or set(checksums) != ARCHIVE_RIDS:
        fail("archives: exact win-x64/linux-x64/linux-arm64 set is required")
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
        require_string(binding, "planFilePath"),
        require_string(binding, "planFileSha256"),
        str(binding.get("variantRulesVersion")),
        sha_object(binding.get("rows")),
        authorization_seed,
        require_commit(require_string(binding, "releaseCommitSha"), "releaseCommitSha"),
        require_digest(require_string(binding, "ociIndexDigest"), "ociIndexDigest"),
        require_hex(require_string(binding, "ociLayoutIndexSha256"), "ociLayoutIndexSha256"),
        require_string(binding, "releaseVersion"),
        sha_object(binding.get("ociPlatforms")),
        sha_object(binding.get("docs")),
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
    if binding.get("scopeId") is not None:
        preimage += "|" + "|".join([
            require_value_free_identity(binding.get("scopeId"), "scopeId"),
            str(binding.get("scopeVersion")),
            require_hex(require_string(binding, "scopeManifestSha256"), "scopeManifestSha256"),
            str(binding.get("scopeAuthorityIssueNumber")),
            require_hex(require_string(binding, "scopeAuthorityIssueBodySha256"), "scopeAuthorityIssueBodySha256"),
            require_hex(require_string(binding, "scopePlanFileSha256"), "scopePlanFileSha256"),
            require_hex(require_string(binding, "migrationBaselineInventoryDigestSha256"), "migrationBaselineInventoryDigestSha256"),
            require_hex(require_string(binding, "migrationDeltaInventoryDigestSha256"), "migrationDeltaInventoryDigestSha256"),
            require_hex(require_string(binding, "migrationFullInventoryDigestSha256"), "migrationFullInventoryDigestSha256"),
            str(require_scope_version(binding.get("migrationPredicateSetVersion"), "migrationPredicateSetVersion")),
            str(require_scope_version(binding.get("migrationSchemaAllowlistVersion"), "migrationSchemaAllowlistVersion")),
            require_hex(require_string(binding, "migrationSchemaAllowlistSha256"), "migrationSchemaAllowlistSha256"),
        ])
    if binding.get("lifecycleVersion") is not None:
        if binding.get("lifecycleVersion") != RUN_LIFECYCLE_VERSION:
            fail("binding lifecycleVersion is unsupported")
        preimage += "|" + "|".join([
            f"lifecycle-v{RUN_LIFECYCLE_VERSION}",
            require_hex(require_string(binding, "bindingNonce"), "bindingNonce"),
        ])
    return sha_bytes(preimage.encode("utf-8"))


def fresh_lifecycle_nonce(label: str, seed: str | None = None) -> str:
    if not SAFE_ID.fullmatch(label):
        fail("lifecycle nonce label is invalid")
    seed_value = "" if seed is None else require_value_free_identity(seed, f"{label}-nonce-seed")
    return sha_bytes(f"{label}|{seed_value}|{secrets.token_hex(32)}".encode("utf-8"))


def create_staging_run_root(run_root: Path) -> None:
    if run_root.exists() or run_root.is_symlink():
        fail("qualificationRunId already exists; bind is write-once")
    ensure_directory_chain(run_root.parent)
    if run_root.parent.is_symlink():
        fail("run root parent is a symlink")
    try:
        run_root.mkdir()
    except FileExistsError:
        fail("qualificationRunId already exists; bind is write-once")
    if run_root.is_symlink() or not run_root.is_dir():
        fail("staging run root is not a real directory")
    fsync_directory(run_root.parent)


def lifecycle_material_inventory(run_root: Path) -> list[dict[str, str]]:
    exact_paths = {
        "authorization.json", "binding.json", "migration-pin.json",
        "phase-manifests/phase-2.json", "scope-manifest.json",
    }
    entries = []
    for path in sorted(run_root.rglob("*")):
        if not path.is_file() or path.is_symlink():
            continue
        relative = path.relative_to(run_root).as_posix()
        if relative in exact_paths or relative.startswith("docs-extract/"):
            entries.append({"path": relative, "sha256": file_sha(path)})
    return entries


def lifecycle_material_root(run_root: Path) -> str:
    return object_root(lifecycle_material_inventory(run_root))


def write_run_ready(run_root: Path, binding: dict[str, Any]) -> dict[str, Any]:
    ready = {
        "schemaVersion": 1,
        "lifecycleVersion": RUN_LIFECYCLE_VERSION,
        "status": "ready",
        "candidateId": binding["candidateId"],
        "bindingId": binding["bindingId"],
        "qualificationRunId": binding["qualificationRunId"],
        "bindingSha256": file_sha(run_root / "binding.json"),
        "authorizationSha256": file_sha(run_root / "authorization.json"),
        "phase2Sha256": file_sha(run_root / "phase-manifests" / "phase-2.json"),
        "materialRootSha256": lifecycle_material_root(run_root),
        "readyAtUtc": utc_now(),
    }
    write_once(run_root / RUN_READY_FILE, ready)
    return ready


def validate_run_ready(run_root: Path, binding: dict[str, Any], ready: dict[str, Any]) -> None:
    expected_identity = {
        "candidateId": binding["candidateId"],
        "bindingId": binding["bindingId"],
        "qualificationRunId": binding["qualificationRunId"],
    }
    if ready.get("schemaVersion") != 1 or ready.get("lifecycleVersion") != RUN_LIFECYCLE_VERSION or ready.get("status") != "ready":
        fail("run-ready.json lifecycle identity mismatch")
    if any(ready.get(field) != value for field, value in expected_identity.items()):
        fail("run-ready.json qualification identity mismatch")
    for field in ("bindingSha256", "authorizationSha256", "phase2Sha256", "materialRootSha256"):
        require_hex(require_string(ready, field), f"run-ready.{field}")
    if ready["bindingSha256"] != file_sha(run_root / "binding.json"):
        fail("run-ready.json binding digest mismatch")
    if ready["authorizationSha256"] != file_sha(run_root / "authorization.json"):
        fail("run-ready.json authorization digest mismatch")
    if ready["phase2Sha256"] != file_sha(run_root / "phase-manifests" / "phase-2.json"):
        fail("run-ready.json Phase 2 digest mismatch")
    if ready["materialRootSha256"] != lifecycle_material_root(run_root):
        fail("run-ready.json material root mismatch")
    if not isinstance(ready.get("readyAtUtc"), str) or not UTC_TIMESTAMP.fullmatch(ready["readyAtUtc"]):
        fail("run-ready.json readyAtUtc is invalid")


def load_binding(run_root: Path, *, allow_staging: bool = False) -> dict[str, Any]:
    binding = read_json(run_root / "binding.json", "binding.json")
    if not isinstance(binding, dict):
        fail("binding.json: object required")
    for field in ("candidateId", "bindingId", "qualificationRunId"):
        require_hex(require_string(binding, field), f"binding.{field}")
    nonce = require_string(binding, "runAttemptNonce")
    lifecycle_version = binding.get("lifecycleVersion")
    ready = None
    if lifecycle_version is not None:
        if lifecycle_version != RUN_LIFECYCLE_VERSION:
            fail("binding lifecycleVersion is unsupported")
        require_hex(require_string(binding, "bindingNonce"), "binding.bindingNonce")
        require_hex(nonce, "binding.runAttemptNonce")
        if allow_staging:
            if (run_root / RUN_READY_FILE).exists() or (run_root / RUN_READY_FILE).is_symlink():
                fail("staging verification cannot reuse a ready run")
        else:
            ready = read_json(run_root / RUN_READY_FILE, RUN_READY_FILE)
            if not isinstance(ready, dict):
                fail("run-ready.json: object required")
    elif not allow_staging:
        # Pre-remediation unsealed runs have no proof that binding,
        # authorization, and Phase 2 were materialized atomically.  They are
        # historical state only; sealed legacy runs remain readable for
        # verification/handoff, but an incomplete legacy run cannot resume.
        legacy_events = sorted((run_root / "run-status-events").glob("*.json"))
        legacy_sealed = False
        for event_path in legacy_events:
            event = read_json(event_path, "legacy run-status event")
            if isinstance(event, dict) and event.get("status") == "sealed" and event.get("qualificationRunId") == binding["qualificationRunId"]:
                legacy_sealed = True
                break
        if not legacy_sealed:
            fail("pre-remediation unsealed run is ineligible; create a fresh binding and qualification run")
    expected_run_id = sha_bytes((binding["bindingId"] + "|" + nonce).encode("utf-8"))
    if expected_run_id != run_root.name or binding["qualificationRunId"] != expected_run_id:
        fail("qualificationRunId/runAttemptNonce binding mismatch")
    scope_profile = None
    if binding.get("scopeId") is not None:
        scope_profile = load_scope_manifest(run_root / "scope-manifest.json")
        scope_expected = {
            "scopeId": scope_profile["scopeId"],
            "scopeVersion": scope_profile["scopeVersion"],
            "scopeAuthorityIssueNumber": scope_profile["authorityIssueNumber"],
            "scopeAuthorityIssueBodySha256": scope_profile["authorityIssueBodySha256"],
            "scopePlanFileSha256": scope_profile["planFileSha256"],
            "scopeManifestSha256": scope_profile["scopeManifestSha256"],
        }
        if any(binding.get(field) != expected for field, expected in scope_expected.items()):
            fail("binding scope authority identity mismatch")
        if binding.get("issueNumber") != scope_profile["authorityIssueNumber"] or binding.get("planRevision") != scope_profile["planRevision"] or binding.get("variantRulesVersion") != scope_profile["variantRulesVersion"]:
            fail("binding v1.3 scope plan identity mismatch")
    elif binding.get("issueNumber") != 456 or binding.get("planRevision") != "12" or binding.get("variantRulesVersion") != VARIANT_RULES_VERSION:
        fail("binding canonical plan/issue identity mismatch")
    if not isinstance(binding.get("planFilePath"), str) or Path(binding["planFilePath"]).is_absolute() or ".." in Path(binding["planFilePath"]).parts:
        fail("binding plan path is unsafe")
    bind_rows({"rows": binding.get("rows"), "number": binding.get("issueNumber")}, scope_profile)
    if not isinstance(binding.get("migrationFileDigests"), list) or binding.get("migrationPinDigestSha256") is None or binding.get("migrationInventoryDigestSha256") is None:
        fail("binding migration PIN fields are missing")
    phase2 = read_json(run_root / "phase-manifests/phase-2.json", "phase-2.json")
    phase2_fields = ("candidateId", "bindingId", "qualificationRunId", "runAttemptNonce", "releaseCommitSha", "releaseVersion", "ociPlatforms", "ociLayoutIndexSha256", "planFilePath", "producerWorkflowRef", "producerWorkflowRunId", "producerWorkflowRunAttempt", "candidateProvenanceSha256", "candidateImageIdentitySha256", "candidatePhase1ManifestSha256", "candidateArchivesDigestSha256")
    if lifecycle_version is not None:
        phase2_fields += ("lifecycleVersion", "bindingNonce")
    if scope_profile is not None:
        phase2_fields += ("scopeId", "scopeVersion", "scopeManifestSha256", "scopeAuthorityIssueNumber", "scopeAuthorityIssueBodySha256", "scopePlanFileSha256", "planRevision", "issueNumber", "variantRulesVersion", "migrationBaselineInventoryDigestSha256", "migrationDeltaInventoryDigestSha256", "migrationFullInventoryDigestSha256", "migrationPredicateSetVersion", "migrationSchemaAllowlistVersion", "migrationSchemaAllowlistSha256", "migrationSchemaAllowlist")
    if any(phase2.get(field) != binding.get(field) for field in phase2_fields) or phase2.get("docs") != binding.get("docs") or phase2.get("authorizationDigestSha256") != binding.get("authorizationDigestSha256"):
        fail("phase-2 manifest identity mismatch")
    auth = read_json(run_root / "authorization.json", "authorization.json")
    if not isinstance(auth, dict) or any(auth.get(field) != binding.get(field) for field in ("candidateId", "bindingId", "qualificationRunId")) or sha_object(auth) != binding.get("authorizationDigestSha256"):
        fail("authorization snapshot digest/identity mismatch")
    if lifecycle_version is not None and auth.get("lifecycleVersion") != lifecycle_version:
        fail("authorization lifecycle identity mismatch")
    if binding["bindingId"] != binding_id_for(binding, auth):
        fail("bindingId recomputation mismatch")
    saved_pin = load_migration_pin(run_root / "migration-pin.json", scope_profile)
    if saved_pin["releaseCommitSha"] != binding.get("releaseCommitSha") or saved_pin["migrationPinDigestSha256"] != binding.get("migrationPinDigestSha256") or saved_pin["migrationInventoryDigestSha256"] != binding.get("migrationInventoryDigestSha256") or saved_pin["migrationFileDigests"] != binding.get("migrationFileDigests"):
        fail("saved migration PIN does not match binding")
    if phase2.get("migrationPinDigestSha256") != saved_pin["migrationPinDigestSha256"] or phase2.get("migrationInventoryDigestSha256") != saved_pin["migrationInventoryDigestSha256"] or phase2.get("migrationFileDigests") != saved_pin["migrationFileDigests"] or phase2.get("releaseCommitSha") != saved_pin["releaseCommitSha"]:
        fail("phase-2 migration PIN identity mismatch")
    if scope_profile is not None and any(binding.get(field) != saved_pin.get(field) for field in ("migrationBaselineInventoryDigestSha256", "migrationDeltaInventoryDigestSha256", "migrationFullInventoryDigestSha256", "migrationPredicateSetVersion")):
        fail("v1.3 migration scope digest identity mismatch")
    if scope_profile is not None and binding.get("migrationPredicateSetVersion") != scope_profile["migrationPredicateSetVersion"]:
        fail("v1.3 migration predicate version mismatch")
    if scope_profile is not None and binding.get("migrationSchemaAllowlistVersion") != scope_profile["migration"]["schemaAllowlistVersion"]:
        fail("v1.3 migration schema allowlist version mismatch")
    if scope_profile is not None and binding.get("migrationSchemaAllowlistSha256") != scope_profile["migration"]["schemaAllowlistSha256"]:
        fail("v1.3 migration schema allowlist digest mismatch")
    if scope_profile is not None and binding.get("migrationSchemaAllowlist") != scope_profile["migration"]["schemaAllowlist"]:
        fail("v1.3 migration schema allowlist mismatch")
    docs = binding.get("docs")
    if not isinstance(docs, dict) or docs.get("sourceCommitSha") != binding.get("releaseCommitSha") or docs.get("extractionMethod") != "git-archive-exact-source-plus-qualified-archive":
        fail("binding docs extraction metadata mismatch")
    docs_files = {"setupGuideJa": "setup-guide.md", "setupGuideEn": "setup-guide.en.md", "setupReleaseBundleJa": "setup-release-bundle.md", "setupReleaseBundleEn": "setup-release-bundle.en.md", "readmeJa": "README.md", "readmeEn": "README.en.md"}
    for key, filename in docs_files.items():
        expected = docs.get(f"{key}Sha256")
        path = run_root / "docs-extract" / filename
        if not isinstance(expected, str) or not HEX64.fullmatch(expected) or file_sha(path) != expected:
            fail(f"docs-extract/{filename}: digest mismatch")
    if scope_profile is None:
        expected = docs.get("candidateReadmeSetupSha256")
        path = run_root / "docs-extract" / "README-SETUP.md"
        if not isinstance(expected, str) or not HEX64.fullmatch(expected) or file_sha(path) != expected:
            fail("docs-extract/README-SETUP.md: digest mismatch")
    candidate_root = run_root.parent.parent / "candidates" / binding["candidateId"] / "intake"
    provenance, identity, archive_digests = candidate_documents(candidate_root)
    if candidate_id(provenance, archive_digests) != binding.get("candidateId"):
        fail("candidateId recomputation mismatch")
    if provenance.get("sourceCommitSha") != binding.get("releaseCommitSha") or provenance.get("ociIndexDigest") != binding.get("ociIndexDigest") or identity.get("sourceCommitSha") != binding.get("releaseCommitSha") or identity.get("imageDigest") != binding.get("ociIndexDigest"):
        fail("candidate intake identity does not match binding")
    if binding.get("producerWorkflowRef") != provenance.get("workflowRef") or binding.get("producerWorkflowRunId") != provenance.get("workflowRunId") or binding.get("producerWorkflowRunAttempt") != provenance.get("workflowRunAttempt") or binding.get("releaseVersion") != provenance.get("releaseVersion") or binding.get("ociPlatforms") != provenance.get("ociPlatforms"):
        fail("candidate producer identity does not match binding")
    if binding.get("candidateProvenanceSha256") != file_sha(candidate_root / "candidate-provenance.json") or binding.get("candidateImageIdentitySha256") != file_sha(candidate_root / "image-identity.json") or binding.get("candidatePhase1ManifestSha256") != file_sha(candidate_root / "phase-1.json") or binding.get("candidateArchivesDigestSha256") != sha_object({"archives": provenance["archives"], "archiveDigests": archive_digests}):
        fail("candidate provenance/archive digest does not match binding")
    if scope_profile is not None:
        validate_rid_readme_binding(docs, run_root, provenance["archives"])
    if binding.get("sourceCommitSha") != binding.get("releaseCommitSha") or not SHA40.fullmatch(str(binding.get("sourceCommitSha", ""))) or not SHA256_DIGEST.fullmatch(str(binding.get("ociIndexDigest", ""))):
        fail("binding source/OCI identity is invalid")
    phase1 = read_json(candidate_root / "phase-1.json", "phase-1.json")
    objects = [{"path": p.relative_to(candidate_root.parent).as_posix(), "sha256": file_sha(p)} for p in sorted(candidate_root.rglob("*")) if p.is_file() and p.name != "phase-1.json"]
    if phase1.get("candidateId") != binding.get("candidateId") or phase1.get("sourceCommitSha") != binding.get("releaseCommitSha") or phase1.get("ociIndexDigest") != binding.get("ociIndexDigest") or phase1.get("workflowRunId") != provenance.get("workflowRunId") or phase1.get("workflowRunAttempt") != provenance.get("workflowRunAttempt") or phase1.get("workflowRef") != provenance.get("workflowRef") or phase1.get("objects") != objects:
        fail("phase-1 object inventory mismatch")
    if ready is not None:
        validate_run_ready(run_root, binding, ready)
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


def scan_paths(run_root: Path) -> list[Path]:
    directory = run_root / "scans"
    return sorted(directory.glob("*.json")) if directory.is_dir() else []


def load_scan_attestations(run_root: Path, evidence: dict[str, dict[str, Any]]) -> None:
    paths = scan_paths(run_root)
    if len(paths) != len(evidence):
        fail("scan attestations must cover every evidence object")
    binding = load_binding(run_root)
    for path in paths:
        value = read_json(path, f"scans/{path.name}")
        if not isinstance(value, dict) or path.stem != value.get("evidenceId"):
            fail(f"scans/{path.name}: filename/evidenceId mismatch")
        evidence_id = require_hex(require_string(value, "evidenceId"), f"scans/{path.name}.evidenceId")
        source = evidence.get(evidence_id)
        if source is None:
            fail(f"scans/{path.name}: evidence object is missing")
        scan = source.get("prohibitedContentScan")
        if any(value.get(field) != binding.get(field) for field in ("qualificationRunId", "bindingId", "candidateId")) or any(value.get(field) != scan.get(field) for field in ("result", "scannerId", "scannerVersion", "reportDigestSha256")):
            fail(f"scans/{path.name}: scan attestation mismatch")
        value_free({"scannerId": value.get("scannerId"), "scannerVersion": value.get("scannerVersion")}, f"scans/{path.name}")
        require_hex(value.get("reportDigestSha256"), f"scans/{path.name}.reportDigestSha256")


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
    load_scan_attestations(run_root, result)
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


def load_scope_manifest(path: Path) -> dict[str, Any]:
    manifest = read_json(path, "scope manifest")
    if not isinstance(manifest, dict):
        fail("scope manifest: object required")
    if manifest.get("schemaVersion") != SCOPE_MANIFEST_SCHEMA_VERSION:
        fail("scope manifest: unsupported schemaVersion")
    if not SAFE_ID.fullmatch(require_string(manifest, "scopeId")):
        fail("scope manifest: unsafe scopeId")
    scope_id = manifest["scopeId"]
    scope_version = manifest.get("scopeVersion")
    if not isinstance(scope_version, int) or scope_version < 1:
        fail("scope manifest: invalid scopeVersion")
    release_version = require_string(manifest, "releaseVersion")
    if not RELEASE_VERSION.fullmatch(release_version):
        fail("scope manifest: invalid releaseVersion")
    authority_issue = manifest.get("authorityIssueNumber")
    if not isinstance(authority_issue, int) or authority_issue < 1:
        fail("scope manifest: invalid authorityIssueNumber")
    authority_body_sha = require_hex(require_string(manifest, "authorityIssueBodySha256"), "authorityIssueBodySha256")
    plan_revision = require_value_free_identity(manifest.get("planRevision"), "planRevision")
    plan_path = require_string(manifest, "planFilePath")
    if Path(plan_path).is_absolute() or ".." in Path(plan_path).parts:
        fail("scope manifest: unsafe planFilePath")
    plan_sha = require_hex(require_string(manifest, "planFileSha256"), "planFileSha256")
    variant_version = manifest.get("variantRulesVersion")
    predicate_version = manifest.get("migrationPredicateSetVersion")
    if not isinstance(variant_version, int) or variant_version < 1 or not isinstance(predicate_version, int) or predicate_version < 1:
        fail("scope manifest: invalid rules/predicate version")
    if scope_id != V13_SCOPE_ID or authority_issue != V13_AUTHORITY_ISSUE or release_version != "1.3.0":
        fail("scope manifest: only the explicit v1.3.0 authority profile is supported")
    raw_rows = manifest.get("scenarioRows")
    if not isinstance(raw_rows, list) or len(raw_rows) != 44:
        fail("scope manifest: exactly 44 scenario rows are required")
    normalized_rows: list[dict[str, Any]] = []
    seen: set[str] = set()
    for index, raw in enumerate(raw_rows):
        if not isinstance(raw, dict) or raw.get("rowIndex") != index:
            fail(f"scope manifest scenarioRows[{index}]: rowIndex mismatch")
        scenario = require_value_free_identity(raw.get("scenarioId"), "scope scenarioId")
        if scenario in seen:
            fail(f"scope manifest: duplicate scenario {scenario}")
        seen.add(scenario)
        reuse_of = raw.get("reuseOf")
        if reuse_of is not None:
            if reuse_of != scenario or scenario not in CANONICAL_VARIANTS or not scenario.startswith("G456-") or int(scenario[5:]) > 41:
                fail(f"scope manifest: invalid legacy reuse mapping for {scenario}")
            gate = CANONICAL_GATES[scenario]
            variants = list(CANONICAL_VARIANTS[scenario])
        else:
            gate = require_value_free_identity(raw.get("gateClass"), "scope gateClass")
            if gate not in {"Hard", "Conditional", "Informational"}:
                fail(f"scope manifest: invalid gateClass for {scenario}")
            raw_variants = raw.get("requiredVariants")
            if not isinstance(raw_variants, list) or len(set(raw_variants)) != len(raw_variants) or any(not isinstance(v, str) or not SAFE_ID.fullmatch(v) for v in raw_variants):
                fail(f"scope manifest: invalid requiredVariants for {scenario}")
            variants = list(raw_variants)
        predicate_set = require_value_free_identity(raw.get("predicateSet"), "predicateSet")
        owner_class = require_value_free_identity(raw.get("ownerRoleClass"), "ownerRoleClass")
        if predicate_set != SCOPE_PREDICATE_SETS.get(scenario) or owner_class != SCOPE_OWNER_CLASSES.get(scenario):
            fail(f"scope manifest: predicate/owner authority mismatch for {scenario}")
        normalized_rows.append({
            "rowIndex": index,
            "scenarioId": scenario,
            "gateClass": gate,
            "requiredVariants": variants,
            "reuseOf": reuse_of,
            "predicateSet": predicate_set,
            "ownerRoleClass": owner_class,
            "informationalNotRequired": gate == "Informational",
        })
    expected_legacy = {f"G456-{number:02d}" for number in range(1, 42)}
    if not expected_legacy.issubset(seen) or seen - expected_legacy != V13_MIGRATION_SCENARIOS:
        fail("scope manifest: expected G456-01..41 plus G583-MIG-01..03")
    migration = manifest.get("migration")
    if not isinstance(migration, dict):
        fail("scope manifest: migration object required")
    schema_allowlist_version = migration.get("schemaAllowlistVersion")
    if not isinstance(schema_allowlist_version, int) or schema_allowlist_version < 1:
        fail("scope manifest: invalid schemaAllowlistVersion")
    schema_allowlist = migration.get("schemaAllowlist")
    if not isinstance(schema_allowlist, dict) or set(schema_allowlist) != set(V13_MIGRATION_DELTA):
        fail("scope manifest: schema allowlist must cover exactly v1.3 migration delta")
    for migration_name, definition in schema_allowlist.items():
        if not isinstance(definition, dict) or not HEX64.fullmatch(str(definition.get("sqlSha256", ""))):
            fail(f"scope manifest: schema allowlist digest missing for {migration_name}")
        for field in ("tables", "indexes", "constraints"):
            if not isinstance(definition.get(field), list) or any(not isinstance(item, str) or not item for item in definition[field]):
                fail(f"scope manifest: schema allowlist {field} missing for {migration_name}")
    baseline = migration.get("baselineInventory")
    delta = migration.get("deltaInventory")
    full = migration.get("fullInventory")
    if baseline != V13_MIGRATION_BASELINE or delta != V13_MIGRATION_DELTA or full != V13_MIGRATION_FULL_INVENTORY or full != baseline + delta:
        fail("scope manifest: baseline/delta/full migration inventory mismatch")
    if migration.get("baselineReleaseVersion") != "1.2.0" or migration.get("scenarioIds") != sorted(V13_MIGRATION_SCENARIOS) or not isinstance(migration.get("inventoryAlgorithm"), str) or not migration["inventoryAlgorithm"].startswith("RFC8785-JCS-"):
        fail("scope manifest: migration authority metadata mismatch")
    return {
        "schemaVersion": manifest["schemaVersion"],
        "scopeId": scope_id,
        "scopeVersion": scope_version,
        "releaseVersion": release_version,
        "authorityIssueNumber": authority_issue,
        "authorityIssueBodySha256": authority_body_sha,
        "planRevision": plan_revision,
        "planFilePath": plan_path,
        "planFileSha256": plan_sha,
        "variantRulesVersion": variant_version,
        "migrationPredicateSetVersion": predicate_version,
        "scenarioRows": normalized_rows,
        "optionalEvidenceKeys": [{"scenarioId": "G456-38", "variantId": "nas"}, {"scenarioId": "G456-39", "variantId": "macos"}, {"scenarioId": "G456-40", "variantId": "mode5-manual"}, {"scenarioId": "G456-41", "variantId": "external-secret-manager-docs"}],
        "migration": {
            "baselineReleaseVersion": "1.2.0",
            "baselineInventory": list(baseline),
            "deltaInventory": list(delta),
            "fullInventory": list(full),
            "inventoryAlgorithm": migration["inventoryAlgorithm"],
            "predicateSetVersion": predicate_version,
            "schemaAllowlistVersion": schema_allowlist_version,
            "schemaAllowlist": schema_allowlist,
            "schemaAllowlistSha256": sha_object(schema_allowlist),
            "scenarioIds": sorted(V13_MIGRATION_SCENARIOS),
        },
        "scopeManifestSha256": sha_object(manifest),
    }


def bind_rows(snapshot: dict[str, Any], scope_profile: dict[str, Any] | None = None) -> list[dict[str, Any]]:
    rows = snapshot.get("rows")
    if not isinstance(rows, list) or not rows:
        fail("issue snapshot rows: non-empty array required")
    if scope_profile is not None:
        expected_rows = scope_profile["scenarioRows"]
        if len(rows) != len(expected_rows):
            fail("issue snapshot rows: scope cardinality mismatch")
        result = []
        for index, row in enumerate(rows):
            if not isinstance(row, dict):
                fail(f"issue snapshot rows[{index}]: object required")
            expected = expected_rows[index]
            scenario = require_string(row, "scenarioId")
            if scenario != expected["scenarioId"] or row.get("rowIndex", index) != index:
                fail(f"issue snapshot rows[{index}]: scope scenario/order mismatch")
            variants = row.get("requiredVariants", [])
            if row.get("gateClass") != expected["gateClass"] or variants != expected["requiredVariants"]:
                fail(f"issue snapshot rows[{scenario}]: scope gate/variant mismatch")
            if "predicateSet" in row and row.get("predicateSet") != expected["predicateSet"]:
                fail(f"issue snapshot rows[{scenario}]: predicate authority mismatch")
            if "ownerRoleClass" in row and row.get("ownerRoleClass") != expected["ownerRoleClass"]:
                fail(f"issue snapshot rows[{scenario}]: owner authority mismatch")
            result.append({
                "rowIndex": index,
                "scenarioId": scenario,
                "scenarioText": require_string(row, "scenarioText"),
                "environmentText": require_string(row, "environmentText"),
                "gateClass": expected["gateClass"],
                "scenarioTextSha256": sha_bytes(require_string(row, "scenarioText").encode("utf-8")),
                "environmentTextSha256": sha_bytes(require_string(row, "environmentText").encode("utf-8")),
                "requiredVariants": list(expected["requiredVariants"]),
                "informationalNotRequired": expected["informationalNotRequired"],
                "reuseOf": expected["reuseOf"],
                "predicateSet": expected["predicateSet"],
                "ownerRoleClass": expected["ownerRoleClass"],
            })
        return result
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
    oci_layout_index_sha = validate_oci_layout(Path(args.oci_layout), expected_digest, provenance["releaseVersion"], source)
    cid = candidate_id(provenance, archive_digests)
    candidate_store = store / "candidates" / cid
    if candidate_store.exists():
        fail("candidateId already exists; intake is write-once")
    intake = candidate_store / "intake"
    handoff = candidate_root / "CANDIDATE-HANDOFF.md"
    if not handoff.is_file() or handoff.is_symlink():
        fail("CANDIDATE-HANDOFF.md: required regular file")
    handoff_text = handoff.read_text(encoding="utf-8")
    if re.search(r"(?:password|secret|token|connectionstring|recipient|sender|subject|raw.?error|bearer|cookie)\s*[:=]", handoff_text, re.I) or re.search(r"-----BEGIN", handoff_text):
        fail("CANDIDATE-HANDOFF.md contains prohibited secret/PII markers")
    for name in ("candidate-provenance.json", "image-identity.json", "CANDIDATE-SHA256SUMS", "CANDIDATE-HANDOFF.md"):
        copy_tree_file(candidate_root / name, intake / name)
    for archive in provenance["archives"]:
        copy_tree_file(candidate_root / archive["archiveFileName"], intake / archive["archiveFileName"])
    manifest = {
        "schemaVersion": 1,
        "candidateId": cid,
        "sourceCommitSha": source,
        "ociIndexDigest": expected_digest,
        "ociLayoutIndexSha256": oci_layout_index_sha,
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


def command_validate_scope(args: argparse.Namespace) -> None:
    profile = load_scope_manifest(Path(args.scope_manifest))
    plan = Path(args.repo_root).resolve() / profile["planFilePath"]
    if file_sha(plan) != profile["planFileSha256"]:
        fail("scope plan file digest mismatch")
    if args.issue_snapshot:
        snapshot = read_json(Path(args.issue_snapshot), "scope issue snapshot")
        if snapshot.get("number") != profile["authorityIssueNumber"] or sha_bytes(require_string(snapshot, "body").encode("utf-8")) != profile["authorityIssueBodySha256"]:
            fail("scope issue snapshot authority mismatch")
        bind_rows(snapshot, profile)
    print(json.dumps({"scopeId": profile["scopeId"], "scopeVersion": profile["scopeVersion"], "scopeManifestSha256": profile["scopeManifestSha256"], "scenarioCount": len(profile["scenarioRows"])}, sort_keys=True))


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


def _normalize_migration_files(files: Any, expected_paths: list[str], label: str) -> list[dict[str, str]]:
    if not isinstance(files, list) or [entry.get("path") for entry in files if isinstance(entry, dict)] != expected_paths or len(files) != len(expected_paths):
        fail(f"{label}: exact ordered inventory required")
    normalized_files = []
    for entry in files:
        if not isinstance(entry, dict):
            fail(f"{label}: file entry must be an object")
        path_value = require_string(entry, "path")
        if path_value.startswith("/") or "\\" in path_value or ".." in Path(path_value).parts:
            fail(f"{label}: unsafe file path")
        normalized_files.append({
            "path": path_value,
            "sha256": require_hex(require_string(entry, "sha256"), f"{label}.sha256"),
            "gitBlobSha": require_commit(require_string(entry, "gitBlobSha"), f"{label}.gitBlobSha"),
        })
    return normalized_files


def load_migration_pin(path: Path, scope_profile: dict[str, Any] | None = None) -> dict[str, Any]:
    pin = read_json(path, "migration pin")
    if not isinstance(pin, dict):
        fail("migration pin: object required")
    without = pin.get("migrationPinWithoutDigest")
    if not isinstance(without, dict):
        fail("migrationPinWithoutDigest: object required")
    if without.get("schemaVersion") != 1:
        fail("migrationPinWithoutDigest.schemaVersion: expected 1")
    require_commit(require_string(without, "releaseCommitSha"), "migrationPinWithoutDigest.releaseCommitSha")
    algorithm = require_string(without, "inventoryAlgorithm")
    if scope_profile is None and algorithm != "RFC8785-JCS-runner-order-migration-inventory-sha256/v1":
        fail("migrationPinWithoutDigest.inventoryAlgorithm: unsupported legacy algorithm")
    if scope_profile is not None and algorithm != scope_profile["migration"]["inventoryAlgorithm"]:
        fail("migrationPinWithoutDigest.inventoryAlgorithm: scope algorithm mismatch")
    if "evidenceDigestSha256" in without:
        fail("migrationPinWithoutDigest: evidenceDigestSha256 is forbidden")
    pin_digest = require_hex(require_string(pin, "migrationPinDigestSha256"), "migrationPinDigestSha256")
    inventory_digest = require_hex(require_string(pin, "migrationInventoryDigestSha256"), "migrationInventoryDigestSha256")
    if sha_object(without) != pin_digest:
        fail("migrationPinDigestSha256: canonical digest mismatch")
    if without.get("inventoryDigestSha256") != inventory_digest:
        fail("migrationInventoryDigestSha256: must match migrationPinWithoutDigest.inventoryDigestSha256")
    if scope_profile is None:
        expected_paths = [f"src/Amane.Mailer/Data/Migrations/{name}" for name in MIGRATION_POST011]
        normalized_files = _normalize_migration_files(without.get("files"), expected_paths, "migrationPinWithoutDigest.files")
        if without.get("inventoryDigestSha256") != inventory_digest:
            fail("migrationInventoryDigestSha256: must match migrationPinWithoutDigest.inventoryDigestSha256")
        return {
            "releaseCommitSha": without["releaseCommitSha"],
            "inventoryDigestSha256": without["inventoryDigestSha256"],
            "migrationPinDigestSha256": pin_digest,
            "migrationInventoryDigestSha256": inventory_digest,
            "migrationFileDigests": normalized_files,
        }
    if without.get("scopeId") != scope_profile["scopeId"] or without.get("scopeVersion") != scope_profile["scopeVersion"] or without.get("authorityIssueNumber") != scope_profile["authorityIssueNumber"] or without.get("authorityIssueBodySha256") != scope_profile["authorityIssueBodySha256"]:
        fail("migration PIN scope authority mismatch")
    migration = scope_profile["migration"]
    if without.get("predicateSetVersion") != migration["predicateSetVersion"] or without.get("schemaAllowlistVersion") != migration["schemaAllowlistVersion"]:
        fail("migration PIN predicate/schema authority mismatch")
    if without.get("baselineInventory") != migration["baselineInventory"] or without.get("deltaInventory") != migration["deltaInventory"] or without.get("fullInventory") != migration["fullInventory"]:
        fail("migration PIN baseline/delta/full inventory mismatch")
    baseline_digest = require_hex(require_string(without, "baselineInventoryDigestSha256"), "baselineInventoryDigestSha256")
    delta_digest = require_hex(require_string(without, "deltaInventoryDigestSha256"), "deltaInventoryDigestSha256")
    full_digest = require_hex(require_string(without, "fullInventoryDigestSha256"), "fullInventoryDigestSha256")
    if inventory_digest != full_digest or without.get("inventoryDigestSha256") != full_digest:
        fail("migration PIN full inventory digest mismatch")
    baseline_files = _normalize_migration_files(without.get("baselineFiles"), [f"src/Amane.Mailer/Data/Migrations/{name}" for name in migration["baselineInventory"]], "migrationPinWithoutDigest.baselineFiles")
    delta_files = _normalize_migration_files(without.get("deltaFiles"), [f"src/Amane.Mailer/Data/Migrations/{name}" for name in migration["deltaInventory"]], "migrationPinWithoutDigest.deltaFiles")
    full_files = _normalize_migration_files(without.get("fullFiles"), [f"src/Amane.Mailer/Data/Migrations/{name}" for name in migration["fullInventory"]], "migrationPinWithoutDigest.fullFiles")
    return {
        "releaseCommitSha": without["releaseCommitSha"],
        "inventoryDigestSha256": full_digest,
        "migrationPinDigestSha256": pin_digest,
        "migrationInventoryDigestSha256": full_digest,
        "migrationFileDigests": full_files,
        "scopeId": scope_profile["scopeId"],
        "scopeVersion": scope_profile["scopeVersion"],
        "authorityIssueNumber": scope_profile["authorityIssueNumber"],
        "authorityIssueBodySha256": scope_profile["authorityIssueBodySha256"],
        "migrationBaselineInventory": list(migration["baselineInventory"]),
        "migrationDeltaInventory": list(migration["deltaInventory"]),
        "migrationFullInventory": list(migration["fullInventory"]),
        "migrationBaselineInventoryDigestSha256": baseline_digest,
        "migrationDeltaInventoryDigestSha256": delta_digest,
        "migrationFullInventoryDigestSha256": full_digest,
        "migrationBaselineFileDigests": baseline_files,
        "migrationDeltaFileDigests": delta_files,
        "migrationFullFileDigests": full_files,
        "migrationPredicateSetVersion": scope_profile["migrationPredicateSetVersion"],
    }


def verify_migration_pin_tree(repo_root: Path, release_commit_sha: str, migration_pin: dict[str, Any], scope_profile: dict[str, Any] | None = None) -> None:
    if not repo_root.is_dir():
        fail("repo-root: directory is missing")
    if git_output(repo_root, "rev-parse", release_commit_sha) != release_commit_sha:
        fail("repo-root does not contain the exact release commit")
    tree_paths = git_output(repo_root, "ls-tree", "-r", "--name-only", release_commit_sha, "--", "src/Amane.Mailer/Data/Migrations").splitlines()
    full_inventory = [path for path in tree_paths if path.endswith(".sql")]
    inventory_names = MIGRATION_FULL_INVENTORY if scope_profile is None else scope_profile["migration"]["fullInventory"]
    expected_inventory = [f"src/Amane.Mailer/Data/Migrations/{name}" for name in inventory_names]
    if full_inventory != expected_inventory:
        fail("migration tree inventory does not match the frozen runner-order inventory")
    inventory_document = {"schemaVersion": 1, "releaseCommitSha": release_commit_sha, "runnerOrderPaths": full_inventory}
    if scope_profile is not None:
        inventory_document.update({"scopeId": scope_profile["scopeId"], "scopeVersion": scope_profile["scopeVersion"], "baselineInventory": scope_profile["migration"]["baselineInventory"], "deltaInventory": scope_profile["migration"]["deltaInventory"]})
    expected_inventory_digest = sha_object(inventory_document)
    if expected_inventory_digest != migration_pin["inventoryDigestSha256"]:
        fail("migration inventory digest does not match the release tree")
    files = migration_pin["migrationFileDigests"] if scope_profile is None else migration_pin["migrationFullFileDigests"]
    for entry in files:
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
    scope_profile = load_scope_manifest(Path(args.scope_manifest)) if args.scope_manifest else None
    if intake_manifest.get("releaseVersion") == "1.3.0" and scope_profile is None:
        fail("v1.3.0 candidate requires an explicit --scope-manifest")
    if scope_profile is None:
        if snapshot.get("number") != 456:
            fail("issue snapshot number: expected 456")
    elif snapshot.get("number") != scope_profile["authorityIssueNumber"]:
        fail("issue snapshot number does not match scope authority")
    body = require_string(snapshot, "body")
    issue_body_sha = sha_bytes(body.encode("utf-8"))
    if scope_profile is not None and issue_body_sha != scope_profile["authorityIssueBodySha256"]:
        fail("issue snapshot body does not match scope authority digest")
    rows = bind_rows(snapshot, scope_profile)
    plan_path = Path(args.plan_file).resolve()
    plan_sha = file_sha(plan_path)
    plan_commit = require_commit(args.plan_commit_sha, "plan-commit-sha")
    plan_relative_path = verify_plan_source(Path(args.repo_root), plan_path, plan_commit, plan_sha)
    if scope_profile is not None and (plan_relative_path != scope_profile["planFilePath"] or plan_sha != scope_profile["planFileSha256"]):
        fail("scope plan path/digest does not match the scope manifest")
    migration_pin = load_migration_pin(Path(args.migration_pin), scope_profile)
    if migration_pin["releaseCommitSha"] != intake_manifest["sourceCommitSha"]:
        fail("migration pin releaseCommitSha does not match candidate sourceCommitSha")
    if not isinstance(intake_manifest.get("ociLayoutIndexSha256"), str) or not HEX64.fullmatch(intake_manifest["ociLayoutIndexSha256"]):
        fail("phase-1 OCI layout index digest is missing")
    verify_migration_pin_tree(Path(args.repo_root).resolve(), intake_manifest["sourceCommitSha"], migration_pin, scope_profile)
    candidate_intake = candidate_store / "intake"
    provenance, identity, archive_digests = candidate_documents(candidate_intake)
    if candidate_id(provenance, archive_digests) != args.candidate_id:
        fail("candidate intake candidateId recomputation mismatch")
    candidate_provenance_sha = file_sha(candidate_intake / "candidate-provenance.json")
    candidate_identity_sha = file_sha(candidate_intake / "image-identity.json")
    candidate_phase1_sha = file_sha(candidate_intake / "phase-1.json")
    candidate_archives_sha = sha_object({"archives": provenance["archives"], "archiveDigests": archive_digests})
    archive_paths = [candidate_intake / archive["archiveFileName"] for archive in provenance["archives"]]
    docs_metadata, docs_payloads = docs_from_release_tree(
        Path(args.repo_root),
        intake_manifest["sourceCommitSha"],
        candidate_intake,
        archive_paths,
        rid_specific=scope_profile is not None,
    )
    run_nonce_seed = require_value_free_identity(args.run_attempt_nonce, "run-attempt-nonce")
    owners = load_owner_map(Path(args.evidence_owners))
    optional = scope_profile["optionalEvidenceKeys"] if scope_profile is not None else [{"scenarioId": "G456-38", "variantId": "nas"}, {"scenarioId": "G456-39", "variantId": "macos"}, {"scenarioId": "G456-40", "variantId": "mode5-manual"}, {"scenarioId": "G456-41", "variantId": "external-secret-manager-docs"}]
    required_keys = {(r["scenarioId"], v) for r in rows for v in r["requiredVariants"]}
    required_keys.update((e["scenarioId"], e["variantId"]) for e in optional)
    owner_keys = {(e["scenarioId"], e["variantId"]) for e in owners}
    if required_keys != owner_keys:
        fail("evidence owners must cover every required and optional key exactly once")
    qualification_lead_role = require_value_free_identity(args.qualification_lead_role, "qualification-lead-role")
    qualification_lead_identity = require_value_free_identity(args.qualification_lead_identity, "qualification-lead-identity")
    conditional_role = require_value_free_identity(args.conditional_approver_role, "conditional-approver-role")
    conditional_identity = require_value_free_identity(args.conditional_approver_identity, "conditional-approver-identity")
    # A v1.3 binding is governed by the selected scope authority.  Keep the
    # historical #456 values only for the legacy profile; never mix them into
    # a v1.3 binding and let a later load_binding call discover the mismatch.
    scope_plan_revision = scope_profile["planRevision"] if scope_profile is not None else "12"
    scope_issue_number = scope_profile["authorityIssueNumber"] if scope_profile is not None else 456
    scope_variant_rules_version = scope_profile["variantRulesVersion"] if scope_profile is not None else VARIANT_RULES_VERSION
    scope_predicate_version = scope_profile["migrationPredicateSetVersion"] if scope_profile is not None else None
    scope_schema_allowlist_version = scope_profile["migration"]["schemaAllowlistVersion"] if scope_profile is not None else None
    binding_material = {
        "lifecycleVersion": RUN_LIFECYCLE_VERSION,
        "bindingNonce": fresh_lifecycle_nonce("binding"),
        "candidateId": args.candidate_id,
        "issueBodySha256": issue_body_sha,
        "planCommitSha": plan_commit,
        "planFilePath": plan_relative_path,
        "planFileSha256": plan_sha,
        "variantRulesVersion": scope_variant_rules_version,
        "migrationPinDigestSha256": migration_pin["migrationPinDigestSha256"],
        "migrationInventoryDigestSha256": migration_pin["migrationInventoryDigestSha256"],
        "releaseCommitSha": intake_manifest["sourceCommitSha"],
        "ociIndexDigest": intake_manifest["ociIndexDigest"],
        "ociLayoutIndexSha256": intake_manifest["ociLayoutIndexSha256"],
        "releaseVersion": provenance["releaseVersion"],
        "ociPlatforms": provenance["ociPlatforms"],
        "producerWorkflowRef": provenance["workflowRef"],
        "producerWorkflowRunId": provenance["workflowRunId"],
        "producerWorkflowRunAttempt": provenance["workflowRunAttempt"],
        "candidateProvenanceSha256": candidate_provenance_sha,
        "candidateImageIdentitySha256": candidate_identity_sha,
        "candidatePhase1ManifestSha256": candidate_phase1_sha,
        "candidateArchivesDigestSha256": candidate_archives_sha,
        "docs": docs_metadata,
        "rows": rows,
    }
    if scope_profile is not None:
        binding_material.update({
            "scopeId": scope_profile["scopeId"],
            "scopeVersion": scope_profile["scopeVersion"],
            "scopeManifestSha256": scope_profile["scopeManifestSha256"],
            "scopeAuthorityIssueNumber": scope_profile["authorityIssueNumber"],
            "scopeAuthorityIssueBodySha256": scope_profile["authorityIssueBodySha256"],
            "scopePlanFileSha256": scope_profile["planFileSha256"],
            "migrationBaselineInventoryDigestSha256": sha_object({"scopeId": scope_profile["scopeId"], "scopeVersion": scope_profile["scopeVersion"], "releaseCommitSha": intake_manifest["sourceCommitSha"], "runnerOrderPaths": [f"src/Amane.Mailer/Data/Migrations/{name}" for name in scope_profile["migration"]["baselineInventory"]]}),
            "migrationDeltaInventoryDigestSha256": sha_object({"scopeId": scope_profile["scopeId"], "scopeVersion": scope_profile["scopeVersion"], "releaseCommitSha": intake_manifest["sourceCommitSha"], "runnerOrderPaths": [f"src/Amane.Mailer/Data/Migrations/{name}" for name in scope_profile["migration"]["deltaInventory"]]}),
            "migrationFullInventoryDigestSha256": migration_pin["migrationFullInventoryDigestSha256"],
            "migrationPredicateSetVersion": scope_predicate_version,
            "migrationSchemaAllowlistVersion": scope_schema_allowlist_version,
            "migrationSchemaAllowlistSha256": scope_profile["migration"]["schemaAllowlistSha256"],
            "migrationSchemaAllowlist": scope_profile["migration"]["schemaAllowlist"],
        })
        if binding_material["migrationBaselineInventoryDigestSha256"] != migration_pin["migrationBaselineInventoryDigestSha256"] or binding_material["migrationDeltaInventoryDigestSha256"] != migration_pin["migrationDeltaInventoryDigestSha256"]:
            fail("scope migration inventory digest does not match the migration PIN")
    authorization_seed = {
        "qualificationLeadRole": qualification_lead_role,
        "qualificationLeadIdentity": qualification_lead_identity,
        "conditionalApproverRole": conditional_role,
        "conditionalApproverIdentity": conditional_identity,
        "evidenceOwners": owners,
    }
    binding_id = binding_id_for(binding_material, authorization_seed)
    run_nonce = fresh_lifecycle_nonce("run", run_nonce_seed)
    run_id = sha_bytes((binding_id + "|" + run_nonce).encode("utf-8"))
    run_root = store / "runs" / run_id
    create_staging_run_root(run_root)
    created = utc_now()
    authorization = {
        "schemaVersion": 1,
        "lifecycleVersion": RUN_LIFECYCLE_VERSION,
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
        "lifecycleVersion": RUN_LIFECYCLE_VERSION,
        "bindingNonce": binding_material["bindingNonce"],
        "bindingId": binding_id,
        "qualificationRunId": run_id,
        "runAttemptNonce": run_nonce,
        "candidateId": args.candidate_id,
        "planRevision": scope_plan_revision,
        "planCommitSha": plan_commit,
        "planFilePath": plan_relative_path,
        "planFileSha256": plan_sha,
        "variantRulesVersion": scope_variant_rules_version,
        "issueNumber": scope_issue_number,
        "issueUpdatedAt": require_string(snapshot, "updatedAt"),
        "issueBodySha256": issue_body_sha,
        "fetchedAtUtc": created,
        "sourceCommitSha": intake_manifest["sourceCommitSha"],
        "releaseCommitSha": intake_manifest["sourceCommitSha"],
        "ociIndexDigest": intake_manifest["ociIndexDigest"],
        "ociLayoutIndexSha256": intake_manifest["ociLayoutIndexSha256"],
        "releaseVersion": provenance["releaseVersion"],
        "ociPlatforms": provenance["ociPlatforms"],
        "producerWorkflowRef": provenance["workflowRef"],
        "producerWorkflowRunId": provenance["workflowRunId"],
        "producerWorkflowRunAttempt": provenance["workflowRunAttempt"],
        "candidateProvenanceSha256": candidate_provenance_sha,
        "candidateImageIdentitySha256": candidate_identity_sha,
        "candidatePhase1ManifestSha256": candidate_phase1_sha,
        "candidateArchivesDigestSha256": candidate_archives_sha,
        "docs": docs_metadata,
        "migrationPinDigestSha256": migration_pin["migrationPinDigestSha256"],
        "migrationInventoryDigestSha256": migration_pin["migrationInventoryDigestSha256"],
        "migrationFileDigests": migration_pin["migrationFileDigests"],
        "rows": rows,
        "optionalEvidenceKeys": optional,
        "authorizationDigestSha256": sha_object(authorization),
    }
    if scope_profile is not None:
        binding.update({
            "scopeId": scope_profile["scopeId"],
            "scopeVersion": scope_profile["scopeVersion"],
            "scopeManifestSha256": scope_profile["scopeManifestSha256"],
            "scopeAuthorityIssueNumber": scope_profile["authorityIssueNumber"],
            "scopeAuthorityIssueBodySha256": scope_profile["authorityIssueBodySha256"],
            "scopePlanFileSha256": scope_profile["planFileSha256"],
            "scopePlanRevision": scope_profile["planRevision"],
            "migrationBaselineInventory": migration_pin["migrationBaselineInventory"],
            "migrationDeltaInventory": migration_pin["migrationDeltaInventory"],
            "migrationFullInventory": migration_pin["migrationFullInventory"],
            "migrationBaselineInventoryDigestSha256": binding_material["migrationBaselineInventoryDigestSha256"],
            "migrationDeltaInventoryDigestSha256": binding_material["migrationDeltaInventoryDigestSha256"],
            "migrationFullInventoryDigestSha256": migration_pin["migrationFullInventoryDigestSha256"],
            "migrationBaselineFileDigests": migration_pin["migrationBaselineFileDigests"],
            "migrationDeltaFileDigests": migration_pin["migrationDeltaFileDigests"],
            "migrationFullFileDigests": migration_pin["migrationFullFileDigests"],
            "migrationPredicateSetVersion": scope_predicate_version,
            "migrationSchemaAllowlistVersion": scope_schema_allowlist_version,
            "migrationSchemaAllowlistSha256": scope_profile["migration"]["schemaAllowlistSha256"],
            "migrationSchemaAllowlist": scope_profile["migration"]["schemaAllowlist"],
        })
    write_once(run_root / "binding.json", binding)
    write_once(run_root / "authorization.json", authorization)
    if scope_profile is not None:
        write_once(run_root / "scope-manifest.json", read_json(Path(args.scope_manifest), "scope manifest"))
    write_once(run_root / "migration-pin.json", read_json(Path(args.migration_pin), "migration pin"))
    docs_paths = {"setupGuideJa": "setup-guide.md", "setupGuideEn": "setup-guide.en.md", "setupReleaseBundleJa": "setup-release-bundle.md", "setupReleaseBundleEn": "setup-release-bundle.en.md", "readmeJa": "README.md", "readmeEn": "README.en.md", "candidateReadmeSetup": "README-SETUP.md"}
    for key, payload in docs_payloads.items():
        if key.startswith("candidateReadmeSetup:"):
            rid = key.split(":", 1)[1]
            write_bytes_once(run_root / "docs-extract" / "candidate-readme-setup" / f"{rid}.md", payload)
        else:
            write_bytes_once(run_root / "docs-extract" / docs_paths[key], payload)
    write_once(run_root / "docs-extract" / "metadata.json", docs_metadata)
    phase2 = {
        "schemaVersion": 1,
        "lifecycleVersion": RUN_LIFECYCLE_VERSION,
        "phase": 2,
        "candidateId": args.candidate_id,
        "bindingId": binding_id,
        "qualificationRunId": run_id,
        "bindingNonce": binding["bindingNonce"],
        "runAttemptNonce": run_nonce,
        "planFilePath": plan_relative_path,
        "authorizationDigestSha256": sha_object(authorization),
        "migrationPinDigestSha256": migration_pin["migrationPinDigestSha256"],
        "migrationInventoryDigestSha256": migration_pin["migrationInventoryDigestSha256"],
        "migrationFileDigests": migration_pin["migrationFileDigests"],
        "releaseCommitSha": migration_pin["releaseCommitSha"],
        "releaseVersion": provenance["releaseVersion"],
        "ociPlatforms": provenance["ociPlatforms"],
        "ociLayoutIndexSha256": intake_manifest["ociLayoutIndexSha256"],
        "producerWorkflowRef": provenance["workflowRef"],
        "producerWorkflowRunId": provenance["workflowRunId"],
        "producerWorkflowRunAttempt": provenance["workflowRunAttempt"],
        "candidateProvenanceSha256": candidate_provenance_sha,
        "candidateImageIdentitySha256": candidate_identity_sha,
        "candidatePhase1ManifestSha256": candidate_phase1_sha,
        "candidateArchivesDigestSha256": candidate_archives_sha,
        "docs": docs_metadata,
        "createdAtUtc": created,
    }
    if scope_profile is not None:
        phase2.update({
            "planRevision": binding["planRevision"],
            "issueNumber": binding["issueNumber"],
            "variantRulesVersion": binding["variantRulesVersion"],
            "scopeId": scope_profile["scopeId"],
            "scopeVersion": scope_profile["scopeVersion"],
            "scopeManifestSha256": scope_profile["scopeManifestSha256"],
            "scopeAuthorityIssueNumber": scope_profile["authorityIssueNumber"],
            "scopeAuthorityIssueBodySha256": scope_profile["authorityIssueBodySha256"],
            "scopePlanFileSha256": scope_profile["planFileSha256"],
            "migrationBaselineInventoryDigestSha256": binding["migrationBaselineInventoryDigestSha256"],
            "migrationDeltaInventoryDigestSha256": binding["migrationDeltaInventoryDigestSha256"],
            "migrationFullInventoryDigestSha256": binding["migrationFullInventoryDigestSha256"],
            "migrationPredicateSetVersion": binding["migrationPredicateSetVersion"],
            "migrationSchemaAllowlistVersion": binding["migrationSchemaAllowlistVersion"],
            "migrationSchemaAllowlistSha256": binding["migrationSchemaAllowlistSha256"],
            "migrationSchemaAllowlist": binding["migrationSchemaAllowlist"],
        })
    write_once(run_root / "phase-manifests" / "phase-2.json", phase2)
    # A run is ineligible until every Phase 2 object has been reloaded and
    # cross-verified.  The ready marker is deliberately the final write.
    load_binding(run_root, allow_staging=True)
    write_run_ready(run_root, binding)
    load_binding(run_root)
    print(json.dumps({"candidateId": args.candidate_id, "bindingId": binding_id, "qualificationRunId": run_id, "ready": True}, sort_keys=True))


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
    scope_fields = {"scopeId", "scopeVersion", "scopeManifestSha256"}
    allowed_scope_fields = scope_fields if binding.get("scopeId") else set()
    unknown = sorted(set(envelope) - COMMON_EVIDENCE_FIELDS - allowed_scope_fields)
    if unknown:
        fail(f"evidence envelope contains unknown fields: {','.join(unknown)}")
    if binding.get("scopeId"):
        if any(field not in envelope for field in scope_fields) or any(envelope.get(field) != binding.get(field) for field in scope_fields):
            fail("evidence envelope scope authority mismatch")
    elif scope_fields & set(envelope):
        fail("legacy evidence envelope cannot carry v1.3 scope fields")
    if envelope.get("schemaVersion") != 1 or envelope.get("kind") != "release-qualification-evidence":
        fail("evidence envelope schema/kind mismatch")
    evidence_id = require_hex(envelope.get("evidenceId"), "evidenceId")
    if envelope.get("candidateId") != binding.get("candidateId") or envelope.get("bindingId") != binding.get("bindingId") or envelope.get("qualificationRunId") != binding.get("qualificationRunId"):
        fail("evidence envelope qualification identity mismatch")
    if envelope.get("sourceCommitSha") != binding.get("releaseCommitSha") or envelope.get("issueBodySha256") != binding.get("issueBodySha256"):
        fail("evidence envelope source/issue identity mismatch")
    if envelope.get("planRevision") != binding.get("planRevision") or envelope.get("planCommitSha") != binding.get("planCommitSha") or envelope.get("planFileSha256") != binding.get("planFileSha256"):
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
    if scenario in V13_MIGRATION_SCENARIOS:
        if binding.get("scopeId") != V13_SCOPE_ID:
            fail(f"{scenario}: v1.3 migration evidence requires the v1.3 scope profile")
        common = {"migrationDecision", "baselineInventory", "deltaInventory", "fullInventory", "expectedFullMigrationInventory", "migrationDirectoryInventoryBefore", "migrationDirectoryInventoryDigestSha256", "migrationDeltaInventoryDigestSha256", "migrationFileDigests", "outcome", "preApplyAppliedMigrations", "preApplyPendingMigrations", "postApplyAppliedMigrations", "postApplyPendingMigrations", "lastAppliedBefore", "lastAppliedAfter"}
        extra = {"schemaContractResult", "piiValueCanaryResult", "schemaAllowlistVersion", "schemaAllowlistSha256"} if scenario == "G583-MIG-03" else set()
        if set(payload) - common - extra:
            fail(f"{scenario}: unknown v1.3 migration typePayload field")
        require_payload_fields(payload, common, scenario)
        baseline = binding["migrationBaselineInventory"]
        delta = binding["migrationDeltaInventory"]
        full = binding["migrationFullInventory"]
        if payload["migrationDecision"] != "INCLUDE" or payload["baselineInventory"] != baseline or payload["deltaInventory"] != delta or payload["fullInventory"] != full or payload["expectedFullMigrationInventory"] != full:
            fail(f"{scenario}: v1.3 baseline/delta/full inventory mismatch")
        if payload["migrationDirectoryInventoryBefore"] != full or payload["migrationDirectoryInventoryDigestSha256"] != binding["migrationFullInventoryDigestSha256"] or payload["migrationDeltaInventoryDigestSha256"] != binding["migrationDeltaInventoryDigestSha256"] or payload["migrationFileDigests"] != binding["migrationFullFileDigests"]:
            fail(f"{scenario}: v1.3 migration PIN evidence mismatch")
        if scenario == "G583-MIG-01":
            success = payload["outcome"] == "applied" and payload["preApplyAppliedMigrations"] == [] and payload["preApplyPendingMigrations"] == full and payload["postApplyAppliedMigrations"] == full and payload["postApplyPendingMigrations"] == [] and payload["lastAppliedBefore"] is None and payload["lastAppliedAfter"] == delta[-1]
        elif scenario == "G583-MIG-02":
            success = payload["outcome"] == "upgraded" and payload["preApplyAppliedMigrations"] == baseline and payload["preApplyPendingMigrations"] == delta and payload["postApplyAppliedMigrations"] == full and payload["postApplyPendingMigrations"] == [] and payload["lastAppliedBefore"] == baseline[-1] and payload["lastAppliedAfter"] == delta[-1]
        else:
            require_payload_fields(payload, extra, scenario)
            success = (
                payload["outcome"] == "schema-checked"
                and payload["schemaContractResult"] == "pass"
                and payload["piiValueCanaryResult"] == "pass"
                and payload["schemaAllowlistVersion"] == binding["migrationSchemaAllowlistVersion"]
                and payload["schemaAllowlistSha256"] == binding["migrationSchemaAllowlistSha256"]
            )
        if envelope["result"] == "PASS" and not success:
            fail(f"{scenario}: PASS requires exact v1.3 migration predicate")
        if envelope["result"] == "FAIL" and success:
            fail(f"{scenario}: FAIL cannot carry a fully successful v1.3 predicate")
        return
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


def _field(field_type: type, *allowed: Any) -> tuple[type, frozenset[Any] | None]:
    return field_type, frozenset(allowed) if allowed else None


def _hard_validator(
    predicate_set: str,
    fields: dict[str, tuple[type, frozenset[Any] | None]],
    pass_predicate: Any,
    fail_predicate: Any,
) -> dict[str, Any]:
    """Build one explicit hard-lane predicate definition.

    The registry carries the scenario-specific field set and both directions of
    the predicate.  The shared executor below only performs the structural
    checks and invokes those declared predicates; it never accepts a generic
    boolean or predicateResult field.
    """
    scenario = predicate_set.removeprefix("legacy-g456-")
    return {
        "predicateSet": predicate_set,
        "procedureId": f"issue-456-g456-{scenario}",
        "procedureRevision": "1",
        "evidenceTypes": EVIDENCE_TYPES[f"G456-{scenario}"],
        "fields": fields,
        "pass": pass_predicate,
        "fail": fail_predicate,
    }


def _safe_pass(payload: dict[str, Any], variant: str, *checks: Any) -> bool:
    return all(check(payload, variant) for check in checks)


def _safe_fail(payload: dict[str, Any], variant: str, *checks: Any) -> bool:
    return any(check(payload, variant) for check in checks)


# Issue #456 defines the meaning of these lanes; this registry defines the
# value-free evidence shape needed to prove each meaning.  Gate classes and
# required variants remain exclusively in the bound Issue #583 scope profile.
HARD_SCENARIO_VALIDATOR_REGISTRY: dict[str, dict[str, Any]] = {
    "G456-01": _hard_validator(
        "legacy-g456-01",
        {
            "runtimeProfile": _field(str, "windows-docker-desktop"), "freshEnvironment": _field(bool),
            "mailpitReady": _field(bool), "mailerStarted": _field(bool), "requestAccepted": _field(bool),
            "deliveryObservedValueFree": _field(bool), "bundleIdentityMatch": _field(bool),
            "outcome": _field(str, "completed", "failed"), "sensitiveOutput": _field(str, "absent", "present"),
        },
        lambda p, v: _safe_pass(p, v, lambda x, _: x["freshEnvironment"], lambda x, _: x["mailpitReady"], lambda x, _: x["mailerStarted"], lambda x, _: x["requestAccepted"], lambda x, _: x["deliveryObservedValueFree"], lambda x, _: x["bundleIdentityMatch"], lambda x, _: x["outcome"] == "completed", lambda x, _: x["sensitiveOutput"] == "absent"),
        lambda p, v: _safe_fail(p, v, lambda x, _: x["outcome"] == "failed", lambda x, _: not x["freshEnvironment"], lambda x, _: not x["mailpitReady"], lambda x, _: not x["mailerStarted"], lambda x, _: not x["requestAccepted"], lambda x, _: not x["deliveryObservedValueFree"], lambda x, _: not x["bundleIdentityMatch"], lambda x, _: x["sensitiveOutput"] == "present"),
    ),
    "G456-02": _hard_validator(
        "legacy-g456-02",
        {
            "runtimeProfile": _field(str, "linux-docker-engine"), "freshEnvironment": _field(bool),
            "mailpitReady": _field(bool), "mailerStarted": _field(bool), "requestAccepted": _field(bool),
            "deliveryObservedValueFree": _field(bool), "bundleIdentityMatch": _field(bool),
            "outcome": _field(str, "completed", "failed"), "sensitiveOutput": _field(str, "absent", "present"),
        },
        lambda p, v: _safe_pass(p, v, lambda x, _: x["freshEnvironment"], lambda x, _: x["mailpitReady"], lambda x, _: x["mailerStarted"], lambda x, _: x["requestAccepted"], lambda x, _: x["deliveryObservedValueFree"], lambda x, _: x["bundleIdentityMatch"], lambda x, _: x["outcome"] == "completed", lambda x, _: x["sensitiveOutput"] == "absent"),
        lambda p, v: _safe_fail(p, v, lambda x, _: x["outcome"] == "failed", lambda x, _: not x["freshEnvironment"], lambda x, _: not x["mailpitReady"], lambda x, _: not x["mailerStarted"], lambda x, _: not x["requestAccepted"], lambda x, _: not x["deliveryObservedValueFree"], lambda x, _: not x["bundleIdentityMatch"], lambda x, _: x["sensitiveOutput"] == "present"),
    ),
    "G456-07": _hard_validator(
        "legacy-g456-07",
        {
            "accessProfile": _field(str, "development-loopback"), "transportProfile": _field(str, "http-loopback"),
            "loopbackOnly": _field(bool), "loginResult": _field(str, "success", "rejected"),
            "setupStatusResult": _field(str, "visible", "hidden"), "adminRouteResult": _field(str, "available", "unavailable"),
            "sensitiveOutput": _field(str, "absent", "present"),
        },
        lambda p, v: _safe_pass(p, v, lambda x, _: x["loopbackOnly"], lambda x, _: x["loginResult"] == "success", lambda x, _: x["setupStatusResult"] == "visible", lambda x, _: x["adminRouteResult"] == "available", lambda x, _: x["sensitiveOutput"] == "absent"),
        lambda p, v: _safe_fail(p, v, lambda x, _: not x["loopbackOnly"], lambda x, _: x["loginResult"] == "rejected", lambda x, _: x["setupStatusResult"] == "hidden", lambda x, _: x["adminRouteResult"] == "unavailable", lambda x, _: x["sensitiveOutput"] == "present"),
    ),
    "G456-08": _hard_validator(
        "legacy-g456-08",
        {
            "accessProfile": _field(str, "production-https"), "transportProfile": _field(str, "https"),
            "secureSessionFlag": _field(bool), "loginResult": _field(str, "success", "rejected"),
            "setupStatusResult": _field(str, "visible", "hidden"), "adminRouteResult": _field(str, "available", "unavailable"),
            "deploymentOvConfirmedShown": _field(bool), "sensitiveOutput": _field(str, "absent", "present"),
        },
        lambda p, v: _safe_pass(p, v, lambda x, _: x["secureSessionFlag"], lambda x, _: x["loginResult"] == "success", lambda x, _: x["setupStatusResult"] == "visible", lambda x, _: x["adminRouteResult"] == "available", lambda x, _: not x["deploymentOvConfirmedShown"], lambda x, _: x["sensitiveOutput"] == "absent"),
        lambda p, v: _safe_fail(p, v, lambda x, _: not x["secureSessionFlag"], lambda x, _: x["loginResult"] == "rejected", lambda x, _: x["setupStatusResult"] == "hidden", lambda x, _: x["adminRouteResult"] == "unavailable", lambda x, _: x["deploymentOvConfirmedShown"], lambda x, _: x["sensitiveOutput"] == "present"),
    ),
    "G456-09": _hard_validator(
        "legacy-g456-09",
        {
            "accessProfile": _field(str, "production-https"), "transportProfile": _field(str, "http"),
            "secureSessionFlag": _field(bool), "httpSessionAccepted": _field(bool), "loginResult": _field(str, "success", "rejected"),
            "adminRouteResult": _field(str, "available", "unavailable"), "httpFallbackAccepted": _field(bool),
            "sensitiveOutput": _field(str, "absent", "present"),
        },
        lambda p, v: _safe_pass(p, v, lambda x, _: x["secureSessionFlag"], lambda x, _: not x["httpSessionAccepted"], lambda x, _: x["loginResult"] == "rejected", lambda x, _: x["adminRouteResult"] == "unavailable", lambda x, _: not x["httpFallbackAccepted"], lambda x, _: x["sensitiveOutput"] == "absent"),
        lambda p, v: _safe_fail(p, v, lambda x, _: not x["secureSessionFlag"], lambda x, _: x["httpSessionAccepted"], lambda x, _: x["loginResult"] == "success", lambda x, _: x["adminRouteResult"] == "available", lambda x, _: x["httpFallbackAccepted"], lambda x, _: x["sensitiveOutput"] == "present"),
    ),
    "G456-10": _hard_validator(
        "legacy-g456-10",
        {
            "accessProfile": _field(str, "production-https"), "transportProfile": _field(str, "http"),
            "amaneAdminAllowHttp": _field(bool), "configRejected": _field(bool),
            "adminRouteResult": _field(str, "available", "unavailable"), "outcome": _field(str, "rejected", "accepted"),
            "sensitiveOutput": _field(str, "absent", "present"),
        },
        lambda p, v: _safe_pass(p, v, lambda x, _: x["amaneAdminAllowHttp"], lambda x, _: x["configRejected"], lambda x, _: x["adminRouteResult"] == "unavailable", lambda x, _: x["outcome"] == "rejected", lambda x, _: x["sensitiveOutput"] == "absent"),
        lambda p, v: _safe_fail(p, v, lambda x, _: not x["configRejected"], lambda x, _: x["adminRouteResult"] == "available", lambda x, _: x["outcome"] == "accepted", lambda x, _: x["sensitiveOutput"] == "present"),
    ),
    "G456-11": _hard_validator(
        "legacy-g456-11",
        {
            "accessProfile": _field(str, "local-dev", "proxy-https"), "addressMismatch": _field(bool),
            "httpStatus": _field(int, 404, 200), "adminRouteResult": _field(str, "available", "unavailable"),
            "routeExposed": _field(bool), "sensitiveOutput": _field(str, "absent", "present"),
        },
        lambda p, v: _safe_pass(p, v, lambda x, lane: x["accessProfile"] == lane, lambda x, _: x["addressMismatch"], lambda x, _: x["httpStatus"] == 404, lambda x, _: x["adminRouteResult"] == "unavailable", lambda x, _: not x["routeExposed"], lambda x, _: x["sensitiveOutput"] == "absent"),
        lambda p, v: _safe_fail(p, v, lambda x, lane: x["accessProfile"] != lane, lambda x, _: not x["addressMismatch"], lambda x, _: x["httpStatus"] != 404, lambda x, _: x["adminRouteResult"] == "available", lambda x, _: x["routeExposed"], lambda x, _: x["sensitiveOutput"] == "present"),
    ),
    "G456-12": _hard_validator(
        "legacy-g456-12",
        {
            "accessProfile": _field(str, "production-https"), "httpsPathAvailable": _field(bool),
            "adminBootstrapResult": _field(str, "not-presented", "presented"), "adminEnabled": _field(bool),
            "adminRouteResult": _field(str, "available", "unavailable"), "mainPathResult": _field(str, "available", "unavailable"),
            "sensitiveOutput": _field(str, "absent", "present"),
        },
        lambda p, v: _safe_pass(p, v, lambda x, _: not x["httpsPathAvailable"], lambda x, _: x["adminBootstrapResult"] == "not-presented", lambda x, _: not x["adminEnabled"], lambda x, _: x["adminRouteResult"] == "unavailable", lambda x, _: x["mainPathResult"] == "available", lambda x, _: x["sensitiveOutput"] == "absent"),
        lambda p, v: _safe_fail(p, v, lambda x, _: x["httpsPathAvailable"], lambda x, _: x["adminBootstrapResult"] == "presented", lambda x, _: x["adminEnabled"], lambda x, _: x["adminRouteResult"] == "available", lambda x, _: x["mainPathResult"] == "unavailable", lambda x, _: x["sensitiveOutput"] == "present"),
    ),
    "G456-13": _hard_validator(
        "legacy-g456-13",
        {
            "bootstrapProfile": _field(str, "fresh-bootstrap"), "freshInstall": _field(bool),
            "bootstrapResult": _field(str, "completed", "failed"), "loginResult": _field(str, "success", "rejected"),
            "setupStatusResult": _field(str, "visible", "hidden"), "bundleIdentityMatch": _field(bool),
            "sendReadyStatusShown": _field(bool), "deploymentOvConfirmedShown": _field(bool), "sensitiveOutput": _field(str, "absent", "present"),
        },
        lambda p, v: _safe_pass(p, v, lambda x, _: x["freshInstall"], lambda x, _: x["bootstrapResult"] == "completed", lambda x, _: x["loginResult"] == "success", lambda x, _: x["setupStatusResult"] == "visible", lambda x, _: x["bundleIdentityMatch"], lambda x, _: x["sendReadyStatusShown"], lambda x, _: not x["deploymentOvConfirmedShown"], lambda x, _: x["sensitiveOutput"] == "absent"),
        lambda p, v: _safe_fail(p, v, lambda x, _: not x["freshInstall"], lambda x, _: x["bootstrapResult"] == "failed", lambda x, _: x["loginResult"] == "rejected", lambda x, _: x["setupStatusResult"] == "hidden", lambda x, _: not x["bundleIdentityMatch"], lambda x, _: not x["sendReadyStatusShown"], lambda x, _: x["deploymentOvConfirmedShown"], lambda x, _: x["sensitiveOutput"] == "present"),
    ),
    "G456-14": _hard_validator(
        "legacy-g456-14",
        {
            "accessProfile": _field(str, "managed"), "usernameRelation": _field(str, "same-user"),
            "reapplyResult": _field(str, "idempotent", "rejected"), "credentialRotated": _field(bool),
            "statePreserved": _field(bool), "routeResult": _field(str, "available", "unavailable"), "sensitiveOutput": _field(str, "absent", "present"),
        },
        lambda p, v: _safe_pass(p, v, lambda x, _: x["reapplyResult"] == "idempotent", lambda x, _: not x["credentialRotated"], lambda x, _: x["statePreserved"], lambda x, _: x["routeResult"] == "available", lambda x, _: x["sensitiveOutput"] == "absent"),
        lambda p, v: _safe_fail(p, v, lambda x, _: x["reapplyResult"] == "rejected", lambda x, _: x["credentialRotated"], lambda x, _: not x["statePreserved"], lambda x, _: x["routeResult"] == "unavailable", lambda x, _: x["sensitiveOutput"] == "present"),
    ),
    "G456-15": _hard_validator(
        "legacy-g456-15",
        {
            "accessProfile": _field(str, "managed"), "usernameRelation": _field(str, "different-user"),
            "credentialRotationAttempt": _field(str, "rejected", "accepted"), "manualExistingAdmin": _field(str, "rejected", "accepted"),
            "reapplyResult": _field(str, "rejected", "idempotent"), "credentialChanged": _field(bool), "sensitiveOutput": _field(str, "absent", "present"),
        },
        lambda p, v: _safe_pass(p, v, lambda x, _: x["credentialRotationAttempt"] == "rejected", lambda x, _: x["manualExistingAdmin"] == "rejected", lambda x, _: x["reapplyResult"] == "rejected", lambda x, _: not x["credentialChanged"], lambda x, _: x["sensitiveOutput"] == "absent"),
        lambda p, v: _safe_fail(p, v, lambda x, _: x["credentialRotationAttempt"] == "accepted", lambda x, _: x["manualExistingAdmin"] == "accepted", lambda x, _: x["reapplyResult"] == "idempotent", lambda x, _: x["credentialChanged"], lambda x, _: x["sensitiveOutput"] == "present"),
    ),
    "G456-16": _hard_validator(
        "legacy-g456-16",
        {
            "executionProfile": _field(str, "automated-fixture", "integrated-follow-on-failure"), "credentialSyncResult": _field(str, "completed", "failed"),
            "subsequentStepResult": _field(str, "failed", "completed"), "configRollbackResult": _field(str, "completed", "failed", "not-applicable"),
            "sqliteStateReport": _field(str, "separate"), "adminRouteAfterRollback": _field(str, "not-exposed", "exposed"),
            "partialSuccessRecorded": _field(bool), "sensitiveOutput": _field(str, "absent", "present"),
        },
        lambda p, v: _safe_pass(p, v, lambda x, lane: x["executionProfile"] == ("automated-fixture" if lane == "ci-auto" else "integrated-follow-on-failure"), lambda x, _: x["credentialSyncResult"] == "completed", lambda x, _: x["subsequentStepResult"] == "failed", lambda x, _: x["configRollbackResult"] == "completed", lambda x, _: x["sqliteStateReport"] == "separate", lambda x, _: x["adminRouteAfterRollback"] == "not-exposed", lambda x, _: x["partialSuccessRecorded"], lambda x, _: x["sensitiveOutput"] == "absent"),
        lambda p, v: _safe_fail(p, v, lambda x, lane: x["executionProfile"] != ("automated-fixture" if lane == "ci-auto" else "integrated-follow-on-failure"), lambda x, _: x["credentialSyncResult"] == "failed", lambda x, _: x["subsequentStepResult"] == "completed", lambda x, _: x["configRollbackResult"] != "completed", lambda x, _: x["adminRouteAfterRollback"] == "exposed", lambda x, _: not x["partialSuccessRecorded"], lambda x, _: x["sensitiveOutput"] == "present"),
    ),
    "G456-17": _hard_validator(
        "legacy-g456-17",
        {
            "executionMode": _field(str, "non-interactive"), "enableRequestResult": _field(str, "rejected", "accepted"),
            "adminEnabled": _field(bool), "sensitiveArgument": _field(bool), "sensitiveHistory": _field(bool),
            "sensitiveProcessList": _field(bool), "sensitiveOutput": _field(str, "absent", "present"),
        },
        lambda p, v: _safe_pass(p, v, lambda x, _: x["enableRequestResult"] == "rejected", lambda x, _: not x["adminEnabled"], lambda x, _: not x["sensitiveArgument"], lambda x, _: not x["sensitiveHistory"], lambda x, _: not x["sensitiveProcessList"], lambda x, _: x["sensitiveOutput"] == "absent"),
        lambda p, v: _safe_fail(p, v, lambda x, _: x["enableRequestResult"] == "accepted", lambda x, _: x["adminEnabled"], lambda x, _: x["sensitiveArgument"], lambda x, _: x["sensitiveHistory"], lambda x, _: x["sensitiveProcessList"], lambda x, _: x["sensitiveOutput"] == "present"),
    ),
    "G456-18": _hard_validator(
        "legacy-g456-18",
        {
            "failureMode": _field(str, "apply-failure"), "previousBundlePresent": _field(bool), "applyResult": _field(str, "failed", "completed"),
            "rollbackResult": _field(str, "completed", "failed", "not-attempted"), "effectiveStateRestored": _field(bool), "integrityMatched": _field(bool),
            "adminRouteAfterRollback": _field(str, "not-exposed", "exposed"), "rollbackClaimedSuccess": _field(bool),
        },
        lambda p, v: _safe_pass(p, v, lambda x, _: x["previousBundlePresent"], lambda x, _: x["applyResult"] == "failed", lambda x, _: x["rollbackResult"] == "completed", lambda x, _: x["effectiveStateRestored"], lambda x, _: x["integrityMatched"], lambda x, _: x["adminRouteAfterRollback"] == "not-exposed", lambda x, _: x["rollbackClaimedSuccess"]),
        lambda p, v: _safe_fail(p, v, lambda x, _: not x["previousBundlePresent"], lambda x, _: x["applyResult"] == "completed", lambda x, _: x["rollbackResult"] != "completed", lambda x, _: not x["effectiveStateRestored"], lambda x, _: not x["integrityMatched"], lambda x, _: x["adminRouteAfterRollback"] == "exposed", lambda x, _: not x["rollbackClaimedSuccess"]),
    ),
    "G456-19": _hard_validator(
        "legacy-g456-19",
        {
            "failureMode": _field(str, "fresh-install-failure"), "previousBundlePresent": _field(bool), "applyResult": _field(str, "failed", "completed"),
            "rollbackResult": _field(str, "not-applicable", "completed"), "rollbackClaimedSuccess": _field(bool),
            "manualInterventionRequired": _field(bool), "adminRouteResult": _field(str, "unavailable", "available"), "partialBundleActive": _field(bool),
        },
        lambda p, v: _safe_pass(p, v, lambda x, _: not x["previousBundlePresent"], lambda x, _: x["applyResult"] == "failed", lambda x, _: x["rollbackResult"] == "not-applicable", lambda x, _: not x["rollbackClaimedSuccess"], lambda x, _: x["manualInterventionRequired"], lambda x, _: x["adminRouteResult"] == "unavailable", lambda x, _: not x["partialBundleActive"]),
        lambda p, v: _safe_fail(p, v, lambda x, _: x["previousBundlePresent"], lambda x, _: x["applyResult"] == "completed", lambda x, _: x["rollbackResult"] == "completed", lambda x, _: x["rollbackClaimedSuccess"], lambda x, _: not x["manualInterventionRequired"], lambda x, _: x["adminRouteResult"] == "available", lambda x, _: x["partialBundleActive"]),
    ),
    "G456-20": _hard_validator(
        "legacy-g456-20",
        {
            "fault": _field(str, "fingerprint-mismatch"), "fingerprintMismatchDetected": _field(bool), "verificationResult": _field(str, "rejected", "accepted"),
            "activationResult": _field(str, "blocked", "activated"), "staleState": _field(str, "not-activated", "activated"), "bundleIntegrityMatched": _field(bool), "sensitiveOutput": _field(str, "absent", "present"),
        },
        lambda p, v: _safe_pass(p, v, lambda x, _: x["fingerprintMismatchDetected"], lambda x, _: x["verificationResult"] == "rejected", lambda x, _: x["activationResult"] == "blocked", lambda x, _: x["bundleIntegrityMatched"], lambda x, _: x["sensitiveOutput"] == "absent"),
        lambda p, v: _safe_fail(p, v, lambda x, _: not x["fingerprintMismatchDetected"], lambda x, _: x["verificationResult"] == "accepted", lambda x, _: x["activationResult"] == "activated", lambda x, _: not x["bundleIntegrityMatched"], lambda x, _: x["sensitiveOutput"] == "present"),
    ),
    "G456-21": _hard_validator(
        "legacy-g456-21",
        {
            "fault": _field(str, "credential-replacement"), "credentialBindingResult": _field(str, "rejected", "accepted"),
            "oldCredentialAccepted": _field(bool), "otherBundleCredentialAccepted": _field(bool), "badMountCredentialAccepted": _field(bool),
            "activationResult": _field(str, "blocked", "activated"), "sensitiveOutput": _field(str, "absent", "present"),
        },
        lambda p, v: _safe_pass(p, v, lambda x, _: x["credentialBindingResult"] == "rejected", lambda x, _: not x["oldCredentialAccepted"], lambda x, _: not x["otherBundleCredentialAccepted"], lambda x, _: not x["badMountCredentialAccepted"], lambda x, _: x["activationResult"] == "blocked", lambda x, _: x["sensitiveOutput"] == "absent"),
        lambda p, v: _safe_fail(p, v, lambda x, _: x["credentialBindingResult"] == "accepted", lambda x, _: x["oldCredentialAccepted"], lambda x, _: x["otherBundleCredentialAccepted"], lambda x, _: x["badMountCredentialAccepted"], lambda x, _: x["activationResult"] == "activated", lambda x, _: x["sensitiveOutput"] == "present"),
    ),
    "G456-22": _hard_validator(
        "legacy-g456-22",
        {
            "fault": _field(str, "stale-launcher-image"), "launcherIdentityMatch": _field(bool), "imageIdentityMatch": _field(bool),
            "verificationResult": _field(str, "rejected", "accepted"), "activationResult": _field(str, "blocked", "activated"), "sensitiveOutput": _field(str, "absent", "present"),
        },
        lambda p, v: _safe_pass(p, v, lambda x, _: not x["launcherIdentityMatch"], lambda x, _: not x["imageIdentityMatch"], lambda x, _: x["verificationResult"] == "rejected", lambda x, _: x["activationResult"] == "blocked", lambda x, _: x["sensitiveOutput"] == "absent"),
        lambda p, v: _safe_fail(p, v, lambda x, _: x["launcherIdentityMatch"], lambda x, _: x["imageIdentityMatch"], lambda x, _: x["verificationResult"] == "accepted", lambda x, _: x["activationResult"] == "activated", lambda x, _: x["sensitiveOutput"] == "present"),
    ),
    "G456-23": _hard_validator(
        "legacy-g456-23",
        {
            "fault": _field(str, "remote-docker-context"), "dockerContext": _field(str, "remote"), "remoteOperationAttempted": _field(bool),
            "remoteMutation": _field(bool), "operationResult": _field(str, "rejected", "completed"), "localOnlyEnforced": _field(bool), "sensitiveOutput": _field(str, "absent", "present"),
        },
        lambda p, v: _safe_pass(p, v, lambda x, _: not x["remoteOperationAttempted"], lambda x, _: not x["remoteMutation"], lambda x, _: x["operationResult"] == "rejected", lambda x, _: x["localOnlyEnforced"], lambda x, _: x["sensitiveOutput"] == "absent"),
        lambda p, v: _safe_fail(p, v, lambda x, _: x["remoteOperationAttempted"], lambda x, _: x["remoteMutation"], lambda x, _: x["operationResult"] == "completed", lambda x, _: not x["localOnlyEnforced"], lambda x, _: x["sensitiveOutput"] == "present"),
    ),
    "G456-24": _hard_validator(
        "legacy-g456-24",
        {
            "fault": _field(str, "command-injection"), "injectionAttempted": _field(bool), "inputRejected": _field(bool),
            "commandExecution": _field(str, "not-executed", "executed"), "shellSpawned": _field(bool), "environmentMutation": _field(bool), "sensitiveOutput": _field(str, "absent", "present"),
        },
        lambda p, v: _safe_pass(p, v, lambda x, _: x["injectionAttempted"], lambda x, _: x["inputRejected"], lambda x, _: x["commandExecution"] == "not-executed", lambda x, _: not x["shellSpawned"], lambda x, _: not x["environmentMutation"], lambda x, _: x["sensitiveOutput"] == "absent"),
        lambda p, v: _safe_fail(p, v, lambda x, _: not x["injectionAttempted"], lambda x, _: not x["inputRejected"], lambda x, _: x["commandExecution"] == "executed", lambda x, _: x["shellSpawned"], lambda x, _: x["environmentMutation"], lambda x, _: x["sensitiveOutput"] == "present"),
    ),
    "G456-25": _hard_validator(
        "legacy-g456-25",
        {
            "fault": _field(str, "path-traversal"), "traversalAttempted": _field(bool), "inputRejected": _field(bool),
            "pathResolution": _field(str, "rejected", "resolved"), "fileReadOutsideRoot": _field(bool), "fileWriteOutsideRoot": _field(bool), "sensitiveOutput": _field(str, "absent", "present"),
        },
        lambda p, v: _safe_pass(p, v, lambda x, _: x["traversalAttempted"], lambda x, _: x["inputRejected"], lambda x, _: x["pathResolution"] == "rejected", lambda x, _: not x["fileReadOutsideRoot"], lambda x, _: not x["fileWriteOutsideRoot"], lambda x, _: x["sensitiveOutput"] == "absent"),
        lambda p, v: _safe_fail(p, v, lambda x, _: not x["traversalAttempted"], lambda x, _: not x["inputRejected"], lambda x, _: x["pathResolution"] == "resolved", lambda x, _: x["fileReadOutsideRoot"], lambda x, _: x["fileWriteOutsideRoot"], lambda x, _: x["sensitiveOutput"] == "present"),
    ),
    "G456-26": _hard_validator(
        "legacy-g456-26",
        {
            "fault": _field(str, "symlink-reparse"), "filesystemObject": _field(str, "symlink", "reparse-point"), "objectDetected": _field(bool),
            "followed": _field(bool), "operationResult": _field(str, "rejected", "completed"), "outsideRootAccess": _field(bool), "sensitiveOutput": _field(str, "absent", "present"),
        },
        lambda p, v: _safe_pass(p, v, lambda x, lane: x["filesystemObject"] == ("reparse-point" if lane == "win-docker" else "symlink"), lambda x, _: x["objectDetected"], lambda x, _: not x["followed"], lambda x, _: x["operationResult"] == "rejected", lambda x, _: not x["outsideRootAccess"], lambda x, _: x["sensitiveOutput"] == "absent"),
        lambda p, v: _safe_fail(p, v, lambda x, lane: x["filesystemObject"] != ("reparse-point" if lane == "win-docker" else "symlink"), lambda x, _: not x["objectDetected"], lambda x, _: x["followed"], lambda x, _: x["operationResult"] == "completed", lambda x, _: x["outsideRootAccess"], lambda x, _: x["sensitiveOutput"] == "present"),
    ),
    "G456-27": _hard_validator(
        "legacy-g456-27",
        {
            "fault": _field(str, "concurrent-setup"), "concurrentRequests": _field(int), "winnerCount": _field(int, 1, 2),
            "loserResult": _field(str, "rejected", "serialized"), "duplicateApply": _field(bool), "stateConsistent": _field(bool), "activeGenerationUnique": _field(bool), "sensitiveOutput": _field(str, "absent", "present"),
        },
        lambda p, v: _safe_pass(p, v, lambda x, _: x["concurrentRequests"] >= 2, lambda x, _: x["winnerCount"] == 1, lambda x, _: x["loserResult"] in {"rejected", "serialized"}, lambda x, _: not x["duplicateApply"], lambda x, _: x["stateConsistent"], lambda x, _: x["activeGenerationUnique"], lambda x, _: x["sensitiveOutput"] == "absent"),
        lambda p, v: _safe_fail(p, v, lambda x, _: x["concurrentRequests"] < 2, lambda x, _: x["winnerCount"] != 1, lambda x, _: x["loserResult"] not in {"rejected", "serialized"}, lambda x, _: x["duplicateApply"], lambda x, _: not x["stateConsistent"], lambda x, _: not x["activeGenerationUnique"], lambda x, _: x["sensitiveOutput"] == "present"),
    ),
    "G456-28": _hard_validator(
        "legacy-g456-28",
        {
            "fault": _field(str, "crash-cancel-recovery"), "recoveryTrigger": _field(str, "crash", "cancel"),
            "recoveryResult": _field(str, "resumed", "manual-intervention", "unsafe"), "partialActivation": _field(bool), "stateConsistent": _field(bool),
            "recoveryRecordValueFree": _field(bool), "adminRouteResult": _field(str, "unavailable", "available"), "sensitiveOutput": _field(str, "absent", "present"),
        },
        lambda p, v: _safe_pass(p, v, lambda x, _: x["recoveryResult"] in {"resumed", "manual-intervention"}, lambda x, _: not x["partialActivation"], lambda x, _: x["stateConsistent"], lambda x, _: x["recoveryRecordValueFree"], lambda x, _: x["adminRouteResult"] == "unavailable", lambda x, _: x["sensitiveOutput"] == "absent"),
        lambda p, v: _safe_fail(p, v, lambda x, _: x["recoveryResult"] == "unsafe", lambda x, _: x["partialActivation"], lambda x, _: not x["stateConsistent"], lambda x, _: not x["recoveryRecordValueFree"], lambda x, _: x["adminRouteResult"] == "available", lambda x, _: x["sensitiveOutput"] == "present"),
    ),
    "G456-30": _hard_validator(
        "legacy-g456-30",
        {
            "fault": _field(str, "web-security"), "requestCredentialPolicy": _field(str, "enforced", "bypassed"), "originPolicy": _field(str, "enforced", "bypassed"),
            "hostPolicy": _field(str, "enforced", "bypassed"), "csrfPolicy": _field(str, "enforced", "bypassed"), "unauthorizedResult": _field(str, "rejected", "accepted"),
            "crossOriginAdminAccess": _field(bool), "sensitiveOutput": _field(str, "absent", "present"),
        },
        lambda p, v: _safe_pass(p, v, lambda x, _: x["requestCredentialPolicy"] == "enforced", lambda x, _: x["originPolicy"] == "enforced", lambda x, _: x["hostPolicy"] == "enforced", lambda x, _: x["csrfPolicy"] == "enforced", lambda x, _: x["unauthorizedResult"] == "rejected", lambda x, _: not x["crossOriginAdminAccess"], lambda x, _: x["sensitiveOutput"] == "absent"),
        lambda p, v: _safe_fail(p, v, lambda x, _: x["requestCredentialPolicy"] == "bypassed", lambda x, _: x["originPolicy"] == "bypassed", lambda x, _: x["hostPolicy"] == "bypassed", lambda x, _: x["csrfPolicy"] == "bypassed", lambda x, _: x["unauthorizedResult"] == "accepted", lambda x, _: x["crossOriginAdminAccess"], lambda x, _: x["sensitiveOutput"] == "present"),
    ),
    "G456-31": _hard_validator(
        "legacy-g456-31",
        {
            "scanTarget": _field(str, "qualification-output"), "sensitiveScan": _field(str, "clean", "findings"), "deliveryAddressValue": _field(str, "absent", "present"),
            "providerErrorOutput": _field(str, "absent", "present"), "hostPathOutput": _field(str, "absent", "present"), "credentialValue": _field(str, "absent", "present"), "outputResult": _field(str, "value-free", "value-bearing"),
        },
        lambda p, v: _safe_pass(p, v, lambda x, _: x["sensitiveScan"] == "clean", lambda x, _: x["deliveryAddressValue"] == "absent", lambda x, _: x["providerErrorOutput"] == "absent", lambda x, _: x["hostPathOutput"] == "absent", lambda x, _: x["credentialValue"] == "absent", lambda x, _: x["outputResult"] == "value-free"),
        lambda p, v: _safe_fail(p, v, lambda x, _: x["sensitiveScan"] == "findings", lambda x, _: x["deliveryAddressValue"] == "present", lambda x, _: x["providerErrorOutput"] == "present", lambda x, _: x["hostPathOutput"] == "present", lambda x, _: x["credentialValue"] == "present", lambda x, _: x["outputResult"] == "value-bearing"),
    ),
    "G456-32": _hard_validator(
        "legacy-g456-32",
        {
            "accessProfile": _field(str, "admin-status"), "authenticationRequired": _field(bool), "authorizationRequired": _field(bool),
            "unauthenticatedResult": _field(str, "rejected", "accepted"), "wrongAddressStatus": _field(int, 404, 200), "authorizedStatus": _field(str, "value-free", "value-bearing"),
            "statusRouteExposed": _field(bool), "sensitiveOutput": _field(str, "absent", "present"),
        },
        lambda p, v: _safe_pass(p, v, lambda x, _: x["authenticationRequired"], lambda x, _: x["authorizationRequired"], lambda x, _: x["unauthenticatedResult"] == "rejected", lambda x, _: x["wrongAddressStatus"] == 404, lambda x, _: x["authorizedStatus"] == "value-free", lambda x, _: x["statusRouteExposed"], lambda x, _: x["sensitiveOutput"] == "absent"),
        lambda p, v: _safe_fail(p, v, lambda x, _: not x["authenticationRequired"], lambda x, _: not x["authorizationRequired"], lambda x, _: x["unauthenticatedResult"] == "accepted", lambda x, _: x["wrongAddressStatus"] == 200, lambda x, _: x["authorizedStatus"] == "value-bearing", lambda x, _: not x["statusRouteExposed"], lambda x, _: x["sensitiveOutput"] == "present"),
    ),
    "G456-33": _hard_validator(
        "legacy-g456-33",
        {
            "executionMode": _field(str, "terminal-non-interactive"), "sensitiveArgument": _field(bool), "sensitiveHistory": _field(bool),
            "sensitiveProcessList": _field(bool), "inputBoundaryResult": _field(str, "rejected", "accepted"), "interactivePromptShown": _field(bool),
            "outputResult": _field(str, "value-free", "value-bearing"), "sensitiveOutput": _field(str, "absent", "present"),
        },
        lambda p, v: _safe_pass(p, v, lambda x, _: not x["sensitiveArgument"], lambda x, _: not x["sensitiveHistory"], lambda x, _: not x["sensitiveProcessList"], lambda x, _: x["inputBoundaryResult"] == "rejected", lambda x, _: not x["interactivePromptShown"], lambda x, _: x["outputResult"] == "value-free", lambda x, _: x["sensitiveOutput"] == "absent"),
        lambda p, v: _safe_fail(p, v, lambda x, _: x["sensitiveArgument"], lambda x, _: x["sensitiveHistory"], lambda x, _: x["sensitiveProcessList"], lambda x, _: x["inputBoundaryResult"] == "accepted", lambda x, _: x["interactivePromptShown"], lambda x, _: x["outputResult"] == "value-bearing", lambda x, _: x["sensitiveOutput"] == "present"),
    ),
    "G456-35": _hard_validator(
        "legacy-g456-35",
        {
            "targetRid": _field(str, "linux-arm64"), "artifactSourceCommitMatch": _field(bool), "artifactIntegrityMatch": _field(bool),
            "startupSmoke": _field(str, "passed", "failed"), "helpCommand": _field(str, "passed", "failed"), "aotBinary": _field(bool),
            "runtimeIdentityMatch": _field(bool), "outputResult": _field(str, "value-free", "value-bearing"), "sensitiveOutput": _field(str, "absent", "present"),
        },
        lambda p, v: _safe_pass(p, v, lambda x, _: x["artifactSourceCommitMatch"], lambda x, _: x["artifactIntegrityMatch"], lambda x, _: x["startupSmoke"] == "passed", lambda x, _: x["helpCommand"] == "passed", lambda x, _: x["aotBinary"], lambda x, _: x["runtimeIdentityMatch"], lambda x, _: x["outputResult"] == "value-free", lambda x, _: x["sensitiveOutput"] == "absent"),
        lambda p, v: _safe_fail(p, v, lambda x, _: not x["artifactSourceCommitMatch"], lambda x, _: not x["artifactIntegrityMatch"], lambda x, _: x["startupSmoke"] == "failed", lambda x, _: x["helpCommand"] == "failed", lambda x, _: not x["aotBinary"], lambda x, _: not x["runtimeIdentityMatch"], lambda x, _: x["outputResult"] == "value-bearing", lambda x, _: x["sensitiveOutput"] == "present"),
    ),
}


V13_IMPLEMENTED_SCENARIO_VALIDATORS = IMPLEMENTED_SCENARIO_VALIDATORS | set(HARD_SCENARIO_VALIDATOR_REGISTRY)


def validate_registered_hard_payload(envelope: dict[str, Any], row: dict[str, Any], spec: dict[str, Any]) -> None:
    scenario = row["scenarioId"]
    bound_predicate_set = row.get("predicateSet", SCOPE_PREDICATE_SETS.get(scenario))
    if bound_predicate_set != spec["predicateSet"]:
        fail(f"{scenario}: predicateSet is not registered for this lane")
    if envelope.get("evidenceType") not in spec["evidenceTypes"]:
        fail(f"{scenario}: evidenceType is not registered for this lane")
    if envelope.get("procedureId") != spec["procedureId"] or envelope.get("procedureRevision") != spec["procedureRevision"]:
        fail(f"{scenario}: procedure identity/revision mismatch")
    payload = envelope["typePayload"]
    expected_fields = set(spec["fields"])
    missing = sorted(expected_fields - set(payload))
    unknown = sorted(set(payload) - expected_fields)
    if missing:
        fail(f"{scenario}: typePayload missing fields: {','.join(missing)}")
    if unknown:
        fail(f"{scenario}: unknown typePayload field: {','.join(unknown)}")
    for field_name, (field_type, allowed) in spec["fields"].items():
        value = payload[field_name]
        if type(value) is not field_type:
            fail(f"{scenario}: typePayload field {field_name} has wrong type")
        if allowed is not None and value not in allowed:
            fail(f"{scenario}: typePayload field {field_name} has an unexpected value")
    if envelope["result"] not in {"PASS", "FAIL"}:
        fail(f"{scenario}: dedicated Hard evidence result must be PASS or FAIL")
    variant = envelope["variantId"]
    if envelope["result"] == "PASS" and not spec["pass"](payload, variant):
        fail(f"{scenario}: PASS predicate mismatch")
    if envelope["result"] == "FAIL":
        if spec["pass"](payload, variant):
            fail(f"{scenario}: FAIL payload is a successful predicate")
        if not spec["fail"](payload, variant):
            fail(f"{scenario}: FAIL payload lacks an explicit failed predicate")


def validate_type_payload(envelope: dict[str, Any], binding: dict[str, Any], row: dict[str, Any]) -> None:
    scenario = row["scenarioId"]
    if binding.get("scopeId") is not None and (row.get("predicateSet") != SCOPE_PREDICATE_SETS.get(scenario) or row.get("ownerRoleClass") != SCOPE_OWNER_CLASSES.get(scenario)):
        fail(f"{scenario}: scope predicate/owner mapping is not registered")
    evidence_type = envelope.get("evidenceType")
    if evidence_type not in EVIDENCE_TYPES.get(scenario, ()):
        fail(f"{scenario}: evidenceType is not allowed")
    payload = envelope["typePayload"]
    if scenario in {"G456-42", "G456-43", "G456-44", "G583-MIG-01", "G583-MIG-02", "G583-MIG-03"}:
        validate_migration_payload(envelope, binding, scenario, payload)
        return
    if binding.get("scopeId") == V13_SCOPE_ID and scenario in HARD_SCENARIO_VALIDATOR_REGISTRY:
        validate_registered_hard_payload(envelope, row, HARD_SCENARIO_VALIDATOR_REGISTRY[scenario])
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
            if scenario in {"G456-42", "G456-43", "G456-44", "G583-MIG-01", "G583-MIG-02", "G583-MIG-03"} and exception_id is not None:
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
    scan = evidence["prohibitedContentScan"]
    write_once(run_root / "scans" / f"{evidence_id}.json", {
        "schemaVersion": 1,
        "kind": "prohibited-content-scan-attestation",
        "evidenceId": evidence_id,
        "qualificationRunId": binding["qualificationRunId"],
        "bindingId": binding["bindingId"],
        "candidateId": binding["candidateId"],
        "result": scan["result"],
        "scannerId": scan["scannerId"],
        "scannerVersion": scan["scannerVersion"],
        "reportDigestSha256": scan["reportDigestSha256"],
    })
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
    verify_plan_source(Path(args.repo_root), Path(args.repo_root) / binding["planFilePath"], binding["planCommitSha"], binding["planFileSha256"])
    scope_profile = load_scope_manifest(run_root / "scope-manifest.json") if binding.get("scopeId") else None
    migration_pin = load_migration_pin(run_root / "migration-pin.json", scope_profile)
    verify_migration_pin_tree(Path(args.repo_root).resolve(), binding["releaseCommitSha"], migration_pin, scope_profile)
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
    if binding.get("scopeId"):
        evidence_index.update({"scopeId": binding["scopeId"], "scopeVersion": binding["scopeVersion"], "scopeManifestSha256": binding["scopeManifestSha256"]})
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
    if binding.get("scopeId"):
        go_no_go.update({"scopeId": binding["scopeId"], "scopeVersion": binding["scopeVersion"], "scopeManifestSha256": binding["scopeManifestSha256"]})
    write_once(run_root / "decision" / "go-no-go.json", go_no_go)
    write_once(run_root / "indexes" / "evidence-index-v1.json", evidence_index)
    phase3_index_sha = file_sha(run_root / "indexes/evidence-index-v1.json")
    write_once(run_root / "phase-manifests" / "phase-3-v1.json", {"schemaVersion": 1, "phase": 3, "qualificationRunId": binding["qualificationRunId"], "latestEvidenceIndexSha256": phase3_index_sha, "createdAtUtc": utc_now()})
    inventory = object_inventory(run_root)
    scan_entries = [entry for entry in inventory if entry["path"].startswith("scans/")]
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
            "scanObjectCount": len(scan_entries),
            "scanRootSha256": object_root(scan_entries),
            "phase3LatestIndexSha256": phase3_index_sha,
            "finalEvidenceIndexSha256": evidence_index_file_sha,
            "goNoGoSha256": file_sha(run_root / "decision/go-no-go.json"),
        },
        "decisionObjectPaths": {"evidenceIndex": "decision/evidence-index.json", "goNoGo": "decision/go-no-go.json"},
        "rootDigestAlgorithm": ROOT_DIGEST_ALGORITHM,
    }
    if binding.get("scopeId"):
        phase4.update({"scopeId": binding["scopeId"], "scopeVersion": binding["scopeVersion"], "scopeManifestSha256": binding["scopeManifestSha256"], "scopeAuthorityIssueNumber": binding["scopeAuthorityIssueNumber"], "scopeAuthorityIssueBodySha256": binding["scopeAuthorityIssueBodySha256"], "migrationFullInventoryDigestSha256": binding["migrationFullInventoryDigestSha256"], "migrationDeltaInventoryDigestSha256": binding["migrationDeltaInventoryDigestSha256"]})
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
    if binding.get("scopeId"):
        event.update({"scopeId": binding["scopeId"], "scopeVersion": binding["scopeVersion"], "scopeManifestSha256": binding["scopeManifestSha256"]})
    event["eventDigestSha256"] = sha_object(event)
    write_once(run_root / "run-status-events" / f"{event_id}.json", event)
    print(json.dumps({"qualificationRunId": binding["qualificationRunId"], "machineVerdict": machine_verdict, "humanDecision": human, "sealedEventId": event_id}, sort_keys=True))


def command_verify(args: argparse.Namespace) -> None:
    run_root = Path(args.run_root).resolve()
    binding = load_binding(run_root)
    auth = load_authorization(run_root)
    verify_plan_source(Path(args.repo_root), Path(args.repo_root) / binding["planFilePath"], binding["planCommitSha"], binding["planFileSha256"])
    scope_profile = load_scope_manifest(run_root / "scope-manifest.json") if binding.get("scopeId") else None
    migration_pin = load_migration_pin(run_root / "migration-pin.json", scope_profile)
    verify_migration_pin_tree(Path(args.repo_root).resolve(), binding["releaseCommitSha"], migration_pin, scope_profile)
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
    if binding.get("scopeId") and any(event.get(field) != binding.get(field) for field in ("scopeId", "scopeVersion", "scopeManifestSha256")):
        fail("sealed run-status event scope authority mismatch")
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
    if binding.get("scopeId") and any(phase4.get(field) != binding.get(field) for field in ("scopeId", "scopeVersion", "scopeManifestSha256", "scopeAuthorityIssueNumber", "scopeAuthorityIssueBodySha256", "migrationFullInventoryDigestSha256", "migrationDeltaInventoryDigestSha256")):
        fail("phase-4 scope authority mismatch")
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
    if decision.get("schemaVersion") != 1 or decision.get("authorizationDigestSha256") != binding.get("authorizationDigestSha256"):
        fail("go/no-go authorization schema/digest mismatch")
    if binding.get("scopeId") and any(decision.get(field) != binding.get(field) for field in ("scopeId", "scopeVersion", "scopeManifestSha256")):
        fail("go/no-go scope authority mismatch")
    if decision.get("humanDecision") not in {"APPROVE", "REJECT", "NOT_DECIDED"}:
        fail("go/no-go human decision is invalid")
    freshness = decision.get("issueFreshnessCheck")
    if not isinstance(freshness, dict) or freshness.get("matchedBinding") is not True or freshness.get("currentIssueBodySha256") != binding.get("issueBodySha256"):
        fail("go/no-go issue freshness binding mismatch")
    expected_entries = [{"scenarioId": scenario, "variantId": variant, "evidenceId": active.get((scenario, variant)), "exceptionId": active_exceptions.get((scenario, variant)), "result": evidence[active[(scenario, variant)]]["result"] if active.get((scenario, variant)) else ("EXCEPTION" if active_exceptions.get((scenario, variant)) else "NOT_RUN")} for scenario, variant in sorted(allowed_keys(binding))]
    evidence_index = read_json(run_root / "decision/evidence-index.json", "decision/evidence-index.json")
    if evidence_index.get("entries") != expected_entries or evidence_index.get("candidateId") != binding.get("candidateId") or evidence_index.get("bindingId") != binding.get("bindingId") or evidence_index.get("qualificationRunId") != binding.get("qualificationRunId"):
        fail("evidence index does not match replayed active state")
    if binding.get("scopeId") and any(evidence_index.get(field) != binding.get(field) for field in ("scopeId", "scopeVersion", "scopeManifestSha256")):
        fail("evidence index scope authority mismatch")
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
    intake.add_argument("--oci-layout", required=True)
    intake.add_argument("--expected-workflow-ref", required=True)
    intake.set_defaults(func=command_intake)
    scope = sub.add_parser("validate-scope")
    scope.add_argument("--scope-manifest", required=True)
    scope.add_argument("--issue-snapshot")
    scope.add_argument("--repo-root", required=True)
    scope.set_defaults(func=command_validate_scope)
    bind = sub.add_parser("bind")
    bind.add_argument("--store-root", required=True)
    bind.add_argument("--candidate-id", required=True)
    bind.add_argument("--issue-snapshot", required=True)
    bind.add_argument("--plan-file", required=True)
    bind.add_argument("--plan-commit-sha", required=True)
    bind.add_argument("--repo-root", required=True)
    bind.add_argument("--migration-pin", required=True)
    bind.add_argument("--scope-manifest")
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
