#!/usr/bin/env bash
# Validate a #455 candidate workflow run before cross-run artifact download.
# Inputs via env (no secrets printed):
#   CANDIDATE_RUN_ID, OCI_ARTIFACT_NAME, CANDIDATE_ARTIFACT_ID (required),
#   CANDIDATE_HANDOFF_ARTIFACT_NAME, CANDIDATE_HANDOFF_ARTIFACT_ID (optional),
#   RELEASE_COMMIT_SHA, EXPECTED_HEAD_BRANCH, EXPECTED_RUN_ATTEMPT (default 1),
#   CANDIDATE_WORKFLOW_ID (optional; required by the #504 Git promotion path),
#   CANDIDATE_WORKFLOW_NAME, CANDIDATE_WORKFLOW_PATH, GITHUB_REPOSITORY
# Optional fixture overrides (for self-test; skips gh api):
#   CANDIDATE_RUN_JSON_FILE, CANDIDATE_ARTIFACTS_JSON_FILE
# Auth for live API mode:
#   GH_TOKEN
set -Eeuo pipefail
set +x

die() { echo "[error] $*" >&2; exit 1; }

: "${CANDIDATE_RUN_ID:?}"
: "${OCI_ARTIFACT_NAME:?}"
: "${CANDIDATE_ARTIFACT_ID:?}"
: "${RELEASE_COMMIT_SHA:?}"
: "${EXPECTED_HEAD_BRANCH:?}"
: "${CANDIDATE_WORKFLOW_NAME:?}"
: "${CANDIDATE_WORKFLOW_PATH:?}"
: "${GITHUB_REPOSITORY:?}"
[[ "${CANDIDATE_RUN_ID}" =~ ^[0-9]+$ ]] || die "candidate_workflow_run_id must be numeric"
[[ "${CANDIDATE_ARTIFACT_ID}" =~ ^[0-9]+$ ]] || die "candidate_artifact_id must be numeric"
EXPECTED_RUN_ATTEMPT="${EXPECTED_RUN_ATTEMPT:-1}"
[[ "${EXPECTED_RUN_ATTEMPT}" =~ ^[1-9][0-9]*$ ]] || die "candidate_workflow_run_attempt must be a positive integer"
export EXPECTED_RUN_ATTEMPT
if [[ -n "${CANDIDATE_HANDOFF_ARTIFACT_NAME:-}" || -n "${CANDIDATE_HANDOFF_ARTIFACT_ID:-}" ]]; then
  [[ -n "${CANDIDATE_HANDOFF_ARTIFACT_NAME:-}" ]] || die "candidate_handoff_artifact_name is required when handoff artifact validation is enabled"
  [[ "${CANDIDATE_HANDOFF_ARTIFACT_ID:-}" =~ ^[0-9]+$ ]] || die "candidate_handoff_artifact_id must be numeric"
fi
[[ "${RELEASE_COMMIT_SHA}" =~ ^[0-9a-f]{40}$ ]] || die "release_commit_sha must be 40 lowercase hex"

command -v python3 >/dev/null 2>&1 || die "python3 is required"

if [[ -n "${CANDIDATE_RUN_JSON_FILE:-}" ]]; then
  [[ -f "${CANDIDATE_RUN_JSON_FILE}" ]] || die "CANDIDATE_RUN_JSON_FILE not found"
  run_json="$(cat "${CANDIDATE_RUN_JSON_FILE}")"
else
  : "${GH_TOKEN:?}"
  command -v gh >/dev/null 2>&1 || die "gh is required"
  run_json="$(gh api "repos/${GITHUB_REPOSITORY}/actions/runs/${CANDIDATE_RUN_ID}")"
fi

# Pass JSON via env so the Python program can use a here-doc without stdin conflict.
export CANDIDATE_RUN_JSON="${run_json}"
python3 - <<'PY'
import json, os, sys

run = json.loads(os.environ["CANDIDATE_RUN_JSON"])
errors = []
checks = [
    ("id", int(os.environ["CANDIDATE_RUN_ID"])),
    ("name", os.environ["CANDIDATE_WORKFLOW_NAME"]),
    ("path", os.environ["CANDIDATE_WORKFLOW_PATH"]),
    ("event", "workflow_dispatch"),
    ("head_branch", os.environ["EXPECTED_HEAD_BRANCH"]),
    ("head_sha", os.environ["RELEASE_COMMIT_SHA"]),
]
if os.environ.get("CANDIDATE_WORKFLOW_ID"):
    checks.insert(1, ("workflow_id", int(os.environ["CANDIDATE_WORKFLOW_ID"])))
for key, expect in checks:
    got = run.get(key)
    if got != expect:
        errors.append(f"{key} {got!r} != {expect!r}")
expected_attempt = int(os.environ["EXPECTED_RUN_ATTEMPT"])
if int(run.get("run_attempt") or 0) != expected_attempt:
    errors.append(f"run_attempt {run.get('run_attempt')!r} != expected {expected_attempt}")
if run.get("status") != "completed":
    errors.append(f"status {run.get('status')!r} != 'completed'")
if run.get("conclusion") != "success":
    errors.append(f"conclusion {run.get('conclusion')!r} != 'success'")
if errors:
    for e in errors:
        print(f"[error] {e}", file=sys.stderr)
    raise SystemExit(1)
print("[info] candidate workflow run identity validated")
PY

if [[ -n "${CANDIDATE_ARTIFACTS_JSON_FILE:-}" ]]; then
  [[ -f "${CANDIDATE_ARTIFACTS_JSON_FILE}" ]] || die "CANDIDATE_ARTIFACTS_JSON_FILE not found"
  arts_json="$(cat "${CANDIDATE_ARTIFACTS_JSON_FILE}")"
else
  : "${GH_TOKEN:?}"
  command -v gh >/dev/null 2>&1 || die "gh is required"
  arts_json="$(gh api "repos/${GITHUB_REPOSITORY}/actions/runs/${CANDIDATE_RUN_ID}/artifacts" --paginate)"
fi

export CANDIDATE_ARTIFACTS_JSON="${arts_json}"
resolve_artifact_id() {
  local artifact_name="$1"
  local artifact_id="$2"
  ARTIFACT_NAME="${artifact_name}" ARTIFACT_ID="${artifact_id}" python3 - <<'PY'
import json, os, sys

payload = json.loads(os.environ["CANDIDATE_ARTIFACTS_JSON"])
arts = payload.get("artifacts") or []
name = os.environ["ARTIFACT_NAME"]
want_id = os.environ["ARTIFACT_ID"].strip()
matches = [a for a in arts if a.get("name") == name]
if not matches:
    print(f"[error] artifact name {name!r} not found on candidate run", file=sys.stderr)
    raise SystemExit(1)
if len(matches) != 1:
    print(
        f"[error] artifact name {name!r} matched {len(matches)} artifacts; refuse ambiguous handoff",
        file=sys.stderr,
    )
    raise SystemExit(1)
art = matches[0]
if art.get("expired") is True:
    print(f"[error] artifact {name!r} is expired", file=sys.stderr)
    raise SystemExit(1)
art_id = str(art.get("id") or "")
if want_id != art_id:
    print(f"[error] candidate_artifact_id {want_id!r} != resolved {art_id!r}", file=sys.stderr)
    raise SystemExit(1)
print(art_id)
PY
}

resolved_id="$(resolve_artifact_id "${OCI_ARTIFACT_NAME}" "${CANDIDATE_ARTIFACT_ID}")"

echo "[info] resolved candidate artifact id=${resolved_id} name=${OCI_ARTIFACT_NAME}"
if [[ -n "${CANDIDATE_HANDOFF_ARTIFACT_NAME:-}" ]]; then
  resolved_handoff_id="$(resolve_artifact_id "${CANDIDATE_HANDOFF_ARTIFACT_NAME}" "${CANDIDATE_HANDOFF_ARTIFACT_ID}")"
  echo "[info] resolved candidate handoff artifact id=${resolved_handoff_id} name=${CANDIDATE_HANDOFF_ARTIFACT_NAME}"
  if [[ -n "${GITHUB_OUTPUT:-}" ]]; then
    echo "handoff_artifact_id=${resolved_handoff_id}" >> "${GITHUB_OUTPUT}"
  fi
fi
if [[ -n "${GITHUB_OUTPUT:-}" ]]; then
  echo "artifact_id=${resolved_id}" >> "${GITHUB_OUTPUT}"
fi
