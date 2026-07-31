#!/usr/bin/env bash
# Build a local OCI image layout for Easy Setup release candidates (#455).
# Does NOT push to GHCR and does NOT create tags or GitHub Releases.
#
# Output:
#   <dest>/  … OCI layout (oci-layout, index.json, blobs/sha256/…)
#   stdout … ociIndexDigest=sha256:...
#
# Requirements: docker buildx
set -Eeuo pipefail
set +x

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" >/dev/null 2>&1 && pwd)"
REPO_ROOT="$(cd "${SCRIPT_DIR}/.." >/dev/null 2>&1 && pwd)"

DEST="${1:-}"
PLATFORM="${2:-linux/amd64}"
SOURCE_SHA="${SOURCE_SHA:-}"
MAILER_VERSION="${MAILER_VERSION:-0.0.0-candidate}"

if [[ -z "${DEST}" ]]; then
  echo "Usage: $0 <oci-layout-dest-dir> [platform]" >&2
  exit 2
fi

if ! command -v docker >/dev/null 2>&1; then
  echo "[error] docker is required to build the candidate OCI layout." >&2
  exit 1
fi

if [[ -z "${SOURCE_SHA}" ]]; then
  SOURCE_SHA="$(git -C "${REPO_ROOT}" rev-parse HEAD)"
fi

mkdir -p "$(dirname "${DEST}")"
rm -rf "${DEST}"
mkdir -p "${DEST}"

echo "[info] Building candidate OCI layout at ${DEST} (platform=${PLATFORM}, no GHCR push)"

docker buildx build \
  --file "${REPO_ROOT}/infra/docker/Dockerfile" \
  --platform "${PLATFORM}" \
  --build-arg "SOURCE_COMMIT=${SOURCE_SHA}" \
  --label "org.opencontainers.image.source=https://github.com/kooiei-in4a/amane-mailer" \
  --label "org.opencontainers.image.revision=${SOURCE_SHA}" \
  --label "org.opencontainers.image.version=${MAILER_VERSION}" \
  --output "type=oci,dest=${DEST}" \
  "${REPO_ROOT}"

if [[ ! -f "${DEST}/oci-layout" || ! -f "${DEST}/index.json" ]]; then
  echo "[error] OCI layout incomplete: missing oci-layout or index.json (B1)." >&2
  exit 1
fi

if [[ ! -d "${DEST}/blobs/sha256" ]]; then
  echo "[error] OCI layout incomplete: missing blobs/sha256." >&2
  exit 1
fi

DIGEST="sha256:$(sha256sum "${DEST}/index.json" | awk '{print $1}')"
echo "ociIndexDigest=${DIGEST}"
echo "${DIGEST}" > "${DEST}/../oci-index.digest"
