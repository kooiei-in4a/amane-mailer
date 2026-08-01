#!/usr/bin/env bash
# Promote a qualified OCI image layout to a registry without rebuild (P-OCI-PROMOTE).
set -Eeuo pipefail
set +x

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" >/dev/null 2>&1 && pwd)"

OCI_LAYOUT=""
EXPECTED_DIGEST=""
REPOSITORY=""
RELEASE_VERSION=""
RELEASE_COMMIT_SHA=""
VERSION_TAG=""
SHA_TAG=""
ATTEST_MODE="EXTERNAL_PROVENANCE"
DRY_RUN="0"
CRANE_BIN=""

die() { echo "[error] $*" >&2; exit 1; }
require_cmd() { command -v "$1" >/dev/null 2>&1 || die "missing required command: $1"; }

resolve_python() {
  local candidate
  for candidate in "${PYTHON:-}" python3.12 python3.11 python3 python; do
    [[ -n "${candidate}" ]] || continue
    if command -v "${candidate}" >/dev/null 2>&1; then
      if "${candidate}" -c "import sys; raise SystemExit(0 if sys.version_info >= (3, 9) else 1)" >/dev/null 2>&1; then
        printf "%s" "${candidate}"
        return 0
      fi
    fi
  done
  for candidate in \
    "/c/Users/${USER:-}/.local/bin/python3.11.exe" \
    "/c/Users/${USERNAME:-}/.local/bin/python3.11.exe"; do
    if [[ -x "${candidate}" ]] && "${candidate}" -c "import sys" >/dev/null 2>&1; then
      printf "%s" "${candidate}"
      return 0
    fi
  done
  die "python3 (>=3.9) is required"
}

usage() { sed -n "2,30p" "$0" | sed "s/^# \{0,1\}//"; exit 2; }

while [[ $# -gt 0 ]]; do
  case "$1" in
    --oci-layout) OCI_LAYOUT="${2:-}"; shift 2 ;;
    --expected-digest) EXPECTED_DIGEST="${2:-}"; shift 2 ;;
    --repository) REPOSITORY="${2:-}"; shift 2 ;;
    --release-version) RELEASE_VERSION="${2:-}"; shift 2 ;;
    --release-commit-sha) RELEASE_COMMIT_SHA="${2:-}"; shift 2 ;;
    --version-tag) VERSION_TAG="${2:-}"; shift 2 ;;
    --sha-tag) SHA_TAG="${2:-}"; shift 2 ;;
    --attest-mode) ATTEST_MODE="${2:-}"; shift 2 ;;
    --crane) CRANE_BIN="${2:-}"; shift 2 ;;
    --dry-run) DRY_RUN="1"; shift ;;
    -h|--help) usage ;;
    *) die "unknown argument: $1" ;;
  esac
done

[[ -n "${OCI_LAYOUT}" && -n "${EXPECTED_DIGEST}" && -n "${REPOSITORY}" ]] || die "missing required arguments"
[[ -n "${RELEASE_VERSION}" && -n "${RELEASE_COMMIT_SHA}" && -n "${VERSION_TAG}" && -n "${SHA_TAG}" ]] || die "missing required arguments"

require_cmd sha256sum
PYTHON_BIN="$(resolve_python)"

# Always install checksum-pinned crane. Never silently use PATH.
# An explicit --crane must be byte-identical to the freshly installed pin.
pin_dir="$(mktemp -d)"
bash "${SCRIPT_DIR}/install-pinned-crane.sh" "${pin_dir}"
PINNED_CRANE="${pin_dir}/crane"
[[ -x "${PINNED_CRANE}" ]] || die "pinned crane binary not executable: ${PINNED_CRANE}"
PINNED_VER="$("${PINNED_CRANE}" version 2>/dev/null | head -n 1 | tr -d '\r')"
[[ "${PINNED_VER}" == "0.20.3" ]] || die "pinned crane version mismatch (got ${PINNED_VER}, expected 0.20.3)"
PINNED_SHA="$(sha256sum "${PINNED_CRANE}" | awk '{print $1}')"
if [[ -n "${CRANE_BIN}" ]]; then
  [[ -x "${CRANE_BIN}" ]] || die "crane binary not executable: ${CRANE_BIN}"
  got_ver="$("${CRANE_BIN}" version 2>/dev/null | head -n 1 | tr -d '\r')"
  [[ "${got_ver}" == "0.20.3" ]] || die "crane --crane version mismatch (got ${got_ver}, expected 0.20.3)"
  got_sha="$(sha256sum "${CRANE_BIN}" | awk '{print $1}')"
  [[ "${got_sha}" == "${PINNED_SHA}" ]] || die "crane --crane SHA-256 mismatch vs pinned v0.20.3 installer"
else
  CRANE_BIN="${PINNED_CRANE}"
fi
echo "[info] using pinned crane v0.20.3 sha256:${PINNED_SHA}"

if [[ "${REPOSITORY}" == *@* ]]; then die "repository must not include a digest"; fi
if [[ "${REPOSITORY}" =~ :[^/]+$ ]]; then die "repository must not include a tag"; fi
[[ "${EXPECTED_DIGEST}" =~ ^sha256:[a-f0-9]{64}$ ]] || die "expected digest must be sha256:<64 lowercase hex>"
[[ "${RELEASE_VERSION}" =~ ^[0-9]+\.[0-9]+\.[0-9]+$ ]] || die "release-version must be major.minor.patch"
[[ "${RELEASE_COMMIT_SHA}" =~ ^[0-9a-f]{40}$ ]] || die "release-commit-sha must be 40 lowercase hex"
[[ "${VERSION_TAG}" == "v${RELEASE_VERSION}" ]] || die "version-tag must be v${RELEASE_VERSION}"
[[ "${SHA_TAG}" == "sha-${RELEASE_COMMIT_SHA}" ]] || die "sha-tag must be sha-${RELEASE_COMMIT_SHA}"
case "${ATTEST_MODE}" in EXTERNAL_PROVENANCE|REGISTRY_ATTEST) ;; *) die "invalid attest-mode" ;; esac

[[ -d "${OCI_LAYOUT}" && -f "${OCI_LAYOUT}/oci-layout" && -f "${OCI_LAYOUT}/index.json" && -d "${OCI_LAYOUT}/blobs/sha256" ]] \
  || die "OCI layout incomplete"

SOURCE_DIGEST="$("${PYTHON_BIN}" - "${OCI_LAYOUT}" <<'PY'
import hashlib, json, pathlib, sys
layout = pathlib.Path(sys.argv[1])
raw = (layout / "index.json").read_bytes()
top = "sha256:" + hashlib.sha256(raw).hexdigest()
doc = json.loads(raw.decode("utf-8"))
ms = doc.get("manifests") or []
INDEX = {
    "application/vnd.oci.image.index.v1+json",
    "application/vnd.docker.distribution.manifest.list.v2+json",
}
if (
    len(ms) == 1
    and isinstance(ms[0], dict)
    and ms[0].get("mediaType") in INDEX
    and not (ms[0].get("platform") or {})
):
    digest = ms[0]["digest"]
    blob = layout / "blobs" / "sha256" / digest[len("sha256:"):]
    nested = blob.read_bytes()
    if "sha256:" + hashlib.sha256(nested).hexdigest() != digest:
        raise SystemExit("nested index digest mismatch")
    print(digest)
else:
    print(top)
PY
)"
if [[ "${SOURCE_DIGEST}" != "${EXPECTED_DIGEST}" ]]; then
  die "canonical source index digest ${SOURCE_DIGEST} != expected ${EXPECTED_DIGEST}"
fi

"${PYTHON_BIN}" - "${OCI_LAYOUT}" "${ATTEST_MODE}" "${RELEASE_VERSION}" "${RELEASE_COMMIT_SHA}" <<'PY' || die "OCI layout validation failed"
import json, pathlib, sys
layout = pathlib.Path(sys.argv[1])
attest_mode = sys.argv[2]
release_version = sys.argv[3]
release_commit = sys.argv[4]
INDEX = {
    "application/vnd.oci.image.index.v1+json",
    "application/vnd.docker.distribution.manifest.list.v2+json",
}

def load(path):
    return json.loads(path.read_text(encoding="utf-8"))

def blob(digest):
    path = layout / "blobs" / "sha256" / digest[len("sha256:"):]
    if not path.is_file():
        raise SystemExit(f"missing blob: {digest}")
    return path

if str(load(layout / "oci-layout").get("imageLayoutVersion")) != "1.0.0":
    raise SystemExit("unsupported imageLayoutVersion")

def walk(doc):
    manifests = doc.get("manifests")
    if not isinstance(manifests, list) or not manifests:
        raise SystemExit("empty manifests")
    if (
        len(manifests) == 1
        and manifests[0].get("mediaType") in INDEX
        and not (manifests[0].get("platform") or {})
    ):
        yield from walk(load(blob(manifests[0]["digest"])))
        return
    for item in manifests:
        annotations = item.get("annotations") or {}
        platform = item.get("platform") or {}
        media = item.get("mediaType")
        digest = item.get("digest")
        if annotations.get("vnd.docker.reference.type") == "attestation-manifest" or (
            platform.get("os") == "unknown" and platform.get("architecture") == "unknown"
        ):
            yield ("attestation", digest, None)
            continue
        if media in INDEX:
            yield from walk(load(blob(digest)))
            continue
        name = f"{platform.get('os')}/{platform.get('architecture')}"
        yield ("runtime", digest, name)

runtime = {}
attestation_count = 0
unexpected = []
for kind, digest, platform_name in walk(load(layout / "index.json")):
    if kind == "attestation":
        attestation_count += 1
        continue
    if platform_name not in ("linux/amd64", "linux/arm64"):
        unexpected.append(platform_name)
        continue
    runtime[platform_name] = digest
    manifest = load(blob(digest))
    labels = (load(blob(manifest["config"]["digest"])).get("config") or {}).get("Labels") or {}
    if labels.get("org.opencontainers.image.version") != release_version:
        raise SystemExit(f"{platform_name} version label mismatch")
    if labels.get("org.opencontainers.image.revision") != release_commit:
        raise SystemExit(f"{platform_name} revision label mismatch")
    for layer in manifest.get("layers") or []:
        blob(layer["digest"])

if "linux/amd64" not in runtime or "linux/arm64" not in runtime:
    raise SystemExit(f"required platforms missing: {sorted(runtime)}")
if unexpected:
    raise SystemExit(f"unexpected platforms: {unexpected}")
if attest_mode == "EXTERNAL_PROVENANCE" and attestation_count:
    raise SystemExit("EXTERNAL_PROVENANCE forbids attestation descriptors")
if attest_mode == "REGISTRY_ATTEST" and attestation_count == 0:
    raise SystemExit("REGISTRY_ATTEST requires attestation descriptors")
print("layout-ok")
PY

VERSION_REF="${REPOSITORY}:${VERSION_TAG}"
SHA_REF="${REPOSITORY}:${SHA_TAG}"

redact_registry_err() {
  # Avoid leaking credentials / tokens from registry client stderr.
  sed -E \
    -e 's/[Bb]earer[[:space:]]+[A-Za-z0-9._~+\/=-]+/Bearer [REDACTED]/g' \
    -e 's/(password|token|authorization|GITHUB_TOKEN|GHCR_TOKEN)[=:][^[:space:]]+/\1=[REDACTED]/gI' \
    -e 's/ghp_[A-Za-z0-9]+/ghp_[REDACTED]/g' \
    -e 's/gho_[A-Za-z0-9]+/gho_[REDACTED]/g'
}

# Classify destination tag lookup:
#   EXISTS  -> stop (no overwrite)
#   ABSENT  -> continue
#   UNKNOWN -> stop (auth/network/TLS/other); never treat as absent
classify_tag_lookup() {
  local ref="$1"
  local errf out rc err
  errf="$(mktemp)"
  set +e
  out="$("${CRANE_BIN}" digest "${ref}" 2>"${errf}")"
  rc=$?
  set -e
  if [[ "${rc}" -eq 0 ]]; then
    rm -f "${errf}"
    printf '%s\n' "EXISTS"
    return 0
  fi
  err="$(redact_registry_err < "${errf}" || true)"
  rm -f "${errf}"
  if echo "${err}" | grep -Eiq \
    'unauthorized|authentication required|denied|forbidden|[[:space:]]401([[:space:]]|$)|[[:space:]]403([[:space:]]|$)|dial tcp|connection refused|i/o timeout|context deadline|tls:|x509:|certificate|no such host|network is unreachable|temporary failure|server misbehaving|EOF|http2:|could not parse reference|server gave HTTP response to HTTPS'; then
    printf '%s\n' "UNKNOWN"
    return 0
  fi
  if echo "${err}" | grep -Eiq 'MANIFEST_UNKNOWN|NAME_UNKNOWN|manifest unknown|name unknown'; then
    printf '%s\n' "ABSENT"
    return 0
  fi
  printf '%s\n' "UNKNOWN"
}

require_tag_absent() {
  local ref="$1"
  local class
  class="$(classify_tag_lookup "${ref}")"
  case "${class}" in
    ABSENT)
      return 0
      ;;
    EXISTS)
      die "destination tag already exists: ${ref}"
      ;;
    UNKNOWN)
      die "destination tag lookup failed (state unknown); refusing to publish: ${ref}"
      ;;
    *)
      die "unexpected tag lookup class '${class}' for ${ref}"
      ;;
  esac
}

require_tag_absent "${VERSION_REF}"
require_tag_absent "${SHA_REF}"

echo "[info] canonical source index digest ${SOURCE_DIGEST}"
echo "[info] validated OCI layout at ${OCI_LAYOUT}"
echo "[info] destination refs: ${VERSION_REF} , ${SHA_REF}"

if [[ "${DRY_RUN}" == "1" ]]; then
  echo "[info] dry-run complete; no push performed"
  exit 0
fi

echo "[info] pushing OCI layout without rebuild"
"${CRANE_BIN}" push "${OCI_LAYOUT}" "${VERSION_REF}"

DEST_VERSION_DIGEST="$("${CRANE_BIN}" digest "${VERSION_REF}")"
if [[ "${DEST_VERSION_DIGEST}" != "${SOURCE_DIGEST}" ]]; then
  die "destination version digest ${DEST_VERSION_DIGEST} != source ${SOURCE_DIGEST}"
fi

"${CRANE_BIN}" copy "${REPOSITORY}@${SOURCE_DIGEST}" "${SHA_REF}"
DEST_SHA_DIGEST="$("${CRANE_BIN}" digest "${SHA_REF}")"
if [[ "${DEST_SHA_DIGEST}" != "${SOURCE_DIGEST}" ]]; then
  die "destination SHA digest ${DEST_SHA_DIGEST} != source ${SOURCE_DIGEST}"
fi
if [[ "${DEST_SHA_DIGEST}" != "${DEST_VERSION_DIGEST}" ]]; then
  die "version tag digest and SHA tag digest diverge"
fi

"${PYTHON_BIN}" - "${CRANE_BIN}" "${VERSION_REF}" "${RELEASE_VERSION}" "${RELEASE_COMMIT_SHA}" <<'PY' || die "post-push inspection failed"
import json, subprocess, sys
crane, version_ref, release_version, release_commit = sys.argv[1:5]
INDEX = {
    "application/vnd.oci.image.index.v1+json",
    "application/vnd.docker.distribution.manifest.list.v2+json",
}

def run(args):
    return subprocess.check_output(args, text=True)

doc = json.loads(run([crane, "manifest", version_ref]))
manifests = doc.get("manifests") or []
if (
    len(manifests) == 1
    and manifests[0].get("mediaType") in INDEX
    and not (manifests[0].get("platform") or {})
):
    repo = version_ref.rsplit(":", 1)[0]
    doc = json.loads(run([crane, "manifest", f"{repo}@{manifests[0]['digest']}"]))
    manifests = doc.get("manifests") or []

runtime = set()
for item in manifests:
    platform = item.get("platform") or {}
    annotations = item.get("annotations") or {}
    if annotations.get("vnd.docker.reference.type") == "attestation-manifest":
        continue
    if platform.get("os") in (None, "unknown"):
        continue
    runtime.add(f"{platform['os']}/{platform['architecture']}")
if "linux/amd64" not in runtime or "linux/arm64" not in runtime:
    raise SystemExit(f"missing platforms after push: {sorted(runtime)}")
labels = (json.loads(run([crane, "config", "--platform", "linux/amd64", version_ref])).get("config") or {}).get("Labels") or {}
if labels.get("org.opencontainers.image.version") != release_version:
    raise SystemExit("published version label mismatch")
if labels.get("org.opencontainers.image.revision") != release_commit:
    raise SystemExit("published revision label mismatch")
print("post-push-ok")
PY

echo "[info] promote complete"
echo "sourceDigest=${SOURCE_DIGEST}"
echo "destinationDigest=${DEST_VERSION_DIGEST}"
echo "versionRef=${VERSION_REF}"
echo "shaRef=${SHA_REF}"