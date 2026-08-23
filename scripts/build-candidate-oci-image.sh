#!/usr/bin/env bash
# Build a local single- or multi-platform OCI image layout for Easy Setup release candidates (#455).
# Does NOT push to GHCR and does NOT create tags or GitHub Releases.
#
# Output:
#   <dest>/                 … OCI layout (oci-layout, index.json, blobs/sha256/…)
#   <dest>/../buildx-metadata.json
#   stdout … imageDigest=sha256:... (from Buildx containerimage.digest)
#
# Requirements: docker buildx
set -Eeuo pipefail
set +x

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" >/dev/null 2>&1 && pwd)"
REPO_ROOT="$(cd "${SCRIPT_DIR}/.." >/dev/null 2>&1 && pwd)"

DEST="${1:-}"
PLATFORM="${2:-linux/amd64,linux/arm64}"
REQUIRED_PLATFORMS="${REQUIRED_PLATFORMS:-${PLATFORM}}"
WRITE_IMAGE_IDENTITY="${WRITE_IMAGE_IDENTITY:-1}"
SOURCE_SHA="${SOURCE_SHA:-}"
MAILER_VERSION="${MAILER_VERSION:-}"
SOURCE_DATE_EPOCH="${SOURCE_DATE_EPOCH:-}"
IMAGE_REPOSITORY="${IMAGE_REPOSITORY:-ghcr.io/kooiei-in4a/amane-mailer}"
IMAGE_TAG="${IMAGE_TAG:-}"

if [[ -z "${DEST}" ]]; then
  echo "Usage: $0 <oci-layout-dest-dir> [platforms]" >&2
  exit 2
fi

if ! command -v docker >/dev/null 2>&1; then
  echo "[error] docker is required to build the candidate OCI layout." >&2
  exit 1
fi

if [[ -z "${SOURCE_SHA}" ]]; then
  SOURCE_SHA="$(git -C "${REPO_ROOT}" rev-parse HEAD)"
fi

if [[ -z "${SOURCE_DATE_EPOCH}" ]]; then
  SOURCE_DATE_EPOCH="$(git -C "${REPO_ROOT}" show -s --format=%ct "${SOURCE_SHA}")"
fi

if [[ -z "${MAILER_VERSION}" ]]; then
  echo "[error] MAILER_VERSION (major.minor.patch) is required." >&2
  exit 1
fi

if [[ ! "${MAILER_VERSION}" =~ ^[0-9]+\.[0-9]+\.[0-9]+$ ]]; then
  echo "[error] MAILER_VERSION must be major.minor.patch only (not ${MAILER_VERSION})." >&2
  exit 1
fi

if [[ ! "${SOURCE_DATE_EPOCH}" =~ ^[0-9]+$ ]]; then
  echo "[error] SOURCE_DATE_EPOCH must be a Unix timestamp in seconds." >&2
  exit 1
fi

if [[ -z "${IMAGE_TAG}" ]]; then
  IMAGE_TAG="sha-${SOURCE_SHA}"
fi

PARENT="$(cd "$(dirname "${DEST}")" >/dev/null 2>&1 && pwd)"
METADATA_FILE="${PARENT}/buildx-metadata.json"
IDENTITY_FILE="${PARENT}/image-identity.json"

mkdir -p "${PARENT}"
rm -rf "${DEST}"
mkdir -p "${DEST}"
rm -f "${METADATA_FILE}"

echo "[info] Building candidate OCI layout at ${DEST} (platform=${PLATFORM}, no GHCR push)"

# EXTERNAL_PROVENANCE (D-ATTEST 方式2): do not embed Buildx provenance/SBOM
# attestation manifests in the candidate OCI index. Registry attestation is out
# of band for v1.2.0; including them would change the image-index digest identity.
docker buildx build \
  --file "${REPO_ROOT}/infra/docker/Dockerfile" \
  --platform "${PLATFORM}" \
  --provenance=false \
  --sbom=false \
  --build-arg "SOURCE_COMMIT=${SOURCE_SHA}" \
  --build-arg "MAILER_VERSION=${MAILER_VERSION}" \
  --build-arg "SOURCE_DATE_EPOCH=${SOURCE_DATE_EPOCH}" \
  --label "org.opencontainers.image.source=https://github.com/kooiei-in4a/amane-mailer" \
  --label "org.opencontainers.image.revision=${SOURCE_SHA}" \
  --label "org.opencontainers.image.version=${MAILER_VERSION}" \
  --metadata-file "${METADATA_FILE}" \
  --output "type=oci,dest=${DEST},tar=false" \
  "${REPO_ROOT}"

if [[ ! -f "${DEST}/oci-layout" || ! -f "${DEST}/index.json" ]]; then
  echo "[error] OCI layout incomplete: missing oci-layout or index.json." >&2
  exit 1
fi

# Buildx may leave a transient "ingest/" directory in type=oci exports.
# It is not part of the OCI Image Layout allowlist — remove before validation.
rm -rf "${DEST}/ingest"

if [[ ! -d "${DEST}/blobs/sha256" ]]; then
  echo "[error] OCI layout incomplete: missing blobs/sha256." >&2
  exit 1
fi

if [[ ! -f "${METADATA_FILE}" ]]; then
  echo "[error] Buildx metadata-file missing: ${METADATA_FILE}" >&2
  exit 1
fi

IMAGE_DIGEST="$(python3 - <<'PY' "${METADATA_FILE}"
import json, sys
meta_path = sys.argv[1]
with open(meta_path, encoding="utf-8") as f:
    meta = json.load(f)

descriptor = meta.get("containerimage.descriptor")
digest = None
if isinstance(descriptor, dict):
    digest = descriptor.get("digest")
if not isinstance(digest, str):
    digest = meta.get("containerimage.digest")
if not isinstance(digest, str) or not digest.startswith("sha256:") or len(digest) != 71:
    raise SystemExit("Buildx metadata missing containerimage.descriptor.digest / containerimage.digest")
hexpart = digest[len("sha256:"):]
if hexpart != hexpart.lower() or any(c not in "0123456789abcdef" for c in hexpart):
    raise SystemExit("Buildx image digest must use lowercase hex")

# index.json is the OCI layout entrypoint only. Buildx digest names the image
# index/manifest blob referenced from index.json manifests[] — do NOT compare
# digest to sha256(index.json). Full graph binding is enforced by validate-oci.
print(digest.lower())
PY
)"

echo "imageDigest=${IMAGE_DIGEST}"
echo "${IMAGE_DIGEST}" > "${PARENT}/oci-index.digest"

# oci-index.digest / ImageDigest / OciIndexDigest mean the Buildx image/index
# descriptor digest (manifests[] target), not sha256(index.json bytes).
# Validate descriptor graph via tools project; bind Buildx digest to layout.
VALIDATE_ARGS=(
  validate-oci
  --layout "${DEST}"
  --image-digest "${IMAGE_DIGEST}"
  --require-platforms "${REQUIRED_PLATFORMS}"
  --metadata-file "${METADATA_FILE}"
)
if [[ "${REQUIRED_PLATFORMS}" != *,* ]]; then
  VALIDATE_ARGS+=(--allow-single-platform)
fi
dotnet run --project "${REPO_ROOT}/tools/Amane.Mailer.ReleaseBundle/Amane.Mailer.ReleaseBundle.csproj" \
  -c "${CONFIGURATION:-Release}" --no-launch-profile -- \
  "${VALIDATE_ARGS[@]}"

if [[ "${WRITE_IMAGE_IDENTITY}" != "0" ]]; then
  dotnet run --project "${REPO_ROOT}/tools/Amane.Mailer.ReleaseBundle/Amane.Mailer.ReleaseBundle.csproj" \
    -c "${CONFIGURATION:-Release}" --no-launch-profile -- \
    write-image-identity \
    --output "${IDENTITY_FILE}" \
    --repository "${IMAGE_REPOSITORY}" \
    --tag "${IMAGE_TAG}" \
    --digest "${IMAGE_DIGEST}" \
    --source-sha "${SOURCE_SHA}" \
    --mailer-version "${MAILER_VERSION}" \
    --platforms "${PLATFORM}"

  echo "[info] Wrote ${IDENTITY_FILE}"
fi
