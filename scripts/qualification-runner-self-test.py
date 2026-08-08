#!/usr/bin/env python3
"""Synthetic, value-free tests for qualification-runner.py.

This test never contacts GitHub, Docker, ACS, a registry, or a durable store.
All candidate/evidence data is generated in a temporary directory.
"""

from __future__ import annotations

import hashlib
import io
import json
import os
import subprocess
import sys
import tarfile
import tempfile
import zipfile
from pathlib import Path


ROOT = Path(__file__).resolve().parent
RUNNER = ROOT / "qualification-runner.py"


def sha(data: bytes) -> str:
    return hashlib.sha256(data).hexdigest()


def write(path: Path, value: object) -> None:
    path.parent.mkdir(parents=True, exist_ok=True)
    if isinstance(value, bytes):
        path.write_bytes(value)
    else:
        path.write_text(json.dumps(value, ensure_ascii=False, sort_keys=True) + "\n", encoding="utf-8")


def bash_path(path: Path) -> str:
    value = str(path).replace("\\", "/")
    if len(value) >= 2 and value[1] == ":":
        return "/mnt/" + value[0].lower() + value[2:]
    return value


def run(*args: str, expect: int = 0) -> str:
    result = subprocess.run([sys.executable, str(RUNNER), *args], text=True, capture_output=True)
    if result.returncode != expect:
        raise AssertionError(f"expected {expect}, got {result.returncode}: {result.stderr}")
    return result.stdout.strip()


def main() -> int:
    with tempfile.TemporaryDirectory(prefix="qualification-runner-self-test-") as temp:
        root = Path(temp)
        candidate = root / "candidate"
        store = root / "store"
        handoff = root / "handoff"
        archive_specs = [("win-x64", "amane-mailer-v1.3.0-windows-x64.zip"), ("linux-x64", "amane-mailer-v1.3.0-linux-x64.tar.gz"), ("linux-arm64", "amane-mailer-v1.3.0-linux-arm64.tar.gz")]
        source = subprocess.check_output(["git", "-C", str(ROOT.parent), "rev-parse", "HEAD"], text=True).strip()
        oci_layout = root / "oci-layout"
        blob_root = oci_layout / "blobs" / "sha256"

        def put_blob(payload: bytes) -> tuple[str, int]:
            digest = "sha256:" + sha(payload)
            write(blob_root / digest.removeprefix("sha256:"), payload)
            return digest, len(payload)

        runtime_descriptors = []
        for platform_name in ("amd64", "arm64"):
            config_bytes = json.dumps({"architecture": platform_name, "os": "linux", "config": {"Labels": {"org.opencontainers.image.version": "1.3.0", "org.opencontainers.image.revision": source}}}, sort_keys=True, separators=(",", ":")).encode("utf-8")
            config_digest, config_size = put_blob(config_bytes)
            manifest_bytes = json.dumps({"schemaVersion": 2, "mediaType": "application/vnd.oci.image.manifest.v1+json", "config": {"mediaType": "application/vnd.oci.image.config.v1+json", "digest": config_digest, "size": config_size}, "layers": []}, sort_keys=True, separators=(",", ":")).encode("utf-8")
            manifest_digest, manifest_size = put_blob(manifest_bytes)
            runtime_descriptors.append({"mediaType": "application/vnd.oci.image.manifest.v1+json", "digest": manifest_digest, "size": manifest_size, "platform": {"os": "linux", "architecture": platform_name}})
        nested_bytes = json.dumps({"schemaVersion": 2, "manifests": runtime_descriptors}, sort_keys=True, separators=(",", ":")).encode("utf-8")
        oci, nested_size = put_blob(nested_bytes)
        write(oci_layout / "oci-layout", {"imageLayoutVersion": "1.0.0"})
        write(oci_layout / "index.json", {"schemaVersion": 2, "manifests": [{"mediaType": "application/vnd.oci.image.index.v1+json", "digest": oci, "size": nested_size}]})
        (oci_layout.parent / "oci-index.digest").write_text(oci + "\n", encoding="utf-8")
        tampered_blob = blob_root / runtime_descriptors[0]["digest"].removeprefix("sha256:")
        tampered_original = tampered_blob.read_bytes()
        archive_digests = {}
        for rid, archive_name in archive_specs:
            manifest = {
                "schemaVersion": 1, "packagingKind": "setup-release-candidate", "artifactId": f"synthetic-{rid}",
                "sourceCommitSha": source, "mailerVersion": "1.3.0", "setupLauncherVersion": "1.3.0",
                "hostRid": rid, "targetRid": rid, "platform": "linux" if rid != "win-x64" else "win", "architecture": "arm64" if rid == "linux-arm64" else "amd64",
                "imageDigest": oci, "ociIndexDigest": oci, "artifactFileName": archive_name,
                "mailpitImageReference": "axllent/mailpit@sha256:" + "c" * 64,
                "composeSha256": "sha256:" + "d" * 64, "composeImageDigestSha256": "sha256:" + "e" * 64,
                "composeRecordedMetadataSha256": "sha256:" + "f" * 64, "composeMailpitSha256": "sha256:" + "0" * 64,
            "payloadTreeSha256": "sha256:" + "b" * 64,
                "supportedRecordedSchemaMin": 1, "supportedRecordedSchemaMax": 1,
                "supportedInspectEffectiveSchemaMin": 1, "supportedInspectEffectiveSchemaMax": 1,
                "supportedReleaseManifestSchemaMin": 1, "supportedReleaseManifestSchemaMax": 1,
            }
            manifest_bytes = json.dumps(manifest, sort_keys=True, separators=(",", ":")).encode("utf-8")
            archive_buffer = io.BytesIO()
            if archive_name.endswith(".zip"):
                with zipfile.ZipFile(archive_buffer, "w", zipfile.ZIP_DEFLATED) as archive_file:
                    archive_file.writestr("release-bundle-manifest.json", manifest_bytes)
                    archive_file.writestr("README-SETUP.md", "synthetic setup guide\n")
            else:
                with tarfile.open(fileobj=archive_buffer, mode="w:gz") as archive_file:
                    info = tarfile.TarInfo("release-bundle-manifest.json")
                    info.size = len(manifest_bytes)
                    archive_file.addfile(info, io.BytesIO(manifest_bytes))
                    readme = b"synthetic setup guide\n"
                    readme_info = tarfile.TarInfo("README-SETUP.md")
                    readme_info.size = len(readme)
                    archive_file.addfile(readme_info, io.BytesIO(readme))
            archive_bytes = archive_buffer.getvalue()
            write(candidate / archive_name, archive_bytes)
            archive_digests[rid] = sha(archive_bytes)
        provenance = {
            "schemaVersion": 1,
            "sourceCommitSha": source,
            "releaseVersion": "1.3.0",
            "workflowRunId": "123456789",
            "workflowRunAttempt": "1",
            "workflowRef": "local",
            "ociIndexDigest": oci,
            "ociPlatforms": ["linux/amd64", "linux/arm64"],
            "archives": [{"artifactName": f"synthetic-{rid}", "targetRid": rid, "archiveFileName": archive_name, "archiveSha256": "sha256:" + archive_digests[rid], "mailerVersion": "1.3.0", "setupLauncherVersion": "1.3.0", "payloadTreeSha256": "sha256:" + "b" * 64, "smokeResult": "passed"} for rid, archive_name in archive_specs],
        }
        write(candidate / "candidate-provenance.json", provenance)
        write(candidate / "image-identity.json", {"sourceCommitSha": source, "imageDigest": oci, "mailerVersion": "1.3.0", "platforms": ["linux/amd64", "linux/arm64"]})
        (candidate / "CANDIDATE-SHA256SUMS").write_text("".join(f"{archive_digests[rid]}  {archive_name}\n" for rid, archive_name in archive_specs), encoding="utf-8")
        write(candidate / "CANDIDATE-HANDOFF.md", "synthetic value-free handoff; prohibited-content secret scan completed\n")
        invalid_provenance = dict(provenance)
        invalid_provenance["unexpected"] = "must be rejected"
        write(candidate / "candidate-provenance.json", invalid_provenance)
        run("intake", "--candidate-root", str(candidate), "--store-root", str(store), "--release-commit-sha", source, "--expected-oci-digest", oci, "--oci-layout", str(oci_layout), "--expected-workflow-ref", "local", expect=1)
        write(candidate / "candidate-provenance.json", provenance)
        tampered_blob.write_bytes(tampered_original + b"tampered")
        run("intake", "--candidate-root", str(candidate), "--store-root", str(root / "tampered-store"), "--release-commit-sha", source, "--expected-oci-digest", oci, "--oci-layout", str(oci_layout), "--expected-workflow-ref", "local", expect=1)
        tampered_blob.write_bytes(tampered_original)
        intake = json.loads(run("intake", "--candidate-root", str(candidate), "--store-root", str(store), "--release-commit-sha", source, "--expected-oci-digest", oci, "--oci-layout", str(oci_layout), "--expected-workflow-ref", "local"))
        candidate_id = intake["candidateId"]

        variants = {
            1: ["win-docker"], 2: ["linux-docker"], 3: ["acs-staging-nosend"], 4: ["acs-staging-real"], 5: ["acs-production"], 6: ["acs-production-release-ov"],
            7: ["admin-local-dev"], 8: ["admin-prod-https"], 9: ["admin-prod-https"], 10: ["admin-prod-https"], 11: ["local-dev", "proxy-https"], 12: ["admin-prod-https"],
            13: ["win-docker", "linux-docker"], 14: ["win-docker", "linux-docker"], 15: ["ci-auto"], 16: ["ci-auto", "admin-integrated"],
            17: ["win-docker", "linux-docker"], 18: ["win-docker", "linux-docker"], 19: ["win-docker", "linux-docker"], 20: ["ci-auto"], 21: ["ci-auto"], 22: ["ci-auto"], 23: ["ci-auto"], 24: ["ci-auto"], 25: ["ci-auto"],
            26: ["win-docker", "linux-docker"], 27: ["ci-auto"], 28: ["ci-auto"], 29: ["win-docker", "linux-docker"], 30: ["ci-auto"], 31: ["ci-auto"], 32: ["ci-auto"], 33: ["win-docker", "linux-docker"], 34: ["linux-arm64"], 35: ["linux-arm64"], 36: ["vps"], 37: ["win-docker", "linux-docker"],
            38: [], 39: [], 40: [], 41: [], 42: ["win-docker", "linux-docker"], 43: ["win-docker", "linux-docker"], 44: ["ci-auto"],
        }
        conditional = {29, 34, 36, 37}
        informational = {38, 39, 40, 41}
        issue = {
            "number": 456,
            "updatedAt": "2026-08-08T00:00:00Z",
            "body": "synthetic issue snapshot; no PII",
            "rows": [{"rowIndex": index, "scenarioId": f"G456-{number:02d}", "scenarioText": f"synthetic scenario {number}", "environmentText": "synthetic environment", "gateClass": "Informational" if number in informational else ("Conditional" if number in conditional else "Hard"), "requiredVariants": variants[number]} for index, number in enumerate(range(1, 45))],
        }
        issue_path = root / "issue.json"
        write(issue_path, issue)
        plan = ROOT.parent / "docs" / "agent-workflows" / "issue-456-release-qualification-plan.md"
        bad_plan = root / "bad-plan.md"
        bad_plan.write_text("wrong plan bytes\n", encoding="utf-8")
        migration_names = [f"{n:03d}_{name}.sql" for n, name in [(1,"initial"),(2,"worker_heartbeats"),(3,"admin_indexes"),(4,"admin_audit_events"),(5,"admin_session_and_throttle"),(6,"admin_users_and_tenant_scopes"),(7,"mail_request_cancelled_status"),(8,"delivery_events"),(9,"mail_request_scheduled_at"),(10,"admin_audit_events_tenant_id"),(11,"bounce_ingestion"),(12,"provider_event_inbox_details"),(13,"provider_queue_dead_letters")]]
        migration_paths = [f"src/Amane.Mailer/Data/Migrations/{name}" for name in migration_names]
        inventory_document = {"schemaVersion": 1, "releaseCommitSha": source, "runnerOrderPaths": migration_paths}
        inventory_digest = sha(json.dumps(inventory_document, ensure_ascii=False, sort_keys=True, separators=(",", ":")).encode("utf-8"))
        migration_files = []
        for path in migration_paths[-2:]:
            file_bytes = subprocess.check_output(["git", "-C", str(ROOT.parent), "show", f"{source}:{path}"])
            blob_sha = subprocess.check_output(["git", "-C", str(ROOT.parent), "rev-parse", f"{source}:{path}"], text=True).strip()
            migration_files.append({"path": path, "sha256": sha(file_bytes), "gitBlobSha": blob_sha})
        migration_pin_without = {
            "schemaVersion": 1,
            "releaseCommitSha": source,
            "inventoryAlgorithm": "RFC8785-JCS-runner-order-migration-inventory-sha256/v1",
            "inventoryDigestSha256": inventory_digest,
            "files": migration_files,
        }
        migration_pin = root / "migration-pin.json"
        write(migration_pin, {
            "migrationPinWithoutDigest": migration_pin_without,
            "migrationPinDigestSha256": sha(json.dumps(migration_pin_without, ensure_ascii=False, sort_keys=True, separators=(",", ":")).encode("utf-8")),
            "migrationInventoryDigestSha256": inventory_digest,
        })
        owners = root / "owners.json"
        restricted_roles = {
            "G456-03": ("maintainer-acs-staging", "maintainer:acs-staging"),
            "G456-04": ("maintainer-acs-staging", "maintainer:acs-staging"),
            "G456-05": ("maintainer-acs-production", "maintainer:acs-production"),
            "G456-06": ("maintainer-acs-production", "maintainer:acs-production"),
            "G456-42": ("maintainer-migration", "maintainer:migration"),
            "G456-43": ("maintainer-migration", "maintainer:migration"),
            "G456-44": ("maintainer-migration", "maintainer:migration"),
        }
        def owner_for_scenario(scenario):
            return restricted_roles.get(scenario, ("lane-owner", "ci:test"))
        owner_entries = [{"scenarioId": f"G456-{number:02d}", "variantId": variant, "ownerRole": owner_for_scenario(f"G456-{number:02d}")[0], "ownerIdentity": owner_for_scenario(f"G456-{number:02d}")[1]} for number in range(1, 45) for variant in variants[number]]
        owner_entries.extend([
            {"scenarioId": "G456-38", "variantId": "nas", "ownerRole": "lane-owner", "ownerIdentity": "ci:test"},
            {"scenarioId": "G456-39", "variantId": "macos", "ownerRole": "lane-owner", "ownerIdentity": "ci:test"},
            {"scenarioId": "G456-40", "variantId": "mode5-manual", "ownerRole": "lane-owner", "ownerIdentity": "ci:test"},
            {"scenarioId": "G456-41", "variantId": "external-secret-manager-docs", "ownerRole": "lane-owner", "ownerIdentity": "ci:test"},
        ])
        write(owners, owner_entries)
        run("bind", "--store-root", str(store), "--candidate-id", candidate_id, "--issue-snapshot", str(issue_path), "--plan-file", str(bad_plan), "--plan-commit-sha", source, "--repo-root", str(ROOT.parent), "--migration-pin", str(migration_pin), "--run-attempt-nonce", "bad-plan", "--evidence-owners", str(owners), "--qualification-lead-role", "qualification-lead", "--qualification-lead-identity", "maintainer:test", "--conditional-approver-role", "conditional-approver", "--conditional-approver-identity", "maintainer:conditional", expect=1)
        def actor_for(scenario, variant):
            matches = [entry for entry in owner_entries if entry["scenarioId"] == scenario and entry["variantId"] == variant]
            assert len(matches) == 1
            return matches[0]["ownerRole"], matches[0]["ownerIdentity"]
        bound = json.loads(run("bind", "--store-root", str(store), "--candidate-id", candidate_id, "--issue-snapshot", str(issue_path), "--plan-file", str(plan), "--plan-commit-sha", source, "--repo-root", str(ROOT.parent), "--migration-pin", str(migration_pin), "--run-attempt-nonce", "self-test-1", "--evidence-owners", str(owners), "--qualification-lead-role", "qualification-lead", "--qualification-lead-identity", "maintainer:test", "--conditional-approver-role", "conditional-approver", "--conditional-approver-identity", "maintainer:conditional"))
        run_root = store / "runs" / bound["qualificationRunId"]
        binding = json.loads((run_root / "binding.json").read_text(encoding="utf-8"))
        evidence_counter = 10
        def envelope(evidence_id, scenario, variant, result, evidence_type, payload):
            owner_role, owner_identity = actor_for(scenario, variant)
            return {
                "schemaVersion": 1, "kind": "release-qualification-evidence", "evidenceType": evidence_type,
                "evidenceId": evidence_id, "candidateId": binding["candidateId"], "sourceCommitSha": binding["releaseCommitSha"],
                "scenarioId": scenario, "variantId": variant, "issueBodySha256": binding["issueBodySha256"], "planRevision": "12",
                "planCommitSha": binding["planCommitSha"], "planFileSha256": binding["planFileSha256"], "bindingId": binding["bindingId"],
                "qualificationRunId": binding["qualificationRunId"], "attempt": 1, "result": result,
                "startedAtUtc": "2026-08-08T00:00:00Z", "finishedAtUtc": "2026-08-08T00:00:01Z",
                "executedByRole": owner_role, "executedByIdentity": owner_identity, "procedureId": f"self-test-{scenario}",
                "procedureRevision": "1", "runnerClass": "synthetic", "toolVersion": "self-test-1", "attestedAtUtc": "2026-08-08T00:00:01Z",
                "identity": {}, "prohibitedContentScan": {"result": "PASS", "scannerId": "qualify-secret-like/1", "scannerVersion": "1", "reportDigestSha256": "a" * 64},
                "typePayload": payload,
            }
        def payload_for(scenario, variant, result):
            if scenario == "G456-03": return {"acsEnvironment": "Staging", "liveSending": False, "sendKind": "none", "mailSendAttempted": False, "testBypassUsed": False, "normalMailerPath": False, "outcome": "configuration-applied", "mailboxConfirmation": "not-required"}
            if scenario == "G456-04": return {"acsEnvironment": "Staging", "sendKind": "typed-fixed-synthetic", "mailSendAttempted": True, "testBypassUsed": False, "outcome": "completed", "mailboxConfirmation": "not-run", "restrictedOpsRecordId": "ops-04"}
            if scenario == "G456-05": return {"acsEnvironment": "Production", "liveSending": True, "sendKind": "none-for-send-ready-assert", "mailSendAttempted": False, "testBypassUsed": False, "effectiveFingerprintMatch": True, "bundleIntegrityMatched": True, "doctorOrReadinessSummary": "pass", "mailboxConfirmation": "not-required-for-send-ready"}
            if scenario == "G456-06": return {"acsEnvironment": "Production", "mailPath": "normal-mailer", "testBypassUsed": False, "sendCompletedValueFree": True, "distinctFromSendReadyEvidenceId": send_ready_evidence_id, "tenantStatusExportForbidden": True, "restrictedOpsRecordId": "ops-06"}
            if scenario in {"G456-42", "G456-43"}:
                fresh = scenario == "G456-42"
                return {"migrationDecision": "INCLUDE", "migrationInventory": ["012_provider_event_inbox_details.sql", "013_provider_queue_dead_letters.sql"], "expectedFullMigrationInventory": [f"{n:03d}_{name}.sql" for n, name in [(1,"initial"),(2,"worker_heartbeats"),(3,"admin_indexes"),(4,"admin_audit_events"),(5,"admin_session_and_throttle"),(6,"admin_users_and_tenant_scopes"),(7,"mail_request_cancelled_status"),(8,"delivery_events"),(9,"mail_request_scheduled_at"),(10,"admin_audit_events_tenant_id"),(11,"bounce_ingestion"),(12,"provider_event_inbox_details"),(13,"provider_queue_dead_letters")]], "expectedThrough011": [f"{n:03d}_{name}.sql" for n, name in [(1,"initial"),(2,"worker_heartbeats"),(3,"admin_indexes"),(4,"admin_audit_events"),(5,"admin_session_and_throttle"),(6,"admin_users_and_tenant_scopes"),(7,"mail_request_cancelled_status"),(8,"delivery_events"),(9,"mail_request_scheduled_at"),(10,"admin_audit_events_tenant_id"),(11,"bounce_ingestion")]], "expectedPost011Inventory": ["012_provider_event_inbox_details.sql", "013_provider_queue_dead_letters.sql"], "migrationDirectoryInventoryBefore": [f"{n:03d}_{name}.sql" for n, name in [(1,"initial"),(2,"worker_heartbeats"),(3,"admin_indexes"),(4,"admin_audit_events"),(5,"admin_session_and_throttle"),(6,"admin_users_and_tenant_scopes"),(7,"mail_request_cancelled_status"),(8,"delivery_events"),(9,"mail_request_scheduled_at"),(10,"admin_audit_events_tenant_id"),(11,"bounce_ingestion"),(12,"provider_event_inbox_details"),(13,"provider_queue_dead_letters")]], "migrationDirectoryInventoryDigestSha256": binding["migrationInventoryDigestSha256"], "migrationFileDigests": binding["migrationFileDigests"], "outcome": "applied" if fresh else "upgraded", "preApplyAppliedMigrations": [] if fresh else [f"{n:03d}_{name}.sql" for n, name in [(1,"initial"),(2,"worker_heartbeats"),(3,"admin_indexes"),(4,"admin_audit_events"),(5,"admin_session_and_throttle"),(6,"admin_users_and_tenant_scopes"),(7,"mail_request_cancelled_status"),(8,"delivery_events"),(9,"mail_request_scheduled_at"),(10,"admin_audit_events_tenant_id"),(11,"bounce_ingestion")]], "preApplyPendingMigrations": [f"{n:03d}_{name}.sql" for n, name in [(1,"initial"),(2,"worker_heartbeats"),(3,"admin_indexes"),(4,"admin_audit_events"),(5,"admin_session_and_throttle"),(6,"admin_users_and_tenant_scopes"),(7,"mail_request_cancelled_status"),(8,"delivery_events"),(9,"mail_request_scheduled_at"),(10,"admin_audit_events_tenant_id"),(11,"bounce_ingestion"),(12,"provider_event_inbox_details"),(13,"provider_queue_dead_letters")]] if fresh else ["012_provider_event_inbox_details.sql", "013_provider_queue_dead_letters.sql"], "postApplyAppliedMigrations": [f"{n:03d}_{name}.sql" for n, name in [(1,"initial"),(2,"worker_heartbeats"),(3,"admin_indexes"),(4,"admin_audit_events"),(5,"admin_session_and_throttle"),(6,"admin_users_and_tenant_scopes"),(7,"mail_request_cancelled_status"),(8,"delivery_events"),(9,"mail_request_scheduled_at"),(10,"admin_audit_events_tenant_id"),(11,"bounce_ingestion"),(12,"provider_event_inbox_details"),(13,"provider_queue_dead_letters")]], "postApplyPendingMigrations": [], "lastAppliedBefore": None if fresh else "011_bounce_ingestion.sql", "lastAppliedAfter": "013_provider_queue_dead_letters.sql"}
            if scenario == "G456-44": return {"migrationDecision": "INCLUDE", "migrationInventory": ["012_provider_event_inbox_details.sql", "013_provider_queue_dead_letters.sql"], "migrationDirectoryInventoryDigestSha256": binding["migrationInventoryDigestSha256"], "migrationFileDigests": binding["migrationFileDigests"], "expectedFullMigrationInventory": [f"{n:03d}_{name}.sql" for n, name in [(1,"initial"),(2,"worker_heartbeats"),(3,"admin_indexes"),(4,"admin_audit_events"),(5,"admin_session_and_throttle"),(6,"admin_users_and_tenant_scopes"),(7,"mail_request_cancelled_status"),(8,"delivery_events"),(9,"mail_request_scheduled_at"),(10,"admin_audit_events_tenant_id"),(11,"bounce_ingestion"),(12,"provider_event_inbox_details"),(13,"provider_queue_dead_letters")]], "expectedThrough011": [f"{n:03d}_{name}.sql" for n, name in [(1,"initial"),(2,"worker_heartbeats"),(3,"admin_indexes"),(4,"admin_audit_events"),(5,"admin_session_and_throttle"),(6,"admin_users_and_tenant_scopes"),(7,"mail_request_cancelled_status"),(8,"delivery_events"),(9,"mail_request_scheduled_at"),(10,"admin_audit_events_tenant_id"),(11,"bounce_ingestion")]], "expectedPost011Inventory": ["012_provider_event_inbox_details.sql", "013_provider_queue_dead_letters.sql"], "migrationDirectoryInventoryBefore": [f"{n:03d}_{name}.sql" for n, name in [(1,"initial"),(2,"worker_heartbeats"),(3,"admin_indexes"),(4,"admin_audit_events"),(5,"admin_session_and_throttle"),(6,"admin_users_and_tenant_scopes"),(7,"mail_request_cancelled_status"),(8,"delivery_events"),(9,"mail_request_scheduled_at"),(10,"admin_audit_events_tenant_id"),(11,"bounce_ingestion"),(12,"provider_event_inbox_details"),(13,"provider_queue_dead_letters")]], "outcome": "schema-checked", "preApplyAppliedMigrations": [], "preApplyPendingMigrations": [], "postApplyAppliedMigrations": [], "postApplyPendingMigrations": [], "lastAppliedBefore": None, "lastAppliedAfter": "013_provider_queue_dead_letters.sql", "addedColumnsByMigration": {"012": ["status_message TEXT NULL", "occurred_at TEXT NULL"]}, "createdTableByMigration": {"013": {"table": "provider_queue_dead_letters", "columns": ["id TEXT NOT NULL PRIMARY KEY", "provider TEXT NOT NULL", "queue_message_id TEXT NOT NULL", "failure_stage TEXT NOT NULL", "last_error_code TEXT NOT NULL", "dequeue_count INTEGER NOT NULL", "created_at TEXT NOT NULL", "updated_at TEXT NOT NULL"], "constraints": ["CHECK (failure_stage IN ('decode', 'parse'))", "CHECK (dequeue_count >= 0)", "UNIQUE (provider, queue_message_id)"], "indexes": ["idx_provider_queue_dead_letters_created ON provider_queue_dead_letters (created_at)"]}}, "piiValueCanaryResult": "pass", "contractResult": "pass"}
            return {"predicateResult": "PASS" if result == "PASS" else "FAIL"}
        def add_evidence(evidence_id, scenario, variant, result="PASS", evidence_type=None, payload=None):
            envelope_path = root / f"evidence-{evidence_id}.json"
            default_type = "manual-smoke" if scenario in {"G456-01", "G456-02"} else "qualification-scenario"
            write(envelope_path, envelope(evidence_id, scenario, variant, result, evidence_type or default_type, payload if payload is not None else payload_for(scenario, variant, result)))
            owner_role, owner_identity = actor_for(scenario, variant)
            run("evidence", "--run-root", str(run_root), "--evidence-id", evidence_id, "--scenario-id", scenario, "--variant-id", variant, "--result", result, "--executed-by-role", owner_role, "--executed-by-identity", owner_identity, "--observations", str(envelope_path))
        first_id = "1" * 64
        add_evidence(first_id, "G456-01", "win-docker", "FAIL", "manual-smoke")
        run("disposition", "--run-root", str(run_root), "--scenario-id", "G456-01", "--variant-id", "win-docker", "--action", "accept", "--target-evidence-id", first_id, "--reason-code", "self-test-fail", "--approved-by-role", "lane-owner", "--approved-by-identity", "ci:test")
        next_id = 3
        send_ready_evidence_id = None
        supported_scenarios = {"G456-03", "G456-04", "G456-05", "G456-06", "G456-42", "G456-43", "G456-44"}
        for number in range(1, 45):
            scenario = f"G456-{number:02d}"
            for variant in variants[number]:
                if scenario not in supported_scenarios:
                    continue
                if scenario == "G456-29" and variant == "win-docker":
                    continue
                eid = f"{next_id:064x}"; next_id += 1
                if scenario == "G456-05" and variant == "acs-production":
                    send_ready_evidence_id = eid
                etype = "manual-smoke" if scenario in {"G456-01", "G456-02"} else ("staging-acs-verification" if scenario in {"G456-03", "G456-04"} else ("production-acs-send-ready" if scenario == "G456-05" else ("release-production-operational-verification" if scenario == "G456-06" else ("linux-arm64-e2e" if scenario == "G456-34" else ("linux-arm64-artifact-smoke" if scenario == "G456-35" else ("vps-verification" if scenario == "G456-36" else ("optional-automation" if scenario == "G456-37" else ("db-migration-fresh-apply" if scenario == "G456-42" else ("db-migration-upgrade" if scenario == "G456-43" else ("db-migration-schema-contract" if scenario == "G456-44" else "qualification-scenario"))))))))))
                add_evidence(eid, scenario, variant, "PASS", etype)
                owner_role, owner_identity = actor_for(scenario, variant)
                run("disposition", "--run-root", str(run_root), "--scenario-id", scenario, "--variant-id", variant, "--action", "accept", "--target-evidence-id", eid, "--reason-code", "self-test-pass", "--approved-by-role", owner_role, "--approved-by-identity", owner_identity)
        # A generic predicateResult must not be able to promote an unimplemented
        # Admin/HTTPS/security lane.
        unsupported = "6" * 64
        unsupported_path = root / "unsupported-pass.json"
        write(unsupported_path, envelope(unsupported, "G456-02", "linux-docker", "PASS", "manual-smoke", {"predicateResult": "PASS"}))
        run("evidence", "--run-root", str(run_root), "--evidence-id", unsupported, "--scenario-id", "G456-02", "--variant-id", "linux-docker", "--result", "PASS", "--executed-by-role", "lane-owner", "--executed-by-identity", "ci:test", "--observations", str(unsupported_path), expect=1)
        contradictory_migration = "7" * 64
        contradictory_path = root / "contradictory-migration.json"
        write(contradictory_path, envelope(contradictory_migration, "G456-42", "win-docker", "FAIL", "db-migration-fresh-apply", payload_for("G456-42", "win-docker", "FAIL")))
        migration_role, migration_identity = actor_for("G456-42", "win-docker")
        run("evidence", "--run-root", str(run_root), "--evidence-id", contradictory_migration, "--scenario-id", "G456-42", "--variant-id", "win-docker", "--result", "FAIL", "--executed-by-role", migration_role, "--executed-by-identity", migration_identity, "--observations", str(contradictory_path), expect=1)
        leaky_evidence = "8" * 64
        leaky_path = root / "leaky-envelope.json"
        leaky = envelope(leaky_evidence, "G456-03", "acs-staging-nosend", "PASS", "staging-acs-verification", payload_for("G456-03", "acs-staging-nosend", "PASS"))
        leaky["notes"] = {"recipient": "user@example.com"}
        write(leaky_path, leaky)
        staging_role, staging_identity = actor_for("G456-03", "acs-staging-nosend")
        run("evidence", "--run-root", str(run_root), "--evidence-id", leaky_evidence, "--scenario-id", "G456-03", "--variant-id", "acs-staging-nosend", "--result", "PASS", "--executed-by-role", staging_role, "--executed-by-identity", staging_identity, "--observations", str(leaky_path), expect=1)
        metadata_leak_evidence = "9" * 64
        metadata_leak_path = root / "metadata-leak-envelope.json"
        metadata_leak = envelope(metadata_leak_evidence, "G456-03", "acs-staging-nosend", "PASS", "staging-acs-verification", payload_for("G456-03", "acs-staging-nosend", "PASS"))
        metadata_leak["procedureId"] = "https://secret.example/?token=redacted"
        write(metadata_leak_path, metadata_leak)
        run("evidence", "--run-root", str(run_root), "--evidence-id", metadata_leak_evidence, "--scenario-id", "G456-03", "--variant-id", "acs-staging-nosend", "--result", "PASS", "--executed-by-role", staging_role, "--executed-by-identity", staging_identity, "--observations", str(metadata_leak_path), expect=1)
        exception_id = "4" * 64
        run("exception", "--run-root", str(run_root), "--exception-id", exception_id, "--scenario-id", "G456-29", "--variant-id", "win-docker", "--reason-not-executable", "synthetic lane unavailable", "--alternate-verification", "synthetic alternate review", "--residual-risk", "synthetic residual risk", "--impact-scope", "synthetic scope", "--created-by-role", "lane-owner", "--created-by-identity", "ci:test")
        exception_approval = json.loads(run("exception-disposition", "--run-root", str(run_root), "--scenario-id", "G456-29", "--variant-id", "win-docker", "--action", "approve", "--target-exception-id", exception_id, "--reason-code", "self-test-conditional", "--approved-by-role", "conditional-approver", "--approved-by-identity", "maintainer:conditional"))
        run("exception-disposition", "--run-root", str(run_root), "--scenario-id", "G456-29", "--variant-id", "win-docker", "--action", "revoke", "--target-exception-id", exception_id, "--reason-code", "https://secret.example/?token=redacted", "--approved-by-role", "conditional-approver", "--approved-by-identity", "maintainer:conditional", expect=1)
        replacement_exception_id = "5" * 64
        run("exception", "--run-root", str(run_root), "--exception-id", replacement_exception_id, "--scenario-id", "G456-29", "--variant-id", "win-docker", "--reason-not-executable", "synthetic lane unavailable", "--alternate-verification", "synthetic alternate review", "--residual-risk", "synthetic residual risk", "--impact-scope", "synthetic scope", "--created-by-role", "lane-owner", "--created-by-identity", "ci:test")
        run("exception-disposition", "--run-root", str(run_root), "--scenario-id", "G456-29", "--variant-id", "win-docker", "--action", "supersede", "--target-exception-id", exception_id, "--superseded-by-exception-id", replacement_exception_id, "--reason-code", "self-test-supersede", "--approved-by-role", "conditional-approver", "--approved-by-identity", "maintainer:conditional")
        run("exception-disposition", "--run-root", str(run_root), "--scenario-id", "G456-29", "--variant-id", "win-docker", "--action", "restore", "--restores-exception-event-id", exception_approval["eventId"], "--reason-code", "self-test-restore", "--approved-by-role", "conditional-approver", "--approved-by-identity", "maintainer:conditional")
        run("seal", "--run-root", str(run_root), "--current-issue-snapshot", str(issue_path), "--repo-root", str(ROOT.parent), "--human-decision", "NOT_DECIDED", "--approved-by-role", "qualification-lead", "--approved-by-identity", "maintainer:test")
        verify = json.loads(run("verify", "--run-root", str(run_root), "--repo-root", str(ROOT.parent)))
        assert verify["machineVerdict"] == "NO_GO"
        run("handoff", "--run-root", str(run_root), "--output-root", str(handoff), "--repo-root", str(ROOT.parent), expect=1)
        sealed_event_path = next((run_root / "run-status-events").glob("*.json"))
        sealed_event = json.loads(sealed_event_path.read_text(encoding="utf-8"))
        sealed_event["canonicalization"] = {"algorithm": "not-jcs", "version": 1}
        unsigned_event = {key: value for key, value in sealed_event.items() if key != "eventDigestSha256"}
        sealed_event["eventDigestSha256"] = sha(json.dumps(unsigned_event, ensure_ascii=False, sort_keys=True, separators=(",", ":")).encode("utf-8"))
        write(sealed_event_path, sealed_event)
        run("verify", "--run-root", str(run_root), "--repo-root", str(ROOT.parent), expect=1)

        # Sealed runs reject new evidence and value-bearing observations.
        run("evidence", "--run-root", str(run_root), "--evidence-id", "9" * 64, "--scenario-id", "G456-01", "--variant-id", "win-docker", "--result", "PASS", "--executed-by-role", "lane-owner", "--executed-by-identity", "ci:test", expect=1)
        bound2 = json.loads(run("bind", "--store-root", str(store), "--candidate-id", candidate_id, "--issue-snapshot", str(issue_path), "--plan-file", str(plan), "--plan-commit-sha", source, "--repo-root", str(ROOT.parent), "--migration-pin", str(migration_pin), "--run-attempt-nonce", "self-test-2", "--evidence-owners", str(owners), "--qualification-lead-role", "qualification-lead", "--qualification-lead-identity", "maintainer:test", "--conditional-approver-role", "conditional-approver", "--conditional-approver-identity", "maintainer:conditional"))
        observations = root / "secret-observations.json"
        saved_binding = binding
        binding = json.loads((store / "runs" / bound2["qualificationRunId"] / "binding.json").read_text(encoding="utf-8"))
        write(observations, envelope("3" * 64, "G456-01", "win-docker", "PASS", "manual-smoke", {"predicateResult": "PASS", "token": "synthetic-token"}))
        binding = saved_binding
        run("evidence", "--run-root", str(store / "runs" / bound2["qualificationRunId"]), "--evidence-id", "3" * 64, "--scenario-id", "G456-01", "--variant-id", "win-docker", "--result", "PASS", "--executed-by-role", "lane-owner", "--executed-by-identity", "ci:test", "--observations", str(observations), expect=1)
    print("[info] qualification-runner self-test passed")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
