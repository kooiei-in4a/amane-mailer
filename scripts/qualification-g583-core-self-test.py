#!/usr/bin/env python3
"""Value-free contract and dispatch self-tests for the G583 S5-A core."""

from __future__ import annotations

import copy
import importlib.util
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
    platform = {
        "scenarioId": "G583-MIG-01",
        "variantId": "win-docker",
        "laneVariant": "win-docker",
        "contractVersion": dispatch.PLATFORM_CONTRACT,
        "hostPlatform": "windows-x64",
        "dockerEngineOS": "linux",
        "containerPlatform": "linux/amd64",
        "measurements": {
            "hostPlatform": {"os": "Windows", "architecture": "amd64"},
            "dockerEngine": {"OSType": "Linux"},
            "containerImage": {"OS": "linux", "Architecture": "amd64"},
            "selectedOciDescriptor": {"platform": "linux/amd64", "manifestDigest": manifest_digest},
        },
        "artifactIdentity": {
            "candidateId": candidate_id,
            "releaseCommitSha": release_sha,
            "ociIndexDigest": index_digest,
            "selectedManifestDigest": manifest_digest,
        },
    }
    dispatch.validate_evidence(platform, authority, manifest)
    linux = copy.deepcopy(platform)
    linux.update({"scenarioId": "G583-MIG-02", "variantId": "linux-docker", "laneVariant": "linux-docker", "hostPlatform": "linux-arm64", "containerPlatform": "linux/arm64"})
    linux["measurements"]["hostPlatform"] = {"os": "linux", "architecture": "arm64"}
    linux["measurements"]["containerImage"] = {"OS": "linux", "Architecture": "arm64"}
    linux["measurements"]["selectedOciDescriptor"] = {"platform": "linux/arm64", "manifestDigest": authority["selectedManifests"]["linux/arm64"]}
    linux["artifactIdentity"]["selectedManifestDigest"] = authority["selectedManifests"]["linux/arm64"]
    dispatch.validate_evidence(linux, authority, manifest)
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

    expect_rejection("missing scenarioId", lambda: dispatch.resolve_dispatch(manifest, None, "win-docker", dispatch.PLATFORM_CONTRACT))
    expect_rejection("missing variantId", lambda: dispatch.resolve_dispatch(manifest, "G583-MIG-01", None, dispatch.PLATFORM_CONTRACT))
    expect_rejection("missing contractVersion", lambda: dispatch.resolve_dispatch(manifest, "G583-MIG-01", "win-docker", None))
    expect_rejection("variant-only dispatch", lambda: dispatch.resolve_dispatch(manifest, None, "linux-docker", dispatch.PLATFORM_CONTRACT))
    expect_rejection("unknown contractVersion", lambda: dispatch.resolve_dispatch(manifest, "G583-MIG-01", "win-docker", "unknown-v1"))
    expect_rejection("G456 contract with G583 scenario", lambda: dispatch.resolve_dispatch(manifest, "G583-MIG-01", "win-docker", "g456-legacy-v4"))
    expect_rejection("G583 contract with G456 scenario", lambda: dispatch.resolve_dispatch(manifest, "G456-01", "win-docker", dispatch.PLATFORM_CONTRACT))
    expect_rejection("MIG03 win-docker", lambda: dispatch.resolve_dispatch(manifest, "G583-MIG-03", "win-docker", dispatch.MIG03_CONTRACT))
    expect_rejection("MIG03 linux-docker", lambda: dispatch.resolve_dispatch(manifest, "G583-MIG-03", "linux-docker", dispatch.MIG03_CONTRACT))
    expect_rejection("MIG01 ci-auto", lambda: dispatch.resolve_dispatch(manifest, "G583-MIG-01", "ci-auto", dispatch.PLATFORM_CONTRACT))
    expect_rejection("MIG02 ci-auto", lambda: dispatch.resolve_dispatch(manifest, "G583-MIG-02", "ci-auto", dispatch.PLATFORM_CONTRACT))

    def mutated_platform(mutate):
        item = copy.deepcopy(platform)
        mutate(item)
        return lambda: dispatch.validate_evidence(item, authority, manifest)

    expect_rejection("host platform missing", mutated_platform(lambda item: item.pop("hostPlatform")))
    expect_rejection("host platform ambiguous", mutated_platform(lambda item: item.update({"hostPlatform": "windows-x64 or linux-x64"})))
    expect_rejection("Docker engine mismatch", mutated_platform(lambda item: (item.update({"dockerEngineOS": "windows"}), item["measurements"]["dockerEngine"].update({"OSType": "windows"}))))
    expect_rejection("container platform ambiguous", mutated_platform(lambda item: item.update({"containerPlatform": "linux/amd64 or linux/arm64"})))
    expect_rejection("selected manifest mismatch", mutated_platform(lambda item: item["artifactIdentity"].update({"selectedManifestDigest": "sha256:" + "f" * 64})))
    expect_rejection("candidate identity mismatch", mutated_platform(lambda item: item["artifactIdentity"].update({"candidateId": "0" * 64})))
    expect_rejection("releaseCommitSha mismatch", mutated_platform(lambda item: item["artifactIdentity"].update({"releaseCommitSha": "0" * 40})))
    mig03_with_platform = {**mig03, "hostPlatform": "linux-x64"}
    expect_rejection("MIG03 Docker platform field", lambda: dispatch.validate_evidence(mig03_with_platform, authority, manifest))
    expect_rejection("metadata-only adapter execution", lambda: dispatch.execute_dispatch(platform, authority, manifest, {}))

    print("[info] G583 S5-A contract/dispatch core self-test passed")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
