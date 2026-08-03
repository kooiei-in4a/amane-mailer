#!/usr/bin/env bash
# Self-test for P-OCI-PROMOTE digest preservation using a local disposable registry.
# Does not touch GHCR or any public registry.
set -Eeuo pipefail
set +x

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" >/dev/null 2>&1 && pwd)"
REPO_ROOT="$(cd "${SCRIPT_DIR}/.." >/dev/null 2>&1 && pwd)"
WORK="$(mktemp -d)"
REGISTRY_NAME="amane-oci-promote-selftest-$$"
REGISTRY_PORT=""
CRANE_DIR="${WORK}/crane"
PASS_COUNT=0
FAIL_COUNT=0

cleanup() {
  if [[ -n "${REGISTRY_NAME}" ]]; then
    docker rm -f "${REGISTRY_NAME}" >/dev/null 2>&1 || true
  fi
  rm -rf "${WORK}"
}
trap cleanup EXIT

die() {
  echo "[FAIL] $*" >&2
  exit 1
}

expect_fail() {
  local name="$1"
  shift
  if "$@" >/dev/null 2>&1; then
    echo "[FAIL] expected failure: ${name}" >&2
    FAIL_COUNT=$((FAIL_COUNT + 1))
    return 1
  fi
  echo "[PASS] negative: ${name}"
  PASS_COUNT=$((PASS_COUNT + 1))
}

expect_pass() {
  local name="$1"
  shift
  if ! "$@"; then
    echo "[FAIL] expected success: ${name}" >&2
    FAIL_COUNT=$((FAIL_COUNT + 1))
    return 1
  fi
  echo "[PASS] ${name}"
  PASS_COUNT=$((PASS_COUNT + 1))
}

command -v docker >/dev/null 2>&1 || die "docker is required for self-test"
command -v python3 >/dev/null 2>&1 || die "python3 is required for self-test"
command -v curl >/dev/null 2>&1 || die "curl is required for self-test"

bash "${SCRIPT_DIR}/install-pinned-crane.sh" "${CRANE_DIR}"
CRANE="${CRANE_DIR}/crane"
if [[ -z "${PYTHON:-}" ]]; then
  if command -v python3 >/dev/null 2>&1 && python3 -c 'import sys; raise SystemExit(0 if sys.version_info >= (3, 9) else 1)' >/dev/null 2>&1; then
    export PYTHON=python3
  elif [[ -x "/c/Users/${USERNAME}/.local/bin/python3.11.exe" ]]; then
    export PYTHON="/c/Users/${USERNAME}/.local/bin/python3.11.exe"
  fi
fi
PROMOTE=(bash "${SCRIPT_DIR}/promote-candidate-oci.sh" --crane "${CRANE}")

RELEASE_VERSION="1.2.0"
RELEASE_COMMIT="013dd6741c2825f518a78d5b28cc1c4718dba5df"
VERSION_TAG="v${RELEASE_VERSION}"
SHA_TAG="sha-${RELEASE_COMMIT}"

# Start local registry (anonymous pull/push on loopback).
docker run -d --rm --name "${REGISTRY_NAME}" -p 0:5000 registry:2 >/dev/null
REGISTRY_PORT="$(docker inspect -f '{{(index (index .NetworkSettings.Ports "5000/tcp") 0).HostPort}}' "${REGISTRY_NAME}")"
[[ -n "${REGISTRY_PORT}" ]] || die "failed to resolve local registry port"
REPO="127.0.0.1:${REGISTRY_PORT}/amane-mailer-selftest"

# Fixture Dockerfile: tiny multi-arch image with required labels applied via buildx.
FIXTURE_DIR="${WORK}/fixture"
mkdir -p "${FIXTURE_DIR}"
cat > "${FIXTURE_DIR}/Dockerfile" <<'EOF'
FROM busybox:1.36.1
EOF

OCI_LAYOUT="${WORK}/oci"
mkdir -p "${OCI_LAYOUT}"

echo "[info] building synthetic multi-arch OCI layout (provenance/sbom disabled)"
docker buildx build \
  --file "${FIXTURE_DIR}/Dockerfile" \
  --platform linux/amd64,linux/arm64 \
  --provenance=false \
  --sbom=false \
  --label "org.opencontainers.image.version=${RELEASE_VERSION}" \
  --label "org.opencontainers.image.revision=${RELEASE_COMMIT}" \
  --output "type=oci,dest=${OCI_LAYOUT},tar=false" \
  "${FIXTURE_DIR}"

SOURCE_DIGEST="$(
  "${PYTHON:-python3}" - "${OCI_LAYOUT}" <<'PY'
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
if len(ms) == 1 and ms[0].get("mediaType") in INDEX and not (ms[0].get("platform") or {}):
    print(ms[0]["digest"])
else:
    print(top)
PY
)"
echo "[info] source index digest ${SOURCE_DIGEST}"

# Positive path
expect_pass "digest-preserving promote" \
  "${PROMOTE[@]}" \
    --oci-layout "${OCI_LAYOUT}" \
    --expected-digest "${SOURCE_DIGEST}" \
    --repository "${REPO}" \
    --release-version "${RELEASE_VERSION}" \
    --release-commit-sha "${RELEASE_COMMIT}" \
    --version-tag "${VERSION_TAG}" \
    --sha-tag "${SHA_TAG}" \
    --attest-mode EXTERNAL_PROVENANCE

VERSION_DIGEST="$("${CRANE}" digest "${REPO}:${VERSION_TAG}")"
SHA_DIGEST="$("${CRANE}" digest "${REPO}:${SHA_TAG}")"
[[ "${VERSION_DIGEST}" == "${SOURCE_DIGEST}" ]] || die "version digest mismatch"
[[ "${SHA_DIGEST}" == "${SOURCE_DIGEST}" ]] || die "sha digest mismatch"
[[ "${VERSION_DIGEST}" == "${SHA_DIGEST}" ]] || die "tag digests diverge"
echo "[PASS] source == version tag == sha tag digests"

# Existing destination tag must fail (no overwrite).
expect_fail "existing destination tag" \
  "${PROMOTE[@]}" \
    --oci-layout "${OCI_LAYOUT}" \
    --expected-digest "${SOURCE_DIGEST}" \
    --repository "${REPO}" \
    --release-version "${RELEASE_VERSION}" \
    --release-commit-sha "${RELEASE_COMMIT}" \
    --version-tag "${VERSION_TAG}" \
    --sha-tag "${SHA_TAG}" \
    --attest-mode EXTERNAL_PROVENANCE

# Wrong expected digest
BAD_DIGEST="sha256:0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef"
expect_fail "expected digest mismatch" \
  "${PROMOTE[@]}" \
    --oci-layout "${OCI_LAYOUT}" \
    --expected-digest "${BAD_DIGEST}" \
    --repository "${REPO}-mismatch" \
    --release-version "${RELEASE_VERSION}" \
    --release-commit-sha "${RELEASE_COMMIT}" \
    --version-tag "${VERSION_TAG}" \
    --sha-tag "${SHA_TAG}" \
    --attest-mode EXTERNAL_PROVENANCE \
    --dry-run

# Missing blob
BROKEN="${WORK}/broken-oci"
cp -a "${OCI_LAYOUT}" "${BROKEN}"
first_blob="$(find "${BROKEN}/blobs/sha256" -type f | head -n 1)"
rm -f "${first_blob}"
expect_fail "missing blob" \
  "${PROMOTE[@]}" \
    --oci-layout "${BROKEN}" \
    --expected-digest "sha256:$(sha256sum "${BROKEN}/index.json" | awk '{print $1}')" \
    --repository "${REPO}-broken" \
    --release-version "${RELEASE_VERSION}" \
    --release-commit-sha "${RELEASE_COMMIT}" \
    --version-tag "${VERSION_TAG}" \
    --sha-tag "${SHA_TAG}" \
    --attest-mode EXTERNAL_PROVENANCE \
    --dry-run

# Wrong version label layout
WRONG_VER_DIR="${WORK}/wrong-ver"
mkdir -p "${WRONG_VER_DIR}"
docker buildx build \
  --file "${FIXTURE_DIR}/Dockerfile" \
  --platform linux/amd64,linux/arm64 \
  --provenance=false --sbom=false \
  --label "org.opencontainers.image.version=9.9.9" \
  --label "org.opencontainers.image.revision=${RELEASE_COMMIT}" \
  --output "type=oci,dest=${WRONG_VER_DIR},tar=false" \
  "${FIXTURE_DIR}"
expect_fail "wrong version label" \
  "${PROMOTE[@]}" \
    --oci-layout "${WRONG_VER_DIR}" \
    --expected-digest "sha256:$(sha256sum "${WRONG_VER_DIR}/index.json" | awk '{print $1}')" \
    --repository "${REPO}-wrongver" \
    --release-version "${RELEASE_VERSION}" \
    --release-commit-sha "${RELEASE_COMMIT}" \
    --version-tag "${VERSION_TAG}" \
    --sha-tag "${SHA_TAG}" \
    --attest-mode EXTERNAL_PROVENANCE \
    --dry-run

# Wrong revision label layout
WRONG_REV_DIR="${WORK}/wrong-rev"
mkdir -p "${WRONG_REV_DIR}"
docker buildx build \
  --file "${FIXTURE_DIR}/Dockerfile" \
  --platform linux/amd64,linux/arm64 \
  --provenance=false --sbom=false \
  --label "org.opencontainers.image.version=${RELEASE_VERSION}" \
  --label "org.opencontainers.image.revision=0000000000000000000000000000000000000000" \
  --output "type=oci,dest=${WRONG_REV_DIR},tar=false" \
  "${FIXTURE_DIR}"
expect_fail "wrong revision label" \
  "${PROMOTE[@]}" \
    --oci-layout "${WRONG_REV_DIR}" \
    --expected-digest "sha256:$(sha256sum "${WRONG_REV_DIR}/index.json" | awk '{print $1}')" \
    --repository "${REPO}-wrongrev" \
    --release-version "${RELEASE_VERSION}" \
    --release-commit-sha "${RELEASE_COMMIT}" \
    --version-tag "${VERSION_TAG}" \
    --sha-tag "${SHA_TAG}" \
    --attest-mode EXTERNAL_PROVENANCE \
    --dry-run

# Single-arch (missing arm64)
SINGLE_DIR="${WORK}/single"
mkdir -p "${SINGLE_DIR}"
docker buildx build \
  --file "${FIXTURE_DIR}/Dockerfile" \
  --platform linux/amd64 \
  --provenance=false --sbom=false \
  --label "org.opencontainers.image.version=${RELEASE_VERSION}" \
  --label "org.opencontainers.image.revision=${RELEASE_COMMIT}" \
  --output "type=oci,dest=${SINGLE_DIR},tar=false" \
  "${FIXTURE_DIR}"
expect_fail "missing arm64" \
  "${PROMOTE[@]}" \
    --oci-layout "${SINGLE_DIR}" \
    --expected-digest "sha256:$(sha256sum "${SINGLE_DIR}/index.json" | awk '{print $1}')" \
    --repository "${REPO}-single" \
    --release-version "${RELEASE_VERSION}" \
    --release-commit-sha "${RELEASE_COMMIT}" \
    --version-tag "${VERSION_TAG}" \
    --sha-tag "${SHA_TAG}" \
    --attest-mode EXTERNAL_PROVENANCE \
    --dry-run


# Registry lookup fail-closed negatives (must NOT treat as tag-absent)
expect_fail "unreachable registry tag lookup" \
  "${PROMOTE[@]}" \
    --oci-layout "${OCI_LAYOUT}" \
    --expected-digest "${SOURCE_DIGEST}" \
    --repository "127.0.0.1:9/amane-mailer" \
    --release-version "${RELEASE_VERSION}" \
    --release-commit-sha "${RELEASE_COMMIT}" \
    --version-tag "${VERSION_TAG}" \
    --sha-tag "${SHA_TAG}" \
    --attest-mode EXTERNAL_PROVENANCE \
    --dry-run

AUTH_REG_NAME="amane-oci-promote-auth-$$"
AUTH_DIR="${WORK}/authreg"
mkdir -p "${AUTH_DIR}/auth"
# Avoid pulling httpd solely for htpasswd; apr1 is accepted by distribution registry auth.
openssl passwd -apr1 'wrong-pass-not-a-secret' | awk '{print "promoter:" $0}' > "${AUTH_DIR}/auth/htpasswd"
docker rm -f "${AUTH_REG_NAME}" >/dev/null 2>&1 || true
docker run -d --rm --name "${AUTH_REG_NAME}" \
  -p 15001:5000 \
  -e REGISTRY_AUTH=htpasswd \
  -e "REGISTRY_AUTH_HTPASSWD_REALM=Registry Realm" \
  -e REGISTRY_AUTH_HTPASSWD_PATH=/auth/htpasswd \
  -v "${AUTH_DIR}/auth:/auth:ro" \
  registry:2 >/dev/null
sleep 2

expect_fail "unauthorized registry tag lookup" \
  "${PROMOTE[@]}" \
    --oci-layout "${OCI_LAYOUT}" \
    --expected-digest "${SOURCE_DIGEST}" \
    --repository "127.0.0.1:15001/amane-mailer" \
    --release-version "${RELEASE_VERSION}" \
    --release-commit-sha "${RELEASE_COMMIT}" \
    --version-tag "${VERSION_TAG}" \
    --sha-tag "${SHA_TAG}" \
    --attest-mode EXTERNAL_PROVENANCE \
    --dry-run

docker rm -f "${AUTH_REG_NAME}" >/dev/null 2>&1 || true

expect_fail "TLS-class / closed-port registry tag lookup" \
  "${PROMOTE[@]}" \
    --oci-layout "${OCI_LAYOUT}" \
    --expected-digest "${SOURCE_DIGEST}" \
    --repository "127.0.0.1:1/amane-mailer" \
    --release-version "${RELEASE_VERSION}" \
    --release-commit-sha "${RELEASE_COMMIT}" \
    --version-tag "${VERSION_TAG}" \
    --sha-tag "${SHA_TAG}" \
    --attest-mode EXTERNAL_PROVENANCE \
    --dry-run

# Secret-like scan of promote script help/dry-run output
SCAN_OUT="${WORK}/scan.txt"
"${PROMOTE[@]}" \
  --oci-layout "${OCI_LAYOUT}" \
  --expected-digest "${SOURCE_DIGEST}" \
  --repository "${REPO}-scan" \
  --release-version "${RELEASE_VERSION}" \
  --release-commit-sha "${RELEASE_COMMIT}" \
  --version-tag "${VERSION_TAG}" \
  --sha-tag "${SHA_TAG}" \
  --attest-mode EXTERNAL_PROVENANCE \
  --dry-run >"${SCAN_OUT}" 2>&1 || true
if grep -Eiq 'ghp_[A-Za-z0-9]|gho_[A-Za-z0-9]|GITHUB_TOKEN=|password=|BEGIN (RSA |OPENSSH )?PRIVATE KEY' "${SCAN_OUT}"; then
  die "secret-like value detected in dry-run output"
fi
echo "[PASS] secret-like scan on dry-run output"

if [[ "${FAIL_COUNT}" -ne 0 ]]; then
  die "self-test failures: ${FAIL_COUNT} (passes=${PASS_COUNT})"
fi

CRANE_VER="$("${CRANE}" version 2>/dev/null | head -n 1 | tr -d '\r')"
PLATFORMS="$("${CRANE}" manifest "${REPO}:${VERSION_TAG}" | python3 -c 'import json,sys
d=json.load(sys.stdin)
ms=d.get("manifests") or []
vals=[]
for m in ms:
  p=m.get("platform") or {}
  os_=p.get("os") or ""
  arch=p.get("architecture") or ""
  if os_ and os_ != "unknown":
    vals.append(f"{os_}/{arch}")
print(",".join(sorted(set(vals))))')"
echo "[info] promote-candidate-oci self-test passed (passes=${PASS_COUNT} fails=${FAIL_COUNT})"
echo "craneVersion=${CRANE_VER}"
echo "sourceDigest=${SOURCE_DIGEST}"
echo "destinationVersionTagDigest=${VERSION_DIGEST}"
echo "destinationShaTagDigest=${SHA_DIGEST}"
echo "platforms=${PLATFORMS}"
echo "negativeTestPasses=${PASS_COUNT}"
echo "finalResult=PASS"
