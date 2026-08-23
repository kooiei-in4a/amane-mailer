#!/usr/bin/env python3
"""Verify a published GHCR image and write value-free release evidence."""

from __future__ import annotations

import argparse
import json
import os
import shutil
import subprocess
import sys
from datetime import datetime, timezone
from pathlib import Path

PLATFORM = "linux/amd64"
SOURCE_LABEL = "https://github.com/kooiei-in4a/amane-mailer"
CRANE_VERSION = "0.20.3"


def die(message: str) -> None:
    print(f"[error] {message}", file=sys.stderr)
    raise SystemExit(1)


def command_exists(command: str) -> bool:
    return Path(command).is_file() if ("/" in command or "\\" in command) else shutil.which(command) is not None


def run_command(command: str, args: list[str], message: str) -> subprocess.CompletedProcess[str]:
    if not command_exists(command):
        die(f"{message}: command is missing")
    result = subprocess.run([command, *args], text=True, capture_output=True, check=False)
    if result.returncode != 0:
        die(message)
    return result


def registry_digest(crane: str, reference: str) -> str:
    result = run_command(crane, ["digest", reference], f"public registry digest lookup failed: {reference}")
    digest = result.stdout.strip()
    if len(digest) != 71 or not digest.startswith("sha256:") or any(
        character not in "0123456789abcdef" for character in digest[7:]
    ):
        die(f"registry returned an invalid digest for {reference}")
    return digest


def load_optional(path_value: str, description: str) -> dict | None:
    if not path_value:
        return None
    try:
        value = json.loads(Path(path_value).read_text(encoding="utf-8"))
    except (OSError, json.JSONDecodeError):
        return {"_error": f"{description} unavailable"}
    return value if isinstance(value, dict) else {"_error": f"{description} is not an object"}


def utc_now() -> str:
    return datetime.now(timezone.utc).isoformat(timespec="seconds").replace("+00:00", "Z")


def parse_args() -> argparse.Namespace:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--repository", default="ghcr.io/kooiei-in4a/amane-mailer")
    parser.add_argument("--expected-digest", required=True)
    parser.add_argument("--release-version", required=True)
    parser.add_argument("--release-commit-sha", required=True)
    parser.add_argument("--crane", default="crane")
    parser.add_argument("--docker", default=os.environ.get("DOCKER_BIN", "docker"))
    parser.add_argument("--report-file", default="artifacts/public-consumer-verification.json")
    parser.add_argument("--evidence-file", default="artifacts/release-publication-evidence.json")
    parser.add_argument("--identity-file", default="")
    parser.add_argument("--build-report", default="")
    parser.add_argument("--reproducibility-report", default="")
    return parser.parse_args()


def main() -> int:
    args = parse_args()
    if not args.repository or any(character not in "abcdefghijklmnopqrstuvwxyz0123456789./_-" for character in args.repository):
        die("repository contains unsupported characters")
    if "@" in args.repository or ":" in args.repository.rsplit("/", 1)[-1]:
        die("repository must not include a tag or digest")
    if (
        len(args.expected_digest) != 71
        or not args.expected_digest.startswith("sha256:")
        or any(character not in "0123456789abcdef" for character in args.expected_digest[7:])
    ):
        die("expected digest must be sha256:<64 lowercase hex>")
    if len(args.release_commit_sha) != 40 or any(
        character not in "0123456789abcdef" for character in args.release_commit_sha
    ):
        die("release commit SHA must be 40 lowercase hex")
    version_parts = args.release_version.split(".")
    if len(version_parts) != 3 or any(not part.isdigit() for part in version_parts):
        die("release version must be major.minor.patch")

    version_ref = f"{args.repository}:v{args.release_version}"
    sha_ref = f"{args.repository}:sha-{args.release_commit_sha}"
    digest_ref = f"{args.repository}@{args.expected_digest}"

    crane_version = run_command(args.crane, ["version"], "crane version check failed").stdout.splitlines()
    if not crane_version or crane_version[0].strip().rstrip("\r") != CRANE_VERSION:
        die(f"crane version must be {CRANE_VERSION}")

    version_digest = registry_digest(args.crane, version_ref)
    sha_digest = registry_digest(args.crane, sha_ref)

    run_command(
        args.docker,
        ["pull", "--platform", PLATFORM, digest_ref],
        "public digest image pull failed for linux/amd64",
    )
    labels_result = run_command(
        args.docker,
        ["image", "inspect", digest_ref, "--format", "{{json .Config.Labels}}"],
        "digest image labels could not be inspected",
    )
    try:
        labels = json.loads(labels_result.stdout)
    except json.JSONDecodeError as exc:
        raise SystemExit("docker returned invalid OCI labels JSON") from exc
    if not isinstance(labels, dict):
        die("docker returned non-object OCI labels")

    run_command(
        args.docker,
        ["run", "--rm", "--platform", PLATFORM, digest_ref, "--help"],
        "digest image --help failed for linux/amd64",
    )

    labels_match = {
        "source": labels.get("org.opencontainers.image.source") == SOURCE_LABEL,
        "revision": labels.get("org.opencontainers.image.revision") == args.release_commit_sha,
        "version": labels.get("org.opencontainers.image.version") == args.release_version,
    }
    public_checks = {
        "versionTagDigestMatches": version_digest == args.expected_digest,
        "immutableShaTagDigestMatches": sha_digest == args.expected_digest,
        "tagDigestsMatch": version_digest == sha_digest,
        "linuxAmd64Pull": True,
        "sourceLabelMatches": labels_match["source"],
        "revisionLabelMatches": labels_match["revision"],
        "versionLabelMatches": labels_match["version"],
        "digestImageHelp": True,
    }
    public_status = "PASS" if all(public_checks.values()) else "FAIL"
    recorded_at = utc_now()

    identity = load_optional(args.identity_file, "build identity")
    build_report = load_optional(args.build_report, "build smoke report")
    repro_report = load_optional(args.reproducibility_report, "reproducibility report")

    build_status = "NOT_PROVIDED"
    build_details: dict[str, object] = {}
    if identity is not None or build_report is not None:
        build_checks = identity.get("checks") if isinstance(identity, dict) else None
        smoke = build_report.get("smoke") if isinstance(build_report, dict) else None
        build_ok = (
            isinstance(identity, dict)
            and isinstance(build_report, dict)
            and "_error" not in identity
            and "_error" not in build_report
            and identity.get("sourceCommitSha") == args.release_commit_sha
            and identity.get("releaseVersion") == args.release_version
            and identity.get("platform") == PLATFORM
            and (identity.get("image") or {}).get("digest") == args.expected_digest
            and isinstance(build_checks, dict)
            and all(value is True for value in build_checks.values())
            and isinstance(smoke, dict)
            and all(value == "PASS" for value in smoke.values())
        )
        build_status = "PASS" if build_ok else "FAIL"
        build_details = {
            "identityFile": Path(args.identity_file).name if args.identity_file else None,
            "reportFile": Path(args.build_report).name if args.build_report else None,
        }

    repro_status = "NOT_PROVIDED"
    repro_details: dict[str, object] = {}
    if repro_report is not None:
        repro_ok = (
            "_error" not in repro_report
            and repro_report.get("sourceCommitSha") == args.release_commit_sha
            and repro_report.get("releaseVersion") == args.release_version
            and repro_report.get("platform") == PLATFORM
            and repro_report.get("expectedDigest") == args.expected_digest
            and repro_report.get("observedDigest") == args.expected_digest
            and repro_report.get("digestMatch") is True
        )
        repro_status = "PASS" if repro_ok else "FAIL"
        repro_details = {
            "reportFile": Path(args.reproducibility_report).name if args.reproducibility_report else None,
            "expectedDigest": repro_report.get("expectedDigest"),
            "observedDigest": repro_report.get("observedDigest"),
        }

    public_report = {
        "schemaVersion": 1,
        "evidenceType": "public-consumer-verification",
        "status": public_status,
        "sourceCommitSha": args.release_commit_sha,
        "releaseVersion": args.release_version,
        "platform": PLATFORM,
        "imageRepository": args.repository,
        "expectedDigest": args.expected_digest,
        "tags": {
            "version": {"ref": version_ref, "verifiedDigest": version_digest},
            "immutableSha": {"ref": sha_ref, "verifiedDigest": sha_digest},
        },
        "digestImage": {
            "ref": digest_ref,
            "pull": "PASS",
            "help": "PASS",
            "ociLabels": {
                "org.opencontainers.image.source": labels.get("org.opencontainers.image.source"),
                "org.opencontainers.image.revision": labels.get("org.opencontainers.image.revision"),
                "org.opencontainers.image.version": labels.get("org.opencontainers.image.version"),
            },
            "labelMatches": labels_match,
        },
        "checks": public_checks,
        "recordedAtUtc": recorded_at,
    }
    Path(args.report_file).parent.mkdir(parents=True, exist_ok=True)
    Path(args.report_file).write_text(json.dumps(public_report, indent=2, sort_keys=True) + "\n", encoding="utf-8")

    workflow_repository = os.environ.get("GITHUB_REPOSITORY", "local/repository")
    workflow_ref = os.environ.get(
        "GITHUB_WORKFLOW_REF",
        f"{workflow_repository}/.github/workflows/publish-release-image.yml@refs/heads/main",
    )
    evidence = {
        "schemaVersion": 1,
        "evidenceType": "release-image-publication",
        "workflowRunId": os.environ.get("GITHUB_RUN_ID", "local-self-test"),
        "workflowRunAttempt": int(os.environ.get("GITHUB_RUN_ATTEMPT", "1")),
        "workflowName": os.environ.get("GITHUB_WORKFLOW", "Publish Release Image"),
        "workflowRef": workflow_ref,
        "gitRef": os.environ.get("GITHUB_REF", "refs/heads/main"),
        "sourceCommitSha": args.release_commit_sha,
        "releaseVersion": args.release_version,
        "platform": PLATFORM,
        "image": {
            "repository": args.repository,
            "publishedDigest": args.expected_digest,
            "versionTag": version_ref,
            "immutableShaTag": sha_ref,
            "verifiedDigests": {
                "versionTag": version_digest,
                "immutableShaTag": sha_digest,
            },
            "ociLabels": public_report["digestImage"]["ociLabels"],
        },
        "checks": {
            "buildSmoke": {"status": build_status, **build_details},
            "noCacheReproducibility": {"status": repro_status, **repro_details},
            "publicConsumerVerification": {
                "status": public_status,
                "reportFile": Path(args.report_file).name,
                **public_checks,
            },
        },
        "recordedAtUtc": recorded_at,
    }
    Path(args.evidence_file).parent.mkdir(parents=True, exist_ok=True)
    Path(args.evidence_file).write_text(json.dumps(evidence, indent=2, sort_keys=True) + "\n", encoding="utf-8")

    if public_status != "PASS" or build_status == "FAIL" or repro_status == "FAIL":
        die("release image verification did not pass all supplied checks")

    print(f"[PASS] public consumer verification: {args.expected_digest}")
    print(f"[PASS] publication evidence: {args.evidence_file}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
