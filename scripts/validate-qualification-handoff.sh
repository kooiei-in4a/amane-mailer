#!/usr/bin/env bash
# Validate the sealed qualification handoff consumed by canonical promotion.
# The qualification store is external to the product build; this gate only
# reads the downloaded, immutable handoff and emits field-level failures.
set -Eeuo pipefail
set +x

ROOT=""
CANDIDATE_ID=""
QUALIFICATION_RUN_ID=""
RELEASE_COMMIT_SHA=""
EXPECTED_DIGEST=""

die() { echo "[error] $*" >&2; exit 1; }

while [[ $# -gt 0 ]]; do
  case "$1" in
    --root) ROOT="${2:-}"; shift 2 ;;
    --candidate-id) CANDIDATE_ID="${2:-}"; shift 2 ;;
    --qualification-run-id) QUALIFICATION_RUN_ID="${2:-}"; shift 2 ;;
    --release-commit-sha) RELEASE_COMMIT_SHA="${2:-}"; shift 2 ;;
    --expected-digest) EXPECTED_DIGEST="${2:-}"; shift 2 ;;
    -h|--help)
      sed -n '2,8p' "$0" | sed 's/^# \{0,1\}//'
      exit 0
      ;;
    *) die "unknown argument: $1" ;;
  esac
done

[[ -d "${ROOT}" ]] || die "qualification handoff directory is missing"
[[ -n "${CANDIDATE_ID}" ]] || die "candidate ID is required"
[[ -n "${QUALIFICATION_RUN_ID}" ]] || die "qualification run ID is required"
[[ "${RELEASE_COMMIT_SHA}" =~ ^[0-9a-f]{40}$ ]] || die "release commit SHA must be 40 lowercase hex"
[[ "${EXPECTED_DIGEST}" =~ ^sha256:[a-f0-9]{64}$ ]] || die "expected digest must be sha256:<64 lowercase hex>"
command -v python3 >/dev/null 2>&1 || die "python3 is required"

export ROOT CANDIDATE_ID QUALIFICATION_RUN_ID RELEASE_COMMIT_SHA EXPECTED_DIGEST
python3 - <<'PY'
import json
import os
from pathlib import Path

root = Path(os.environ["ROOT"])
candidate = os.environ["CANDIDATE_ID"]
run_id = os.environ["QUALIFICATION_RUN_ID"]
commit = os.environ["RELEASE_COMMIT_SHA"]
digest = os.environ["EXPECTED_DIGEST"]

def fail(field, message):
    raise SystemExit(f"{field}: {message}")

def load(path, field):
    if not path.is_file():
        fail(field, "missing")
    try:
        return json.loads(path.read_text(encoding="utf-8"))
    except Exception:
        fail(field, "invalid JSON")

binding_paths = list(root.rglob("binding.json"))
go_paths = list(root.glob("decision/go-no-go.json")) + [
    p for p in root.rglob("go-no-go.json") if p != root / "decision/go-no-go.json"
]
event_paths = list({p for p in root.rglob("run-status-events/*.json")})
if len(binding_paths) != 1:
    fail("binding.json", "exactly one binding is required")
if len(go_paths) != 1:
    fail("decision/go-no-go.json", "exactly one go/no-go decision is required")
if len(event_paths) != 1:
    fail("run-status-events", "exactly one terminal run-status event is required")

binding = load(binding_paths[0], "binding.json")
decision = load(go_paths[0], "decision/go-no-go.json")
event = load(event_paths[0], "run-status-event")

for field, value in (("binding.candidateId", binding.get("candidateId")),
                     ("decision.candidateId", decision.get("candidateId")),
                     ("event.candidateId", event.get("candidateId"))):
    if value != candidate:
        fail(field, "mismatch")
for field, value in (("binding.qualificationRunId", binding.get("qualificationRunId")),
                     ("decision.qualificationRunId", decision.get("qualificationRunId")),
                     ("event.qualificationRunId", event.get("qualificationRunId"))):
    if value != run_id:
        fail(field, "mismatch")

if binding.get("releaseCommitSha") not in (None, commit):
    fail("binding.releaseCommitSha", "mismatch")
if decision.get("sourceCommitSha") not in (None, commit):
    fail("decision.sourceCommitSha", "mismatch")
if decision.get("ociIndexDigest") not in (None, digest):
    fail("decision.ociIndexDigest", "mismatch")

if decision.get("machineVerdict") != "GO_ELIGIBLE":
    fail("decision.machineVerdict", "must be GO_ELIGIBLE")
if decision.get("humanDecision") != "APPROVE":
    fail("decision.humanDecision", "must be APPROVE")
if decision.get("runSealed") is not True:
    fail("decision.runSealed", "must be true")
if event.get("status") != "sealed":
    fail("run-status-event.status", "must be sealed")
if event.get("runStatusEventSequence") not in (1, "1"):
    fail("run-status-event.runStatusEventSequence", "must be 1")

issue_check = decision.get("issueFreshnessCheck") or {}
if issue_check and issue_check.get("matchedBinding") is not True:
    fail("decision.issueFreshnessCheck.matchedBinding", "must be true")

print("[info] sealed qualification binding validated")
PY

echo "[info] qualification handoff validation passed (no registry access)"
