#!/usr/bin/env bash
# Assemble native per-platform OCI candidate layouts into the final #455 artifact.
# This is build-only: it never logs in, pushes, tags, or rebuilds an image.
set -Eeuo pipefail
set +x

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" >/dev/null 2>&1 && pwd)"
REPO_ROOT="$(cd "${SCRIPT_DIR}/.." >/dev/null 2>&1 && pwd)"

AMD64_LAYOUT="${1:-}"
AMD64_METADATA="${2:-}"
ARM64_LAYOUT="${3:-}"
ARM64_METADATA="${4:-}"
OUT_ROOT="${5:-}"

if [[ -z "${AMD64_LAYOUT}" || -z "${AMD64_METADATA}" || -z "${ARM64_LAYOUT}" || -z "${ARM64_METADATA}" || -z "${OUT_ROOT}" ]]; then
  echo "Usage: $0 <amd64-layout> <amd64-metadata> <arm64-layout> <arm64-metadata> <output-root>" >&2
  exit 2
fi

if [[ -e "${OUT_ROOT}" ]]; then
  if [[ -n "$(find "${OUT_ROOT}" -mindepth 1 -maxdepth 1 -print -quit)" ]]; then
    echo "[error] assembler output root must be empty: ${OUT_ROOT}" >&2
    exit 1
  fi
else
  mkdir -p "${OUT_ROOT}"
fi

dotnet run --project "${REPO_ROOT}/tools/Amane.Mailer.ReleaseBundle/Amane.Mailer.ReleaseBundle.csproj" \
  -c "${CONFIGURATION:-Release}" --no-launch-profile -- \
  assemble-oci \
  --amd64-layout "${AMD64_LAYOUT}" \
  --amd64-metadata "${AMD64_METADATA}" \
  --arm64-layout "${ARM64_LAYOUT}" \
  --arm64-metadata "${ARM64_METADATA}" \
  --output "${OUT_ROOT}" \
  --repository "${IMAGE_REPOSITORY:-ghcr.io/kooiei-in4a/amane-mailer}" \
  --tag "${IMAGE_TAG:-sha-${SOURCE_SHA:-unknown}}" \
  --source-sha "${SOURCE_SHA:-}" \
  --mailer-version "${MAILER_VERSION:-}"

echo "[info] Assembled final candidate OCI artifact at ${OUT_ROOT} (no registry push)"
