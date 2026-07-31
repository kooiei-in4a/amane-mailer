#!/usr/bin/env bash
# Write handoff notes from #455 candidate packaging to #456 / #458.
set -Eeuo pipefail
set +x

OUT_ROOT="${1:-}"
SOURCE_SHA="${2:-}"
OCI_INDEX_DIGEST="${3:-}"
MAILER_VERSION="${4:-}"

if [[ -z "${OUT_ROOT}" || -z "${SOURCE_SHA}" || -z "${OCI_INDEX_DIGEST}" || -z "${MAILER_VERSION}" ]]; then
  echo "Usage: $0 <out-root> <source-sha> <oci-index-digest> <mailer-version>" >&2
  exit 2
fi

HANDOFF="${OUT_ROOT}/CANDIDATE-HANDOFF.md"
{
  echo "# Easy Setup release-candidate handoff (#455 → #456 / #458)"
  echo
  echo "- Generated at (UTC): $(date -u +%Y-%m-%dT%H:%M:%SZ)"
  echo "- Source commit SHA: \`${SOURCE_SHA}\`"
  echo "- Mailer version label: \`${MAILER_VERSION}\`"
  echo "- OCI index digest (local layout; **not** pushed to GHCR): \`${OCI_INDEX_DIGEST}\`"
  echo
  echo "## Ownership"
  echo
  echo "| Issue | Owns |"
  echo "|-------|------|"
  echo "| #455 (this packaging) | Reproducible candidate generation, secret scan, artifact smoke, checksums |"
  echo "| #456 | Qualification / go-no-go on these candidates |"
  echo "| #458 | Tag, GHCR publish, GitHub Release, public checksum recording |"
  echo
  echo "## Explicit non-goals completed as non-goals"
  echo
  echo "- No Git tag created"
  echo "- No GHCR push"
  echo "- No GitHub Release"
  echo "- No MSI / deb / rpm"
  echo "- No auto-updater"
  echo
  echo "## Next commands for #456"
  echo
  echo "1. Verify each staged \`release-bundle-manifest.json\` schemaVersion=1"
  echo "2. Confirm \`ociIndexDigest\` equals \`imageDigest\`"
  echo "3. Run qualification scenarios from issue #456 using these archives"
  echo "4. Keep runtime Docker smoke evidence separate from artifact smoke"
} > "${HANDOFF}"

echo "[info] Wrote ${HANDOFF}"
