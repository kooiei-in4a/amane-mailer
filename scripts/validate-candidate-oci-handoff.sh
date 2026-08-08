#!/usr/bin/env bash
# Validate the candidate OCI artifact and its handoff provenance before login.
# This is intentionally read-only: it never contacts a registry.
set -Eeuo pipefail
set +x

OCI_ROOT=""
HANDOFF_ROOT=""
EXPECTED_DIGEST=""
RELEASE_VERSION=""
RELEASE_COMMIT_SHA=""
CANDIDATE_RUN_ID=""
CANDIDATE_RUN_ATTEMPT=""
CANDIDATE_ID=""
IMAGE_REPOSITORY=""

die() { echo "[error] $*" >&2; exit 1; }

while [[ $# -gt 0 ]]; do
  case "$1" in
    --oci-root) OCI_ROOT="${2:-}"; shift 2 ;;
    --handoff-root) HANDOFF_ROOT="${2:-}"; shift 2 ;;
    --expected-digest) EXPECTED_DIGEST="${2:-}"; shift 2 ;;
    --release-version) RELEASE_VERSION="${2:-}"; shift 2 ;;
    --release-commit-sha) RELEASE_COMMIT_SHA="${2:-}"; shift 2 ;;
    --candidate-run-id) CANDIDATE_RUN_ID="${2:-}"; shift 2 ;;
    --candidate-run-attempt) CANDIDATE_RUN_ATTEMPT="${2:-}"; shift 2 ;;
    --candidate-id) CANDIDATE_ID="${2:-}"; shift 2 ;;
    --repository) IMAGE_REPOSITORY="${2:-}"; shift 2 ;;
    -h|--help)
      sed -n '2,18p' "$0" | sed 's/^# \{0,1\}//'
      exit 0
      ;;
    *) die "unknown argument: $1" ;;
  esac
done

[[ -d "${OCI_ROOT}" && -f "${OCI_ROOT}/oci-layout" && -f "${OCI_ROOT}/index.json" ]] \
  || die "OCI layout is incomplete"
[[ -d "${HANDOFF_ROOT}" ]] || die "candidate handoff directory is missing"
[[ "${EXPECTED_DIGEST}" =~ ^sha256:[a-f0-9]{64}$ ]] || die "expected digest must be sha256:<64 lowercase hex>"
[[ "${RELEASE_VERSION}" =~ ^[0-9]+\.[0-9]+\.[0-9]+$ ]] || die "release version must be major.minor.patch"
[[ "${RELEASE_COMMIT_SHA}" =~ ^[0-9a-f]{40}$ ]] || die "release commit SHA must be 40 lowercase hex"
[[ "${CANDIDATE_RUN_ID}" =~ ^[0-9]+$ ]] || die "candidate workflow run ID must be numeric"
[[ "${CANDIDATE_RUN_ATTEMPT}" =~ ^[1-9][0-9]*$ ]] || die "candidate workflow run attempt must be a positive integer"
[[ "${CANDIDATE_ID}" =~ ^[0-9a-f]{64}$ ]] || die "candidate ID must be 64 lowercase hex"
[[ -n "${IMAGE_REPOSITORY}" ]] || die "image repository is required"

command -v python3 >/dev/null 2>&1 || die "python3 is required"

export OCI_ROOT HANDOFF_ROOT EXPECTED_DIGEST RELEASE_VERSION RELEASE_COMMIT_SHA
export CANDIDATE_RUN_ID CANDIDATE_RUN_ATTEMPT CANDIDATE_ID IMAGE_REPOSITORY
python3 - <<'PY'
import hashlib
import json
import os
from pathlib import Path

oci = Path(os.environ["OCI_ROOT"])
handoff = Path(os.environ["HANDOFF_ROOT"])
expected = os.environ["EXPECTED_DIGEST"]
version = os.environ["RELEASE_VERSION"]
commit = os.environ["RELEASE_COMMIT_SHA"]
run_id = os.environ["CANDIDATE_RUN_ID"]
run_attempt = os.environ["CANDIDATE_RUN_ATTEMPT"]
candidate_id = os.environ["CANDIDATE_ID"]
repository = os.environ["IMAGE_REPOSITORY"]

def fail(field, message):
    raise SystemExit(f"{field}: {message}")

def read_json(path, field):
    if not path.is_file():
        fail(field, "missing")
    try:
        return json.loads(path.read_text(encoding="utf-8"))
    except Exception:
        fail(field, "invalid JSON")

def equal(field, actual, wanted):
    if actual != wanted:
        fail(field, "mismatch")

identity = read_json(handoff / "image-identity.json", "image-identity.json")
provenance = read_json(handoff / "candidate-provenance.json", "candidate-provenance.json")
oci_identity = read_json(oci.parent / "image-identity.json", "OCI image-identity.json")
metadata = read_json(oci.parent / "buildx-metadata.json", "buildx-metadata.json")

equal("imageRepository", identity.get("imageRepository"), repository)
equal("sourceCommitSha", identity.get("sourceCommitSha"), commit)
equal("mailerVersion", identity.get("mailerVersion"), version)
equal("imageTag", identity.get("imageTag"), f"sha-{commit}")
equal("imageDigest", identity.get("imageDigest"), expected)
equal("oci imageRepository", oci_identity.get("imageRepository"), repository)
equal("oci sourceCommitSha", oci_identity.get("sourceCommitSha"), commit)
equal("oci mailerVersion", oci_identity.get("mailerVersion"), version)
equal("oci imageTag", oci_identity.get("imageTag"), f"sha-{commit}")
equal("oci imageDigest", oci_identity.get("imageDigest"), expected)

equal("provenance.schemaVersion", provenance.get("schemaVersion"), 1)
equal("provenance.sourceCommitSha", provenance.get("sourceCommitSha"), commit)
equal("provenance.releaseVersion", provenance.get("releaseVersion"), version)
equal("provenance.workflowRunId", str(provenance.get("workflowRunId")), run_id)
equal("provenance.workflowRunAttempt", str(provenance.get("workflowRunAttempt")), run_attempt)
equal("provenance.imageRepository", provenance.get("imageRepository"), repository)
equal("provenance.imageTag", provenance.get("imageTag"), f"sha-{commit}")
equal("provenance.ociIndexDigest", provenance.get("ociIndexDigest"), expected)
if sorted(provenance.get("ociPlatforms") or []) != ["linux/amd64", "linux/arm64"]:
    fail("provenance.ociPlatforms", "must contain exactly linux/amd64 and linux/arm64")
archives = provenance.get("archives") or []
if not archives:
    fail("provenance.archives", "must not be empty")
archive_digests = []
for archive in sorted(archives, key=lambda item: str(item.get("targetRid") or "")):
    value = archive.get("archiveSha256")
    if not isinstance(value, str) or not value.startswith("sha256:"):
        fail("provenance.archives.archiveSha256", "invalid digest")
    archive_digests.append(value)
derived_candidate_id = hashlib.sha256(
    (commit + "|" + run_id + "|" + run_attempt + "|" + expected + "|" + "|".join(archive_digests)).encode("utf-8")
).hexdigest()
equal("candidateId", derived_candidate_id, candidate_id)

digest_file = oci.parent / "oci-index.digest"
if not digest_file.is_file():
    fail("oci-index.digest", "missing")
equal("oci-index.digest", digest_file.read_text(encoding="utf-8").strip(), expected)

index = read_json(oci / "index.json", "index.json")
manifests = index.get("manifests")
if not isinstance(manifests, list) or len(manifests) != 1:
    fail("index.json", "must contain one final image-index descriptor")
root = manifests[0]
if root.get("mediaType") not in {
    "application/vnd.oci.image.index.v1+json",
    "application/vnd.docker.distribution.manifest.list.v2+json",
}:
    fail("index.json", "root descriptor is not an image index")
if root.get("platform"):
    fail("index.json", "root image-index descriptor must not have a platform")
digest = root.get("digest")
if digest != expected or not isinstance(digest, str) or not digest.startswith("sha256:"):
    fail("index.json", "root descriptor digest mismatch")
blob = oci / "blobs" / "sha256" / digest.removeprefix("sha256:")
if not blob.is_file():
    fail("image-index blob", "missing")
if "sha256:" + hashlib.sha256(blob.read_bytes()).hexdigest() != expected:
    fail("image-index blob", "digest mismatch")

descriptor = metadata.get("containerimage.descriptor") or {}
equal("buildx metadata digest", metadata.get("containerimage.digest"), expected)
equal("buildx descriptor digest", descriptor.get("digest"), expected)

print("[info] candidate OCI, provenance, identity, and metadata bindings validated")
PY

echo "[info] candidate handoff validation passed (no registry access)"
