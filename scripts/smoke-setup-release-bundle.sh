#!/usr/bin/env bash
# Artifact smoke for staged Easy Setup candidate host trees (#455).
# Distinct from runtime Docker release-image smoke (scripts/release-smoke.sh).
set -Eeuo pipefail
set +x

STAGED_ROOT="${1:-}"
if [[ -z "${STAGED_ROOT}" || ! -d "${STAGED_ROOT}" ]]; then
  echo "Usage: $0 <staged-root>" >&2
  exit 2
fi

pass() { echo "[PASS] $*"; }
fail() { echo "[FAIL] $*" >&2; exit 1; }

smoke_one() {
  local rid_dir="$1"
  local rid
  rid="$(basename "${rid_dir}")"
  local bin
  if [[ "${rid}" == "win-x64" ]]; then
    bin="${rid_dir}/Amane.Mailer.exe"
  else
    bin="${rid_dir}/Amane.Mailer"
  fi

  [[ -f "${bin}" ]] || fail "${rid}: host binary missing"
  [[ -f "${rid_dir}/release-bundle-manifest.json" ]] || fail "${rid}: manifest missing"
  [[ -f "${rid_dir}/SHA256SUMS" ]] || fail "${rid}: SHA256SUMS missing"
  [[ -f "${rid_dir}/compose.yml" ]] || fail "${rid}: compose.yml missing"
  [[ -f "${rid_dir}/compose.image-digest.yml" ]] || fail "${rid}: digest overlay missing"
  [[ -f "${rid_dir}/README-SETUP.md" ]] || fail "${rid}: README-SETUP.md missing"

  if grep -qiE '"imageTag"[[:space:]]*:[[:space:]]*"latest"' \
    "${rid_dir}/release-bundle-manifest.json"; then
    fail "${rid}: manifest pins latest"
  fi

  if ! grep -q 'ociIndexDigest' "${rid_dir}/release-bundle-manifest.json"; then
    fail "${rid}: manifest missing ociIndexDigest"
  fi

  # Host binary smoke only when the binary matches this OS/arch.
  local can_exec=0
  case "$(uname -s)-$(uname -m)" in
    Linux-x86_64)
      [[ "${rid}" == "linux-x64" ]] && can_exec=1
      ;;
    Linux-aarch64)
      [[ "${rid}" == "linux-arm64" ]] && can_exec=1
      ;;
    MINGW*|MSYS*|CYGWIN*|Windows*)
      [[ "${rid}" == "win-x64" ]] && can_exec=1
      ;;
  esac

  if [[ "${can_exec}" == "1" ]]; then
    chmod +x "${bin}" 2>/dev/null || true
    "${bin}" --help >/dev/null || fail "${rid}: --help failed"
    pass "${rid}: --help"
    "${bin}" setup assistant --help >/dev/null || fail "${rid}: setup assistant --help failed"
    pass "${rid}: setup assistant --help"
  else
    pass "${rid}: binary present (exec smoke skipped; cross-RID)"
  fi
}

found=0
for rid_dir in "${STAGED_ROOT}"/*; do
  [[ -d "${rid_dir}" ]] || continue
  found=1
  smoke_one "${rid_dir}"
done

[[ "${found}" == "1" ]] || fail "No staged RID directories under ${STAGED_ROOT}"
pass "artifact smoke complete (runtime Docker smoke is separate)"
