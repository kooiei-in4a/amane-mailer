#!/usr/bin/env bash
# Install a checksum-pinned crane binary from go-containerregistry.
# Never downloads "latest". Apache-2.0.
set -Eeuo pipefail
set +x

CRANE_VERSION_PIN="0.20.3"
CRANE_VERSION="${CRANE_VERSION:-${CRANE_VERSION_PIN}}"
INSTALL_DIR="${1:-}"

# From https://github.com/google/go-containerregistry/releases/download/v0.20.3/checksums.txt
CRANE_LINUX_AMD64_ARCHIVE="go-containerregistry_Linux_x86_64.tar.gz"
CRANE_LINUX_AMD64_SHA256="36c67a932f489b3f2724b64af90b599a8ef2aa7b004872597373c0ad694dc059"
CRANE_LINUX_ARM64_ARCHIVE="go-containerregistry_Linux_arm64.tar.gz"
CRANE_LINUX_ARM64_SHA256="d2235f7779cd39c6e40f43701d2512c997409f629fb53e621ede0d57d3f995e2"
CRANE_WINDOWS_AMD64_ARCHIVE="go-containerregistry_Windows_x86_64.tar.gz"
CRANE_WINDOWS_AMD64_SHA256="939c63961fc2e9d7f0cc2b6a1af9d17a5b2f6a37ffb63d961b47f786aadb732b"

die() {
  echo "[error] $*" >&2
  exit 1
}

if [[ -z "${INSTALL_DIR}" ]]; then
  die "Usage: $0 <install-dir>"
fi

if [[ "${CRANE_VERSION}" != "${CRANE_VERSION_PIN}" ]]; then
  die "CRANE_VERSION must be exactly ${CRANE_VERSION_PIN} (got ${CRANE_VERSION})"
fi

mkdir -p "${INSTALL_DIR}"
os="$(uname -s)"
arch="$(uname -m)"
crane_bin_name="crane"

case "${os}" in
  Linux)
    case "${arch}" in
      x86_64|amd64)
        archive="${CRANE_LINUX_AMD64_ARCHIVE}"
        expect_sha="${CRANE_LINUX_AMD64_SHA256}"
        ;;
      aarch64|arm64)
        archive="${CRANE_LINUX_ARM64_ARCHIVE}"
        expect_sha="${CRANE_LINUX_ARM64_SHA256}"
        ;;
      *)
        die "unsupported Linux arch for pinned crane install: ${arch}"
        ;;
    esac
    ;;
  MINGW*|MSYS*|CYGWIN*)
    if [[ "${arch}" != "x86_64" && "${arch}" != "amd64" ]]; then
      die "unsupported Windows arch for pinned crane install: ${arch}"
    fi
    archive="${CRANE_WINDOWS_AMD64_ARCHIVE}"
    expect_sha="${CRANE_WINDOWS_AMD64_SHA256}"
    crane_bin_name="crane.exe"
    ;;
  *)
    die "unsupported OS/arch for pinned crane install: ${os}/${arch}"
    ;;
esac

url="https://github.com/google/go-containerregistry/releases/download/v${CRANE_VERSION_PIN}/${archive}"
tmp="$(mktemp -d)"
trap 'rm -rf "${tmp}"' EXIT

archive_path="${tmp}/${archive}"
echo "[info] downloading pinned crane v${CRANE_VERSION_PIN} (${archive})"
curl -fsSL --retry 3 --retry-delay 1 -o "${archive_path}" "${url}"

if command -v sha256sum >/dev/null 2>&1; then
  got_sha="$(sha256sum "${archive_path}" | awk '{print $1}')"
else
  got_sha="$(openssl dgst -sha256 "${archive_path}" | awk '{print $NF}')"
fi
if [[ "${got_sha}" != "${expect_sha}" ]]; then
  die "crane archive SHA-256 mismatch (got ${got_sha}, expected ${expect_sha})"
fi

tar -xzf "${archive_path}" -C "${tmp}" "${crane_bin_name}"
install -m 0755 "${tmp}/${crane_bin_name}" "${INSTALL_DIR}/crane"

"${INSTALL_DIR}/crane" version >/dev/null
echo "[info] installed crane to ${INSTALL_DIR}/crane (v${CRANE_VERSION_PIN})"