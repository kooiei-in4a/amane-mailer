#!/usr/bin/env bash
# Fixture self-test for scripts/validate-candidate-oci-run.sh (no live GitHub API).
set -Eeuo pipefail
set +x

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" >/dev/null 2>&1 && pwd)"
FIXDIR="${SCRIPT_DIR}/testdata/candidate-oci-run"
VALIDATOR="${SCRIPT_DIR}/validate-candidate-oci-run.sh"
PASS_COUNT=0
FAIL_COUNT=0

die() { echo "[FAIL] $*" >&2; exit 1; }

expect_pass() {
  local name="$1"; shift
  if "$@" >/tmp/validate-oci-pass.out 2>/tmp/validate-oci-pass.err; then
    echo "[PASS] ${name}"
    PASS_COUNT=$((PASS_COUNT + 1))
  else
    echo "[FAIL] expected success: ${name}" >&2
    cat /tmp/validate-oci-pass.err >&2 || true
    FAIL_COUNT=$((FAIL_COUNT + 1))
  fi
}

expect_fail() {
  local name="$1"; shift
  if "$@" >/tmp/validate-oci-fail.out 2>/tmp/validate-oci-fail.err; then
    echo "[FAIL] expected failure: ${name}" >&2
    cat /tmp/validate-oci-fail.out >&2 || true
    FAIL_COUNT=$((FAIL_COUNT + 1))
  else
    echo "[PASS] ${name}"
    PASS_COUNT=$((PASS_COUNT + 1))
  fi
}

base_env() {
  export CANDIDATE_RUN_ID="4550001"
  export OCI_ARTIFACT_NAME="setup-release-candidate-oci"
  export CANDIDATE_ARTIFACT_ID="900001"
  export RELEASE_COMMIT_SHA="013dd6741c2825f518a78d5b28cc1c4718dba5df"
  export EXPECTED_HEAD_BRANCH="release/v1.2.0-rc"
  export CANDIDATE_WORKFLOW_NAME="Generate Setup Release Candidate"
  export CANDIDATE_WORKFLOW_PATH=".github/workflows/generate-setup-release-candidate.yml"
  export GITHUB_REPOSITORY="kooiei-in4a/amane-mailer"
  export CANDIDATE_RUN_JSON_FILE="${FIXDIR}/valid-run.json"
  export CANDIDATE_ARTIFACTS_JSON_FILE="${FIXDIR}/valid-artifacts.json"
  unset GH_TOKEN || true
}

run_validator() {
  bash "${VALIDATOR}"
}

[[ -x "${VALIDATOR}" || -f "${VALIDATOR}" ]] || die "validator missing"
[[ -d "${FIXDIR}" ]] || die "fixture dir missing"

base_env
expect_pass "valid candidate run" run_validator

base_env
export CANDIDATE_RUN_JSON_FILE="${FIXDIR}/wrong-workflow-name.json"
expect_fail "wrong workflow name" run_validator

base_env
export CANDIDATE_RUN_JSON_FILE="${FIXDIR}/wrong-workflow-path.json"
expect_fail "wrong workflow path" run_validator

base_env
export CANDIDATE_RUN_JSON_FILE="${FIXDIR}/wrong-event.json"
expect_fail "wrong event" run_validator

base_env
export CANDIDATE_RUN_JSON_FILE="${FIXDIR}/wrong-head-branch.json"
expect_fail "wrong head branch" run_validator

base_env
export CANDIDATE_RUN_JSON_FILE="${FIXDIR}/wrong-head-sha.json"
expect_fail "wrong head SHA" run_validator

base_env
export CANDIDATE_RUN_JSON_FILE="${FIXDIR}/wrong-attempt.json"
expect_fail "run_attempt != 1" run_validator

base_env
export CANDIDATE_RUN_JSON_FILE="${FIXDIR}/incomplete-run.json"
expect_fail "incomplete run" run_validator

base_env
export CANDIDATE_RUN_JSON_FILE="${FIXDIR}/failed-run.json"
expect_fail "failed run" run_validator

base_env
export CANDIDATE_ARTIFACTS_JSON_FILE="${FIXDIR}/artifact-missing.json"
expect_fail "artifact missing" run_validator

base_env
export CANDIDATE_ARTIFACTS_JSON_FILE="${FIXDIR}/artifact-duplicated.json"
expect_fail "artifact duplicated" run_validator

base_env
export CANDIDATE_ARTIFACTS_JSON_FILE="${FIXDIR}/artifact-expired.json"
expect_fail "artifact expired" run_validator

base_env
export CANDIDATE_ARTIFACT_ID="999999"
expect_fail "artifact ID mismatch" run_validator

if [[ "${FAIL_COUNT}" -ne 0 ]]; then
  die "validate-candidate-oci-run self-test failures: ${FAIL_COUNT} (passes=${PASS_COUNT})"
fi

echo "[info] validate-candidate-oci-run self-test passed (passes=${PASS_COUNT} fails=${FAIL_COUNT})"
echo "finalResult=PASS"
