#!/usr/bin/env bash
# Portable fail-close probes for classify-crane-digest-lookup.sh
# Used by release-client-self-test (Linux + Windows Git Bash / MSYS).
set -Eeuo pipefail

ROOT="$(cd "$(dirname "$0")/.." && pwd)"
HELPER="${ROOT}/scripts/classify-crane-digest-lookup.sh"
# shellcheck source=scripts/classify-crane-digest-lookup.sh
source "${HELPER}"

WORK="$(mktemp -d "${TMPDIR:-/tmp}/amane-crane-classify.XXXXXX")"
cleanup() { rm -rf "${WORK}"; }
trap cleanup EXIT

cat >"${WORK}/crane-auth" <<'EOF'
#!/usr/bin/env bash
set -euo pipefail
cmd="${1:-}"
shift || true
case "${cmd}" in
  digest)
    echo "unauthorized: authentication required" >&2
    exit 1
    ;;
  copy)
    echo "COPY_EXECUTED" >&2
    exit 0
    ;;
  *)
    echo "unexpected crane cmd: ${cmd}" >&2
    exit 99
    ;;
esac
EOF
chmod +x "${WORK}/crane-auth"

cat >"${WORK}/crane-absent" <<'EOF'
#!/usr/bin/env bash
echo "MANIFEST_UNKNOWN: manifest unknown" >&2
exit 1
EOF
chmod +x "${WORK}/crane-absent"

cat >"${WORK}/crane-malformed" <<'EOF'
#!/usr/bin/env bash
set -euo pipefail
cmd="${1:-}"
shift || true
case "${cmd}" in
  digest)
    echo "malformed-value"
    exit 0
    ;;
  copy)
    echo "COPY_EXECUTED" >&2
    exit 0
    ;;
  *)
    echo "unexpected crane cmd: ${cmd}" >&2
    exit 99
    ;;
esac
EOF
chmod +x "${WORK}/crane-malformed"

initial="$(classify_crane_digest_lookup "${WORK}/crane-auth" 'ghcr.io/example/amane-mailer:latest')"
initial_class="${initial%%|*}"
if [[ "${initial_class}" != "UNKNOWN" ]]; then
  echo "WORKFLOW_LATEST_UNKNOWN_INITIAL=FAIL class=${initial_class}" >&2
  exit 1
fi
echo "WORKFLOW_LATEST_UNKNOWN_INITIAL=PASS"

copy_executed=0
pre_lookup="$(classify_crane_digest_lookup "${WORK}/crane-auth" 'ghcr.io/example/amane-mailer:latest')"
pre_class="${pre_lookup%%|*}"
case "${pre_class}" in
  PRESENT|ABSENT)
    "${WORK}/crane-auth" copy 'ghcr.io/example/amane-mailer@sha256:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa' 'ghcr.io/example/amane-mailer:latest' && copy_executed=1
    ;;
  UNKNOWN)
    echo 'STOP_NO_COPY'
    ;;
  *)
    echo "unexpected pre_class=${pre_class}" >&2
    exit 1
    ;;
esac
if [[ "${pre_class}" != "UNKNOWN" || "${copy_executed}" -ne 0 ]]; then
  echo "WORKFLOW_LATEST_UNKNOWN_PRECOPY=FAIL class=${pre_class} copy=${copy_executed}" >&2
  exit 1
fi
echo "WORKFLOW_LATEST_UNKNOWN_PRECOPY=PASS"

absent="$(classify_crane_digest_lookup "${WORK}/crane-absent" 'ghcr.io/example/amane-mailer:latest')"
absent_class="${absent%%|*}"
if [[ "${absent_class}" != "ABSENT" ]]; then
  echo "CLASSIFY_ABSENT_CONTROL=FAIL class=${absent_class}" >&2
  exit 1
fi
echo "CLASSIFY_ABSENT_CONTROL=PASS"

malformed="$(classify_crane_digest_lookup "${WORK}/crane-malformed" 'ghcr.io/example/amane-mailer:latest')"
malformed_class="${malformed%%|*}"
if [[ "${malformed_class}" != "UNKNOWN" ]]; then
  echo "CLASSIFY_MALFORMED_SUCCESS=FAIL class=${malformed_class}" >&2
  exit 1
fi
echo "CLASSIFY_MALFORMED_SUCCESS=PASS"

malformed_copy_executed=0
malformed_pre="$(classify_crane_digest_lookup "${WORK}/crane-malformed" 'ghcr.io/example/amane-mailer:latest')"
malformed_pre_class="${malformed_pre%%|*}"
case "${malformed_pre_class}" in
  PRESENT|ABSENT)
    "${WORK}/crane-malformed" copy 'ghcr.io/example/amane-mailer@sha256:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa' 'ghcr.io/example/amane-mailer:latest' && malformed_copy_executed=1
    ;;
  UNKNOWN)
    echo 'STOP_NO_COPY'
    ;;
  *)
    echo "unexpected malformed_pre_class=${malformed_pre_class}" >&2
    exit 1
    ;;
esac
if [[ "${malformed_pre_class}" != "UNKNOWN" || "${malformed_copy_executed}" -ne 0 ]]; then
  echo "WORKFLOW_LATEST_MALFORMED_PRECOPY=FAIL class=${malformed_pre_class} copy=${malformed_copy_executed}" >&2
  exit 1
fi
echo "WORKFLOW_LATEST_MALFORMED_PRECOPY=PASS"
