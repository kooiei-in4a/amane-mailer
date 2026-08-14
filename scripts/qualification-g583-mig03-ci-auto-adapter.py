#!/usr/bin/env python3
"""Fail-closed pre-qualification adapter for G583-MIG-03 / ci-auto.

This adapter is deliberately independent from the MIG01/MIG02 Docker routes.
It reads the additive G583 dispatcher only to verify the bound route and never
imports a Docker adapter, the qualification runner, or a qualification store.
Its output is a value-free observation report; it is not formal qualification
evidence and this tool has no command that can execute qualification.
"""

from __future__ import annotations

import argparse
import copy
import hashlib
import importlib.util
import json
import re
import sys
from pathlib import Path
from typing import Any, Callable


ROOT = Path(__file__).resolve().parent.parent
MANIFEST_PATH = Path(__file__).with_name("qualification-g583-mig03-adapter-manifest.json")
DISPATCH_PATH = Path(__file__).with_name("qualification-g583-dispatch.py")
SCOPE_PATH = ROOT / "docs" / "qualification" / "v1.3.0-scope.json"

SCENARIO = "G583-MIG-03"
VARIANT = "ci-auto"
CONTRACT_VERSION = "g583-mig03-ci-auto-v1"
OWNER = "maintainer-migration"
INVENTORY_ALGORITHM = "RFC8785-JCS-runner-order-migration-inventory-sha256/v2"
HEX64 = re.compile(r"^[0-9a-f]{64}$")
SHA40 = re.compile(r"^[0-9a-f]{40}$")
SHA256_DIGEST = re.compile(r"^sha256:[0-9a-f]{64}$")

INPUT_FIELDS = {
    "scenarioId", "variantId", "laneVariant", "contractVersion", "ownerRole",
    "artifactIdentity", "binding", "migrationPin", "fixtureResult",
}
DOCKER_FIELDS = {"hostPlatform", "dockerEngineOS", "containerPlatform", "selectedManifestDigest"}
BINDING_FIELDS = {
    "candidateId", "releaseCommitSha", "ociIndexDigest", "migrationPinDigestSha256",
    "migrationInventoryDigestSha256", "migrationBaselineInventory",
    "migrationDeltaInventory", "migrationFullInventory",
    "migrationBaselineInventoryDigestSha256", "migrationDeltaInventoryDigestSha256",
    "migrationFullInventoryDigestSha256", "migrationFullFileDigests",
    "migrationPredicateSetVersion", "migrationSchemaAllowlistVersion",
    "migrationSchemaAllowlistSha256",
}
PIN_FIELDS = {"migrationPinWithoutDigest", "migrationPinDigestSha256", "migrationInventoryDigestSha256"}
PIN_WITHOUT_FIELDS = {
    "schemaVersion", "releaseCommitSha", "inventoryAlgorithm", "scopeId", "scopeVersion",
    "authorityIssueNumber", "authorityIssueBodySha256", "predicateSetVersion",
    "schemaAllowlistVersion", "baselineInventory", "deltaInventory", "fullInventory",
    "inventoryDigestSha256", "baselineInventoryDigestSha256",
    "deltaInventoryDigestSha256", "fullInventoryDigestSha256", "baselineFiles",
    "deltaFiles", "fullFiles",
}
FIXTURE_OBSERVATIONS = {
    "migration014To018SchemaResult": "pass",
    "constraintsResult": "pass",
    "indexesResult": "pass",
    "piiValueCanaryResult": "pass",
    "valueFreeEvidenceCanaryResult": "pass",
}


class AdapterError(Exception):
    """Expected fail-closed validation error."""


def fail(message: str) -> None:
    raise AdapterError(message)


def jcs(value: Any) -> bytes:
    """Canonical JSON for the scalar-only qualification boundary."""
    def reject_floats(item: Any) -> None:
        if isinstance(item, float):
            fail("floating point values are forbidden")
        if isinstance(item, dict):
            for key, child in item.items():
                if not isinstance(key, str) or any(ord(character) > 0x7f for character in key):
                    fail("object keys must be ASCII strings")
                reject_floats(child)
        elif isinstance(item, list):
            for child in item:
                reject_floats(child)

    reject_floats(value)
    return json.dumps(value, ensure_ascii=False, sort_keys=True, separators=(",", ":"), allow_nan=False).encode("utf-8")


def sha_object(value: Any) -> str:
    return hashlib.sha256(jcs(value)).hexdigest()


def sha_file(path: Path) -> str:
    if not path.is_file() or path.is_symlink():
        fail("required file is missing or symlinked")
    return hashlib.sha256(path.read_bytes()).hexdigest()


def read_json(path: Path, label: str) -> Any:
    if not path.is_file() or path.is_symlink():
        fail(f"{label}: missing or symlinked")
    try:
        return json.loads(path.read_text(encoding="utf-8"))
    except (OSError, UnicodeError, json.JSONDecodeError):
        fail(f"{label}: invalid JSON")


def require_object(value: Any, label: str) -> dict[str, Any]:
    if not isinstance(value, dict):
        fail(f"{label}: object required")
    return value


def require_fields(value: dict[str, Any], required: set[str], label: str) -> None:
    missing = sorted(required - set(value))
    unknown = sorted(set(value) - required)
    if missing or unknown:
        fail(f"{label}: fields must match exactly")


def require_string(value: Any, label: str) -> str:
    if not isinstance(value, str) or not value:
        fail(f"{label}: non-empty string required")
    return value


def require_hex(value: Any, label: str) -> str:
    text = require_string(value, label)
    if not HEX64.fullmatch(text):
        fail(f"{label}: lowercase 64-hex required")
    return text


def require_commit(value: Any, label: str) -> str:
    text = require_string(value, label)
    if not SHA40.fullmatch(text):
        fail(f"{label}: lowercase 40-hex required")
    return text


def require_digest(value: Any, label: str) -> str:
    text = require_string(value, label)
    if not SHA256_DIGEST.fullmatch(text):
        fail(f"{label}: sha256 digest required")
    return text


def load_manifest() -> dict[str, Any]:
    manifest = require_object(read_json(MANIFEST_PATH, "MIG03 adapter manifest"), "MIG03 adapter manifest")
    fields = {
        "schemaVersion", "adapterId", "adapterRevision", "scenarioId", "laneVariant",
        "contractVersion", "ownerRole", "adapterInterface", "fixture", "inputFields",
        "forbiddenDockerFields", "qualificationExecution",
    }
    require_fields(manifest, fields, "MIG03 adapter manifest")
    if (
        manifest.get("schemaVersion") != 1
        or manifest.get("adapterId") != "g583-mig03-ci-auto-adapter"
        or manifest.get("adapterRevision") != "1"
        or manifest.get("scenarioId") != SCENARIO
        or manifest.get("laneVariant") != VARIANT
        or manifest.get("contractVersion") != CONTRACT_VERSION
        or manifest.get("ownerRole") != OWNER
        or manifest.get("adapterInterface") != "g583-mig03-ci-auto-observations-v1"
        or manifest.get("qualificationExecution") != "forbidden"
        or manifest.get("inputFields") != [
            "scenarioId", "variantId", "laneVariant", "contractVersion", "ownerRole",
            "artifactIdentity", "binding", "migrationPin", "fixtureResult",
        ]
        or manifest.get("forbiddenDockerFields") != [
            "hostPlatform", "dockerEngineOS", "containerPlatform", "selectedManifestDigest",
        ]
    ):
        fail("MIG03 adapter manifest identity/isolation mismatch")
    fixture = require_object(manifest.get("fixture"), "MIG03 adapter fixture")
    require_fields(fixture, {"producerScript", "fixtureId", "fixtureRevision", "sourceTestId", "testSelector"}, "MIG03 adapter fixture")
    if fixture != {
        "producerScript": "qualification-g583-mig03-fixture-producer.py",
        "fixtureId": "g583-mig03-ci-auto-schema-contract",
        "fixtureRevision": "1",
        "sourceTestId": "Amane.Mailer.Tests.Qualification.G583MigrationSchemaContractFixtureTests.Qualification_fixture_G583_MIG_03_ci_auto_emits_value_free_schema_contract_result",
        "testSelector": "FullyQualifiedName=Amane.Mailer.Tests.Qualification.G583MigrationSchemaContractFixtureTests.Qualification_fixture_G583_MIG_03_ci_auto_emits_value_free_schema_contract_result",
    }:
        fail("MIG03 adapter fixture identity mismatch")
    return manifest


def load_scope() -> dict[str, Any]:
    scope = require_object(read_json(SCOPE_PATH, "v1.3 scope"), "v1.3 scope")
    if scope.get("scopeId") != "v1.3.0-rc-qualification" or scope.get("scopeVersion") != 1 or scope.get("authorityIssueNumber") != 583:
        fail("v1.3 scope identity mismatch")
    migration = require_object(scope.get("migration"), "v1.3 migration scope")
    for key in ("baselineInventory", "deltaInventory", "fullInventory", "schemaAllowlist"):
        if key not in migration:
            fail("v1.3 migration scope is incomplete")
    baseline = migration["baselineInventory"]
    delta = migration["deltaInventory"]
    full = migration["fullInventory"]
    allowlist = migration["schemaAllowlist"]
    if (
        migration.get("inventoryAlgorithm") != INVENTORY_ALGORITHM
        or migration.get("predicateSetVersion") != 1
        or migration.get("schemaAllowlistVersion") != 1
        or not all(isinstance(value, list) and all(isinstance(item, str) and item for item in value) for value in (baseline, delta, full))
        or full != baseline + delta
        or len(baseline) != 13
        or len(delta) != 5
        or len(full) != 18
        or set(allowlist) != set(delta)
    ):
        fail("v1.3 migration inventory/schema authority mismatch")
    for name in delta:
        entry = require_object(allowlist.get(name), "schema allowlist entry")
        if set(entry) != {"sqlSha256", "tables", "indexes", "constraints"} or not HEX64.fullmatch(str(entry.get("sqlSha256", ""))):
            fail("v1.3 schema allowlist is malformed")
        if not all(isinstance(entry[key], list) and all(isinstance(item, str) and item for item in entry[key]) for key in ("tables", "indexes", "constraints")):
            fail("v1.3 schema allowlist is malformed")
    return {
        "scopeId": scope["scopeId"],
        "scopeVersion": scope["scopeVersion"],
        "authorityIssueNumber": scope["authorityIssueNumber"],
        "authorityIssueBodySha256": require_hex(scope.get("authorityIssueBodySha256"), "scope authorityIssueBodySha256"),
        "inventoryAlgorithm": migration["inventoryAlgorithm"],
        "predicateSetVersion": migration["predicateSetVersion"],
        "schemaAllowlistVersion": migration["schemaAllowlistVersion"],
        "schemaAllowlistSha256": sha_object(allowlist),
        "baselineInventory": list(baseline),
        "deltaInventory": list(delta),
        "fullInventory": list(full),
    }


def load_dispatch() -> Any:
    spec = importlib.util.spec_from_file_location("qualification_g583_dispatch_for_mig03", DISPATCH_PATH)
    if spec is None or spec.loader is None:
        fail("G583 dispatcher cannot be loaded")
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


def paths_for(inventory: list[str]) -> list[str]:
    return [f"src/Amane.Mailer/Data/Migrations/{name}" for name in inventory]


def inventory_digest(scope: dict[str, Any], release_commit: str, inventory: list[str], *, full: bool) -> str:
    document: dict[str, Any] = {
        "scopeId": scope["scopeId"],
        "scopeVersion": scope["scopeVersion"],
        "releaseCommitSha": release_commit,
        "runnerOrderPaths": paths_for(inventory),
    }
    if full:
        document.update({
            "schemaVersion": 1,
            "baselineInventory": scope["baselineInventory"],
            "deltaInventory": scope["deltaInventory"],
        })
    return sha_object(document)


def validate_file_digests(value: Any, expected_paths: list[str], label: str) -> list[dict[str, str]]:
    if not isinstance(value, list) or len(value) != len(expected_paths):
        fail(f"{label}: exact migration file list required")
    normalized: list[dict[str, str]] = []
    for entry, expected_path in zip(value, expected_paths, strict=True):
        item = require_object(entry, label)
        require_fields(item, {"path", "sha256", "gitBlobSha"}, label)
        if item.get("path") != expected_path:
            fail(f"{label}: migration file order/path mismatch")
        normalized.append({
            "path": expected_path,
            "sha256": require_hex(item.get("sha256"), f"{label}.sha256"),
            "gitBlobSha": require_commit(item.get("gitBlobSha"), f"{label}.gitBlobSha"),
        })
    return normalized


def validate_migration_pin(pin_value: Any, scope: dict[str, Any], release_commit: str) -> dict[str, Any]:
    pin = require_object(pin_value, "migration PIN")
    require_fields(pin, PIN_FIELDS, "migration PIN")
    without = require_object(pin.get("migrationPinWithoutDigest"), "migrationPinWithoutDigest")
    require_fields(without, PIN_WITHOUT_FIELDS, "migrationPinWithoutDigest")
    pin_digest = require_hex(pin.get("migrationPinDigestSha256"), "migrationPinDigestSha256")
    inventory_digest_value = require_hex(pin.get("migrationInventoryDigestSha256"), "migrationInventoryDigestSha256")
    if sha_object(without) != pin_digest or "evidenceDigestSha256" in without:
        fail("migration PIN digest/preimage mismatch")
    if (
        without.get("schemaVersion") != 1
        or without.get("releaseCommitSha") != release_commit
        or without.get("inventoryAlgorithm") != scope["inventoryAlgorithm"]
        or without.get("scopeId") != scope["scopeId"]
        or without.get("scopeVersion") != scope["scopeVersion"]
        or without.get("authorityIssueNumber") != scope["authorityIssueNumber"]
        or without.get("authorityIssueBodySha256") != scope["authorityIssueBodySha256"]
        or without.get("predicateSetVersion") != scope["predicateSetVersion"]
        or without.get("schemaAllowlistVersion") != scope["schemaAllowlistVersion"]
        or without.get("baselineInventory") != scope["baselineInventory"]
        or without.get("deltaInventory") != scope["deltaInventory"]
        or without.get("fullInventory") != scope["fullInventory"]
    ):
        fail("migration PIN scope/inventory identity mismatch")
    baseline_digest = inventory_digest(scope, release_commit, scope["baselineInventory"], full=False)
    delta_digest = inventory_digest(scope, release_commit, scope["deltaInventory"], full=False)
    full_digest = inventory_digest(scope, release_commit, scope["fullInventory"], full=True)
    if (
        without.get("baselineInventoryDigestSha256") != baseline_digest
        or without.get("deltaInventoryDigestSha256") != delta_digest
        or without.get("fullInventoryDigestSha256") != full_digest
        or without.get("inventoryDigestSha256") != full_digest
        or inventory_digest_value != full_digest
    ):
        fail("migration PIN inventory digest mismatch")
    baseline_files = validate_file_digests(without.get("baselineFiles"), paths_for(scope["baselineInventory"]), "baselineFiles")
    delta_files = validate_file_digests(without.get("deltaFiles"), paths_for(scope["deltaInventory"]), "deltaFiles")
    full_files = validate_file_digests(without.get("fullFiles"), paths_for(scope["fullInventory"]), "fullFiles")
    if full_files != baseline_files + delta_files:
        fail("migration PIN full file identity mismatch")
    return {
        "migrationPinDigestSha256": pin_digest,
        "migrationInventoryDigestSha256": full_digest,
        "migrationBaselineInventoryDigestSha256": baseline_digest,
        "migrationDeltaInventoryDigestSha256": delta_digest,
        "migrationFullInventoryDigestSha256": full_digest,
        "migrationFullFileDigests": full_files,
    }


def validate_binding(value: Any, scope: dict[str, Any], pin: dict[str, Any]) -> dict[str, Any]:
    binding = require_object(value, "binding")
    require_fields(binding, BINDING_FIELDS, "binding")
    candidate = require_hex(binding.get("candidateId"), "binding.candidateId")
    release = require_commit(binding.get("releaseCommitSha"), "binding.releaseCommitSha")
    index = require_digest(binding.get("ociIndexDigest"), "binding.ociIndexDigest")
    if (
        binding.get("migrationPinDigestSha256") != pin["migrationPinDigestSha256"]
        or binding.get("migrationInventoryDigestSha256") != pin["migrationInventoryDigestSha256"]
        or binding.get("migrationBaselineInventory") != scope["baselineInventory"]
        or binding.get("migrationDeltaInventory") != scope["deltaInventory"]
        or binding.get("migrationFullInventory") != scope["fullInventory"]
        or binding.get("migrationBaselineInventoryDigestSha256") != pin["migrationBaselineInventoryDigestSha256"]
        or binding.get("migrationDeltaInventoryDigestSha256") != pin["migrationDeltaInventoryDigestSha256"]
        or binding.get("migrationFullInventoryDigestSha256") != pin["migrationFullInventoryDigestSha256"]
        or binding.get("migrationFullFileDigests") != pin["migrationFullFileDigests"]
        or binding.get("migrationPredicateSetVersion") != scope["predicateSetVersion"]
        or binding.get("migrationSchemaAllowlistVersion") != scope["schemaAllowlistVersion"]
        or binding.get("migrationSchemaAllowlistSha256") != scope["schemaAllowlistSha256"]
    ):
        fail("binding migration PIN/schema authority mismatch")
    return {"candidateId": candidate, "releaseCommitSha": release, "ociIndexDigest": index, **binding}


def validate_fixture_result(value: Any, manifest: dict[str, Any]) -> dict[str, Any]:
    fixture_result = require_object(value, "fixture result")
    fields = {
        "schemaVersion", "kind", "fixtureId", "fixtureRevision", "scenarioId", "variantId",
        "sourceTestId", "result", "operationExitCode", "observations",
    }
    require_fields(fixture_result, fields, "fixture result")
    fixture = manifest["fixture"]
    if (
        fixture_result.get("schemaVersion") != 1
        or fixture_result.get("kind") != "qualification-fixture-result"
        or fixture_result.get("fixtureId") != fixture["fixtureId"]
        or fixture_result.get("fixtureRevision") != fixture["fixtureRevision"]
        or fixture_result.get("scenarioId") != SCENARIO
        or fixture_result.get("variantId") != VARIANT
        or fixture_result.get("sourceTestId") != fixture["sourceTestId"]
        or fixture_result.get("result") != "PASS"
        or fixture_result.get("operationExitCode") != 0
        or fixture_result.get("observations") != FIXTURE_OBSERVATIONS
    ):
        fail("fixture result identity/schema/privacy contract mismatch")
    return fixture_result


def validate_input(value: Any) -> tuple[dict[str, Any], dict[str, Any], dict[str, Any], dict[str, Any]]:
    item = require_object(value, "MIG03 input")
    require_fields(item, INPUT_FIELDS, "MIG03 input")
    if DOCKER_FIELDS & set(item):
        fail("MIG03 input must not contain Docker platform fields")
    if (
        item.get("scenarioId") != SCENARIO
        or item.get("variantId") != VARIANT
        or item.get("laneVariant") != VARIANT
        or item.get("contractVersion") != CONTRACT_VERSION
        or item.get("ownerRole") != OWNER
    ):
        fail("MIG03 scenario/variant/contract/owner mismatch")
    manifest = load_manifest()
    scope = load_scope()
    binding_without_pin = require_object(item.get("binding"), "binding")
    release = require_commit(binding_without_pin.get("releaseCommitSha"), "binding.releaseCommitSha")
    pin = validate_migration_pin(item.get("migrationPin"), scope, release)
    binding = validate_binding(binding_without_pin, scope, pin)
    dispatch = load_dispatch()
    core_manifest = dispatch.load_manifest()
    evidence = {
        "scenarioId": item["scenarioId"], "variantId": item["variantId"],
        "laneVariant": item["laneVariant"], "contractVersion": item["contractVersion"],
        "artifactIdentity": item["artifactIdentity"],
    }
    try:
        dispatch.validate_evidence(evidence, binding, core_manifest)
    except dispatch.DispatchError as exc:
        raise AdapterError("Core G583 route/artifact identity rejected") from exc
    fixture = validate_fixture_result(item.get("fixtureResult"), manifest)
    return item, binding, pin, fixture


def build_observations(value: Any) -> dict[str, Any]:
    item, binding, pin, fixture = validate_input(value)
    fixture_digest = sha_object(fixture)
    report = {
        "schemaVersion": 1,
        "kind": "g583-mig03-ci-auto-prequalification-observations",
        "qualificationExecuted": False,
        "scenarioId": SCENARIO,
        "variantId": VARIANT,
        "laneVariant": VARIANT,
        "contractVersion": CONTRACT_VERSION,
        "ownerRole": OWNER,
        "artifactIdentity": {
            "candidateId": binding["candidateId"],
            "releaseCommitSha": binding["releaseCommitSha"],
            "ociIndexDigest": binding["ociIndexDigest"],
        },
        "migration": {
            "migrationDecision": "INCLUDE",
            "migrationPinDigestSha256": pin["migrationPinDigestSha256"],
            "migrationInventoryDigestSha256": pin["migrationInventoryDigestSha256"],
            "baselineInventory": binding["migrationBaselineInventory"],
            "deltaInventory": binding["migrationDeltaInventory"],
            "fullInventory": binding["migrationFullInventory"],
            "baselineInventoryDigestSha256": pin["migrationBaselineInventoryDigestSha256"],
            "deltaInventoryDigestSha256": pin["migrationDeltaInventoryDigestSha256"],
            "fullInventoryDigestSha256": pin["migrationFullInventoryDigestSha256"],
            "schemaAllowlistVersion": binding["migrationSchemaAllowlistVersion"],
            "schemaAllowlistSha256": binding["migrationSchemaAllowlistSha256"],
        },
        "schema": {
            "migrationRange": "014..018",
            "schemaContractResult": "pass",
            "constraintsResult": "pass",
            "indexesResult": "pass",
        },
        "privacy": {
            "piiValueCanaryResult": "pass",
            "valueFreeEvidenceCanaryResult": "pass",
            "prohibitedContentScan": {
                "result": "PASS",
                "scannerId": "g583-mig03-value-free/1",
                "reportDigestSha256": fixture_digest,
            },
        },
        "fixture": {
            "fixtureId": fixture["fixtureId"],
            "fixtureRevision": fixture["fixtureRevision"],
            "fixtureResultDigestSha256": fixture_digest,
        },
    }
    if DOCKER_FIELDS & set(report):
        fail("MIG03 report contains Docker platform fields")
    return report


def self_test_input() -> dict[str, Any]:
    scope = load_scope()
    release = "b" * 40
    def files(inventory: list[str], marker: str) -> list[dict[str, str]]:
        return [
            {"path": path, "sha256": marker * 64, "gitBlobSha": "a" * 40}
            for path in paths_for(inventory)
        ]
    baseline_files = files(scope["baselineInventory"], "1")
    delta_files = files(scope["deltaInventory"], "2")
    full_files = baseline_files + delta_files
    without = {
        "schemaVersion": 1,
        "releaseCommitSha": release,
        "inventoryAlgorithm": scope["inventoryAlgorithm"],
        "scopeId": scope["scopeId"],
        "scopeVersion": scope["scopeVersion"],
        "authorityIssueNumber": scope["authorityIssueNumber"],
        "authorityIssueBodySha256": scope["authorityIssueBodySha256"],
        "predicateSetVersion": scope["predicateSetVersion"],
        "schemaAllowlistVersion": scope["schemaAllowlistVersion"],
        "baselineInventory": scope["baselineInventory"],
        "deltaInventory": scope["deltaInventory"],
        "fullInventory": scope["fullInventory"],
        "inventoryDigestSha256": inventory_digest(scope, release, scope["fullInventory"], full=True),
        "baselineInventoryDigestSha256": inventory_digest(scope, release, scope["baselineInventory"], full=False),
        "deltaInventoryDigestSha256": inventory_digest(scope, release, scope["deltaInventory"], full=False),
        "fullInventoryDigestSha256": inventory_digest(scope, release, scope["fullInventory"], full=True),
        "baselineFiles": baseline_files,
        "deltaFiles": delta_files,
        "fullFiles": full_files,
    }
    pin = {
        "migrationPinWithoutDigest": without,
        "migrationPinDigestSha256": sha_object(without),
        "migrationInventoryDigestSha256": without["inventoryDigestSha256"],
    }
    binding = {
        "candidateId": "a" * 64,
        "releaseCommitSha": release,
        "ociIndexDigest": "sha256:" + "c" * 64,
        "migrationPinDigestSha256": pin["migrationPinDigestSha256"],
        "migrationInventoryDigestSha256": pin["migrationInventoryDigestSha256"],
        "migrationBaselineInventory": scope["baselineInventory"],
        "migrationDeltaInventory": scope["deltaInventory"],
        "migrationFullInventory": scope["fullInventory"],
        "migrationBaselineInventoryDigestSha256": without["baselineInventoryDigestSha256"],
        "migrationDeltaInventoryDigestSha256": without["deltaInventoryDigestSha256"],
        "migrationFullInventoryDigestSha256": without["fullInventoryDigestSha256"],
        "migrationFullFileDigests": full_files,
        "migrationPredicateSetVersion": scope["predicateSetVersion"],
        "migrationSchemaAllowlistVersion": scope["schemaAllowlistVersion"],
        "migrationSchemaAllowlistSha256": scope["schemaAllowlistSha256"],
    }
    manifest = load_manifest()
    return {
        "scenarioId": SCENARIO,
        "variantId": VARIANT,
        "laneVariant": VARIANT,
        "contractVersion": CONTRACT_VERSION,
        "ownerRole": OWNER,
        "artifactIdentity": {
            "candidateId": binding["candidateId"],
            "releaseCommitSha": binding["releaseCommitSha"],
            "ociIndexDigest": binding["ociIndexDigest"],
        },
        "binding": binding,
        "migrationPin": pin,
        "fixtureResult": {
            "schemaVersion": 1,
            "kind": "qualification-fixture-result",
            "fixtureId": manifest["fixture"]["fixtureId"],
            "fixtureRevision": manifest["fixture"]["fixtureRevision"],
            "scenarioId": SCENARIO,
            "variantId": VARIANT,
            "sourceTestId": manifest["fixture"]["sourceTestId"],
            "result": "PASS",
            "operationExitCode": 0,
            "observations": dict(FIXTURE_OBSERVATIONS),
        },
    }


def expect_rejection(label: str, mutate: Callable[[dict[str, Any]], None]) -> None:
    item = self_test_input()
    mutate(item)
    try:
        build_observations(item)
    except AdapterError:
        return
    raise AssertionError(f"negative case accepted: {label}")


def refresh_pin_digest(item: dict[str, Any]) -> None:
    pin = item["migrationPin"]
    pin["migrationPinDigestSha256"] = sha_object(pin["migrationPinWithoutDigest"])


def command_self_test(_: argparse.Namespace) -> int:
    report = build_observations(self_test_input())
    if report["qualificationExecuted"] is not False or DOCKER_FIELDS & set(report):
        raise AssertionError("MIG03 report isolation failed")
    negatives: list[tuple[str, Callable[[dict[str, Any]], None]]] = [
        ("wrong scenario", lambda item: item.update({"scenarioId": "G583-MIG-01"})),
        ("wrong owner", lambda item: item.update({"ownerRole": "lane-owner"})),
        ("missing contract", lambda item: item.pop("contractVersion")),
        ("wrong contract", lambda item: item.update({"contractVersion": "g583-s5a-platform-v1"})),
        ("wrong variant", lambda item: item.update({"variantId": "win-docker", "laneVariant": "win-docker"})),
        ("host platform", lambda item: item.update({"hostPlatform": "linux-x64"})),
        ("Docker engine", lambda item: item.update({"dockerEngineOS": "linux"})),
        ("container platform", lambda item: item.update({"containerPlatform": "linux/amd64"})),
        ("selected manifest", lambda item: item.update({"selectedManifestDigest": "sha256:" + "d" * 64})),
        ("migration PIN mismatch", lambda item: item["migrationPin"].update({"migrationPinDigestSha256": "0" * 64})),
        ("inventory mismatch", lambda item: item["binding"].update({"migrationFullInventory": []})),
        ("missing migration", lambda item: (item["migrationPin"]["migrationPinWithoutDigest"].update({"deltaInventory": item["migrationPin"]["migrationPinWithoutDigest"]["deltaInventory"][:-1]}), refresh_pin_digest(item))),
        ("unexpected migration", lambda item: (item["migrationPin"]["migrationPinWithoutDigest"].update({"deltaInventory": item["migrationPin"]["migrationPinWithoutDigest"]["deltaInventory"] + ["019_unexpected.sql"]}), refresh_pin_digest(item))),
        ("allowlist version", lambda item: item["binding"].update({"migrationSchemaAllowlistVersion": 2})),
        ("allowlist digest", lambda item: item["binding"].update({"migrationSchemaAllowlistSha256": "0" * 64})),
        ("schema contract", lambda item: item["fixtureResult"]["observations"].update({"migration014To018SchemaResult": "fail"})),
        ("privacy canary", lambda item: item["fixtureResult"]["observations"].update({"piiValueCanaryResult": "fail"})),
    ]
    for label, mutate in negatives:
        expect_rejection(label, mutate)
    print(json.dumps({"negativeCases": len(negatives), "result": "PASS", "scenarioId": SCENARIO, "variantId": VARIANT}, sort_keys=True))
    return 0


def command_manifest(_: argparse.Namespace) -> int:
    manifest = load_manifest()
    print(json.dumps({"adapterId": manifest["adapterId"], "contractVersion": CONTRACT_VERSION, "dockerDependency": False, "result": "PASS"}, sort_keys=True))
    return 0


def command_build_observations(args: argparse.Namespace) -> int:
    report = build_observations(read_json(Path(args.input), "MIG03 input"))
    output = json.dumps(report, sort_keys=True, separators=(",", ":")) + "\n"
    if args.output:
        path = Path(args.output)
        if path.exists() or path.is_symlink():
            fail("output already exists")
        path.parent.mkdir(parents=True, exist_ok=True)
        path.write_text(output, encoding="utf-8", newline="\n")
    else:
        sys.stdout.write(output)
    return 0


def build_parser() -> argparse.ArgumentParser:
    parser = argparse.ArgumentParser(description=__doc__)
    sub = parser.add_subparsers(dest="command", required=True)
    manifest = sub.add_parser("manifest")
    manifest.set_defaults(func=command_manifest)
    self_test = sub.add_parser("self-test")
    self_test.set_defaults(func=command_self_test)
    build = sub.add_parser("build-observations")
    build.add_argument("--input", required=True)
    build.add_argument("--output")
    build.set_defaults(func=command_build_observations)
    return parser


def main(argv: list[str] | None = None) -> int:
    try:
        args = build_parser().parse_args(argv)
        return args.func(args)
    except AdapterError as exc:
        print(f"[error] {exc}", file=sys.stderr)
        return 1
    except (OSError, ValueError, TypeError) as exc:
        print(f"[error] operation failed: {type(exc).__name__}", file=sys.stderr)
        return 1


if __name__ == "__main__":
    raise SystemExit(main())
