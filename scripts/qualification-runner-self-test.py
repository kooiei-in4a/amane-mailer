#!/usr/bin/env python3
"""Synthetic, value-free tests for qualification-runner.py.

This test never contacts GitHub, Docker, ACS, a registry, or a durable store.
All candidate/evidence data is generated in a temporary directory.
"""

from __future__ import annotations

import hashlib
import importlib.util
import io
import json
import os
import copy
import shutil
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
        scope_manifest = ROOT.parent / "docs" / "qualification" / "v1.3.0-scope.json"
        run("validate-scope", "--scope-manifest", str(scope_manifest), "--repo-root", str(ROOT.parent))
        malformed_scope = root / "malformed-scope.json"
        malformed = json.loads(scope_manifest.read_text(encoding="utf-8"))
        malformed["migration"]["deltaInventory"] = malformed["migration"]["deltaInventory"][:-1]
        write(malformed_scope, malformed)
        run("validate-scope", "--scope-manifest", str(malformed_scope), "--repo-root", str(ROOT.parent), expect=1)
        spec = importlib.util.spec_from_file_location("qualification_runner_scope_test", RUNNER)
        runner = importlib.util.module_from_spec(spec)
        assert spec.loader is not None
        spec.loader.exec_module(runner)
        profile = runner.load_scope_manifest(scope_manifest)
        v13_hard = {row["scenarioId"] for row in profile["scenarioRows"] if row["gateClass"] == "Hard"}
        rc5_dedicated = {
            "G456-03", "G456-04", "G456-05", "G456-06",
            "G456-42", "G456-43", "G456-44",
            "G583-MIG-01", "G583-MIG-02", "G583-MIG-03",
        }
        rc5_gap = sorted(v13_hard - rc5_dedicated)
        expected_rc5_gap = sorted(
            {"G456-01", "G456-02"}
            | {f"G456-{number:02d}" for number in range(7, 29)}
            | {"G456-30", "G456-31", "G456-32", "G456-33", "G456-35"}
        )
        if rc5_gap != expected_rc5_gap or len(rc5_gap) != 29:
            raise AssertionError(f"RC5 machine-derived Hard validator gap changed: {rc5_gap}")
        if v13_hard - set(runner.V13_IMPLEMENTED_SCENARIO_VALIDATORS):
            raise AssertionError("v1.3 Hard scenario has no dedicated validator")
        if set(runner.HARD_SCENARIO_VALIDATOR_REGISTRY) != set(expected_rc5_gap):
            raise AssertionError("Hard validator registry is not exactly the RC5-derived gap")
        migration = profile["migration"]
        v13_binding = {
            "scopeId": profile["scopeId"],
            "scopeVersion": profile["scopeVersion"],
            "scopeAuthorityIssueNumber": profile["authorityIssueNumber"],
            "scopeAuthorityIssueBodySha256": profile["authorityIssueBodySha256"],
            "scopePlanFileSha256": profile["planFileSha256"],
            "scopeManifestSha256": profile["scopeManifestSha256"],
            "issueNumber": profile["authorityIssueNumber"],
            "planRevision": profile["planRevision"],
            "variantRulesVersion": profile["variantRulesVersion"],
            "migrationBaselineInventory": migration["baselineInventory"],
            "migrationDeltaInventory": migration["deltaInventory"],
            "migrationFullInventory": migration["fullInventory"],
            "migrationFullInventoryDigestSha256": "a" * 64,
            "migrationDeltaInventoryDigestSha256": "b" * 64,
            "migrationBaselineInventoryDigestSha256": "c" * 64,
            "migrationFullFileDigests": [],
            "migrationPredicateSetVersion": profile["migrationPredicateSetVersion"],
            "migrationSchemaAllowlistVersion": migration["schemaAllowlistVersion"],
            "migrationSchemaAllowlistSha256": migration["schemaAllowlistSha256"],
            "migrationSchemaAllowlist": migration["schemaAllowlist"],
            "rows": profile["scenarioRows"],
        }
        if v13_binding["planRevision"] != "1" or v13_binding["issueNumber"] != 583 or v13_binding["variantRulesVersion"] != 5:
            raise AssertionError("v1.3 scope binding identity was not materialized")
        if v13_binding["migrationPredicateSetVersion"] != 1 or v13_binding["migrationSchemaAllowlistVersion"] != 1:
            raise AssertionError("v1.3 scope versions must remain numeric")
        v13_snapshot_rows = [
            {**row, "scenarioText": f"synthetic {row['scenarioId']}", "environmentText": "synthetic v1.3 lane"}
            for row in profile["scenarioRows"]
        ]
        runner.bind_rows({"number": 583, "rows": v13_snapshot_rows}, profile)
        # Exercise the v1.3 load path with a complete, value-free synthetic
        # binding.  This deliberately avoids product/ACS work while proving
        # that binding, phase-2, authority, and numeric scope versions can be
        # reloaded together (the failure in F584-01 occurred only on reload).
        smoke_root = root / "v13-scope-load-smoke"
        smoke_nonce = "scope-smoke-1"
        smoke_archive_specs = [
            ("win-x64", "synthetic-win-x64.zip", "d" * 64),
            ("linux-x64", "synthetic-linux-x64.tar.gz", "e" * 64),
            ("linux-arm64", "synthetic-linux-arm64.tar.gz", "f" * 64),
        ]
        smoke_provenance = {
            "sourceCommitSha": "b" * 40, "workflowRunId": "583000001", "workflowRunAttempt": "1",
            "workflowRef": "local/scope-smoke", "releaseVersion": "1.3.0",
            "ociIndexDigest": "sha256:" + "c" * 64, "ociPlatforms": ["linux/amd64", "linux/arm64"],
            "archives": [{"targetRid": rid, "archiveFileName": archive_name, "archiveSha256": "sha256:" + archive_sha} for rid, archive_name, archive_sha in smoke_archive_specs],
        }
        smoke_identity = {"sourceCommitSha": smoke_provenance["sourceCommitSha"], "imageDigest": smoke_provenance["ociIndexDigest"]}
        smoke_archives = {rid: archive_sha for rid, _, archive_sha in smoke_archive_specs}
        smoke_candidate_id = runner.candidate_id(smoke_provenance, smoke_archives)
        smoke_rows = [
            {**row, "scenarioText": f"synthetic {row['scenarioId']}", "environmentText": "synthetic v1.3 lane"}
            for row in profile["scenarioRows"]
        ]
        smoke_docs_dir = smoke_root / "docs-extract"
        smoke_docs = {}
        for key, filename in {"setupGuideJa": "setup-guide.md", "setupGuideEn": "setup-guide.en.md", "setupReleaseBundleJa": "setup-release-bundle.md", "setupReleaseBundleEn": "setup-release-bundle.en.md", "readmeJa": "README.md", "readmeEn": "README.en.md"}.items():
            payload = f"synthetic {key}\n".encode()
            write(smoke_docs_dir / filename, payload)
            smoke_docs[f"{key}Sha256"] = sha(payload)
        smoke_readme_mapping = {}
        for rid, archive_name, archive_sha in smoke_archive_specs:
            payload = f"synthetic candidate README {rid}\n".encode()
            smoke_readme_mapping[rid] = {
                "archiveFileName": archive_name,
                "archiveSha256": "sha256:" + archive_sha,
                "targetRid": rid,
                "manifestTargetRid": rid,
                "sha256": sha(payload),
            }
            write(smoke_docs_dir / "candidate-readme-setup" / f"{rid}.md", payload)
        smoke_docs["candidateReadmeSetupByRid"] = smoke_readme_mapping
        smoke_docs["candidateReadmeSetupByRidSha256"] = runner.sha_object(smoke_readme_mapping)
        smoke_docs.update({"sourceCommitSha": smoke_provenance["sourceCommitSha"], "extractionMethod": "git-archive-exact-source-plus-qualified-archive"})
        smoke_candidate_root = smoke_root / "candidates" / smoke_candidate_id / "intake"
        write(smoke_candidate_root / "candidate-provenance.json", smoke_provenance)
        write(smoke_candidate_root / "image-identity.json", smoke_identity)
        smoke_objects = [
            {"path": f"intake/{name}", "sha256": runner.file_sha(smoke_candidate_root / name)}
            for name in ("candidate-provenance.json", "image-identity.json")
        ]
        write(smoke_candidate_root / "phase-1.json", {"candidateId": smoke_candidate_id, "sourceCommitSha": smoke_provenance["sourceCommitSha"], "ociIndexDigest": smoke_provenance["ociIndexDigest"], "workflowRunId": smoke_provenance["workflowRunId"], "workflowRunAttempt": smoke_provenance["workflowRunAttempt"], "workflowRef": smoke_provenance["workflowRef"], "objects": smoke_objects})
        smoke_file_sha = {name: runner.file_sha(smoke_candidate_root / name) for name in ("candidate-provenance.json", "image-identity.json", "phase-1.json")}
        smoke_pin = {
            "releaseCommitSha": smoke_provenance["sourceCommitSha"], "migrationPinDigestSha256": "e" * 64,
            "migrationInventoryDigestSha256": "f" * 64, "migrationFileDigests": [],
            "migrationBaselineInventory": migration["baselineInventory"], "migrationDeltaInventory": migration["deltaInventory"],
            "migrationFullInventory": migration["fullInventory"], "migrationBaselineInventoryDigestSha256": "1" * 64,
            "migrationDeltaInventoryDigestSha256": "2" * 64, "migrationFullInventoryDigestSha256": "3" * 64,
            "migrationBaselineFileDigests": [], "migrationDeltaFileDigests": [], "migrationFullFileDigests": [],
            "migrationPredicateSetVersion": 1,
        }
        smoke_auth = {"schemaVersion": 1, "qualificationRunId": "pending", "bindingId": "pending", "candidateId": smoke_candidate_id, "qualificationLeadRole": "qualification-lead", "qualificationLeadIdentity": "maintainer:scope-smoke", "conditionalApproverRole": "conditional-approver", "conditionalApproverIdentity": "maintainer:scope-smoke", "evidenceOwners": [], "createdAtUtc": "2026-08-08T00:00:00Z"}
        smoke_binding = {
            "schemaVersion": 1, "candidateId": smoke_candidate_id, "planRevision": profile["planRevision"], "planCommitSha": "a" * 40, "planFilePath": profile["planFilePath"], "planFileSha256": profile["planFileSha256"], "variantRulesVersion": profile["variantRulesVersion"], "issueNumber": 583, "issueUpdatedAt": "2026-08-08T00:00:00Z", "issueBodySha256": profile["authorityIssueBodySha256"], "fetchedAtUtc": "2026-08-08T00:00:00Z", "sourceCommitSha": smoke_provenance["sourceCommitSha"], "releaseCommitSha": smoke_provenance["sourceCommitSha"], "ociIndexDigest": smoke_provenance["ociIndexDigest"], "ociLayoutIndexSha256": "4" * 64, "releaseVersion": "1.3.0", "ociPlatforms": smoke_provenance["ociPlatforms"], "producerWorkflowRef": smoke_provenance["workflowRef"], "producerWorkflowRunId": smoke_provenance["workflowRunId"], "producerWorkflowRunAttempt": smoke_provenance["workflowRunAttempt"], "candidateProvenanceSha256": smoke_file_sha["candidate-provenance.json"], "candidateImageIdentitySha256": smoke_file_sha["image-identity.json"], "candidatePhase1ManifestSha256": smoke_file_sha["phase-1.json"], "candidateArchivesDigestSha256": runner.sha_object({"archives": smoke_provenance["archives"], "archiveDigests": smoke_archives}), "docs": smoke_docs, "migrationPinDigestSha256": smoke_pin["migrationPinDigestSha256"], "migrationInventoryDigestSha256": smoke_pin["migrationInventoryDigestSha256"], "migrationFileDigests": [], "rows": runner.bind_rows({"number": 583, "rows": smoke_rows}, profile), "optionalEvidenceKeys": profile["optionalEvidenceKeys"], "scopeId": profile["scopeId"], "scopeVersion": profile["scopeVersion"], "scopeManifestSha256": profile["scopeManifestSha256"], "scopeAuthorityIssueNumber": profile["authorityIssueNumber"], "scopeAuthorityIssueBodySha256": profile["authorityIssueBodySha256"], "scopePlanFileSha256": profile["planFileSha256"], "migrationBaselineInventory": migration["baselineInventory"], "migrationDeltaInventory": migration["deltaInventory"], "migrationFullInventory": migration["fullInventory"], "migrationBaselineInventoryDigestSha256": smoke_pin["migrationBaselineInventoryDigestSha256"], "migrationDeltaInventoryDigestSha256": smoke_pin["migrationDeltaInventoryDigestSha256"], "migrationFullInventoryDigestSha256": smoke_pin["migrationFullInventoryDigestSha256"], "migrationBaselineFileDigests": [], "migrationDeltaFileDigests": [], "migrationFullFileDigests": [], "migrationPredicateSetVersion": 1, "migrationSchemaAllowlistVersion": 1, "migrationSchemaAllowlistSha256": migration["schemaAllowlistSha256"], "migrationSchemaAllowlist": migration["schemaAllowlist"],
        }
        smoke_auth["bindingId"] = runner.binding_id_for(smoke_binding, smoke_auth)
        smoke_auth["qualificationRunId"] = runner.sha_bytes((smoke_auth["bindingId"] + "|" + smoke_nonce).encode())
        smoke_binding.update({"bindingId": smoke_auth["bindingId"], "qualificationRunId": smoke_auth["qualificationRunId"], "runAttemptNonce": smoke_nonce, "authorizationDigestSha256": runner.sha_object(smoke_auth)})
        smoke_root = smoke_root / "runs" / smoke_auth["qualificationRunId"]
        for key, filename in {"setupGuideJa": "setup-guide.md", "setupGuideEn": "setup-guide.en.md", "setupReleaseBundleJa": "setup-release-bundle.md", "setupReleaseBundleEn": "setup-release-bundle.en.md", "readmeJa": "README.md", "readmeEn": "README.en.md"}.items():
            write(smoke_root / "docs-extract" / filename, f"synthetic {key}\n".encode())
        for rid, archive_name, _ in smoke_archive_specs:
            write(smoke_root / "docs-extract" / "candidate-readme-setup" / f"{rid}.md", f"synthetic candidate README {rid}\n".encode())
        write(smoke_root / "binding.json", smoke_binding)
        write(smoke_root / "authorization.json", smoke_auth)
        write(smoke_root / "scope-manifest.json", json.loads(scope_manifest.read_text(encoding="utf-8")))
        write(smoke_root / "migration-pin.json", {})
        phase2 = {"candidateId": smoke_candidate_id, "bindingId": smoke_auth["bindingId"], "qualificationRunId": smoke_auth["qualificationRunId"], "runAttemptNonce": smoke_nonce, "releaseCommitSha": smoke_provenance["sourceCommitSha"], "releaseVersion": "1.3.0", "ociPlatforms": smoke_provenance["ociPlatforms"], "ociLayoutIndexSha256": "4" * 64, "planFilePath": profile["planFilePath"], "producerWorkflowRef": smoke_provenance["workflowRef"], "producerWorkflowRunId": smoke_provenance["workflowRunId"], "producerWorkflowRunAttempt": smoke_provenance["workflowRunAttempt"], "candidateProvenanceSha256": smoke_file_sha["candidate-provenance.json"], "candidateImageIdentitySha256": smoke_file_sha["image-identity.json"], "candidatePhase1ManifestSha256": smoke_file_sha["phase-1.json"], "candidateArchivesDigestSha256": smoke_binding["candidateArchivesDigestSha256"], "docs": smoke_docs, "authorizationDigestSha256": smoke_binding["authorizationDigestSha256"], "migrationPinDigestSha256": smoke_pin["migrationPinDigestSha256"], "migrationInventoryDigestSha256": smoke_pin["migrationInventoryDigestSha256"], "migrationFileDigests": [], "scopeId": profile["scopeId"], "scopeVersion": profile["scopeVersion"], "scopeManifestSha256": profile["scopeManifestSha256"], "scopeAuthorityIssueNumber": 583, "scopeAuthorityIssueBodySha256": profile["authorityIssueBodySha256"], "scopePlanFileSha256": profile["planFileSha256"], "planRevision": "1", "issueNumber": 583, "variantRulesVersion": 5, "migrationBaselineInventoryDigestSha256": smoke_pin["migrationBaselineInventoryDigestSha256"], "migrationDeltaInventoryDigestSha256": smoke_pin["migrationDeltaInventoryDigestSha256"], "migrationFullInventoryDigestSha256": smoke_pin["migrationFullInventoryDigestSha256"], "migrationPredicateSetVersion": 1, "migrationSchemaAllowlistVersion": 1, "migrationSchemaAllowlistSha256": migration["schemaAllowlistSha256"], "migrationSchemaAllowlist": migration["schemaAllowlist"]}
        assert smoke_binding["candidateArchivesDigestSha256"] == runner.sha_object({"archives": smoke_provenance["archives"], "archiveDigests": smoke_archives})
        assert smoke_binding["candidateProvenanceSha256"] == runner.file_sha(smoke_candidate_root / "candidate-provenance.json")
        assert smoke_binding["candidateImageIdentitySha256"] == runner.file_sha(smoke_candidate_root / "image-identity.json")
        assert smoke_binding["candidatePhase1ManifestSha256"] == runner.file_sha(smoke_candidate_root / "phase-1.json")
        write(smoke_root / "phase-manifests" / "phase-2.json", phase2)
        original_pin_loader = runner.load_migration_pin
        original_candidate_documents = runner.candidate_documents
        runner.load_migration_pin = lambda _path, _profile: smoke_pin
        runner.candidate_documents = lambda _path: (smoke_provenance, smoke_identity, smoke_archives)
        try:
            runner.load_binding(smoke_root)
        finally:
            runner.load_migration_pin = original_pin_loader
            runner.candidate_documents = original_candidate_documents
        migration_payload = {
            "migrationDecision": "INCLUDE",
            "baselineInventory": migration["baselineInventory"],
            "deltaInventory": migration["deltaInventory"],
            "fullInventory": migration["fullInventory"],
            "expectedFullMigrationInventory": migration["fullInventory"],
            "migrationDirectoryInventoryBefore": migration["fullInventory"],
            "migrationDirectoryInventoryDigestSha256": "a" * 64,
            "migrationDeltaInventoryDigestSha256": "b" * 64,
            "migrationFileDigests": [],
            "outcome": "applied",
            "preApplyAppliedMigrations": [],
            "preApplyPendingMigrations": migration["fullInventory"],
            "postApplyAppliedMigrations": migration["fullInventory"],
            "postApplyPendingMigrations": [],
            "lastAppliedBefore": None,
            "lastAppliedAfter": migration["deltaInventory"][-1],
        }
        runner.validate_migration_payload({"result": "PASS"}, v13_binding, "G583-MIG-01", migration_payload)
        schema_payload = dict(migration_payload)
        schema_payload.update({
            "outcome": "schema-checked", "lastAppliedAfter": migration["deltaInventory"][-1],
            "schemaContractResult": "pass", "piiValueCanaryResult": "pass",
            "schemaAllowlistVersion": 1, "schemaAllowlistSha256": migration["schemaAllowlistSha256"],
        })
        runner.validate_migration_payload({"result": "PASS"}, v13_binding, "G583-MIG-03", schema_payload)
        tampered_schema = dict(schema_payload)
        tampered_schema["schemaAllowlistSha256"] = "0" * 64
        try:
            runner.validate_migration_payload({"result": "PASS"}, v13_binding, "G583-MIG-03", tampered_schema)
        except runner.RunnerError:
            pass
        else:
            raise AssertionError("v1.3 schema predicate accepted a tampered allowlist digest")
        v13_evidence_binding = {
            **v13_binding,
            "candidateId": "3" * 64,
            "releaseCommitSha": "4" * 40,
            "issueBodySha256": "5" * 64,
            "planRevision": profile["planRevision"],
            "planCommitSha": "6" * 40,
            "planFileSha256": "7" * 64,
            "bindingId": "8" * 64,
            "qualificationRunId": "9" * 64,
        }
        v13_migration_row = next(row for row in profile["scenarioRows"] if row["scenarioId"] == "G583-MIG-03")
        v13_evidence_key = ("G583-MIG-03", v13_migration_row["requiredVariants"][0])
        v13_evidence_auth = {"evidenceOwners": [{"scenarioId": v13_evidence_key[0], "variantId": v13_evidence_key[1], "ownerRole": "lane-owner", "ownerIdentity": "ci:test"}]}
        def v13_evidence_envelope(payload, result="PASS"):
            owner = v13_evidence_auth["evidenceOwners"][0]
            return {
                "schemaVersion": 1, "kind": "release-qualification-evidence", "evidenceType": runner.EVIDENCE_TYPES["G583-MIG-03"][0],
                "evidenceId": "a" * 64, "candidateId": v13_evidence_binding["candidateId"], "sourceCommitSha": v13_evidence_binding["releaseCommitSha"],
                "scopeId": v13_evidence_binding["scopeId"], "scopeVersion": v13_evidence_binding["scopeVersion"], "scopeManifestSha256": v13_evidence_binding["scopeManifestSha256"],
                "scenarioId": v13_evidence_key[0], "variantId": v13_evidence_key[1], "issueBodySha256": v13_evidence_binding["issueBodySha256"],
                "planRevision": v13_evidence_binding["planRevision"], "planCommitSha": v13_evidence_binding["planCommitSha"], "planFileSha256": v13_evidence_binding["planFileSha256"],
                "bindingId": v13_evidence_binding["bindingId"], "qualificationRunId": v13_evidence_binding["qualificationRunId"], "attempt": 1, "result": result,
                "startedAtUtc": "2026-08-08T00:00:00Z", "finishedAtUtc": "2026-08-08T00:00:01Z", "executedByRole": owner["ownerRole"], "executedByIdentity": owner["ownerIdentity"],
                "procedureId": "v13-migration-schema-contract", "procedureRevision": "1", "runnerClass": "synthetic", "toolVersion": "self-test-1", "attestedAtUtc": "2026-08-08T00:00:01Z",
                "identity": {}, "prohibitedContentScan": {"result": "PASS", "scannerId": "qualify-secret-like/1", "scannerVersion": "1", "reportDigestSha256": "b" * 64},
                "typePayload": payload,
            }
        runner.validate_evidence_envelope(v13_evidence_envelope(schema_payload), v13_evidence_binding, v13_evidence_auth, v13_evidence_key)
        def expect_v13_evidence_rejection(label, mutate):
            candidate_envelope = v13_evidence_envelope(copy.deepcopy(schema_payload))
            mutate(candidate_envelope)
            try:
                runner.validate_evidence_envelope(candidate_envelope, v13_evidence_binding, v13_evidence_auth, v13_evidence_key)
            except runner.RunnerError:
                return
            raise AssertionError(f"v1.3 G583-MIG-03 negative fixture unexpectedly passed: {label}")
        expect_v13_evidence_rejection("wrong schema allowlist version", lambda item: item["typePayload"].update({"schemaAllowlistVersion": 2}))
        expect_v13_evidence_rejection("wrong schema allowlist digest", lambda item: item["typePayload"].update({"schemaAllowlistSha256": "0" * 64}))
        expect_v13_evidence_rejection("missing schema allowlist version", lambda item: item["typePayload"].pop("schemaAllowlistVersion"))
        expect_v13_evidence_rejection("missing schema allowlist digest", lambda item: item["typePayload"].pop("schemaAllowlistSha256"))
        expect_v13_evidence_rejection("schema contract not pass", lambda item: item["typePayload"].update({"schemaContractResult": "fail"}))
        expect_v13_evidence_rejection("PII canary not pass", lambda item: item["typePayload"].update({"piiValueCanaryResult": "fail"}))
        expect_v13_evidence_rejection("old full schema allowlist field", lambda item: item["typePayload"].update({"schemaAllowlist": {}}))
        expect_v13_evidence_rejection("unexpected migration field", lambda item: item["typePayload"].update({"unexpectedField": "value"}))
        expect_v13_evidence_rejection("wrong source identity", lambda item: item.update({"sourceCommitSha": "0" * 40}))
        expect_v13_evidence_rejection("wrong binding identity", lambda item: item.update({"bindingId": "0" * 64}))
        expect_v13_evidence_rejection("wrong qualification run identity", lambda item: item.update({"qualificationRunId": "0" * 64}))
        expect_v13_evidence_rejection("wrong owner identity", lambda item: item.update({"executedByIdentity": "ci:wrong"}))
        invalid_migration_payload = dict(migration_payload)
        invalid_migration_payload["deltaInventory"] = ["012_provider_event_inbox_details.sql"]
        try:
            runner.validate_migration_payload({"result": "PASS"}, v13_binding, "G583-MIG-01", invalid_migration_payload)
        except runner.RunnerError:
            pass
        else:
            raise AssertionError("v1.3 migration validator accepted a legacy delta")
        candidate = root / "candidate"
        store = root / "store"
        handoff = root / "handoff"
        archive_specs = [("win-x64", "amane-mailer-v1.3.0-windows-x64.zip"), ("linux-x64", "amane-mailer-v1.3.0-linux-x64.tar.gz"), ("linux-arm64", "amane-mailer-v1.3.0-linux-arm64.tar.gz")]
        migration_names = [f"{n:03d}_{name}.sql" for n, name in [(1,"initial"),(2,"worker_heartbeats"),(3,"admin_indexes"),(4,"admin_audit_events"),(5,"admin_session_and_throttle"),(6,"admin_users_and_tenant_scopes"),(7,"mail_request_cancelled_status"),(8,"delivery_events"),(9,"mail_request_scheduled_at"),(10,"admin_audit_events_tenant_id"),(11,"bounce_ingestion"),(12,"provider_event_inbox_details"),(13,"provider_queue_dead_letters")]]
        migration_paths = [f"src/Amane.Mailer/Data/Migrations/{name}" for name in migration_names]
        repo_root = ROOT.parent
        plan = ROOT.parent / "docs" / "agent-workflows" / "issue-456-release-qualification-plan.md"
        source = subprocess.check_output(["git", "-C", str(ROOT.parent), "rev-parse", "HEAD"], text=True).strip()
        legacy_inventory = subprocess.check_output(
            ["git", "-C", str(ROOT.parent), "ls-tree", "-r", "--name-only", source, "--", "src/Amane.Mailer/Data/Migrations"],
            text=True,
        ).splitlines()
        if len(legacy_inventory) > 13:
            # Keep the legacy profile regression fixture independent of commit
            # parent availability in shallow CI checkouts.
            legacy_repo = root / "legacy-repo"
            shutil.copytree(ROOT.parent / "docs", legacy_repo / "docs")
            for readme_name in ("README.md", "README.en.md"):
                (legacy_repo / readme_name).write_bytes((ROOT.parent / readme_name).read_bytes())
            legacy_plan = legacy_repo / plan.relative_to(ROOT.parent)
            legacy_plan.parent.mkdir(parents=True, exist_ok=True)
            legacy_plan.write_bytes(plan.read_bytes())
            for migration_path in migration_paths:
                destination = legacy_repo / migration_path
                destination.parent.mkdir(parents=True, exist_ok=True)
                destination.write_bytes((ROOT.parent / migration_path).read_bytes())
            subprocess.run(["git", "-C", str(legacy_repo), "init", "--quiet"], check=True)
            subprocess.run(["git", "-C", str(legacy_repo), "add", "--", "docs", "README.md", "README.en.md", *migration_paths], check=True)
            subprocess.run(["git", "-C", str(legacy_repo), "-c", "user.name=qualification-self-test", "-c", "user.email=qualification-self-test@example.invalid", "commit", "--quiet", "-m", "legacy qualification fixture"], check=True)
            source = subprocess.check_output(["git", "-C", str(legacy_repo), "rev-parse", "HEAD"], text=True).strip()
            repo_root = legacy_repo
            plan = legacy_plan
        oci_layout = root / "oci-layout"
        blob_root = oci_layout / "blobs" / "sha256"

        def put_blob(payload: bytes) -> tuple[str, int]:
            digest = "sha256:" + sha(payload)
            write(blob_root / digest.removeprefix("sha256:"), payload)
            return digest, len(payload)

        runtime_descriptors = []
        for platform_name in ("amd64", "arm64"):
            config_bytes = json.dumps({"architecture": platform_name, "os": "linux", "config": {"Labels": {"org.opencontainers.image.version": "1.2.0", "org.opencontainers.image.revision": source}}}, sort_keys=True, separators=(",", ":")).encode("utf-8")
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
                "sourceCommitSha": source, "mailerVersion": "1.2.0", "setupLauncherVersion": "1.2.0",
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
            "releaseVersion": "1.2.0",
            "workflowRunId": "123456789",
            "workflowRunAttempt": "1",
            "workflowRef": "local",
            "ociIndexDigest": oci,
            "ociPlatforms": ["linux/amd64", "linux/arm64"],
            "archives": [{"artifactName": f"synthetic-{rid}", "targetRid": rid, "archiveFileName": archive_name, "archiveSha256": "sha256:" + archive_digests[rid], "mailerVersion": "1.2.0", "setupLauncherVersion": "1.2.0", "payloadTreeSha256": "sha256:" + "b" * 64, "smokeResult": "passed"} for rid, archive_name in archive_specs],
        }
        write(candidate / "candidate-provenance.json", provenance)
        write(candidate / "image-identity.json", {"sourceCommitSha": source, "imageDigest": oci, "mailerVersion": "1.2.0", "platforms": ["linux/amd64", "linux/arm64"]})
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
        rid_candidate = root / "rid-candidate"
        rid_candidate.mkdir()
        rid_archive_paths = []
        rid_archive_records = []
        for rid, archive_name in archive_specs:
            original = candidate / archive_name
            manifest_bytes = json.dumps(runner.archive_manifest(original), sort_keys=True, separators=(",", ":")).encode()
            output = rid_candidate / archive_name
            readme = f"synthetic setup guide {rid}\n".encode()
            if archive_name.endswith(".zip"):
                with zipfile.ZipFile(output, "w", zipfile.ZIP_DEFLATED) as archive_file:
                    archive_file.writestr("release-bundle-manifest.json", manifest_bytes)
                    archive_file.writestr("README-SETUP.md", readme)
            else:
                with tarfile.open(output, "w:gz") as archive_file:
                    manifest_info = tarfile.TarInfo("release-bundle-manifest.json")
                    manifest_info.size = len(manifest_bytes)
                    archive_file.addfile(manifest_info, io.BytesIO(manifest_bytes))
                    readme_info = tarfile.TarInfo("README-SETUP.md")
                    readme_info.size = len(readme)
                    archive_file.addfile(readme_info, io.BytesIO(readme))
            rid_archive_paths.append(output)
            rid_archive_records.append({"targetRid": rid, "archiveFileName": archive_name, "archiveSha256": "sha256:" + sha(output.read_bytes())})
        rid_docs, rid_payloads = runner.docs_from_release_tree(repo_root, source, rid_candidate, rid_archive_paths, rid_specific=True)
        rid_docs_root = root / "rid-docs"
        for key, payload in rid_payloads.items():
            if key.startswith("candidateReadmeSetup:"):
                rid = key.split(":", 1)[1]
                write(rid_docs_root / "docs-extract" / "candidate-readme-setup" / f"{rid}.md", payload)
        runner.validate_rid_readme_binding(rid_docs, rid_docs_root, rid_archive_records)
        if len({entry["sha256"] for entry in rid_docs["candidateReadmeSetupByRid"].values()}) != 3:
            raise AssertionError("RID-specific README regression fixture must contain three distinct digests")

        def expect_rid_failure(label: str, callback) -> None:
            try:
                callback()
            except runner.RunnerError:
                return
            raise AssertionError(f"RID README negative fixture unexpectedly passed: {label}")

        expect_rid_failure("missing RID", lambda: runner.docs_from_release_tree(repo_root, source, rid_candidate, rid_archive_paths[:2], rid_specific=True))
        expect_rid_failure("duplicate RID", lambda: runner.readme_mapping_from_archives(rid_candidate, [rid_archive_records[0], rid_archive_records[0]]))
        unexpected_record = dict(rid_archive_records[0])
        unexpected_record["targetRid"] = "linux-mips"
        expect_rid_failure("unexpected RID", lambda: runner.readme_mapping_from_archives(rid_candidate, [unexpected_record]))
        swapped_record = dict(rid_archive_records[0])
        swapped_record["targetRid"] = "linux-x64"
        expect_rid_failure("wrong RID mapping", lambda: runner.readme_mapping_from_archives(rid_candidate, [swapped_record]))
        missing_extract = rid_docs_root / "docs-extract" / "candidate-readme-setup" / "linux-arm64.md"
        missing_extract.unlink()
        expect_rid_failure("docs-extract missing RID", lambda: runner.validate_rid_readme_binding(rid_docs, rid_docs_root, rid_archive_records))
        write(rid_docs_root / "docs-extract" / "candidate-readme-setup" / "linux-arm64.md", rid_payloads["candidateReadmeSetup:linux-arm64"])
        unexpected_extract = rid_docs_root / "docs-extract" / "candidate-readme-setup" / "linux-mips.md"
        write(unexpected_extract, b"unexpected RID\n")
        expect_rid_failure("docs-extract unexpected RID", lambda: runner.validate_rid_readme_binding(rid_docs, rid_docs_root, rid_archive_records))
        unexpected_extract.unlink()
        tampered_extract = rid_docs_root / "docs-extract" / "candidate-readme-setup" / "linux-x64.md"
        tampered_extract.write_bytes(tampered_extract.read_bytes() + b"tampered")
        expect_rid_failure("README tampered after bind", lambda: runner.validate_rid_readme_binding(rid_docs, rid_docs_root, rid_archive_records))
        tampered_extract.write_bytes(rid_payloads["candidateReadmeSetup:linux-x64"])
        tampered_mapping = json.loads(json.dumps(rid_docs))
        tampered_mapping["candidateReadmeSetupByRidSha256"] = "0" * 64
        expect_rid_failure("recorded digest tampered", lambda: runner.validate_rid_readme_binding(tampered_mapping, rid_docs_root, rid_archive_records))
        fourth_archive = dict(rid_archive_records[0])
        fourth_archive["targetRid"] = "win-x64-extra"
        expect_rid_failure("unexpected fourth RID", lambda: runner.readme_mapping_from_archives(rid_candidate, rid_archive_records + [fourth_archive]))
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
        bad_plan = root / "bad-plan.md"
        bad_plan.write_text("wrong plan bytes\n", encoding="utf-8")
        inventory_document = {"schemaVersion": 1, "releaseCommitSha": source, "runnerOrderPaths": migration_paths}
        inventory_digest = sha(json.dumps(inventory_document, ensure_ascii=False, sort_keys=True, separators=(",", ":")).encode("utf-8"))
        migration_files = []
        for path in migration_paths[-2:]:
            file_bytes = subprocess.check_output(["git", "-C", str(repo_root), "show", f"{source}:{path}"])
            blob_sha = subprocess.check_output(["git", "-C", str(repo_root), "rev-parse", f"{source}:{path}"], text=True).strip()
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
        run("bind", "--store-root", str(store), "--candidate-id", candidate_id, "--issue-snapshot", str(issue_path), "--plan-file", str(bad_plan), "--plan-commit-sha", source, "--repo-root", str(repo_root), "--migration-pin", str(migration_pin), "--run-attempt-nonce", "bad-plan", "--evidence-owners", str(owners), "--qualification-lead-role", "qualification-lead", "--qualification-lead-identity", "maintainer:test", "--conditional-approver-role", "conditional-approver", "--conditional-approver-identity", "maintainer:conditional", expect=1)
        def actor_for(scenario, variant):
            matches = [entry for entry in owner_entries if entry["scenarioId"] == scenario and entry["variantId"] == variant]
            assert len(matches) == 1
            return matches[0]["ownerRole"], matches[0]["ownerIdentity"]
        bound = json.loads(run("bind", "--store-root", str(store), "--candidate-id", candidate_id, "--issue-snapshot", str(issue_path), "--plan-file", str(plan), "--plan-commit-sha", source, "--repo-root", str(repo_root), "--migration-pin", str(migration_pin), "--run-attempt-nonce", "self-test-1", "--evidence-owners", str(owners), "--qualification-lead-role", "qualification-lead", "--qualification-lead-identity", "maintainer:test", "--conditional-approver-role", "conditional-approver", "--conditional-approver-identity", "maintainer:conditional"))
        run_root = store / "runs" / bound["qualificationRunId"]
        binding = json.loads((run_root / "binding.json").read_text(encoding="utf-8"))
        evidence_counter = 10
        def envelope(evidence_id, scenario, variant, result, evidence_type, payload):
            owner_role, owner_identity = actor_for(scenario, variant)
            validator = runner.HARD_SCENARIO_VALIDATOR_REGISTRY.get(scenario)
            return {
                "schemaVersion": 1, "kind": "release-qualification-evidence", "evidenceType": evidence_type,
                "evidenceId": evidence_id, "candidateId": binding["candidateId"], "sourceCommitSha": binding["releaseCommitSha"],
                "scenarioId": scenario, "variantId": variant, "issueBodySha256": binding["issueBodySha256"], "planRevision": "12",
                "planCommitSha": binding["planCommitSha"], "planFileSha256": binding["planFileSha256"], "bindingId": binding["bindingId"],
                "qualificationRunId": binding["qualificationRunId"], "attempt": 1, "result": result,
                "startedAtUtc": "2026-08-08T00:00:00Z", "finishedAtUtc": "2026-08-08T00:00:01Z",
                "executedByRole": owner_role, "executedByIdentity": owner_identity, "procedureId": validator["procedureId"] if validator else f"self-test-{scenario}",
                "procedureRevision": validator["procedureRevision"] if validator else "1", "runnerClass": "synthetic", "toolVersion": "self-test-1", "attestedAtUtc": "2026-08-08T00:00:01Z",
                "identity": {}, "prohibitedContentScan": {"result": "PASS", "scannerId": "qualify-secret-like/1", "scannerVersion": "1", "reportDigestSha256": "a" * 64},
                "typePayload": payload,
            }
        hard_positive = {
            "G456-01": {"runtimeProfile": "windows-docker-desktop", "freshEnvironment": True, "mailpitReady": True, "mailerStarted": True, "requestAccepted": True, "deliveryObservedValueFree": True, "bundleIdentityMatch": True, "outcome": "completed", "sensitiveOutput": "absent"},
            "G456-02": {"runtimeProfile": "linux-docker-engine", "freshEnvironment": True, "mailpitReady": True, "mailerStarted": True, "requestAccepted": True, "deliveryObservedValueFree": True, "bundleIdentityMatch": True, "outcome": "completed", "sensitiveOutput": "absent"},
            "G456-07": {"accessProfile": "development-loopback", "transportProfile": "http-loopback", "loopbackOnly": True, "loginResult": "success", "setupStatusResult": "visible", "adminRouteResult": "available", "sensitiveOutput": "absent"},
            "G456-08": {"accessProfile": "production-https", "transportProfile": "https", "secureSessionFlag": True, "loginResult": "success", "setupStatusResult": "visible", "adminRouteResult": "available", "deploymentOvConfirmedShown": False, "sensitiveOutput": "absent"},
            "G456-09": {"accessProfile": "production-https", "transportProfile": "http", "secureSessionFlag": True, "httpSessionAccepted": False, "loginResult": "rejected", "adminRouteResult": "unavailable", "httpFallbackAccepted": False, "sensitiveOutput": "absent"},
            "G456-10": {"accessProfile": "production-https", "transportProfile": "http", "amaneAdminAllowHttp": True, "configRejected": True, "adminRouteResult": "unavailable", "outcome": "rejected", "sensitiveOutput": "absent"},
            "G456-11": {"accessProfile": "local-dev", "addressMismatch": True, "httpStatus": 404, "adminRouteResult": "unavailable", "routeExposed": False, "sensitiveOutput": "absent"},
            "G456-12": {"accessProfile": "production-https", "httpsPathAvailable": False, "adminBootstrapResult": "not-presented", "adminEnabled": False, "adminRouteResult": "unavailable", "mainPathResult": "available", "sensitiveOutput": "absent"},
            "G456-13": {"bootstrapProfile": "fresh-bootstrap", "freshInstall": True, "bootstrapResult": "completed", "loginResult": "success", "setupStatusResult": "visible", "bundleIdentityMatch": True, "sendReadyStatusShown": True, "deploymentOvConfirmedShown": False, "sensitiveOutput": "absent"},
            "G456-14": {"accessProfile": "managed", "usernameRelation": "same-user", "reapplyResult": "idempotent", "credentialRotated": False, "statePreserved": True, "routeResult": "available", "sensitiveOutput": "absent"},
            "G456-15": {"accessProfile": "managed", "usernameRelation": "different-user", "credentialRotationAttempt": "rejected", "manualExistingAdmin": "rejected", "reapplyResult": "rejected", "credentialChanged": False, "sensitiveOutput": "absent"},
            "G456-16": {"executionProfile": "automated-fixture", "credentialSyncResult": "completed", "subsequentStepResult": "failed", "configRollbackResult": "completed", "sqliteStateReport": "separate", "adminRouteAfterRollback": "not-exposed", "partialSuccessRecorded": True, "sensitiveOutput": "absent"},
            "G456-17": {"executionMode": "non-interactive", "enableRequestResult": "rejected", "adminEnabled": False, "sensitiveArgument": False, "sensitiveHistory": False, "sensitiveProcessList": False, "sensitiveOutput": "absent"},
            "G456-18": {"failureMode": "apply-failure", "previousBundlePresent": True, "applyResult": "failed", "rollbackResult": "completed", "effectiveStateRestored": True, "integrityMatched": True, "adminRouteAfterRollback": "not-exposed", "rollbackClaimedSuccess": True},
            "G456-19": {"failureMode": "fresh-install-failure", "previousBundlePresent": False, "applyResult": "failed", "rollbackResult": "not-applicable", "rollbackClaimedSuccess": False, "manualInterventionRequired": True, "adminRouteResult": "unavailable", "partialBundleActive": False},
            "G456-20": {"fault": "fingerprint-mismatch", "fingerprintMismatchDetected": True, "verificationResult": "rejected", "activationResult": "blocked", "staleState": "not-activated", "bundleIntegrityMatched": True, "sensitiveOutput": "absent"},
            "G456-21": {"fault": "credential-replacement", "credentialBindingResult": "rejected", "oldCredentialAccepted": False, "otherBundleCredentialAccepted": False, "badMountCredentialAccepted": False, "activationResult": "blocked", "sensitiveOutput": "absent"},
            "G456-22": {"fault": "stale-launcher-image", "launcherIdentityMatch": False, "imageIdentityMatch": False, "verificationResult": "rejected", "activationResult": "blocked", "sensitiveOutput": "absent"},
            "G456-23": {"fault": "remote-docker-context", "dockerContext": "remote", "remoteOperationAttempted": False, "remoteMutation": False, "operationResult": "rejected", "localOnlyEnforced": True, "sensitiveOutput": "absent"},
            "G456-24": {"fault": "command-injection", "injectionAttempted": True, "inputRejected": True, "commandExecution": "not-executed", "shellSpawned": False, "environmentMutation": False, "sensitiveOutput": "absent"},
            "G456-25": {"fault": "path-traversal", "traversalAttempted": True, "inputRejected": True, "pathResolution": "rejected", "fileReadOutsideRoot": False, "fileWriteOutsideRoot": False, "sensitiveOutput": "absent"},
            "G456-26": {"fault": "symlink-reparse", "filesystemObject": "symlink", "objectDetected": True, "followed": False, "operationResult": "rejected", "outsideRootAccess": False, "sensitiveOutput": "absent"},
            "G456-27": {"fault": "concurrent-setup", "concurrentRequests": 2, "winnerCount": 1, "loserResult": "serialized", "duplicateApply": False, "stateConsistent": True, "activeGenerationUnique": True, "sensitiveOutput": "absent"},
            "G456-28": {"fault": "crash-cancel-recovery", "recoveryTrigger": "crash", "recoveryResult": "resumed", "partialActivation": False, "stateConsistent": True, "recoveryRecordValueFree": True, "adminRouteResult": "unavailable", "sensitiveOutput": "absent"},
            "G456-30": {"fault": "web-security", "requestCredentialPolicy": "enforced", "originPolicy": "enforced", "hostPolicy": "enforced", "csrfPolicy": "enforced", "unauthorizedResult": "rejected", "crossOriginAdminAccess": False, "sensitiveOutput": "absent"},
            "G456-31": {"scanTarget": "qualification-output", "sensitiveScan": "clean", "deliveryAddressValue": "absent", "providerErrorOutput": "absent", "hostPathOutput": "absent", "credentialValue": "absent", "outputResult": "value-free"},
            "G456-32": {"accessProfile": "admin-status", "authenticationRequired": True, "authorizationRequired": True, "unauthenticatedResult": "rejected", "wrongAddressStatus": 404, "authorizedStatus": "value-free", "statusRouteExposed": True, "sensitiveOutput": "absent"},
            "G456-33": {"executionMode": "terminal-non-interactive", "sensitiveArgument": False, "sensitiveHistory": False, "sensitiveProcessList": False, "inputBoundaryResult": "rejected", "interactivePromptShown": False, "outputResult": "value-free", "sensitiveOutput": "absent"},
            "G456-35": {"targetRid": "linux-arm64", "artifactSourceCommitMatch": True, "artifactIntegrityMatch": True, "startupSmoke": "passed", "helpCommand": "passed", "aotBinary": True, "runtimeIdentityMatch": True, "outputResult": "value-free", "sensitiveOutput": "absent"},
        }
        hard_fail_mutations = {
            "G456-01": ("outcome", "failed"), "G456-02": ("outcome", "failed"), "G456-07": ("loginResult", "rejected"),
            "G456-08": ("secureSessionFlag", False), "G456-09": ("httpSessionAccepted", True), "G456-10": ("outcome", "accepted"),
            "G456-11": ("httpStatus", 200), "G456-12": ("adminEnabled", True), "G456-13": ("bootstrapResult", "failed"),
            "G456-14": ("reapplyResult", "rejected"), "G456-15": ("credentialRotationAttempt", "accepted"), "G456-16": ("credentialSyncResult", "failed"),
            "G456-17": ("enableRequestResult", "accepted"), "G456-18": ("rollbackResult", "failed"), "G456-19": ("partialBundleActive", True),
            "G456-20": ("verificationResult", "accepted"), "G456-21": ("oldCredentialAccepted", True), "G456-22": ("launcherIdentityMatch", True),
            "G456-23": ("remoteOperationAttempted", True), "G456-24": ("commandExecution", "executed"), "G456-25": ("pathResolution", "resolved"),
            "G456-26": ("followed", True), "G456-27": ("winnerCount", 2), "G456-28": ("recoveryResult", "unsafe"),
            "G456-30": ("originPolicy", "bypassed"), "G456-31": ("sensitiveScan", "findings"), "G456-32": ("wrongAddressStatus", 200),
            "G456-33": ("sensitiveArgument", True), "G456-35": ("startupSmoke", "failed"),
        }
        def payload_for(scenario, variant, result):
            if scenario in hard_positive:
                payload = copy.deepcopy(hard_positive[scenario])
                if scenario == "G456-11":
                    payload["accessProfile"] = variant
                if scenario == "G456-16":
                    payload["executionProfile"] = "automated-fixture" if variant == "ci-auto" else "integrated-follow-on-failure"
                if scenario == "G456-26":
                    payload["filesystemObject"] = "reparse-point" if variant == "win-docker" else "symlink"
                if result == "FAIL":
                    field, value = hard_fail_mutations[scenario]
                    payload[field] = value
                return payload
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
        add_evidence(first_id, "G456-29", "win-docker", "FAIL", "qualification-scenario")
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
        # Every v1.3 dedicated validator must accept a semantically failing
        # evidence payload too.  Dispatch is exercised directly against the
        # v1.3 scope rows so the legacy profile remains fail-closed below.
        for scenario in sorted(runner.HARD_SCENARIO_VALIDATOR_REGISTRY):
            number = int(scenario[5:])
            for variant in variants[number]:
                row = next(row for row in profile["scenarioRows"] if row["scenarioId"] == scenario)
                spec_for_scenario = runner.HARD_SCENARIO_VALIDATOR_REGISTRY[scenario]
                positive_payload = payload_for(scenario, variant, "PASS")
                positive_envelope = {"evidenceType": runner.EVIDENCE_TYPES[scenario][0], "procedureId": spec_for_scenario["procedureId"], "procedureRevision": spec_for_scenario["procedureRevision"], "typePayload": positive_payload, "result": "PASS", "variantId": variant}
                runner.validate_registered_hard_payload(positive_envelope, row, spec_for_scenario)
                failed_payload = payload_for(scenario, variant, "FAIL")
                failed_envelope = {**positive_envelope, "result": "FAIL", "typePayload": failed_payload}
                runner.validate_registered_hard_payload(failed_envelope, row, spec_for_scenario)
        # A typed v1.3 payload must not silently become a legacy #456 PASS.
        legacy_typed_pass = root / "legacy-typed-pass.json"
        write(legacy_typed_pass, envelope("a" * 64, "G456-10", "admin-prod-https", "PASS", "qualification-scenario", payload_for("G456-10", "admin-prod-https", "PASS")))
        legacy_role, legacy_identity = actor_for("G456-10", "admin-prod-https")
        run("evidence", "--run-root", str(run_root), "--evidence-id", "a" * 64, "--scenario-id", "G456-10", "--variant-id", "admin-prod-https", "--result", "PASS", "--executed-by-role", legacy_role, "--executed-by-identity", legacy_identity, "--observations", str(legacy_typed_pass), expect=1)
        def reject_hard_fixture(label, mutate, cli_scenario="G456-10", cli_variant="admin-prod-https", cli_actor=None):
            nonlocal next_id
            eid = f"{next_id:064x}"; next_id += 1
            item = envelope(eid, "G456-10", "admin-prod-https", "PASS", "qualification-scenario", payload_for("G456-10", "admin-prod-https", "PASS"))
            mutate(item)
            path = root / f"negative-{label}.json"
            write(path, item)
            role, identity = cli_actor or actor_for(cli_scenario, cli_variant)
            run("evidence", "--run-root", str(run_root), "--evidence-id", eid, "--scenario-id", cli_scenario, "--variant-id", cli_variant, "--result", "PASS", "--executed-by-role", role, "--executed-by-identity", identity, "--observations", str(path), expect=1)

        reject_hard_fixture("scenario-id", lambda item: item.update({"scenarioId": "G456-11"}), "G456-11", "proxy-https")
        reject_hard_fixture("variant-id", lambda item: item.update({"variantId": "admin-prod-https"}), "G456-09", "admin-prod-https")
        reject_hard_fixture("owner", lambda item: item.update({"executedByRole": "wrong-owner", "executedByIdentity": "ci:wrong"}))
        reject_hard_fixture("procedure", lambda item: item.update({"procedureId": "wrong-procedure"}))
        reject_hard_fixture("missing-predicate", lambda item: item["typePayload"].pop("amaneAdminAllowHttp"))
        reject_hard_fixture("unexpected-predicate", lambda item: item["typePayload"].update({"allChecksPassed": True}))
        reject_hard_fixture("wrong-type", lambda item: item["typePayload"].update({"configRejected": "true"}))
        reject_hard_fixture("contradictory-pass", lambda item: item.update({"typePayload": payload_for("G456-10", "admin-prod-https", "FAIL")}))
        reject_hard_fixture("contradictory-fail", lambda item: item.update({"result": "FAIL"}))
        reject_hard_fixture("tampered-scan", lambda item: item["prohibitedContentScan"].update({"reportDigestSha256": "z" * 64}))
        for field, value in (("candidateId", "0" * 64), ("sourceCommitSha", "0" * 40), ("bindingId", "0" * 64), ("qualificationRunId", "0" * 64)):
            reject_hard_fixture(f"identity-{field}", lambda item, field=field, value=value: item.update({field: value}))
        bad_row = copy.deepcopy(next(row for row in binding["rows"] if row["scenarioId"] == "G456-10"))
        bad_row["predicateSet"] = "legacy-g456-wrong"
        try:
            runner.validate_registered_hard_payload(
                envelope("f" * 64, "G456-10", "admin-prod-https", "PASS", "qualification-scenario", payload_for("G456-10", "admin-prod-https", "PASS")),
                bad_row,
                runner.HARD_SCENARIO_VALIDATOR_REGISTRY["G456-10"],
            )
        except runner.RunnerError:
            pass
        else:
            raise AssertionError("wrong predicateSet was accepted")
        # A generic predicateResult must not bypass a dedicated validator.
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
        run("seal", "--run-root", str(run_root), "--current-issue-snapshot", str(issue_path), "--repo-root", str(repo_root), "--human-decision", "NOT_DECIDED", "--approved-by-role", "qualification-lead", "--approved-by-identity", "maintainer:test")
        verify = json.loads(run("verify", "--run-root", str(run_root), "--repo-root", str(repo_root)))
        assert verify["machineVerdict"] == "NO_GO"
        run("handoff", "--run-root", str(run_root), "--output-root", str(handoff), "--repo-root", str(repo_root), expect=1)
        sealed_event_path = next((run_root / "run-status-events").glob("*.json"))
        sealed_event = json.loads(sealed_event_path.read_text(encoding="utf-8"))
        sealed_event["canonicalization"] = {"algorithm": "not-jcs", "version": 1}
        unsigned_event = {key: value for key, value in sealed_event.items() if key != "eventDigestSha256"}
        sealed_event["eventDigestSha256"] = sha(json.dumps(unsigned_event, ensure_ascii=False, sort_keys=True, separators=(",", ":")).encode("utf-8"))
        write(sealed_event_path, sealed_event)
        run("verify", "--run-root", str(run_root), "--repo-root", str(repo_root), expect=1)

        # Sealed runs reject new evidence and value-bearing observations.
        run("evidence", "--run-root", str(run_root), "--evidence-id", "9" * 64, "--scenario-id", "G456-01", "--variant-id", "win-docker", "--result", "PASS", "--executed-by-role", "lane-owner", "--executed-by-identity", "ci:test", expect=1)
        bound2 = json.loads(run("bind", "--store-root", str(store), "--candidate-id", candidate_id, "--issue-snapshot", str(issue_path), "--plan-file", str(plan), "--plan-commit-sha", source, "--repo-root", str(repo_root), "--migration-pin", str(migration_pin), "--run-attempt-nonce", "self-test-2", "--evidence-owners", str(owners), "--qualification-lead-role", "qualification-lead", "--qualification-lead-identity", "maintainer:test", "--conditional-approver-role", "conditional-approver", "--conditional-approver-identity", "maintainer:conditional"))
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
