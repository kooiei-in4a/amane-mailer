#!/usr/bin/env python3
"""Value-free contract and dispatch self-tests for the G583 S5-A core."""

from __future__ import annotations

import copy
import importlib.util
import json
import tempfile
from pathlib import Path


ROOT = Path(__file__).resolve().parent.parent
DISPATCH = ROOT / "scripts" / "qualification-g583-dispatch.py"


def load_dispatch():
    spec = importlib.util.spec_from_file_location("qualification_g583_dispatch", DISPATCH)
    module = importlib.util.module_from_spec(spec)
    assert spec.loader is not None
    spec.loader.exec_module(module)
    return module


def main() -> int:
    dispatch = load_dispatch()
    schema = dispatch.require_object(dispatch.read_json(dispatch.DEFAULT_SCHEMA, "contract schema"), "contract schema")
    dispatch.validate_schema_artifact(schema)
    manifest = dispatch.load_manifest()
    if {tuple(registration[field] for field in ("scenarioId", "variantId", "contractVersion")) for registration in manifest["registrations"]} != dispatch.EXPECTED_ROUTES:
        raise AssertionError("dispatch routes changed")
    if len(manifest["registrations"]) != 5:
        raise AssertionError("exactly five dispatch routes are required")
    platform_contract = dispatch.validate_contract_document(
        dispatch.require_object(
            dispatch.read_json(ROOT / "docs" / "qualification" / "g583-s5a-platform-contract-v1.json", "platform contract"),
            "platform contract",
        ),
        "platform contract",
    )
    platform_routes = {
        (item["scenarioId"], item["laneVariant"], item["contractVersion"])
        for item in platform_contract
    }
    if platform_routes != dispatch.PLATFORM_ROUTES or len(platform_contract) != len(dispatch.EXPECTED_PLATFORM_CONTRACTS):
        raise AssertionError("platform contract route/allocation authority changed")
    for route in sorted(dispatch.EXPECTED_ROUTES):
        registration = dispatch.resolve_dispatch(manifest, *route)
        if tuple(registration[field] for field in ("scenarioId", "variantId", "contractVersion")) != route:
            raise AssertionError(f"route did not resolve exactly: {route}")

    candidate_id = "a" * 64
    release_sha = "b" * 40
    index_digest = "sha256:" + "c" * 64
    manifest_digest = "sha256:" + "d" * 64
    authority = {
        "candidateId": candidate_id,
        "releaseCommitSha": release_sha,
        "ociIndexDigest": index_digest,
        "selectedManifests": {"linux/amd64": manifest_digest, "linux/arm64": "sha256:" + "e" * 64},
    }
    def platform_evidence(scenario: str, variant: str, host: str, container: str):
        host_measurements = {
            "windows-x64": {"os": "Windows", "architecture": "amd64"},
            "linux-x64": {"os": "linux", "architecture": "amd64"},
            "linux-arm64": {"os": "linux", "architecture": "arm64"},
        }
        container_architecture = {"linux/amd64": "amd64", "linux/arm64": "arm64"}[container]
        selected_manifest = authority["selectedManifests"][container]
        return {
            "scenarioId": scenario,
            "variantId": variant,
            "laneVariant": variant,
            "contractVersion": dispatch.PLATFORM_CONTRACT,
            "hostPlatform": host,
            "dockerEngineOS": "linux",
            "containerPlatform": container,
            "measurements": {
                "hostPlatform": host_measurements[host],
                "dockerEngine": {"OSType": "Linux"},
                "containerImage": {"OS": "linux", "Architecture": container_architecture},
                "selectedOciDescriptor": {"platform": container, "manifestDigest": selected_manifest},
            },
            "artifactIdentity": {
                "candidateId": candidate_id,
                "releaseCommitSha": release_sha,
                "ociIndexDigest": index_digest,
                "selectedManifestDigest": selected_manifest,
            },
        }

    platform = platform_evidence("G583-MIG-01", "win-docker", "windows-x64", "linux/amd64")
    mig01_linux = platform_evidence("G583-MIG-01", "linux-docker", "linux-x64", "linux/amd64")
    mig02_win = platform_evidence("G583-MIG-02", "win-docker", "windows-x64", "linux/amd64")
    mig02_linux = platform_evidence("G583-MIG-02", "linux-docker", "linux-arm64", "linux/arm64")
    for evidence in (platform, mig01_linux, mig02_win, mig02_linux):
        dispatch.validate_evidence(evidence, authority, manifest)
    mig03 = {
        "scenarioId": "G583-MIG-03",
        "variantId": "ci-auto",
        "laneVariant": "ci-auto",
        "contractVersion": dispatch.MIG03_CONTRACT,
        "artifactIdentity": {
            "candidateId": candidate_id,
            "releaseCommitSha": release_sha,
            "ociIndexDigest": index_digest,
        },
    }
    dispatch.validate_evidence(mig03, authority, manifest)

    def expect_rejection(label, action):
        try:
            action()
        except dispatch.DispatchError:
            return
        raise AssertionError(f"negative case was accepted: {label}")

    raw_manifest = dispatch.require_object(dispatch.read_json(dispatch.DEFAULT_MANIFEST, "dispatch manifest"), "dispatch manifest")
    with tempfile.TemporaryDirectory(prefix="qualification-g583-core-self-test-") as temp:
        def reject_manifest(label, mutate):
            item = copy.deepcopy(raw_manifest)
            mutate(item)
            path = Path(temp) / f"{label}.json"
            path.write_text(json.dumps(item), encoding="utf-8")
            expect_rejection(label, lambda: dispatch.load_manifest(path))

        reject_manifest("missing-route", lambda item: item["registrations"].pop())
        reject_manifest(
            "duplicate-route",
            lambda item: item["registrations"].__setitem__(1, copy.deepcopy(item["registrations"][0])),
        )
        reject_manifest(
            "unexpected-sixth-route",
            lambda item: item["registrations"].append({
                **copy.deepcopy(item["registrations"][0]),
                "scenarioId": "G583-MIG-99",
            }),
        )

    raw_platform_contract = dispatch.require_object(
        dispatch.read_json(ROOT / "docs" / "qualification" / "g583-s5a-platform-contract-v1.json", "platform contract"),
        "platform contract",
    )
    missing_platform_route = copy.deepcopy(raw_platform_contract)
    missing_platform_route["contracts"] = [
        item for item in missing_platform_route["contracts"]
        if (item["scenarioId"], item["laneVariant"]) != ("G583-MIG-01", "linux-docker")
    ]
    expect_rejection(
        "platform-contract-missing-route",
        lambda: dispatch.validate_contract_document(missing_platform_route, "platform contract"),
    )

    expect_rejection("missing scenarioId", lambda: dispatch.resolve_dispatch(manifest, None, "win-docker", dispatch.PLATFORM_CONTRACT))
    expect_rejection("missing variantId", lambda: dispatch.resolve_dispatch(manifest, "G583-MIG-01", None, dispatch.PLATFORM_CONTRACT))
    expect_rejection("missing contractVersion", lambda: dispatch.resolve_dispatch(manifest, "G583-MIG-01", "win-docker", None))
    expect_rejection("variant-only dispatch", lambda: dispatch.resolve_dispatch(manifest, None, "linux-docker", dispatch.PLATFORM_CONTRACT))
    expect_rejection("unknown tuple", lambda: dispatch.resolve_dispatch(manifest, "G583-MIG-99", "win-docker", dispatch.PLATFORM_CONTRACT))
    expect_rejection("unknown contractVersion", lambda: dispatch.resolve_dispatch(manifest, "G583-MIG-01", "win-docker", "unknown-v1"))
    expect_rejection("G456 contract with G583 scenario", lambda: dispatch.resolve_dispatch(manifest, "G583-MIG-01", "win-docker", "g456-legacy-v4"))
    expect_rejection("G583 contract with G456 scenario", lambda: dispatch.resolve_dispatch(manifest, "G456-01", "win-docker", dispatch.PLATFORM_CONTRACT))
    expect_rejection("MIG03 win-docker", lambda: dispatch.resolve_dispatch(manifest, "G583-MIG-03", "win-docker", dispatch.MIG03_CONTRACT))
    expect_rejection("MIG03 linux-docker", lambda: dispatch.resolve_dispatch(manifest, "G583-MIG-03", "linux-docker", dispatch.MIG03_CONTRACT))
    expect_rejection("MIG01 ci-auto", lambda: dispatch.resolve_dispatch(manifest, "G583-MIG-01", "ci-auto", dispatch.PLATFORM_CONTRACT))
    expect_rejection("MIG02 ci-auto", lambda: dispatch.resolve_dispatch(manifest, "G583-MIG-02", "ci-auto", dispatch.PLATFORM_CONTRACT))

    def mutated_platform(source, mutate):
        item = copy.deepcopy(source)
        mutate(item)
        return lambda: dispatch.validate_evidence(item, authority, manifest)

    expect_rejection("host platform missing", mutated_platform(platform, lambda item: item.pop("hostPlatform")))
    expect_rejection("ambiguous linux host platform", mutated_platform(mig01_linux, lambda item: item.update({"hostPlatform": "linux-x64 or linux-arm64"})))
    expect_rejection("Docker engine mismatch", mutated_platform(platform, lambda item: (item.update({"dockerEngineOS": "windows"}), item["measurements"]["dockerEngine"].update({"OSType": "windows"}))))
    expect_rejection("ambiguous linux container platform", mutated_platform(mig02_linux, lambda item: item.update({"containerPlatform": "linux/amd64 or linux/arm64"})))
    expect_rejection("selected manifest mismatch", mutated_platform(platform, lambda item: item["artifactIdentity"].update({"selectedManifestDigest": "sha256:" + "f" * 64})))
    expect_rejection("candidate identity mismatch", mutated_platform(platform, lambda item: item["artifactIdentity"].update({"candidateId": "0" * 64})))
    expect_rejection("releaseCommitSha mismatch", mutated_platform(platform, lambda item: item["artifactIdentity"].update({"releaseCommitSha": "0" * 40})))
    mig03_with_platform = {**mig03, "hostPlatform": "linux-x64"}
    expect_rejection("MIG03 Docker platform field", lambda: dispatch.validate_evidence(mig03_with_platform, authority, manifest))
    expect_rejection("metadata-only adapter execution", lambda: dispatch.execute_dispatch(platform, authority, manifest, {}))

    print("[info] G583 S5-A contract/dispatch core self-test passed")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
