#!/usr/bin/env bash
# Static contract self-test for the verify-only public release image workflow.
set -Eeuo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" >/dev/null 2>&1 && pwd)"
REPO_ROOT="$(cd "${SCRIPT_DIR}/.." >/dev/null 2>&1 && pwd)"
WORKFLOW="${REPO_ROOT}/.github/workflows/verify-public-release-image.yml"

[[ -f "${WORKFLOW}" ]] || { echo '[error] verify-only workflow is missing' >&2; exit 1; }
bash -n "${BASH_SOURCE[0]}"

for needle in \
  'workflow_dispatch:' \
  'publication_run_id:' \
  'publication_source_sha:' \
  'release_version:' \
  'expected_digest:' \
  'contents: read' \
  'actions: read' \
  'actions/download-artifact@3e5f45b2cfb9172054b4087a40e8e0b5a5461e7c' \
  'run-id: ${{ inputs.publication_run_id }}' \
  'scripts/verify-published-release-image.sh' \
  'release-publication-evidence.json' \
  'public-consumer-verification.json'
do
  grep -F -- "${needle}" "${WORKFLOW}" >/dev/null \
    || { echo "[error] missing workflow contract: ${needle}" >&2; exit 1; }
done

if grep -nE 'packages:[[:space:]]+write|docker (build|login|push)|buildx build|crane (push|copy)' "${WORKFLOW}"; then
  echo '[error] verify-only workflow must not build, login, or publish' >&2
  exit 1
fi

echo 'verify-public-release-image-workflow-self-test: PASS'
