#!/usr/bin/env python3
"""Fail-closed MIG01/MIG02 Docker-fixture adapter for G583 S5-A.

This adapter is deliberately separate from the legacy G456 lane adapter.  It
accepts only one structured result produced by the checked-in Docker fixture,
revalidates the corrected Core platform/OCI contract, and emits value-free
observations.  It does not write qualification evidence, dispatch candidates,
or execute a formal qualification run.
"""

from __future__ import annotations

import argparse
import copy
import importlib.util
import json
import re
import sys
from pathlib import Path
from typing import Any


ROOT = Path(__file__).resolve().parent.parent
SCRIPTS = ROOT / "scripts"
MANIFEST_PATH = SCRIPTS / "qualification-g583-migration-adapter-manifest.json"
CORE_DISPATCH_PATH = SCRIPTS / "qualification-g583-dispatch.py"
HEX64 = re.compile(r"^[0-9a-f]{64}$")
SHA40 = re.compile(r"^[0-9a-f]{40}$")
SHA256 = re.compile(r"^sha256:[0-9a-f]{64}$")
SAFE_ID = re.compile(r"^[A-Za-z0-9][A-Za-z0-9._-]{0,127}$")

BASELINE_TAG = "v1.2.0"
BASELINE_COMMIT = "c173db1d03725e754c4432d02b7c43ceed98c3c0"
BASELINE_DIGEST = "sha256:ded98629afda63d1f736807cc942e5d92c6cdf08cfc33beba2f2b277d19b2759"
BASELINE_FILES = [
    "001_initial.sql",
    "002_worker_heartbeats.sql",
    "003_admin_indexes.sql",
    "004_admin_audit_events.sql",
    "005_admin_session_and_throttle.sql",
    "006_admin_users_and_tenant_scopes.sql",
    "007_mail_request_cancelled_status.sql",
    "008_delivery_events.sql",
    "009_mail_request_scheduled_at.sql",
    "010_admin_audit_events_tenant_id.sql",
    "011_bounce_ingestion.sql",
    "012_provider_event_inbox_details.sql",
    "013_provider_queue_dead_letters.sql",
]
DELTA_FILES = [
    "014_mail_request_delivery_unknown_status.sql",
    "015_attachment_spool_and_submission_evidence.sql",
    "016_recipient_persistence_and_plain_submission_evidence.sql",
    "017_recipient_delivery_events.sql",
    "018_admin_user_capabilities.sql",
]
FULL_FILES = BASELINE_FILES + DELTA_FILES
BASELINE_CHECKSUMS = {
    "001_initial.sql": "3a0466fb82710e3c51037fcab5eaaae4a923bc756793da6902713540c9a7d0a6",
    "002_worker_heartbeats.sql": "b6a1136d99aa93f0acb46866bdbe3e9b08aa5952a6a3ab8661b93df97a456a0d",
    "003_admin_indexes.sql": "540bd76eac05274f5f82912a4cd9433fd336816556a8ad1ef2caa00d723a4233",
    "004_admin_audit_events.sql": "ac32a2431e3dfe9ca777c0f8d38c3738590876bd1d04a63678ada9629a04de93",
    "005_admin_session_and_throttle.sql": "30ab19553844c1ecefe7a6674582c3f3958284a3e3205422feeae71f19ce70d0",
    "006_admin_users_and_tenant_scopes.sql": "0f542657086a76e3962726f1524a091578469edd0178c9caf60ad1c63c1d5a64",
    "007_mail_request_cancelled_status.sql": "0695f6dd8fa389bede90d8678d3251ce56ee03e1ef05317ebdebd940b28bf319",
    "008_delivery_events.sql": "fdb6aa145d7e18be3da0fb3a71fe8a983729d43d3451246faa2165a77fae875a",
    "009_mail_request_scheduled_at.sql": "79c84b2d93c497a38f0c3c2ffce5182c11e26d45f521f39f518a58d4d1f9f8c9",
    "010_admin_audit_events_tenant_id.sql": "2a03c43f86680db94ff5a597beb0d232d89a80dda415c40c51761b4ff0da6151",
    "011_bounce_ingestion.sql": "40fa915a34eacc6f17b2de11bc981717054a1a034d1c426b9389ebb2567e56ad",
    "012_provider_event_inbox_details.sql": "1a81b1cdc5cda8ac64772fb09f48461bdb3254b8a8c5835b8393effb8dee1d6a",
    "013_provider_queue_dead_letters.sql": "c451149acf9679fd9ee1add927905b4a705298a08588fb6a0764fdc7a817c368",
}
DELTA_CHECKSUMS = {
    "014_mail_request_delivery_unknown_status.sql": "db4ffe02069d0b899958f191a4de5cbae4327ea1cee14892cda268f61edb31f6",
    "015_attachment_spool_and_submission_evidence.sql": "f5cd7f7b885bab55fb77ff630c6ee42ce2094e22d5fe1615123d2cc4b2fdd7f8",
    "016_recipient_persistence_and_plain_submission_evidence.sql": "c95e5b5c2d7b3ac52ab7ce6afc591f0a0e97aac59a151b9476bfca751dccc0c5",
    "017_recipient_delivery_events.sql": "4e7f15fb61bc1bccd0386fecb3d267c23b0ce6044c87f7999c8fe1a74a1b2bdb",
    "018_admin_user_capabilities.sql": "94af8770dec3a0e0ec925ce6a1946ad73f51f564e7137f2d82934b4fffb7f471",
}
ROUTE_KEYS = {
    ("G583-MIG-01", "win-docker", "g583-s5a-platform-v1"),
    ("G583-MIG-01", "linux-docker", "g583-s5a-platform-v1"),
    ("G583-MIG-02", "win-docker", "g583-s5a-platform-v1"),
    ("G583-MIG-02", "linux-docker", "g583-s5a-platform-v1"),
}


class AdapterError(Exception):
    """Expected, safe fail-closed rejection."""


def fail(message: str) -> None:
    raise AdapterError(message)


def read_json(path: Path, label: str) -> Any:
    if not path.is_file() or path.is_symlink():
        fail(f"{label}: missing or symlink")
    try:
        return json.loads(path.read_text(encoding="utf-8"))
    except (OSError, UnicodeError, json.JSONDecodeError) as exc:
        raise AdapterError(f"{label}: invalid JSON") from exc


def require_object(value: Any, label: str) -> dict[str, Any]:
    if not isinstance(value, dict):
        fail(f"{label}: object required")
    return value


def require_fields(value: dict[str, Any], required: set[str], allowed: set[str], label: str) -> None:
    missing = sorted(required - set(value))
    unknown = sorted(set(value) - allowed)
    if missing:
        fail(f"{label}: missing fields: {','.join(missing)}")
    if unknown:
        fail(f"{label}: unknown fields: {','.join(unknown)}")


def require_string(value: Any, label: str) -> str:
    if not isinstance(value, str) or not value:
        fail(f"{label}: non-empty string required")
    return value


def require_safe_id(value: Any, label: str) -> str:
    text = require_string(value, label)
    if not SAFE_ID.fullmatch(text):
        fail(f"{label}: invalid identifier")
    return text


def require_digest(value: Any, label: str) -> str:
    text = require_string(value, label)
    if not SHA256.fullmatch(text):
        fail(f"{label}: SHA-256 OCI digest required")
    return text


def require_hex(value: Any, label: str, pattern: re.Pattern[str]) -> str:
    text = require_string(value, label)
    if not pattern.fullmatch(text):
        fail(f"{label}: invalid digest")
    return text


def require_inventory(value: Any, expected_files: list[str], label: str) -> list[dict[str, str]]:
    if not isinstance(value, list) or len(value) != len(expected_files):
        fail(f"{label}: exact migration inventory required")
    inventory: list[dict[str, str]] = []
    for index, raw in enumerate(value):
        entry = require_object(raw, f"{label}[{index}]")
        require_fields(entry, {"fileName", "sha256"}, {"fileName", "sha256"}, f"{label}[{index}]")
        file_name = require_string(entry.get("fileName"), f"{label}[{index}].fileName")
        if file_name != expected_files[index]:
            fail(f"{label}: migration ordering or file set mismatch")
        inventory.append({"fileName": file_name, "sha256": require_hex(entry.get("sha256"), f"{label}[{index}].sha256", HEX64)})
    return inventory


def inventory_names(inventory: list[dict[str, str]]) -> list[str]:
    return [entry["fileName"] for entry in inventory]


def inventory_checksums(inventory: list[dict[str, str]]) -> dict[str, str]:
    return {entry["fileName"]: entry["sha256"] for entry in inventory}


def load_core() -> Any:
    spec = importlib.util.spec_from_file_location("qualification_g583_dispatch_for_migration_adapter", CORE_DISPATCH_PATH)
    if spec is None or spec.loader is None:
        fail("corrected Core dispatcher could not be loaded")
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


def validate_manifest(raw: Any) -> dict[str, Any]:
    manifest = require_object(raw, "migration adapter manifest")
    required = {
        "schemaVersion", "adapterId", "adapterRevision", "adapterScript", "fixtureProducerScript",
        "fixtureContractVersion", "coreDispatchScript", "coreDispatchManifest", "g456Isolation",
        "baselineAuthority", "candidateDeltaInventory", "routes",
    }
    require_fields(manifest, required, required, "migration adapter manifest")
    if (
        manifest.get("schemaVersion") != 1
        or manifest.get("adapterId") != "g583-s5a-migration-docker-adapter-v1"
        or manifest.get("adapterRevision") != "1"
        or manifest.get("adapterScript") != Path(__file__).name
        or manifest.get("fixtureProducerScript") != "qualification-g583-migration-fixture-producer.py"
        or manifest.get("fixtureContractVersion") != 1
        or manifest.get("coreDispatchScript") != CORE_DISPATCH_PATH.name
        or manifest.get("coreDispatchManifest") != "qualification-g583-dispatch-manifest.json"
    ):
        fail("migration adapter manifest: fixed adapter identity mismatch")
    if manifest.get("g456Isolation") != {
        "scenarioPrefix": "G456-",
        "managedBy": "qualification-lane-adapter.py",
        "behavioralChange": "none",
    }:
        fail("migration adapter manifest: G456 isolation boundary mismatch")

    baseline = require_object(manifest.get("baselineAuthority"), "baselineAuthority")
    require_fields(
        baseline,
        {"releaseTag", "releaseCommitSha", "ociIndexDigest", "migrationInventory"},
        {"releaseTag", "releaseCommitSha", "ociIndexDigest", "migrationInventory"},
        "baselineAuthority",
    )
    if (
        baseline.get("releaseTag") != BASELINE_TAG
        or baseline.get("releaseCommitSha") != BASELINE_COMMIT
        or baseline.get("ociIndexDigest") != BASELINE_DIGEST
    ):
        fail("migration adapter manifest: v1.2 baseline authority mismatch")
    baseline_inventory = require_inventory(baseline.get("migrationInventory"), BASELINE_FILES, "baselineAuthority.migrationInventory")
    delta_inventory = require_inventory(manifest.get("candidateDeltaInventory"), DELTA_FILES, "candidateDeltaInventory")
    if inventory_checksums(baseline_inventory) != BASELINE_CHECKSUMS:
        fail("migration adapter manifest: authoritative v1.2 baseline checksum mismatch")
    if inventory_checksums(delta_inventory) != DELTA_CHECKSUMS:
        fail("migration adapter manifest: v1.3 014..018 checksum authority mismatch")

    routes = manifest.get("routes")
    if not isinstance(routes, list) or len(routes) != len(ROUTE_KEYS):
        fail("migration adapter manifest: exactly four MIG01/MIG02 Docker routes are required")
    route_fields = {"scenarioId", "variantId", "contractVersion", "fixtureId", "sourceTestId", "migrationMode"}
    normalized_routes: list[dict[str, str]] = []
    keys: set[tuple[str, str, str]] = set()
    for index, raw_route in enumerate(routes):
        route = require_object(raw_route, f"routes[{index}]")
        require_fields(route, route_fields, route_fields, f"routes[{index}]")
        scenario = require_safe_id(route.get("scenarioId"), "scenarioId")
        variant = require_safe_id(route.get("variantId"), "variantId")
        contract = require_safe_id(route.get("contractVersion"), "contractVersion")
        key = (scenario, variant, contract)
        if key in keys or key not in ROUTE_KEYS:
            fail("migration adapter manifest: unsupported or duplicate route")
        keys.add(key)
        fixture_id = require_safe_id(route.get("fixtureId"), "fixtureId")
        source_test = require_string(route.get("sourceTestId"), "sourceTestId")
        expected_source = f"Amane.Mailer.Tests.Qualification.G583MigrationDockerFixtureTests.Qualification_fixture_{scenario.replace('-', '_')}_{variant.replace('-', '_')}"
        if source_test != expected_source or not source_test.startswith("Amane.Mailer.Tests.Qualification."):
            fail("migration adapter manifest: fixture source test identity mismatch")
        mode = route.get("migrationMode")
        expected_mode = "fresh" if scenario == "G583-MIG-01" else "upgrade"
        expected_fixture = f"g583-mig-{scenario.rsplit('-', 1)[1]}-{variant}"
        if mode != expected_mode or fixture_id != expected_fixture:
            fail("migration adapter manifest: fixture operation identity mismatch")
        normalized_routes.append({
            "scenarioId": scenario,
            "variantId": variant,
            "contractVersion": contract,
            "fixtureId": fixture_id,
            "sourceTestId": source_test,
            "migrationMode": mode,
        })
    if keys != ROUTE_KEYS:
        fail("migration adapter manifest: route set mismatch")
    return {
        **manifest,
        "baselineAuthority": {**baseline, "migrationInventory": baseline_inventory},
        "candidateDeltaInventory": delta_inventory,
        "routes": normalized_routes,
    }


def load_manifest(path: Path = MANIFEST_PATH) -> dict[str, Any]:
    return validate_manifest(read_json(path, "migration adapter manifest"))


def route_for(manifest: dict[str, Any], scenario_id: Any, variant_id: Any, contract_version: Any) -> dict[str, str]:
    scenario = require_safe_id(scenario_id, "scenarioId")
    variant = require_safe_id(variant_id, "variantId")
    contract = require_safe_id(contract_version, "contractVersion")
    if scenario.startswith("G456-"):
        fail("G456 scenario is isolated from the G583 migration adapter")
    matches = [
        route for route in manifest["routes"]
        if (route["scenarioId"], route["variantId"], route["contractVersion"]) == (scenario, variant, contract)
    ]
    if len(matches) != 1:
        fail("scenarioId/variantId/contractVersion is not a registered MIG01/MIG02 Docker route")
    return matches[0]


def validate_authority(raw: Any) -> dict[str, Any]:
    authority = require_object(raw, "artifactAuthority")
    required = {"candidateId", "releaseCommitSha", "ociIndexDigest", "selectedManifests"}
    allowed = required | {"candidateImageReference", "baselineImageReference"}
    require_fields(authority, required, allowed, "artifactAuthority")
    candidate_id = require_hex(authority.get("candidateId"), "artifactAuthority.candidateId", HEX64)
    release = require_hex(authority.get("releaseCommitSha"), "artifactAuthority.releaseCommitSha", SHA40)
    index = require_digest(authority.get("ociIndexDigest"), "artifactAuthority.ociIndexDigest")
    selected = require_object(authority.get("selectedManifests"), "artifactAuthority.selectedManifests")
    if set(selected) != {"linux/amd64", "linux/arm64"}:
        fail("artifactAuthority.selectedManifests: exact linux/amd64 and linux/arm64 map required")
    selected_manifests = {platform: require_digest(digest, f"artifactAuthority.selectedManifests.{platform}") for platform, digest in selected.items()}
    result: dict[str, Any] = {
        "candidateId": candidate_id,
        "releaseCommitSha": release,
        "ociIndexDigest": index,
        "selectedManifests": selected_manifests,
    }
    for image_field, expected_digest in (("candidateImageReference", index), ("baselineImageReference", BASELINE_DIGEST)):
        value = authority.get(image_field)
        if value is None:
            continue
        reference = require_string(value, f"artifactAuthority.{image_field}")
        if not reference.endswith("@" + expected_digest):
            fail(f"artifactAuthority.{image_field}: image reference must be index-digest pinned")
        result[image_field] = reference
    return result


def require_name_list(value: Any, expected: list[str], label: str) -> list[str]:
    if not isinstance(value, list) or any(not isinstance(item, str) for item in value) or value != expected:
        fail(f"{label}: exact ordered migration list required")
    return list(value)


def validate_checksums(value: Any, inventory: list[dict[str, str]], label: str) -> dict[str, str]:
    checksums = require_object(value, label)
    expected = inventory_checksums(inventory)
    if set(checksums) != set(expected):
        fail(f"{label}: migration checksum file set mismatch")
    for name, checksum in expected.items():
        if checksums.get(name) != checksum:
            fail(f"{label}: migration checksum mismatch")
    return dict(expected)


def validate_baseline(value: Any, baseline: dict[str, Any]) -> None:
    actual = require_object(value, "migration.baseline")
    required = {"releaseTag", "releaseCommitSha", "ociIndexDigest", "inventory"}
    require_fields(actual, required, required, "migration.baseline")
    if (
        actual.get("releaseTag") != baseline["releaseTag"]
        or actual.get("releaseCommitSha") != baseline["releaseCommitSha"]
        or actual.get("ociIndexDigest") != baseline["ociIndexDigest"]
    ):
        fail("migration.baseline: authoritative v1.2 identity mismatch")
    if actual.get("inventory") != inventory_names(baseline["migrationInventory"]):
        fail("migration.baseline: exact v1.2 001..013 inventory required")


def validate_migration(raw: Any, route: dict[str, str], manifest: dict[str, Any]) -> dict[str, Any]:
    migration = require_object(raw, "migration")
    fields = {
        "initialDatabaseState", "migrationService", "migrationCommand", "beforeInventory", "appliedInventory",
        "finalInventory", "checksums", "missingMigrations", "unexpectedMigrations", "pendingMigrations",
        "lastApplied", "currentSchemaReady", "candidateArtifactIdentity", "baseline",
    }
    require_fields(migration, fields, fields, "migration")
    baseline = manifest["baselineAuthority"]
    base_names = inventory_names(baseline["migrationInventory"])
    delta_names = inventory_names(manifest["candidateDeltaInventory"])
    full_inventory = baseline["migrationInventory"] + manifest["candidateDeltaInventory"]
    full_names = inventory_names(full_inventory)
    if migration.get("migrationService") != "mailer-migrate" or migration.get("migrationCommand") != ["db", "migrate"]:
        fail("migration: candidate mailer-migrate db migrate operation required")
    if migration.get("missingMigrations") != [] or migration.get("unexpectedMigrations") != [] or migration.get("pendingMigrations") != []:
        fail("migration: missing, unexpected, or pending migration remains")
    if migration.get("lastApplied") != "018_admin_user_capabilities.sql":
        fail("migration: lastApplied must be 018_admin_user_capabilities.sql")
    if migration.get("currentSchemaReady") is not True or migration.get("candidateArtifactIdentity") is not True:
        fail("migration: candidate schema or artifact identity did not pass")
    validate_checksums(migration.get("checksums"), full_inventory, "migration.checksums")
    if route["migrationMode"] == "fresh":
        if migration.get("initialDatabaseState") != "absent" or migration.get("baseline") is not None:
            fail("MIG01: fresh database and no v1.2 baseline record required")
        require_name_list(migration.get("beforeInventory"), [], "MIG01.beforeInventory")
        require_name_list(migration.get("appliedInventory"), full_names, "MIG01.appliedInventory")
    else:
        if migration.get("initialDatabaseState") != "v1.2.0-001..013":
            fail("MIG02: authoritative v1.2 database baseline required")
        validate_baseline(migration.get("baseline"), baseline)
        require_name_list(migration.get("beforeInventory"), base_names, "MIG02.beforeInventory")
        require_name_list(migration.get("appliedInventory"), delta_names, "MIG02.appliedInventory")
    require_name_list(migration.get("finalInventory"), full_names, "migration.finalInventory")
    return {
        "initialDatabaseState": migration["initialDatabaseState"],
        "beforeInventory": list(migration["beforeInventory"]),
        "appliedInventory": list(migration["appliedInventory"]),
        "finalInventory": list(migration["finalInventory"]),
        "lastApplied": migration["lastApplied"],
        "checksumVerification": "PASS",
        "pendingMigrations": [],
    }


def validate_fixture_report(raw: Any, authority_raw: Any, manifest: dict[str, Any], core: Any | None = None) -> tuple[dict[str, Any], dict[str, str], dict[str, Any]]:
    report = require_object(raw, "fixture report")
    fields = {
        "schemaVersion", "kind", "fixtureId", "fixtureRevision", "scenarioId", "variantId", "laneVariant",
        "contractVersion", "result", "operationExitCode", "platform", "artifactIdentity", "migration",
    }
    require_fields(report, fields, fields, "fixture report")
    if report.get("schemaVersion") != 1 or report.get("kind") != "g583-migration-docker-fixture" or report.get("fixtureRevision") != "1":
        fail("fixture report: schema or fixture contract mismatch")
    if report.get("result") != "PASS" or report.get("operationExitCode") != 0:
        fail("fixture report: candidate Docker migration operation did not pass")
    route = route_for(manifest, report.get("scenarioId"), report.get("variantId"), report.get("contractVersion"))
    if report.get("laneVariant") != route["variantId"] or report.get("fixtureId") != route["fixtureId"]:
        fail("fixture report: route, lane, or fixture identity mismatch")
    authority = validate_authority(authority_raw)
    platform = require_object(report.get("platform"), "platform")
    platform_fields = {"hostPlatform", "dockerEngineOS", "containerPlatform", "measurements"}
    require_fields(platform, platform_fields, platform_fields, "platform")
    evidence = {
        "scenarioId": route["scenarioId"],
        "variantId": route["variantId"],
        "laneVariant": report["laneVariant"],
        "contractVersion": route["contractVersion"],
        "hostPlatform": platform.get("hostPlatform"),
        "dockerEngineOS": platform.get("dockerEngineOS"),
        "containerPlatform": platform.get("containerPlatform"),
        "measurements": platform.get("measurements"),
        "artifactIdentity": report.get("artifactIdentity"),
    }
    core_module = core or load_core()
    core_manifest = core_module.load_manifest()
    try:
        core_module.validate_evidence(evidence, authority, core_manifest)
    except core_module.DispatchError as exc:
        raise AdapterError("fixture report: corrected Core platform/identity contract rejected evidence") from exc
    observations = validate_migration(report.get("migration"), route, manifest)
    return observations, route, evidence


def validate_and_build_observations(
    evidence: Any,
    artifactAuthority: Any,
    registration: Any | None = None,
    *,
    manifest: dict[str, Any] | None = None,
) -> dict[str, Any]:
    """Core-manifest-compatible callable for the G583 S5-A adapter interface."""
    active_manifest = manifest or load_manifest()
    observations, route, core_evidence = validate_fixture_report(evidence, artifactAuthority, active_manifest)
    if registration is not None:
        supplied = require_object(registration, "registration")
        key = tuple(supplied.get(field) for field in ("scenarioId", "variantId", "contractVersion"))
        expected = (route["scenarioId"], route["variantId"], route["contractVersion"])
        if key != expected:
            fail("registration does not match fixture route")
    return {
        "schemaVersion": 1,
        "kind": "g583-migration-qualification-observations",
        "scenarioId": route["scenarioId"],
        "variantId": route["variantId"],
        "contractVersion": route["contractVersion"],
        "result": "PASS",
        "platformContractResult": "PASS",
        "artifactIdentityResult": "PASS",
        "migration": observations,
        "selectedManifestDigest": core_evidence["artifactIdentity"]["selectedManifestDigest"],
    }


def valid_authority() -> dict[str, Any]:
    return {
        "candidateId": "a" * 64,
        "releaseCommitSha": "b" * 40,
        "ociIndexDigest": "sha256:" + "c" * 64,
        "selectedManifests": {"linux/amd64": "sha256:" + "d" * 64, "linux/arm64": "sha256:" + "e" * 64},
        "candidateImageReference": "example.invalid/amane@sha256:" + "c" * 64,
        "baselineImageReference": "example.invalid/amane@" + BASELINE_DIGEST,
    }


def valid_report(manifest: dict[str, Any], scenario: str = "G583-MIG-01", variant: str = "win-docker") -> dict[str, Any]:
    authority = valid_authority()
    route = route_for(manifest, scenario, variant, "g583-s5a-platform-v1")
    host = "windows-x64" if variant == "win-docker" else "linux-x64"
    host_probe = {"windows-x64": {"os": "windows", "architecture": "amd64"}, "linux-x64": {"os": "linux", "architecture": "amd64"}}[host]
    base_inventory = manifest["baselineAuthority"]["migrationInventory"]
    delta_inventory = manifest["candidateDeltaInventory"]
    full_inventory = base_inventory + delta_inventory
    is_upgrade = route["migrationMode"] == "upgrade"
    migration: dict[str, Any] = {
        "initialDatabaseState": "v1.2.0-001..013" if is_upgrade else "absent",
        "migrationService": "mailer-migrate",
        "migrationCommand": ["db", "migrate"],
        "beforeInventory": inventory_names(base_inventory) if is_upgrade else [],
        "appliedInventory": inventory_names(delta_inventory) if is_upgrade else inventory_names(full_inventory),
        "finalInventory": inventory_names(full_inventory),
        "checksums": inventory_checksums(full_inventory),
        "missingMigrations": [],
        "unexpectedMigrations": [],
        "pendingMigrations": [],
        "lastApplied": "018_admin_user_capabilities.sql",
        "currentSchemaReady": True,
        "candidateArtifactIdentity": True,
        "baseline": {
            "releaseTag": BASELINE_TAG,
            "releaseCommitSha": BASELINE_COMMIT,
            "ociIndexDigest": BASELINE_DIGEST,
            "inventory": inventory_names(base_inventory),
        } if is_upgrade else None,
    }
    selected = authority["selectedManifests"]["linux/amd64"]
    return {
        "schemaVersion": 1,
        "kind": "g583-migration-docker-fixture",
        "fixtureId": route["fixtureId"],
        "fixtureRevision": "1",
        "scenarioId": scenario,
        "variantId": variant,
        "laneVariant": variant,
        "contractVersion": route["contractVersion"],
        "result": "PASS",
        "operationExitCode": 0,
        "platform": {
            "hostPlatform": host,
            "dockerEngineOS": "linux",
            "containerPlatform": "linux/amd64",
            "measurements": {
                "hostPlatform": host_probe,
                "dockerEngine": {"OSType": "linux"},
                "containerImage": {"OS": "linux", "Architecture": "amd64"},
                "selectedOciDescriptor": {"platform": "linux/amd64", "manifestDigest": selected},
            },
        },
        "artifactIdentity": {
            "candidateId": authority["candidateId"],
            "releaseCommitSha": authority["releaseCommitSha"],
            "ociIndexDigest": authority["ociIndexDigest"],
            "selectedManifestDigest": selected,
        },
        "migration": migration,
    }


def self_test() -> int:
    manifest = load_manifest()
    authority = valid_authority()
    core = load_core()
    tampered_manifest = copy.deepcopy(read_json(MANIFEST_PATH, "migration adapter manifest"))
    tampered_manifest["baselineAuthority"]["migrationInventory"][0]["sha256"] = "0" * 64
    try:
        validate_manifest(tampered_manifest)
    except AdapterError:
        pass
    else:
        raise AssertionError("baseline checksum authority tamper was accepted")
    for scenario, variant in (("G583-MIG-01", "win-docker"), ("G583-MIG-01", "linux-docker"), ("G583-MIG-02", "win-docker"), ("G583-MIG-02", "linux-docker")):
        report = valid_report(manifest, scenario, variant)
        output = validate_and_build_observations(report, authority)
        if output["result"] != "PASS" or output["migration"]["lastApplied"] != "018_admin_user_capabilities.sql":
            raise AssertionError("positive migration fixture was not accepted")

    def rejected(label: str, mutate) -> None:
        report = valid_report(manifest, "G583-MIG-02", "linux-docker")
        current_authority = copy.deepcopy(authority)
        mutate(report, current_authority)
        try:
            validate_fixture_report(report, current_authority, manifest, core)
        except AdapterError:
            return
        raise AssertionError(f"negative case was accepted: {label}")

    rejected("missing hostPlatform", lambda report, _: report["platform"].pop("hostPlatform"))
    rejected("wrong hostPlatform", lambda report, _: report["platform"].update({"hostPlatform": "windows-x64"}))
    rejected("missing dockerEngineOS", lambda report, _: report["platform"].pop("dockerEngineOS"))
    rejected("windows Docker engine", lambda report, _: (report["platform"].update({"dockerEngineOS": "windows"}), report["platform"]["measurements"]["dockerEngine"].update({"OSType": "windows"})))
    rejected("ambiguous containerPlatform", lambda report, _: report["platform"].update({"containerPlatform": "linux/amd64 or linux/arm64"}))
    rejected("OCI descriptor mismatch", lambda report, _: report["platform"]["measurements"]["selectedOciDescriptor"].update({"manifestDigest": "sha256:" + "f" * 64}))
    rejected("selected manifest mismatch", lambda report, _: report["artifactIdentity"].update({"selectedManifestDigest": "sha256:" + "f" * 64}))
    rejected("candidateId mismatch", lambda report, _: report["artifactIdentity"].update({"candidateId": "0" * 64}))
    rejected("releaseCommitSha mismatch", lambda report, _: report["artifactIdentity"].update({"releaseCommitSha": "0" * 40}))
    rejected("ociIndexDigest mismatch", lambda report, _: report["artifactIdentity"].update({"ociIndexDigest": "sha256:" + "0" * 64}))
    rejected("wrong contractVersion", lambda report, _: report.update({"contractVersion": "g583-mig03-ci-auto-v1"}))
    rejected("variant-only dispatch", lambda report, _: report.update({"scenarioId": None}))
    rejected("G456 contract into G583", lambda report, _: report.update({"scenarioId": "G456-42"}))
    rejected("MIG01 ci-auto", lambda report, _: report.update({"scenarioId": "G583-MIG-01", "variantId": "ci-auto", "laneVariant": "ci-auto"}))
    rejected("MIG02 ci-auto", lambda report, _: report.update({"variantId": "ci-auto", "laneVariant": "ci-auto"}))
    rejected("invalid v1.2 baseline", lambda report, _: report["migration"]["baseline"].update({"releaseCommitSha": "0" * 40}))
    rejected("missing migration", lambda report, _: report["migration"].update({"finalInventory": report["migration"]["finalInventory"][:-1]}))
    rejected("unexpected migration", lambda report, _: report["migration"].update({"pendingMigrations": ["019_unexpected.sql"]}))
    rejected("checksum mismatch", lambda report, _: report["migration"]["checksums"].update({"018_admin_user_capabilities.sql": "0" * 64}))
    rejected("pending migration", lambda report, _: report["migration"].update({"pendingMigrations": ["018_admin_user_capabilities.sql"]}))
    print("[info] G583 MIG01/MIG02 migration Docker adapter self-test passed")
    return 0


def command_validate(args: argparse.Namespace) -> int:
    manifest = load_manifest(Path(args.manifest))
    report = read_json(Path(args.fixture_report), "fixture report")
    authority = read_json(Path(args.artifact_authority), "artifact authority")
    result = validate_and_build_observations(report, authority, manifest=manifest)
    output = json.dumps(result, sort_keys=True, separators=(",", ":")) + "\n"
    if args.output:
        destination = Path(args.output)
        if destination.exists() or destination.is_symlink():
            fail("output already exists; observations are write-once")
        destination.parent.mkdir(parents=True, exist_ok=True)
        destination.write_text(output, encoding="utf-8", newline="\n")
    else:
        sys.stdout.write(output)
    return 0


def command_manifest(args: argparse.Namespace) -> int:
    manifest = load_manifest(Path(args.manifest))
    print(json.dumps({"adapterId": manifest["adapterId"], "routeCount": len(manifest["routes"]), "result": "PASS"}, sort_keys=True))
    return 0


def build_parser() -> argparse.ArgumentParser:
    parser = argparse.ArgumentParser(description=__doc__)
    sub = parser.add_subparsers(dest="command", required=True)
    validate = sub.add_parser("validate")
    validate.add_argument("--fixture-report", required=True)
    validate.add_argument("--artifact-authority", required=True)
    validate.add_argument("--manifest", default=str(MANIFEST_PATH))
    validate.add_argument("--output")
    validate.set_defaults(func=command_validate)
    manifest = sub.add_parser("manifest")
    manifest.add_argument("--manifest", default=str(MANIFEST_PATH))
    manifest.set_defaults(func=command_manifest)
    self_test_parser = sub.add_parser("self-test")
    self_test_parser.set_defaults(func=lambda _: self_test())
    return parser


def main(argv: list[str] | None = None) -> int:
    try:
        args = build_parser().parse_args(argv)
        return args.func(args)
    except AdapterError as exc:
        print(f"[error] {exc}", file=sys.stderr)
        return 1
    except (OSError, ValueError) as exc:
        print(f"[error] operation failed: {type(exc).__name__}", file=sys.stderr)
        return 1


if __name__ == "__main__":
    raise SystemExit(main())
