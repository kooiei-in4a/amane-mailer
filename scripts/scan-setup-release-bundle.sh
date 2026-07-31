#!/usr/bin/env bash
# Secret / private-config scan for staged Easy Setup candidate trees (#455).
set -Eeuo pipefail
set +x

STAGED_ROOT="${1:-}"
if [[ -z "${STAGED_ROOT}" || ! -d "${STAGED_ROOT}" ]]; then
  echo "Usage: $0 <staged-root>" >&2
  exit 2
fi

echo "[info] Scanning ${STAGED_ROOT} for forbidden secret-like paths"

mapfile -t hits < <(
  find "${STAGED_ROOT}" \( \
    -name '.env' -o \
    -name 'tenants.json' -o \
    -name 'secrets.env' -o \
    -name 'rclone.conf' -o \
    -name 'host-sealing-key' -o \
    -name 'acs_connection_string' -o \
    -name 'queue_connection_string' -o \
    -name 'id_rsa' -o \
    -name '*.db' -o \
    -name '*.db.age' \
  \) -print || true
)

if ((${#hits[@]} > 0)); then
  echo "[error] Forbidden paths detected in candidate tree:" >&2
  printf '  %s\n' "${hits[@]}" >&2
  exit 1
fi

# Fail if any staged text file contains an obvious ACS endpoint secret pattern.
# Values are never printed.
if grep -RIl --exclude='*.exe' --exclude='Amane.Mailer' \
  -E 'endpoint=https://.*\.communication\.azure\.com/;accesskey=' \
  "${STAGED_ROOT}" >/dev/null 2>&1; then
  echo "[error] Candidate tree appears to embed an ACS connection-string pattern." >&2
  exit 1
fi

echo "[PASS] secret scan"
