#!/usr/bin/env bash
# Validate a #455 candidate workflow run before cross-run artifact download.
# Inputs via env (no secrets printed):
#   CANDIDATE_RUN_ID, OCI_ARTIFACT_NAME, CANDIDATE_ARTIFACT_ID (optional),
#   RELEASE_COMMIT_SHA, EXPECTED_HEAD_BRANCH,
#   CANDIDATE_WORKFLOW_NAME, CANDIDATE_WORKFLOW_PATH, GITHUB_REPOSITORY, GH_TOKEN
set -Eeuo pipefail
set +x

die() { echo "[error] $*" >&2; exit 1; }

: "${CANDIDATE_RUN_ID:?}"
: "${OCI_ARTIFACT_NAME:?}"
: "${RELEASE_COMMIT_SHA:?}"
: "${EXPECTED_HEAD_BRANCH:?}"
: "${CANDIDATE_WORKFLOW_NAME:?}"
: "${CANDIDATE_WORKFLOW_PATH:?}"
: "${GITHUB_REPOSITORY:?}"
: "${GH_TOKEN:?}"

[[ "${CANDIDATE_RUN_ID}" =~ ^[0-9]+$ ]] || die "candidate_workflow_run_id must be numeric"
[[ "${RELEASE_COMMIT_SHA}" =~ ^[0-9a-f]{40}$ ]] || die "release_commit_sha must be 40 lowercase hex"
if [[ -n "${CANDIDATE_ARTIFACT_ID:-}" && ! "${CANDIDATE_ARTIFACT_ID}" =~ ^[0-9]+$ ]]; then
  die "candidate_artifact_id must be numeric when provided"
fi

command -v gh >/dev/null 2>&1 || die "gh is required"
command -v python3 >/dev/null 2>&1 || die "python3 is required"

run_json="$(gh api "repos/${GITHUB_REPOSITORY}/actions/runs/${CANDIDATE_RUN_ID}")"
printf '%s' "${run_json}" | python3 - <<'PY'
import json, os, sys
run = json.load(sys.stdin)
errors = []
checks = [
    ("name", os.environ["CANDIDATE_WORKFLOW_NAME"]),
    ("path", os.environ["CANDIDATE_WORKFLOW_PATH"]),
    ("event", "workflow_dispatch"),
    ("head_branch", os.environ["EXPECTED_HEAD_BRANCH"]),
    ("head_sha", os.environ["RELEASE_COMMIT_SHA"]),
]
for key, expect in checks:
    got = run.get(key)
    if got != expect:
        errors.append(f"{key} {got!r} != {expect!r}")
if int(run.get("run_attempt") or 0) != 1:
    errors.append(f"run_attempt {run.get('run_attempt')!r} != 1")
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

arts_json="$(gh api "repos/${GITHUB_REPOSITORY}/actions/runs/${CANDIDATE_RUN_ID}/artifacts" --paginate)"
resolved_id="$(printf '%s' "${arts_json}" | python3 - <<'PY'
import json, os, sys
payload = json.load(sys.stdin)
arts = payload.get("artifacts") or []
name = os.environ["OCI_ARTIFACT_NAME"]
want_id = (os.environ.get("CANDIDATE_ARTIFACT_ID") or "").strip()
matches = [a for a in arts if a.get("name") == name]
if not matches:
    print(f"[error] artifact name {name!r} not found on candidate run", file=sys.stderr)
    raise SystemExit(1)
if len(matches) != 1:
    print(f"[error] artifact name {name!r} matched {len(matches)} artifacts; refuse ambiguous handoff", file=sys.stderr)
    raise SystemExit(1)
art = matches[0]
if art.get("expired") is True:
    print(f"[error] artifact {name!r} is expired", file=sys.stderr)
    raise SystemExit(1)
art_id = str(art.get("id") or "")
if want_id and want_id != art_id:
    print(f"[error] candidate_artifact_id {want_id!r} != resolved {art_id!r}", file=sys.stderr)
    raise SystemExit(1)
print(art_id)
PY
)"

echo "[info] resolved candidate artifact id=${resolved_id} name=${OCI_ARTIFACT_NAME}"
if [[ -n "${GITHUB_OUTPUT:-}" ]]; then
  echo "artifact_id=${resolved_id}" >> "${GITHUB_OUTPUT}"
fi
