#!/usr/bin/env bash
# Artifact smoke for Easy Setup candidate archives (#455).
# Runs against an EXTRACTED archive on a matching OS/arch runner.
# Distinct from runtime Docker release-image smoke (scripts/release-smoke.sh).
set -Eeuo pipefail
set +x

ARCHIVE_PATH="${1:-}"
EXPECTED_ARCHIVE_SHA="${2:-}"
EXPECTED_RID="${3:-}"
EXPECTED_VERSION="${4:-}"

if [[ -z "${ARCHIVE_PATH}" || -z "${EXPECTED_ARCHIVE_SHA}" || -z "${EXPECTED_RID}" || -z "${EXPECTED_VERSION}" ]]; then
  echo "Usage: $0 <archive> <archiveSha256> <rid> <release_version>" >&2
  exit 2
fi

pass() { echo "[PASS] $*"; }
fail() { echo "[FAIL] $*" >&2; exit 1; }

[[ -f "${ARCHIVE_PATH}" ]] || fail "archive missing: ${ARCHIVE_PATH}"

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" >/dev/null 2>&1 && pwd)"
if [[ ! "${EXPECTED_ARCHIVE_SHA}" =~ ^sha256:[a-f0-9]{64}$ ]]; then
  fail "malformed expected archive sha: ${EXPECTED_ARCHIVE_SHA}"
fi
# Hash file bytes directly — never parse sha256sum path output on Windows.
actual_sha="$(python3 "${SCRIPT_DIR}/compute-file-sha256.py" "${ARCHIVE_PATH}")"
[[ "${actual_sha}" =~ ^sha256:[a-f0-9]{64}$ ]] || fail "malformed actual archive sha: ${actual_sha}"
[[ "${actual_sha}" == "${EXPECTED_ARCHIVE_SHA}" ]] || fail "archive sha mismatch"

# Confirm this runner can execute the RID (fail closed — never PASS on presence alone).
case "$(uname -s)-$(uname -m)" in
  Linux-x86_64)
    [[ "${EXPECTED_RID}" == "linux-x64" ]] || fail "cross-RID exec not allowed on this runner (${EXPECTED_RID})"
    ;;
  Linux-aarch64)
    [[ "${EXPECTED_RID}" == "linux-arm64" ]] || fail "cross-RID exec not allowed on this runner (${EXPECTED_RID})"
    ;;
  MINGW*|MSYS*|CYGWIN*|Windows_NT*|Windows*)
    [[ "${EXPECTED_RID}" == "win-x64" ]] || fail "cross-RID exec not allowed on this runner (${EXPECTED_RID})"
    ;;
  *)
    # Windows GitHub-hosted runners often report MINGW via bash.
    if [[ "${EXPECTED_RID}" == "win-x64" ]] && [[ "${OS:-}" == "Windows_NT" ]]; then
      :
    else
      fail "unsupported smoke host for RID ${EXPECTED_RID}"
    fi
    ;;
esac

TMP="$(mktemp -d)"
cleanup() { rm -rf "${TMP}"; }
trap cleanup EXIT

echo "[info] Extracting ${ARCHIVE_PATH} -> ${TMP}"
if [[ "${ARCHIVE_PATH}" == *.zip ]]; then
  # Require unzip so backslash entry warnings fail closed (do not fall back to
  # Expand-Archive, which would hide Compress-Archive regressions).
  command -v unzip >/dev/null 2>&1 || fail "unzip required for zip archives"
  python3 "${SCRIPT_DIR}/assert-posix-zip-entries.py" "${ARCHIVE_PATH}" --rid "${EXPECTED_RID}"
  unzip -t "${ARCHIVE_PATH}" >/dev/null || fail "unzip -t failed"
  unzip -q "${ARCHIVE_PATH}" -d "${TMP}"
else
  tar -C "${TMP}" -xzf "${ARCHIVE_PATH}"
fi

rid_dir="${TMP}/${EXPECTED_RID}"
if [[ ! -d "${rid_dir}" ]]; then
  # Some zip tools may flatten; accept a single top-level dir.
  mapfile -t tops < <(find "${TMP}" -mindepth 1 -maxdepth 1 -type d)
  if [[ ${#tops[@]} -eq 1 ]]; then
    rid_dir="${tops[0]}"
  else
    fail "extracted RID directory missing"
  fi
fi

if [[ "${EXPECTED_RID}" == "win-x64" ]]; then
  bin="${rid_dir}/Amane.Mailer.exe"
else
  bin="${rid_dir}/Amane.Mailer"
fi

[[ -f "${bin}" ]] || fail "host binary missing after extract"
[[ -f "${rid_dir}/release-bundle-manifest.json" ]] || fail "manifest missing"
[[ -f "${rid_dir}/FILES-SHA256SUMS" || -f "${rid_dir}/SHA256SUMS" ]] || fail "FILES-SHA256SUMS missing"
[[ -f "${rid_dir}/LICENSE" ]] || fail "LICENSE missing"
[[ -f "${rid_dir}/compose.yml" ]] || fail "compose.yml missing"
[[ -f "${rid_dir}/compose.image-digest.yml" ]] || fail "digest overlay missing"
[[ -f "${rid_dir}/README-SETUP.md" ]] || fail "README-SETUP.md missing"
[[ ! -d "${rid_dir}/oci" ]] || fail "host archive must not embed oci/"

# Inventory checksum verification
python3 - <<'PY' "${rid_dir}" "${EXPECTED_RID}" "${EXPECTED_VERSION}"
import json, pathlib, sys, hashlib
root = pathlib.Path(sys.argv[1])
rid = sys.argv[2]
version = sys.argv[3]
manifest = json.loads((root / "release-bundle-manifest.json").read_text(encoding="utf-8"))
assert manifest.get("schemaVersion") == 1
assert manifest.get("targetRid") == rid or manifest.get("hostRid") == rid
assert manifest.get("mailerVersion") == version
assert manifest.get("setupLauncherVersion") == version
assert manifest.get("launcherVersionMin") == version
assert manifest.get("launcherVersionMax") == version
assert version in (manifest.get("artifactFileName") or "") or ("v" + version) in (manifest.get("artifactFileName") or "")
assert manifest.get("mailpitImageReference")
assert "latest" not in (manifest.get("imageTag") or "").lower()
assert manifest.get("payloadTreeSha256", "").startswith("sha256:")
assert not manifest.get("ociLayoutRelativePath")
print("manifest-ok")
PY

# Verify FILES-SHA256SUMS (hash bytes directly; avoid sha256sum path escapes)
checksums="${rid_dir}/FILES-SHA256SUMS"
[[ -f "${checksums}" ]] || checksums="${rid_dir}/SHA256SUMS"
python3 - <<'PY' "${checksums}" "${rid_dir}"
import hashlib, pathlib, sys
sums = pathlib.Path(sys.argv[1])
root = pathlib.Path(sys.argv[2])
for line in sums.read_text(encoding="utf-8").splitlines():
    line = line.strip()
    if not line:
        continue
    parts = line.split()
    if len(parts) < 2:
        raise SystemExit(f"malformed checksum line: {line!r}")
    hex_digest, rel = parts[0], parts[1]
    rel = rel[1:] if rel.startswith("*") else rel
    if len(hex_digest) != 64 or any(c not in "0123456789abcdef" for c in hex_digest):
        raise SystemExit(f"malformed hex digest: {hex_digest!r}")
    path = root / rel
    if not path.is_file():
        raise SystemExit(f"checksum path missing: {rel}")
    h = hashlib.sha256()
    with path.open("rb") as handle:
        while True:
            chunk = handle.read(1024 * 1024)
            if not chunk:
                break
            h.update(chunk)
    got = h.hexdigest()
    if got != hex_digest:
        raise SystemExit(f"checksum mismatch: {rel}")
print("files-sha256sums-ok")
PY
pass "FILES-SHA256SUMS"

# Linux: executable bit must survive extract WITHOUT chmod.
if [[ "${EXPECTED_RID}" == linux-* ]]; then
  [[ -x "${bin}" ]] || fail "executable bit missing after extract (no chmod)"
  pass "executable bit preserved"
fi

# Secret / PII structural scan
REPO_ROOT="$(cd "${SCRIPT_DIR}/.." >/dev/null 2>&1 && pwd)"
bash "${REPO_ROOT}/scripts/scan-setup-release-bundle.sh" "${rid_dir}"

# Binary version core assert + real exec smoke
dotnet run --project "${REPO_ROOT}/tools/Amane.Mailer.ReleaseBundle/Amane.Mailer.ReleaseBundle.csproj" \
  -c "${CONFIGURATION:-Release}" --no-launch-profile -- \
  assert-binary-version \
  --binary "${bin}" \
  --expected-core "${EXPECTED_VERSION}"
pass "binary version core"

"${bin}" --help >/dev/null || fail "--help failed"
pass "--help"

if "${bin}" setup assistant --help >/dev/null 2>&1; then
  pass "setup assistant --help"
elif "${bin}" setup assistant-self-check >/dev/null 2>&1; then
  pass "setup assistant-self-check"
else
  fail "setup assistant --help / assistant-self-check failed"
fi

pass "artifact archive smoke complete for ${EXPECTED_RID}"
