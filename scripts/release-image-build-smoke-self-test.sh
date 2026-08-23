#!/usr/bin/env bash
# Static contract checks for the first #649 release-image automation slice.
set -Eeuo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" >/dev/null 2>&1 && pwd)"
REPO_ROOT="$(cd "${SCRIPT_DIR}/.." >/dev/null 2>&1 && pwd)"
TARGET="${REPO_ROOT}/scripts/release-image-build-smoke.sh"
WORKFLOW="${REPO_ROOT}/.github/workflows/release-image-build-smoke.yml"
PUBLISH_TARGET="${REPO_ROOT}/scripts/publish-release-image.sh"
PUBLISH_WORKFLOW="${REPO_ROOT}/.github/workflows/publish-release-image.yml"
REPRO_TARGET="${REPO_ROOT}/scripts/check-release-image-reproducibility.sh"

bash -n "${TARGET}"
bash -n "${PUBLISH_TARGET}"
bash -n "${REPRO_TARGET}"

grep -F -- '--platform "${PLATFORM}"' "${TARGET}" >/dev/null
grep -F -- '--build-arg "SOURCE_COMMIT=${SOURCE_SHA}"' "${TARGET}" >/dev/null
grep -F -- '--provenance=false' "${TARGET}" >/dev/null
grep -F -- '--sbom=false' "${TARGET}" >/dev/null
grep -F -- '--output "type=docker,dest=${DOCKER_IMAGE_TAR}"' "${TARGET}" >/dev/null
grep -F -- '--output "type=oci,dest=${IMAGE_TAR},rewrite-timestamp=true"' "${TARGET}" >/dev/null
grep -F -- 'tar -xf "${IMAGE_TAR}" -C "${OCI_LAYOUT_DIR}"' "${TARGET}" >/dev/null
grep -F -- 'docker load --input "${DOCKER_IMAGE_TAR}"' "${TARGET}" >/dev/null
grep -F -- 'OCI layout digest does not match Buildx metadata digest' "${TARGET}" >/dev/null
grep -F -- 'oci-layout' "${TARGET}" >/dev/null
grep -F -- '--build-arg "SOURCE_DATE_EPOCH=${SOURCE_DATE_EPOCH}"' "${TARGET}" >/dev/null
grep -F -- '/p:CI=true' "${REPO_ROOT}/infra/docker/Dockerfile" >/dev/null
grep -F -- '/p:ContinuousIntegrationBuild=true' "${REPO_ROOT}/infra/docker/Dockerfile" >/dev/null
grep -F -- 'export MAILER_PULL_POLICY="never"' "${TARGET}" >/dev/null
grep -F -- 'container --help' "${TARGET}" >/dev/null
grep -F -- "'/healthz'" "${TARGET}" >/dev/null
grep -F -- "'/readyz'" "${TARGET}" >/dev/null
grep -F -- 'sourceDateEpoch' "${TARGET}" >/dev/null

grep -F -- 'ARG SOURCE_DATE_EPOCH=' "${REPO_ROOT}/infra/docker/Dockerfile" >/dev/null
grep -F -- 'DeterministicStaticWebAssetsTimestamp' "${REPO_ROOT}/infra/docker/Dockerfile" >/dev/null
grep -F -- 'NormalizeDeterministicStaticWebAssetSourceTimestamps' "${REPO_ROOT}/src/Amane.Mailer/Amane.Mailer.csproj" >/dev/null
grep -F -- 'NormalizeDeterministicPublishStaticWebAssetTimestamps' "${REPO_ROOT}/src/Amane.Mailer/Amane.Mailer.csproj" >/dev/null

grep -F -- '"${CRANE_BIN}" push "${OCI_LAYOUT}" "${VERSION_REF}"' "${PUBLISH_TARGET}" >/dev/null
grep -F -- '"${CRANE_BIN}" copy "${REPOSITORY}@${EXPECTED_DIGEST}" "${SHA_REF}"' "${PUBLISH_TARGET}" >/dev/null
grep -F -- 'smoke-tested digest' "${PUBLISH_TARGET}" >/dev/null
grep -F -- 'packages: write' "${PUBLISH_WORKFLOW}" >/dev/null
grep -F -- 'Publish exact smoke-tested digest without rebuild' "${PUBLISH_WORKFLOW}" >/dev/null
grep -F -- '--no-cache' "${REPRO_TARGET}" >/dev/null
grep -F -- '--provenance=false' "${REPRO_TARGET}" >/dev/null
grep -F -- '--sbom=false' "${REPRO_TARGET}" >/dev/null
grep -F -- 'reproducibility digest mismatch' "${REPRO_TARGET}" >/dev/null
grep -F -- 'Rebuild without cache and require the same digest' "${WORKFLOW}" "${PUBLISH_WORKFLOW}" >/dev/null
grep -E -- 'MAILPIT_IMAGE: axllent/mailpit@sha256:[0-9a-f]{64}$' "${WORKFLOW}" "${PUBLISH_WORKFLOW}" >/dev/null
grep -F -- 'ref: ${{ github.sha }}' "${WORKFLOW}" "${PUBLISH_WORKFLOW}" >/dev/null
grep -F -- 'source_sha must equal the workflow commit GITHUB_SHA' "${WORKFLOW}" "${PUBLISH_WORKFLOW}" >/dev/null

if grep -F -- 'ref: ${{ inputs.source_sha }}' "${WORKFLOW}" "${PUBLISH_WORKFLOW}"; then
  echo '[error] release workflows must not checkout an arbitrary source_sha input' >&2
  exit 1
fi

if grep -nE 'docker (login|push)|packages:[[:space:]]*write' "${TARGET}" "${WORKFLOW}"; then
  echo '[error] initial build-smoke slice must not publish or request package write permission' >&2
  exit 1
fi

if grep -nE '^[[:space:]]+actions:[[:space:]]*write[[:space:]]*$' "${WORKFLOW}"; then
  echo '[error] build-smoke workflow must not request actions write permission' >&2
  exit 1
fi

if grep -nF 'ALLOW_DIRTY_WORKTREE' "${WORKFLOW}"; then
  echo '[error] workflow must always enforce an exact clean source checkout' >&2
  exit 1
fi

echo 'release-image-build-smoke-self-test: PASS'
