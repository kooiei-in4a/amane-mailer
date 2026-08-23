#!/usr/bin/env bash
# Rebuild the exact linux/amd64 release image without cache and compare its
# manifest digest with the image that already passed release-image-build-smoke.
# This is a gate for reproducibility; it never logs in or publishes anything.
set -Eeuo pipefail
set +x

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" >/dev/null 2>&1 && pwd)"
REPO_ROOT="$(cd "${SCRIPT_DIR}/.." >/dev/null 2>&1 && pwd)"

SOURCE_SHA="${SOURCE_SHA:-}"
MAILER_VERSION="${MAILER_VERSION:-}"
EXPECTED_DIGEST="${EXPECTED_DIGEST:-}"
REPORT_DIR="${REPORT_DIR:-${REPO_ROOT}/artifacts/release-image-reproducibility}"
SOURCE_DATE_EPOCH="${SOURCE_DATE_EPOCH:-}"
ALLOW_DIRTY_WORKTREE="${ALLOW_DIRTY_WORKTREE:-0}"
PLATFORM="linux/amd64"

die() { printf '[error] %s\n' "$*" >&2; exit 1; }

command -v git >/dev/null 2>&1 || die 'git is required'
command -v python3 >/dev/null 2>&1 || die 'python3 is required'
command -v docker >/dev/null 2>&1 || die 'docker is required'
[[ "${SOURCE_SHA}" =~ ^[0-9a-f]{40}$ ]] || die 'SOURCE_SHA must be a lowercase 40-hex commit SHA'
[[ "${MAILER_VERSION}" =~ ^[0-9]+\.[0-9]+\.[0-9]+$ ]] || die 'MAILER_VERSION must be major.minor.patch'
[[ "${EXPECTED_DIGEST}" =~ ^sha256:[a-f0-9]{64}$ ]] || die 'EXPECTED_DIGEST must be sha256:<64 lowercase hex>'

CURRENT_SHA="$(git -C "${REPO_ROOT}" rev-parse HEAD)"
[[ "${CURRENT_SHA}" == "${SOURCE_SHA}" ]] || die "checked-out HEAD ${CURRENT_SHA} does not match SOURCE_SHA ${SOURCE_SHA}"
SOURCE_DATE_EPOCH="${SOURCE_DATE_EPOCH:-$(git -C "${REPO_ROOT}" show -s --format=%ct "${SOURCE_SHA}")}"
[[ "${SOURCE_DATE_EPOCH}" =~ ^[0-9]+$ ]] || die 'SOURCE_DATE_EPOCH must be a Unix timestamp in seconds'
WORKTREE_STATUS="$(git -C "${REPO_ROOT}" status --porcelain --untracked-files=all)"
if [[ -n "${WORKTREE_STATUS}" && "${ALLOW_DIRTY_WORKTREE}" != "1" ]]; then
  die 'working tree must be clean before the reproducibility rebuild'
fi

docker buildx version >/dev/null 2>&1 || die 'docker buildx is not available'
mkdir -p "${REPORT_DIR}"
METADATA_FILE="${REPORT_DIR}/rebuild-metadata.json"
BUILD_LOG="${REPORT_DIR}/rebuild.log"
REPORT_FILE="${REPORT_DIR}/report.json"
IMAGE_TAR="${REPORT_DIR}/rebuild.oci.tar"
IMAGE_REF="amane-mailer-release-reproducibility:sha-${SOURCE_SHA}"

if ! docker buildx build \
  --no-cache \
  --platform "${PLATFORM}" \
  --file "${REPO_ROOT}/infra/docker/Dockerfile" \
  --tag "${IMAGE_REF}" \
  --provenance=false \
  --sbom=false \
  --output "type=oci,dest=${IMAGE_TAR},rewrite-timestamp=true" \
  --build-arg "SOURCE_COMMIT=${SOURCE_SHA}" \
  --build-arg "MAILER_VERSION=${MAILER_VERSION}" \
  --build-arg "SOURCE_DATE_EPOCH=${SOURCE_DATE_EPOCH}" \
  --label "org.opencontainers.image.source=https://github.com/kooiei-in4a/amane-mailer" \
  --label "org.opencontainers.image.revision=${SOURCE_SHA}" \
  --label "org.opencontainers.image.version=${MAILER_VERSION}" \
  --metadata-file "${METADATA_FILE}" \
  "${REPO_ROOT}" 2>&1 | tee "${BUILD_LOG}"; then
  die 'no-cache reproducibility build failed'
fi

python3 - "${METADATA_FILE}" "${REPORT_FILE}" "${EXPECTED_DIGEST}" "${SOURCE_SHA}" "${MAILER_VERSION}" "${SOURCE_DATE_EPOCH}" "${PLATFORM}" <<'PY'
import json, sys
from pathlib import Path

metadata_path, report_path, expected, source_sha, version, epoch, platform = sys.argv[1:]
metadata = json.loads(Path(metadata_path).read_text(encoding="utf-8"))
descriptor = metadata.get("containerimage.descriptor")
observed = descriptor.get("digest") if isinstance(descriptor, dict) else metadata.get("containerimage.digest")
if not isinstance(observed, str) or not observed.startswith("sha256:"):
    raise SystemExit("Buildx metadata did not contain a valid image digest")
report = {
    "schemaVersion": 1,
    "sourceCommitSha": source_sha,
    "releaseVersion": version,
    "platform": platform,
    "sourceDateEpoch": int(epoch),
    "expectedDigest": expected,
    "observedDigest": observed,
    "digestMatch": observed == expected,
}
Path(report_path).write_text(json.dumps(report, indent=2, sort_keys=True) + "\n", encoding="utf-8")
if observed != expected:
    raise SystemExit(f"reproducibility digest mismatch: observed {observed} != expected {expected}")
print(f"[PASS] reproducibility digest: {observed}")
PY

rm -f "${IMAGE_TAR}"
