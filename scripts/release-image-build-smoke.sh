#!/usr/bin/env bash
# Build and smoke-test one exact release image without publishing it.
#
# This is the first, deliberately small, release-engineering slice for #649:
#   exact source SHA -> linux/amd64 image/OCI layout -> identity checks -> smoke
#
# The script does not log in to a registry, push an image, run qualification,
# or mutate any durable release state. The compose smoke uses only the safe
# example tenant and a throwaway SQLite volume.
#
# Required environment:
#   SOURCE_SHA       exact 40-hex commit checked out in the working tree
#   MAILER_VERSION   major.minor.patch image version label
#   SOURCE_DATE_EPOCH reproducible build timestamp (defaults to the source commit timestamp)
#
# Optional environment:
#   IMAGE_REPOSITORY        local image repository (default amane-mailer-release-smoke)
#   IMAGE_TAG               local image tag (default sha-${SOURCE_SHA})
#   REPORT_DIR              report directory (default artifacts/release-image-build-smoke)
#   RELEASE_SMOKE_PROJECT   compose project name
#   MAILER_HTTP_PORT        host port for health/readiness checks (default 15280)
#   MAILPIT_HTTP_PORT       helper host port (default 18025)
#   MAILPIT_IMAGE           helper image (default axllent/mailpit:latest)
#   RELEASE_SMOKE_INIT_IMAGE helper image for the throwaway data volume
#   ALLOW_DIRTY_WORKTREE    development-only escape hatch; never set in CI
#
# Dependencies: bash, git, curl, tar, python3, docker with buildx and compose.
set -Eeuo pipefail
set +x

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" >/dev/null 2>&1 && pwd)"
REPO_ROOT="$(cd "${SCRIPT_DIR}/.." >/dev/null 2>&1 && pwd)"

SOURCE_SHA="${SOURCE_SHA:-}"
MAILER_VERSION="${MAILER_VERSION:-}"
IMAGE_REPOSITORY="${IMAGE_REPOSITORY:-amane-mailer-release-smoke}"
IMAGE_TAG="${IMAGE_TAG:-}"
REPORT_DIR="${REPORT_DIR:-${REPO_ROOT}/artifacts/release-image-build-smoke}"
PLATFORM="linux/amd64"
MAILER_HTTP_PORT="${MAILER_HTTP_PORT:-15280}"
MAILPIT_HTTP_PORT="${MAILPIT_HTTP_PORT:-18025}"
MAILPIT_IMAGE="${MAILPIT_IMAGE:-axllent/mailpit:latest}"
RELEASE_SMOKE_INIT_IMAGE="${RELEASE_SMOKE_INIT_IMAGE:-busybox:1.37}"
RELEASE_SMOKE_PROJECT="${RELEASE_SMOKE_PROJECT:-}"
ALLOW_DIRTY_WORKTREE="${ALLOW_DIRTY_WORKTREE:-0}"
COMPOSE_FILE="${REPO_ROOT}/infra/docker/docker-compose.release-smoke.yml"

die() {
  printf '[error] %s\n' "$*" >&2
  exit 1
}

require_command() {
  command -v "$1" >/dev/null 2>&1 || die "required command is missing: $1"
}

require_command git
require_command curl
require_command python3
require_command docker

[[ -n "${SOURCE_SHA}" ]] || die 'SOURCE_SHA is required'
[[ "${SOURCE_SHA}" =~ ^[0-9a-f]{40}$ ]] || die 'SOURCE_SHA must be a lowercase 40-hex commit SHA'
[[ -n "${MAILER_VERSION}" ]] || die 'MAILER_VERSION is required'
[[ "${MAILER_VERSION}" =~ ^[0-9]+\.[0-9]+\.[0-9]+$ ]] || die 'MAILER_VERSION must be major.minor.patch'
[[ "${IMAGE_REPOSITORY}" =~ ^[a-z0-9./_-]+$ ]] || die 'IMAGE_REPOSITORY contains unsupported characters'

if [[ -z "${IMAGE_TAG}" ]]; then
  IMAGE_TAG="sha-${SOURCE_SHA}"
fi
[[ "${IMAGE_TAG}" =~ ^[A-Za-z0-9_.-]+$ ]] || die 'IMAGE_TAG contains unsupported characters'

if [[ -z "${RELEASE_SMOKE_PROJECT}" ]]; then
  RELEASE_SMOKE_PROJECT="release-image-build-smoke-${SOURCE_SHA:0:12}"
fi
[[ "${RELEASE_SMOKE_PROJECT}" =~ ^[a-z0-9][a-z0-9_-]*$ ]] || die 'RELEASE_SMOKE_PROJECT contains unsupported characters'

docker buildx version >/dev/null 2>&1 || die 'docker buildx is not available'
docker compose version >/dev/null 2>&1 || die 'docker compose is not available'

CURRENT_SHA="$(git -C "${REPO_ROOT}" rev-parse HEAD)"
[[ "${CURRENT_SHA}" == "${SOURCE_SHA}" ]] || die "checked-out HEAD ${CURRENT_SHA} does not match SOURCE_SHA ${SOURCE_SHA}"
SOURCE_DATE_EPOCH="${SOURCE_DATE_EPOCH:-$(git -C "${REPO_ROOT}" show -s --format=%ct "${SOURCE_SHA}")}"
[[ "${SOURCE_DATE_EPOCH}" =~ ^[0-9]+$ ]] || die 'SOURCE_DATE_EPOCH must be a Unix timestamp in seconds'
WORKTREE_STATUS="$(git -C "${REPO_ROOT}" status --porcelain --untracked-files=all)"
if [[ -n "${WORKTREE_STATUS}" ]]; then
  if [[ "${ALLOW_DIRTY_WORKTREE}" != "1" ]]; then
    die 'working tree must be clean before the exact-source build'
  fi
  printf '[warning] ALLOW_DIRTY_WORKTREE=1; use only for local development\n' >&2
fi

mkdir -p "${REPORT_DIR}"
IMAGE_REF="${IMAGE_REPOSITORY}:${IMAGE_TAG}"
METADATA_FILE="${REPORT_DIR}/buildx-metadata.json"
INSPECT_FILE="${REPORT_DIR}/docker-image-inspect.json"
IDENTITY_FILE="${REPORT_DIR}/identity.json"
HELP_FILE="${REPORT_DIR}/help.txt"
HEALTH_FILE="${REPORT_DIR}/healthz.json"
READY_FILE="${REPORT_DIR}/readyz.json"
BUILD_LOG="${REPORT_DIR}/build.log"
COMPOSE_LOG="${REPORT_DIR}/compose-up.log"
REPORT_FILE="${REPORT_DIR}/report.json"
IMAGE_TAR="${REPORT_DIR}/image.oci.tar"
DOCKER_IMAGE_TAR="${REPORT_DIR}/image.docker.tar"
OCI_LAYOUT_DIR="${REPORT_DIR}/oci-layout"

printf '== Amane Mailer release image build smoke ==\n'
printf 'source:   %s\n' "${SOURCE_SHA}"
printf 'version:  %s\n' "${MAILER_VERSION}"
printf 'platform: %s\n' "${PLATFORM}"
printf 'timestamp: %s\n' "${SOURCE_DATE_EPOCH}"
printf 'image:    %s\n' "${IMAGE_REF}"

# Keep the Docker archive first and the OCI archive last. Buildx writes the
# last exporter descriptor to the metadata file, so the publication digest
# must remain the OCI layout digest while docker load gets a compatible
# single-platform archive for the runtime smoke checks.
if ! docker buildx build \
  --platform "${PLATFORM}" \
  --file "${REPO_ROOT}/infra/docker/Dockerfile" \
  --tag "${IMAGE_REF}" \
  --provenance=false \
  --sbom=false \
  --output "type=docker,dest=${DOCKER_IMAGE_TAR}" \
  --output "type=oci,dest=${IMAGE_TAR},rewrite-timestamp=true" \
  --build-arg "SOURCE_COMMIT=${SOURCE_SHA}" \
  --build-arg "MAILER_VERSION=${MAILER_VERSION}" \
  --build-arg "SOURCE_DATE_EPOCH=${SOURCE_DATE_EPOCH}" \
  --label "org.opencontainers.image.source=https://github.com/kooiei-in4a/amane-mailer" \
  --label "org.opencontainers.image.revision=${SOURCE_SHA}" \
  --label "org.opencontainers.image.version=${MAILER_VERSION}" \
  --metadata-file "${METADATA_FILE}" \
  "${REPO_ROOT}" 2>&1 | tee "${BUILD_LOG}"; then
  die 'docker image build failed'
fi

rm -rf "${OCI_LAYOUT_DIR}"
mkdir -p "${OCI_LAYOUT_DIR}"
if ! tar -xf "${IMAGE_TAR}" -C "${OCI_LAYOUT_DIR}"; then
  die 'OCI layout extraction failed'
fi

if ! docker load --input "${DOCKER_IMAGE_TAR}" >"${REPORT_DIR}/docker-load.log" 2>&1; then
  cat "${REPORT_DIR}/docker-load.log" >&2
  die 'docker image load failed'
fi
rm -f "${IMAGE_TAR}" "${DOCKER_IMAGE_TAR}"

[[ -f "${OCI_LAYOUT_DIR}/oci-layout" && -f "${OCI_LAYOUT_DIR}/index.json" ]] \
  || die 'built OCI layout is incomplete'

docker image inspect "${IMAGE_REF}" >"${INSPECT_FILE}" \
  || die 'built image could not be inspected'

python3 - "${METADATA_FILE}" "${INSPECT_FILE}" "${OCI_LAYOUT_DIR}/index.json" "${IDENTITY_FILE}" "${IMAGE_REF}" "${SOURCE_SHA}" "${MAILER_VERSION}" "${PLATFORM}" "${SOURCE_DATE_EPOCH}" <<'PY'
import json
import sys
from pathlib import Path

metadata_path, inspect_path, index_path, identity_path, image_ref, source_sha, version, platform, source_date_epoch = sys.argv[1:]

metadata = json.loads(Path(metadata_path).read_text(encoding="utf-8"))
inspect_values = json.loads(Path(inspect_path).read_text(encoding="utf-8"))
if not isinstance(inspect_values, list) or len(inspect_values) != 1:
    raise SystemExit("docker image inspect must return exactly one image")
image = inspect_values[0]

descriptor = metadata.get("containerimage.descriptor")
digest = descriptor.get("digest") if isinstance(descriptor, dict) else None
if not isinstance(digest, str):
    digest = metadata.get("containerimage.digest")
if not isinstance(digest, str) or not digest.startswith("sha256:") or len(digest) != 71:
    raise SystemExit("Buildx metadata did not contain a valid image digest")

layout = json.loads(Path(index_path).read_text(encoding="utf-8"))
manifests = layout.get("manifests") or []
if len(manifests) != 1:
    raise SystemExit("release smoke OCI layout must contain exactly one runtime manifest")
layout_descriptor = manifests[0]
layout_platform = layout_descriptor.get("platform") or {}
if layout_descriptor.get("digest") != digest:
    raise SystemExit("OCI layout digest does not match Buildx metadata digest")
if layout_platform.get("os") != "linux" or layout_platform.get("architecture") != "amd64":
    raise SystemExit("release smoke OCI layout must be linux/amd64")

labels = image.get("Config", {}).get("Labels", {})
checks = {
    "os": image.get("Os") == "linux",
    "architecture": image.get("Architecture") == "amd64",
    "source_label": labels.get("org.opencontainers.image.revision") == source_sha,
    "version_label": labels.get("org.opencontainers.image.version") == version,
    "source_url_label": labels.get("org.opencontainers.image.source") == "https://github.com/kooiei-in4a/amane-mailer",
    "oci_layout_digest": layout_descriptor.get("digest") == digest,
    "oci_layout_platform": layout_platform.get("os") == "linux" and layout_platform.get("architecture") == "amd64",
}
failed = [name for name, passed in checks.items() if not passed]
if failed:
    raise SystemExit("image identity check failed: " + ", ".join(failed))

identity = {
    "schemaVersion": 1,
    "sourceCommitSha": source_sha,
    "releaseVersion": version,
    "platform": platform,
    "sourceDateEpoch": int(source_date_epoch),
    "image": {
        "ref": image_ref,
        "digest": digest,
        "imageId": image.get("Id"),
    },
    "labels": {
        "org.opencontainers.image.source": labels.get("org.opencontainers.image.source"),
        "org.opencontainers.image.revision": labels.get("org.opencontainers.image.revision"),
        "org.opencontainers.image.version": labels.get("org.opencontainers.image.version"),
    },
    "checks": checks,
}
Path(identity_path).write_text(json.dumps(identity, indent=2, sort_keys=True) + "\n", encoding="utf-8")
print(f"[PASS] image identity: {digest}")
PY

if docker run --rm --platform "${PLATFORM}" "${IMAGE_REF}" --help >"${HELP_FILE}" 2>&1; then
  printf '[PASS] container --help\n'
else
  die 'container --help failed'
fi

MAILER_URL="http://127.0.0.1:${MAILER_HTTP_PORT}"
COMPOSE=(docker compose -f "${COMPOSE_FILE}")
export MAILER_IMAGE_REPOSITORY="${IMAGE_REPOSITORY}"
export MAILER_IMAGE_TAG="${IMAGE_TAG}"
export MAILER_IMAGE_PLATFORM="${PLATFORM}"
export MAILER_PULL_POLICY="never"
export MAILPIT_IMAGE
export RELEASE_SMOKE_INIT_IMAGE
export MAILER_HTTP_PORT
export MAILPIT_HTTP_PORT
export RELEASE_SMOKE_PROJECT
export MAIL_SERVICE_TOKEN="local-mail-service-token"

cleanup() {
  local status=$?
  if [[ "${status}" -ne 0 ]]; then
    printf '[diagnostic] compose status\n' >&2
    "${COMPOSE[@]}" ps >&2 2>/dev/null || true
  fi
  "${COMPOSE[@]}" down -v --remove-orphans >/dev/null 2>&1 || true
  exit "${status}"
}
trap cleanup EXIT

if ! "${COMPOSE[@]}" up -d --wait mailer >"${COMPOSE_LOG}" 2>&1; then
  cat "${COMPOSE_LOG}" >&2
  die 'release-smoke compose failed to start'
fi

wait_for_http() {
  local path="$1" output="$2" attempt
  for attempt in $(seq 1 30); do
    if curl -fsS -m 15 "${MAILER_URL}${path}" -o "${output}"; then
      return 0
    fi
    sleep 2
  done
  return 1
}

if wait_for_http '/healthz' "${HEALTH_FILE}"; then
  printf '[PASS] GET /healthz -> 200\n'
else
  die 'GET /healthz did not return 200'
fi

if wait_for_http '/readyz' "${READY_FILE}"; then
  printf '[PASS] GET /readyz -> 200\n'
else
  die 'GET /readyz did not return 200'
fi

python3 - "${IDENTITY_FILE}" "${REPORT_FILE}" "${HELP_FILE}" "${HEALTH_FILE}" "${READY_FILE}" <<'PY'
import json
import sys
from pathlib import Path

identity_path, report_path, help_path, health_path, ready_path = sys.argv[1:]
identity = json.loads(Path(identity_path).read_text(encoding="utf-8"))
report = {
    "schemaVersion": 1,
    "sourceCommitSha": identity["sourceCommitSha"],
    "releaseVersion": identity["releaseVersion"],
    "platform": identity["platform"],
    "sourceDateEpoch": identity["sourceDateEpoch"],
    "image": identity["image"],
    "identityChecks": identity["checks"],
    "smoke": {
        "containerHelp": "PASS",
        "healthz": "PASS",
        "readyz": "PASS",
    },
    "outputs": {
        "helpFile": Path(help_path).name,
        "healthzFile": Path(health_path).name,
        "readyzFile": Path(ready_path).name,
    },
}
Path(report_path).write_text(json.dumps(report, indent=2, sort_keys=True) + "\n", encoding="utf-8")
PY

printf '[PASS] report: %s\n' "${REPORT_FILE}"
