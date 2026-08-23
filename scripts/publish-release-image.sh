#!/usr/bin/env bash
# Publish the exact OCI layout that already passed release-image-build-smoke.
#
# This script never rebuilds. It pushes the supplied single-platform layout to
# the version tag, verifies the registry digest, then copies that digest to the
# immutable source-SHA tag. It is intentionally separate from the historical
# multi-platform qualification promotion path.
set -Eeuo pipefail
set +x

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" >/dev/null 2>&1 && pwd)"

OCI_LAYOUT=""
EXPECTED_DIGEST=""
REPOSITORY="ghcr.io/kooiei-in4a/amane-mailer"
RELEASE_VERSION=""
RELEASE_COMMIT_SHA=""
CRANE_BIN=""
DRY_RUN="0"

die() { echo "[error] $*" >&2; exit 1; }
usage() { sed -n '2,14p' "$0" | sed 's/^# \{0,1\}//'; exit 2; }

while [[ $# -gt 0 ]]; do
  case "$1" in
    --oci-layout) OCI_LAYOUT="${2:-}"; shift 2 ;;
    --expected-digest) EXPECTED_DIGEST="${2:-}"; shift 2 ;;
    --repository) REPOSITORY="${2:-}"; shift 2 ;;
    --release-version) RELEASE_VERSION="${2:-}"; shift 2 ;;
    --release-commit-sha) RELEASE_COMMIT_SHA="${2:-}"; shift 2 ;;
    --crane) CRANE_BIN="${2:-}"; shift 2 ;;
    --dry-run) DRY_RUN="1"; shift ;;
    -h|--help) usage ;;
    *) die "unknown argument: $1" ;;
  esac
done

[[ -n "${OCI_LAYOUT}" && -n "${EXPECTED_DIGEST}" && -n "${REPOSITORY}" ]] || die 'OCI layout, expected digest, and repository are required'
[[ -n "${RELEASE_VERSION}" && -n "${RELEASE_COMMIT_SHA}" ]] || die 'release version and commit SHA are required'
[[ "${REPOSITORY}" =~ ^[a-z0-9./_-]+$ ]] || die 'repository contains unsupported characters'
[[ "${REPOSITORY}" != *@* && ! "${REPOSITORY}" =~ :[^/]+$ ]] || die 'repository must not include a tag or digest'
[[ "${EXPECTED_DIGEST}" =~ ^sha256:[a-f0-9]{64}$ ]] || die 'expected digest must be sha256:<64 lowercase hex>'
[[ "${RELEASE_VERSION}" =~ ^[0-9]+\.[0-9]+\.[0-9]+$ ]] || die 'release version must be major.minor.patch'
[[ "${RELEASE_COMMIT_SHA}" =~ ^[0-9a-f]{40}$ ]] || die 'release commit SHA must be 40 lowercase hex'
[[ -d "${OCI_LAYOUT}" && -f "${OCI_LAYOUT}/oci-layout" && -f "${OCI_LAYOUT}/index.json" && -d "${OCI_LAYOUT}/blobs/sha256" ]] \
  || die 'OCI layout is incomplete'

command -v sha256sum >/dev/null 2>&1 || die 'sha256sum is required'
PYTHON_BIN="${PYTHON:-python3}"
command -v "${PYTHON_BIN}" >/dev/null 2>&1 || die 'python3 is required'

if [[ -z "${CRANE_BIN}" ]]; then
  pin_dir="$(mktemp -d)"
  trap 'rm -rf "${pin_dir}"' EXIT
  bash "${SCRIPT_DIR}/install-pinned-crane.sh" "${pin_dir}"
  CRANE_BIN="${pin_dir}/crane"
fi
[[ -x "${CRANE_BIN}" ]] || die "crane is not executable: ${CRANE_BIN}"
[[ "$(${CRANE_BIN} version 2>/dev/null | head -n 1 | tr -d '\r')" == '0.20.3' ]] \
  || die 'crane version must be 0.20.3'

VERSION_REF="${REPOSITORY}:v${RELEASE_VERSION}"
SHA_REF="${REPOSITORY}:sha-${RELEASE_COMMIT_SHA}"

"${PYTHON_BIN}" - "${OCI_LAYOUT}" "${EXPECTED_DIGEST}" "${RELEASE_VERSION}" "${RELEASE_COMMIT_SHA}" <<'PY'
import hashlib, json, pathlib, sys

layout = pathlib.Path(sys.argv[1])
expected, version, revision = sys.argv[2:]

def load_blob(digest, parse_json=True):
    if not isinstance(digest, str) or not digest.startswith("sha256:"):
        raise SystemExit("invalid OCI digest")
    path = layout / "blobs" / "sha256" / digest[7:]
    if not path.is_file() or hashlib.sha256(path.read_bytes()).hexdigest() != digest[7:]:
        raise SystemExit(f"missing or invalid OCI blob: {digest}")
    if parse_json:
        return json.loads(path.read_text(encoding="utf-8"))
    return path.read_bytes()

index = json.loads((layout / "index.json").read_text(encoding="utf-8"))
manifests = index.get("manifests") or []
if len(manifests) != 1:
    raise SystemExit("release image must contain exactly one runtime manifest")
descriptor = manifests[0]
if descriptor.get("digest") != expected:
    raise SystemExit("OCI runtime digest does not match the smoke-test digest")
if descriptor.get("mediaType") != "application/vnd.oci.image.manifest.v1+json":
    raise SystemExit("release image must use an OCI runtime manifest")
platform = descriptor.get("platform") or {}
if platform.get("os") != "linux" or platform.get("architecture") != "amd64":
    raise SystemExit("release image must be linux/amd64")
manifest = load_blob(expected)
if manifest.get("schemaVersion") != 2 or manifest.get("mediaType") != "application/vnd.oci.image.manifest.v1+json":
    raise SystemExit("OCI runtime manifest has an unexpected schema")
config_digest = (manifest.get("config") or {}).get("digest")
config = load_blob(config_digest)
labels = ((config.get("config") or {}).get("Labels") or {})
if labels.get("org.opencontainers.image.revision") != revision:
    raise SystemExit("OCI revision label mismatch")
if labels.get("org.opencontainers.image.version") != version:
    raise SystemExit("OCI version label mismatch")
for layer in manifest.get("layers") or []:
    load_blob(layer.get("digest"), parse_json=False)
print("layout-ok")
PY

redact_registry_err() {
  sed -E \
    -e 's/[Bb]earer[[:space:]]+[A-Za-z0-9._~+\/= -]+/Bearer [REDACTED]/g' \
    -e 's/(password|token|authorization|GITHUB_TOKEN|GHCR_TOKEN)[=:][^[:space:]]+/\1=[REDACTED]/gI' \
    -e 's/ghp_[A-Za-z0-9]+/ghp_[REDACTED]/g' \
    -e 's/gho_[A-Za-z0-9]+/gho_[REDACTED]/g'
}

require_tag_absent() {
  local ref="$1" errf out rc err
  errf="$(mktemp)"
  set +e
  out="$("${CRANE_BIN}" digest "${ref}" 2>"${errf}")"
  rc=$?
  set -e
  if [[ "${rc}" -eq 0 ]]; then
    rm -f "${errf}"
    die "destination tag already exists: ${ref} (digest ${out})"
  fi
  err="$(redact_registry_err < "${errf}" || true)"
  rm -f "${errf}"
  if echo "${err}" | grep -Eiq 'MANIFEST_UNKNOWN|NAME_UNKNOWN|manifest unknown|name unknown'; then
    return 0
  fi
  printf '%s\n' "${err}" >&2
  die "destination tag lookup failed; refusing to publish: ${ref}"
}

require_tag_absent "${VERSION_REF}"
require_tag_absent "${SHA_REF}"
echo "[info] smoke-tested digest ${EXPECTED_DIGEST}"
echo "[info] destination refs: ${VERSION_REF} , ${SHA_REF}"

if [[ "${DRY_RUN}" == "1" ]]; then
  echo '[info] dry-run complete; no push performed'
  exit 0
fi

"${CRANE_BIN}" push "${OCI_LAYOUT}" "${VERSION_REF}"
published_version_digest="$("${CRANE_BIN}" digest "${VERSION_REF}")"
[[ "${published_version_digest}" == "${EXPECTED_DIGEST}" ]] \
  || die "published version digest ${published_version_digest} != smoke digest ${EXPECTED_DIGEST}"

"${CRANE_BIN}" copy "${REPOSITORY}@${EXPECTED_DIGEST}" "${SHA_REF}"
published_sha_digest="$("${CRANE_BIN}" digest "${SHA_REF}")"
[[ "${published_sha_digest}" == "${EXPECTED_DIGEST}" ]] \
  || die "published SHA tag digest ${published_sha_digest} != smoke digest ${EXPECTED_DIGEST}"

echo '[info] publish complete; both tags point to the smoke-tested digest'
echo "sourceDigest=${EXPECTED_DIGEST}"
echo "versionRef=${VERSION_REF}"
echo "shaRef=${SHA_REF}"
