#!/usr/bin/env bash
# Fixture self-test for release-smoke preflight (issue #506). No registry access required.
set -Eeuo pipefail
set +x

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" >/dev/null 2>&1 && pwd)"
REPO_ROOT="$(cd "$SCRIPT_DIR/.." >/dev/null 2>&1 && pwd)"
# shellcheck source=lib/release-smoke-preflight.sh
source "$SCRIPT_DIR/lib/release-smoke-preflight.sh"

PASS_COUNT=0
FAIL_COUNT=0
FAKE_BIN=""
FAKE_LOG=""

cleanup() {
  if [[ -n "${FAKE_BIN}" && -d "${FAKE_BIN}" ]]; then
    rm -rf "${FAKE_BIN}"
  fi
}
trap cleanup EXIT

pass() {
  echo "[PASS] $1"
  PASS_COUNT=$((PASS_COUNT + 1))
}

fail_case() {
  echo "[FAIL] $1" >&2
  FAIL_COUNT=$((FAIL_COUNT + 1))
}

expect_preflight_fail() {
  local name="$1"
  shift
  if ( "$@" ) >/tmp/release-smoke-preflight-pass.out 2>/tmp/release-smoke-preflight-pass.err; then
    fail_case "expected failure: ${name}"
    cat /tmp/release-smoke-preflight-pass.err >&2 || true
  else
    pass "${name}"
  fi
}

setup_fake_docker() {
  local endpoint="${1:-unix:///var/run/docker.sock}"
  FAKE_BIN="$(mktemp -d)"
  FAKE_LOG="${FAKE_BIN}/docker.log"
  : >"${FAKE_LOG}"
  cat >"${FAKE_BIN}/docker" <<EOF
#!/usr/bin/env bash
set -eu
case "\${1:-}" in
  compose)
    shift
    if [[ "\${1:-}" == "version" ]]; then
      exit 0
    fi
    printf '%s\n' "\$*" >> "${FAKE_LOG}"
    exit 0
    ;;
  context)
    if [[ "\${2:-}" == "inspect" ]]; then
      printf '%s\n' "${endpoint}"
      exit 0
    fi
    ;;
esac
exit 1
EOF
  chmod +x "${FAKE_BIN}/docker"
  export PATH="${FAKE_BIN}:${PATH}"
  export RELEASE_SMOKE_SKIP_DOCKER_ENDPOINT_CHECK=0
}

run_preflight() {
  release_smoke_preflight_run "$REPO_ROOT"
}

base_env() {
  unset MAILER_IMAGE_DIGEST COMPOSE_PROJECT_NAME COMPOSE_FILE DOCKER_HOST RELEASE_SMOKE_PROJECT || true
  export MAILER_IMAGE_TAG='v1.3.6'
  export RELEASE_SMOKE_PROJECT='amane-mailer-release-smoke'
  setup_fake_docker 'unix:///var/run/docker.sock'
}

# N1: missing artifact
base_env
unset MAILER_IMAGE_TAG MAILER_IMAGE_DIGEST
expect_preflight_fail 'N1 missing artifact' run_preflight

# N2: latest tag rejected
base_env
export MAILER_IMAGE_TAG='latest'
expect_preflight_fail 'N2 latest tag rejected' run_preflight

# N3: tag and digest both supplied
base_env
export MAILER_IMAGE_TAG='v1.3.6'
export MAILER_IMAGE_DIGEST='sha256:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa'
expect_preflight_fail 'N3 tag and digest both supplied' run_preflight

# N4: malformed digest
base_env
unset MAILER_IMAGE_TAG
export MAILER_IMAGE_DIGEST='sha256:NOTVALID'
expect_preflight_fail 'N4 malformed digest' run_preflight

# N5: invalid project name
base_env
export RELEASE_SMOKE_PROJECT='unrelated-canary'
expect_preflight_fail 'N5 invalid RELEASE_SMOKE_PROJECT' run_preflight

# N8: remote docker endpoint
base_env
setup_fake_docker 'tcp://127.0.0.1:2375'
expect_preflight_fail 'N8 remote docker endpoint' run_preflight

# Positive preflight + N6/N7 compose argv isolation
base_env
if run_preflight >/tmp/release-smoke-preflight-ok.out 2>/tmp/release-smoke-preflight-ok.err; then
  pass 'preflight success with explicit tag'
else
  fail_case 'preflight success with explicit tag'
  cat /tmp/release-smoke-preflight-ok.err >&2 || true
fi

if [[ "${MAILER_IMAGE_REFERENCE}" == "ghcr.io/kooiei-in4a/amane-mailer:v1.3.6" ]]; then
  pass 'MAILER_IMAGE_REFERENCE resolved from tag'
else
  fail_case "MAILER_IMAGE_REFERENCE resolved from tag (got ${MAILER_IMAGE_REFERENCE})"
fi

COMPOSE=(docker compose -p "$RELEASE_SMOKE_PROJECT" -f "$RELEASE_SMOKE_COMPOSE_FILE")
COMPOSE_PROJECT_NAME='unrelated-canary' COMPOSE_FILE='unrelated-compose.yml' "${COMPOSE[@]}" ps >/dev/null 2>&1 || true
logged="$(cat "${FAKE_LOG}")"
if grep -F -- '-p amane-mailer-release-smoke' <<<"${logged}" >/dev/null \
  && grep -F -- '-f' <<<"${logged}" >/dev/null \
  && grep -F -- 'infra/docker/docker-compose.release-smoke.yml' <<<"${logged}" >/dev/null \
  && ! grep -F -- 'unrelated-canary' <<<"${logged}" >/dev/null \
  && ! grep -F -- 'unrelated-compose.yml' <<<"${logged}" >/dev/null; then
  pass 'N6/N7 compose argv ignores COMPOSE_PROJECT_NAME and COMPOSE_FILE'
else
  fail_case 'N6/N7 compose argv ignores COMPOSE_PROJECT_NAME and COMPOSE_FILE'
  echo "logged: ${logged}" >&2
fi

# Verify preflight failure does not invoke docker compose (subshell contains exit 2).
neg_bin="$(mktemp -d)"
neg_log="${neg_bin}/docker.log"
: >"${neg_log}"
cat >"${neg_bin}/docker" <<EOF
#!/usr/bin/env bash
set -eu
case "\${1:-}" in
  compose)
    shift
    if [[ "\${1:-}" == "version" ]]; then
      exit 0
    fi
    printf '%s\n' "\$*" >> "${neg_log}"
    exit 0
    ;;
  context)
    if [[ "\${2:-}" == "inspect" ]]; then
      printf '%s\n' 'unix:///var/run/docker.sock'
      exit 0
    fi
    ;;
esac
exit 1
EOF
chmod +x "${neg_bin}/docker"
PATH="${neg_bin}:${PATH}" RELEASE_SMOKE_SKIP_DOCKER_ENDPOINT_CHECK=0 \
  env -u MAILER_IMAGE_TAG -u MAILER_IMAGE_DIGEST MAILER_IMAGE_TAG= \
  bash -c "source '${SCRIPT_DIR}/lib/release-smoke-preflight.sh'; release_smoke_preflight_run '${REPO_ROOT}'" \
  >/dev/null 2>&1 || true
if [[ ! -s "${neg_log}" ]]; then
  pass 'negative cases perform zero docker compose mutations'
else
  fail_case 'negative cases perform zero docker compose mutations'
  cat "${neg_log}" >&2
fi
rm -rf "${neg_bin}"

if [[ "${FAIL_COUNT}" -ne 0 ]]; then
  echo "[info] release-smoke-preflight-self-test failures: ${FAIL_COUNT} (passes=${PASS_COUNT})" >&2
  exit 1
fi

echo "[info] release-smoke-preflight-self-test passed (passes=${PASS_COUNT} fails=${FAIL_COUNT})"
