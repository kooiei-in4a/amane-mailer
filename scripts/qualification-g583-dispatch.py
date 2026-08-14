#!/usr/bin/env python3
"""Fail-closed G583 S5-A contract validation and dispatch resolution.

This module owns only the additive G583 route.  It never imports or executes
the G456 adapter, never runs a migration fixture, and never writes a
qualification store.  Variable-slice adapters may register later through the
``g583-s5a-adapter-v1`` callable interface in the dispatch manifest.
"""

from __future__ import annotations

import argparse
import json
import re
import sys
from pathlib import Path
from typing import Any, Callable, Mapping


ROOT = Path(__file__).resolve().parent.parent
DEFAULT_SCHEMA = ROOT / "scripts" / "qualification-g583-contract-schema.json"
DEFAULT_MANIFEST = ROOT / "scripts" / "qualification-g583-dispatch-manifest.json"
PLATFORM_CONTRACT = "g583-s5a-platform-v1"
MIG03_CONTRACT = "g583-mig03-ci-auto-v1"
G583_CONTRACT_VERSIONS = {PLATFORM_CONTRACT, MIG03_CONTRACT}
HEX64 = re.compile(r"^[0-9a-f]{64}$")
SHA40 = re.compile(r"^[0-9a-f]{40}$")
SHA256_DIGEST = re.compile(r"^sha256:[0-9a-f]{64}$")
SAFE_ID = re.compile(r"^[A-Za-z0-9][A-Za-z0-9._-]{0,127}$")

PLATFORM_POLICY = {
    "authority": "candidate-binding",
    "requiredFields": ["candidateId", "releaseCommitSha", "ociIndexDigest", "selectedManifestDigest"],
    "selectedManifestRule": "must-match-selected-oci-descriptor",
}
CI_POLICY = {
    "authority": "candidate-binding",
    "requiredFields": ["candidateId", "releaseCommitSha", "ociIndexDigest"],
}
PLATFORM_ALLOCATIONS = {
    ("G583-MIG-01", "win-docker", "windows-x64", "linux", "linux/amd64"),
    *{
        ("G583-MIG-02", "linux-docker", host, "linux", container)
        for host in ("linux-x64", "linux-arm64")
        for container in ("linux/amd64", "linux/arm64")
    },
}
EXPECTED_ROUTES = {
    ("G583-MIG-01", "win-docker", PLATFORM_CONTRACT),
    ("G583-MIG-02", "linux-docker", PLATFORM_CONTRACT),
    ("G583-MIG-03", "ci-auto", MIG03_CONTRACT),
}


class DispatchError(Exception):
    """Expected contract or dispatch rejection."""


def fail(message: str) -> None:
    raise DispatchError(message)


def read_json(path: Path, label: str) -> Any:
    if not path.is_file() or path.is_symlink():
        fail(f"{label}: missing or symlink")
    try:
        return json.loads(path.read_text(encoding="utf-8"))
    except (OSError, json.JSONDecodeError):
        fail(f"{label}: invalid JSON")


def require_object(value: Any, label: str) -> dict[str, Any]:
    if not isinstance(value, dict):
        fail(f"{label}: object required")
    return value


def require_scalar(value: Any, label: str) -> str:
    if not isinstance(value, str) or not value:
        fail(f"{label}: non-empty scalar string required")
    return value


def require_safe_id(value: Any, label: str) -> str:
    text = require_scalar(value, label)
    if not SAFE_ID.fullmatch(text):
        fail(f"{label}: invalid identifier")
    return text


def require_fields(value: dict[str, Any], required: set[str], allowed: set[str], label: str) -> None:
    missing = sorted(required - set(value))
    unknown = sorted(set(value) - allowed)
    if missing:
        fail(f"{label}: missing fields: {','.join(missing)}")
    if unknown:
        fail(f"{label}: unknown fields: {','.join(unknown)}")


def validate_schema_artifact(schema: dict[str, Any]) -> None:
    if schema.get("$schema") != "https://json-schema.org/draft/2020-12/schema":
        fail("contract schema: draft 2020-12 is required")
    definitions = schema.get("$defs")
    if not isinstance(definitions, dict) or set(definitions) != {
        "platformArtifactIdentity", "ciArtifactIdentity",
        "winDockerPlatformContract", "linuxDockerPlatformContract", "mig03Contract",
    }:
        fail("contract schema: required definitions are missing")


def validate_contract_document(document: dict[str, Any], label: str = "contract document") -> list[dict[str, Any]]:
    require_fields(document, {"schemaVersion", "contracts"}, {"schemaVersion", "contracts"}, label)
    if document.get("schemaVersion") != 1:
        fail(f"{label}: unsupported schemaVersion")
    contracts = document.get("contracts")
    if not isinstance(contracts, list) or not contracts:
        fail(f"{label}: non-empty contracts array required")
    seen: set[tuple[str, ...]] = set()
    normalized = []
    for index, raw in enumerate(contracts):
        contract = require_object(raw, f"{label}.contracts[{index}]")
        scenario = require_safe_id(contract.get("scenarioId"), "scenarioId")
        lane = require_safe_id(contract.get("laneVariant"), "laneVariant")
        version = require_safe_id(contract.get("contractVersion"), "contractVersion")
        if version == PLATFORM_CONTRACT:
            fields = {"scenarioId", "laneVariant", "contractVersion", "hostPlatform", "dockerEngineOS", "containerPlatform", "artifactIdentity"}
            require_fields(contract, fields, fields, f"{label}.contracts[{index}]")
            host = require_scalar(contract.get("hostPlatform"), "hostPlatform")
            engine = require_scalar(contract.get("dockerEngineOS"), "dockerEngineOS")
            container = require_scalar(contract.get("containerPlatform"), "containerPlatform")
            if (scenario, lane, host, engine, container) not in PLATFORM_ALLOCATIONS:
                fail(f"{label}.contracts[{index}]: unsupported platform allocation")
            if contract.get("artifactIdentity") != PLATFORM_POLICY:
                fail(f"{label}.contracts[{index}]: artifact identity policy mismatch")
            identity = (scenario, lane, version, host, engine, container)
        elif version == MIG03_CONTRACT:
            fields = {"scenarioId", "laneVariant", "contractVersion", "artifactIdentity"}
            require_fields(contract, fields, fields, f"{label}.contracts[{index}]")
            if (scenario, lane) != ("G583-MIG-03", "ci-auto"):
                fail(f"{label}.contracts[{index}]: MIG03 scenario/lane mismatch")
            if contract.get("artifactIdentity") != CI_POLICY:
                fail(f"{label}.contracts[{index}]: artifact identity policy mismatch")
            identity = (scenario, lane, version)
        else:
            fail(f"{label}.contracts[{index}]: unknown contractVersion")
        if identity in seen:
            fail(f"{label}: duplicate contract allocation")
        seen.add(identity)
        normalized.append(dict(contract))
    return normalized


def safe_repo_path(value: Any, label: str) -> str:
    text = require_scalar(value, label)
    path = Path(text)
    if path.is_absolute() or "\\" in text or any(part in ("", ".", "..") for part in path.parts):
        fail(f"{label}: unsafe repository path")
    return text


def load_manifest(path: Path = DEFAULT_MANIFEST, repo_root: Path = ROOT) -> dict[str, Any]:
    manifest = require_object(read_json(path, "dispatch manifest"), "dispatch manifest")
    required = {"schemaVersion", "dispatchKey", "adapterInterface", "g456Isolation", "registrations"}
    require_fields(manifest, required, required, "dispatch manifest")
    if manifest.get("schemaVersion") != 1 or manifest.get("dispatchKey") != ["scenarioId", "variantId", "contractVersion"]:
        fail("dispatch manifest: dispatch key must include scenarioId, variantId, and contractVersion")
    interface = require_object(manifest.get("adapterInterface"), "adapterInterface")
    if interface != {
        "name": "g583-s5a-adapter-v1",
        "callable": "validate_and_build_observations",
        "arguments": ["evidence", "artifactAuthority", "registration"],
        "return": "value-free-qualification-observations",
    }:
        fail("dispatch manifest: adapter interface mismatch")
    isolation = require_object(manifest.get("g456Isolation"), "g456Isolation")
    if isolation != {"scenarioPrefix": "G456-", "managedBy": "qualification-lane-adapter.py", "behavioralChange": "none"}:
        fail("dispatch manifest: G456 isolation boundary mismatch")
    registrations = manifest.get("registrations")
    if not isinstance(registrations, list) or len(registrations) != len(EXPECTED_ROUTES):
        fail("dispatch manifest: exactly three G583 routes are required")
    routes: set[tuple[str, str, str]] = set()
    normalized = []
    contract_cache: dict[str, list[dict[str, Any]]] = {}
    registration_fields = {"scenarioId", "variantId", "contractVersion", "contractPath", "adapterStatus", "adapterId"}
    for index, raw in enumerate(registrations):
        registration = require_object(raw, f"registrations[{index}]")
        require_fields(registration, registration_fields, registration_fields, f"registrations[{index}]")
        key = (
            require_safe_id(registration.get("scenarioId"), "scenarioId"),
            require_safe_id(registration.get("variantId"), "variantId"),
            require_safe_id(registration.get("contractVersion"), "contractVersion"),
        )
        if key in routes:
            fail("dispatch manifest: duplicate dispatch key")
        routes.add(key)
        contract_path = safe_repo_path(registration.get("contractPath"), "contractPath")
        if contract_path not in contract_cache:
            document = require_object(read_json(repo_root / contract_path, contract_path), contract_path)
            contract_cache[contract_path] = validate_contract_document(document, contract_path)
        allocations = [item for item in contract_cache[contract_path] if (item["scenarioId"], item["laneVariant"], item["contractVersion"]) == key]
        if not allocations:
            fail("dispatch manifest: route is not present in its contract document")
        status = registration.get("adapterStatus")
        adapter_id = registration.get("adapterId")
        if status == "metadata-only":
            if adapter_id is not None:
                fail("dispatch manifest: metadata-only route cannot name an adapter")
        elif status == "active":
            require_safe_id(adapter_id, "adapterId")
        else:
            fail("dispatch manifest: invalid adapterStatus")
        normalized.append({**registration, "contractAllocations": allocations})
    if routes != EXPECTED_ROUTES:
        fail("dispatch manifest: scenario/variant/contract registration mismatch")
    return {**manifest, "registrations": normalized}


def resolve_dispatch(
    manifest: dict[str, Any],
    scenario_id: Any,
    variant_id: Any,
    contract_version: Any,
) -> dict[str, Any]:
    scenario = require_safe_id(scenario_id, "scenarioId")
    variant = require_safe_id(variant_id, "variantId")
    version = require_safe_id(contract_version, "contractVersion")
    if scenario.startswith("G456-"):
        fail("G456 scenario is isolated from the G583 dispatcher")
    if version not in G583_CONTRACT_VERSIONS:
        fail("unknown G583 contractVersion")
    matches = [
        registration for registration in manifest.get("registrations", [])
        if (registration.get("scenarioId"), registration.get("variantId"), registration.get("contractVersion")) == (scenario, variant, version)
    ]
    if len(matches) != 1:
        fail("scenarioId/variantId/contractVersion combination is not registered")
    return matches[0]


def lower_scalar(value: Any, label: str) -> str:
    return require_scalar(value, label).lower()


def validate_artifact_identity(
    actual: Any,
    authority: Any,
    *,
    container_platform: str | None,
    selected_manifest_digest: str | None,
) -> None:
    identity = require_object(actual, "artifactIdentity")
    bound = require_object(authority, "artifactAuthority")
    required = {"candidateId", "releaseCommitSha", "ociIndexDigest"}
    if container_platform is not None:
        required.add("selectedManifestDigest")
    require_fields(identity, required, required, "artifactIdentity")
    if not HEX64.fullmatch(str(identity.get("candidateId", ""))):
        fail("artifactIdentity.candidateId is invalid")
    if not SHA40.fullmatch(str(identity.get("releaseCommitSha", ""))):
        fail("artifactIdentity.releaseCommitSha is invalid")
    if not SHA256_DIGEST.fullmatch(str(identity.get("ociIndexDigest", ""))):
        fail("artifactIdentity.ociIndexDigest is invalid")
    for field in ("candidateId", "releaseCommitSha", "ociIndexDigest"):
        if identity.get(field) != bound.get(field):
            fail(f"artifactIdentity.{field} does not match candidate/binding authority")
    if container_platform is not None:
        actual_manifest = identity.get("selectedManifestDigest")
        if not SHA256_DIGEST.fullmatch(str(actual_manifest or "")):
            fail("artifactIdentity.selectedManifestDigest is invalid")
        selected = bound.get("selectedManifests")
        if not isinstance(selected, dict) or selected.get(container_platform) != actual_manifest:
            fail("selected manifest does not match the bound OCI descriptor")
        if selected_manifest_digest != actual_manifest:
            fail("selected manifest does not match measured OCI descriptor")


def validate_platform_evidence(evidence: dict[str, Any], authority: dict[str, Any], registration: dict[str, Any]) -> None:
    fields = {
        "scenarioId", "variantId", "laneVariant", "contractVersion", "hostPlatform",
        "dockerEngineOS", "containerPlatform", "measurements", "artifactIdentity",
    }
    require_fields(evidence, fields, fields, "platform evidence")
    if evidence.get("laneVariant") != evidence.get("variantId"):
        fail("laneVariant must exactly match the bound variantId")
    measurements = require_object(evidence.get("measurements"), "measurements")
    measurement_fields = {"hostPlatform", "dockerEngine", "containerImage", "selectedOciDescriptor"}
    require_fields(measurements, measurement_fields, measurement_fields, "measurements")
    host_probe = require_object(measurements.get("hostPlatform"), "measurements.hostPlatform")
    require_fields(host_probe, {"os", "architecture"}, {"os", "architecture"}, "measurements.hostPlatform")
    host_map = {
        ("windows", "amd64"): "windows-x64",
        ("linux", "amd64"): "linux-x64",
        ("linux", "arm64"): "linux-arm64",
    }
    measured_host = host_map.get((lower_scalar(host_probe.get("os"), "host os"), lower_scalar(host_probe.get("architecture"), "host architecture")))
    if measured_host is None or evidence.get("hostPlatform") != measured_host:
        fail("host platform probe is missing, unsupported, ambiguous, or mismatched")
    engine_probe = require_object(measurements.get("dockerEngine"), "measurements.dockerEngine")
    require_fields(engine_probe, {"OSType"}, {"OSType"}, "measurements.dockerEngine")
    measured_engine = lower_scalar(engine_probe.get("OSType"), "Docker Engine Info.OSType")
    if measured_engine != "linux" or evidence.get("dockerEngineOS") != measured_engine:
        fail("Docker Engine OS is missing or mismatched")
    image_probe = require_object(measurements.get("containerImage"), "measurements.containerImage")
    require_fields(image_probe, {"OS", "Architecture"}, {"OS", "Architecture"}, "measurements.containerImage")
    measured_container = f"{lower_scalar(image_probe.get('OS'), 'image OS')}/{lower_scalar(image_probe.get('Architecture'), 'image architecture')}"
    if measured_container not in {"linux/amd64", "linux/arm64"} or evidence.get("containerPlatform") != measured_container:
        fail("container platform is missing, unsupported, ambiguous, or mismatched")
    descriptor = require_object(measurements.get("selectedOciDescriptor"), "measurements.selectedOciDescriptor")
    require_fields(descriptor, {"platform", "manifestDigest"}, {"platform", "manifestDigest"}, "measurements.selectedOciDescriptor")
    descriptor_platform = require_scalar(descriptor.get("platform"), "selected descriptor platform")
    descriptor_digest = require_scalar(descriptor.get("manifestDigest"), "selected descriptor manifest digest")
    if descriptor_platform != measured_container or not SHA256_DIGEST.fullmatch(descriptor_digest):
        fail("selected OCI descriptor does not match the measured container platform")
    allocation = (
        evidence.get("scenarioId"), evidence.get("laneVariant"), evidence.get("hostPlatform"),
        evidence.get("dockerEngineOS"), evidence.get("containerPlatform"),
    )
    allowed = {
        (item["scenarioId"], item["laneVariant"], item["hostPlatform"], item["dockerEngineOS"], item["containerPlatform"])
        for item in registration["contractAllocations"]
    }
    if allocation not in allowed:
        fail("measured platform allocation does not match the selected contract")
    validate_artifact_identity(
        evidence.get("artifactIdentity"), authority,
        container_platform=measured_container,
        selected_manifest_digest=descriptor_digest,
    )


def validate_mig03_evidence(evidence: dict[str, Any], authority: dict[str, Any]) -> None:
    fields = {"scenarioId", "variantId", "laneVariant", "contractVersion", "artifactIdentity"}
    require_fields(evidence, fields, fields, "MIG03 evidence")
    if evidence.get("laneVariant") != evidence.get("variantId"):
        fail("laneVariant must exactly match the bound variantId")
    validate_artifact_identity(evidence.get("artifactIdentity"), authority, container_platform=None, selected_manifest_digest=None)


def validate_evidence(evidence: Any, artifact_authority: Any, manifest: dict[str, Any]) -> dict[str, Any]:
    item = require_object(evidence, "evidence")
    registration = resolve_dispatch(
        manifest,
        item.get("scenarioId"),
        item.get("variantId"),
        item.get("contractVersion"),
    )
    if registration["contractVersion"] == PLATFORM_CONTRACT:
        validate_platform_evidence(item, require_object(artifact_authority, "artifactAuthority"), registration)
    else:
        validate_mig03_evidence(item, require_object(artifact_authority, "artifactAuthority"))
    return registration


def execute_dispatch(
    evidence: dict[str, Any],
    artifact_authority: dict[str, Any],
    manifest: dict[str, Any],
    adapter_registry: Mapping[str, Callable[..., Any]],
) -> Any:
    registration = validate_evidence(evidence, artifact_authority, manifest)
    if registration.get("adapterStatus") != "active" or not registration.get("adapterId"):
        fail("G583 adapter is not active for this metadata-only registration")
    adapter = adapter_registry.get(registration["adapterId"])
    if not callable(adapter):
        fail("active G583 adapter is not registered")
    return adapter(evidence=evidence, artifactAuthority=artifact_authority, registration=registration)


def command_validate_contracts(args: argparse.Namespace) -> None:
    schema = require_object(read_json(Path(args.schema), "contract schema"), "contract schema")
    validate_schema_artifact(schema)
    platform = validate_contract_document(require_object(read_json(Path(args.platform_contract), "platform contract"), "platform contract"), "platform contract")
    mig03 = validate_contract_document(require_object(read_json(Path(args.mig03_contract), "MIG03 contract"), "MIG03 contract"), "MIG03 contract")
    manifest = load_manifest(Path(args.manifest), Path(args.repo_root).resolve())
    print(json.dumps({"contracts": len(platform) + len(mig03), "routes": len(manifest["registrations"]), "result": "PASS"}, sort_keys=True))


def command_resolve(args: argparse.Namespace) -> None:
    manifest = load_manifest(Path(args.manifest), Path(args.repo_root).resolve())
    registration = resolve_dispatch(manifest, args.scenario_id, args.variant_id, args.contract_version)
    print(json.dumps({field: registration[field] for field in ("scenarioId", "variantId", "contractVersion", "contractPath", "adapterStatus")}, sort_keys=True))


def command_validate_evidence(args: argparse.Namespace) -> None:
    manifest = load_manifest(Path(args.manifest), Path(args.repo_root).resolve())
    evidence = read_json(Path(args.evidence), "evidence")
    authority = read_json(Path(args.artifact_authority), "artifact authority")
    registration = validate_evidence(evidence, authority, manifest)
    print(json.dumps({"scenarioId": registration["scenarioId"], "variantId": registration["variantId"], "contractVersion": registration["contractVersion"], "result": "PASS"}, sort_keys=True))


def build_parser() -> argparse.ArgumentParser:
    parser = argparse.ArgumentParser(description=__doc__)
    sub = parser.add_subparsers(dest="command", required=True)
    contracts = sub.add_parser("validate-contracts")
    contracts.add_argument("--schema", default=str(DEFAULT_SCHEMA))
    contracts.add_argument("--platform-contract", default=str(ROOT / "docs" / "qualification" / "g583-s5a-platform-contract-v1.json"))
    contracts.add_argument("--mig03-contract", default=str(ROOT / "docs" / "qualification" / "g583-s5a-mig03-ci-auto-contract-v1.json"))
    contracts.add_argument("--manifest", default=str(DEFAULT_MANIFEST))
    contracts.add_argument("--repo-root", default=str(ROOT))
    contracts.set_defaults(func=command_validate_contracts)
    resolve = sub.add_parser("resolve")
    resolve.add_argument("--scenario-id", required=True)
    resolve.add_argument("--variant-id", required=True)
    resolve.add_argument("--contract-version", required=True)
    resolve.add_argument("--manifest", default=str(DEFAULT_MANIFEST))
    resolve.add_argument("--repo-root", default=str(ROOT))
    resolve.set_defaults(func=command_resolve)
    evidence = sub.add_parser("validate-evidence")
    evidence.add_argument("--evidence", required=True)
    evidence.add_argument("--artifact-authority", required=True)
    evidence.add_argument("--manifest", default=str(DEFAULT_MANIFEST))
    evidence.add_argument("--repo-root", default=str(ROOT))
    evidence.set_defaults(func=command_validate_evidence)
    return parser


def main(argv: list[str] | None = None) -> int:
    try:
        args = build_parser().parse_args(argv)
        args.func(args)
        return 0
    except DispatchError as exc:
        print(f"[error] {exc}", file=sys.stderr)
        return 1


if __name__ == "__main__":
    raise SystemExit(main())
