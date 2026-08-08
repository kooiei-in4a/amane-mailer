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

manifest = load(root / "handoff-manifest.json", "handoff-manifest.json")
if manifest.get("schemaVersion") != 1 or manifest.get("publicationOnly") is not True:
    fail("handoff-manifest", "schemaVersion=1 and publicationOnly=true are required")
if manifest.get("candidateId") != candidate or not isinstance(manifest.get("bindingId"), str) or not isinstance(manifest.get("qualificationRunId"), str) or not isinstance(manifest.get("sealedEventId"), str):
    fail("handoff-manifest.identity", "candidate/binding/run/sealed event identity is required")

object_entries = manifest.get("objects")
if not isinstance(object_entries, list) or len(object_entries) != 3:
    fail("handoff-manifest.objects", "exactly the three sealed JSON objects are required")
object_map = {}
for entry in object_entries:
    if not isinstance(entry, dict) or set(entry) != {"path", "sha256"}:
        fail("handoff-manifest.objects", "object entries must contain only path and sha256")
    path = entry.get("path")
    if not isinstance(path, str) or not path or Path(path).is_absolute() or ".." in Path(path).parts or "\\" in path:
        fail("handoff-manifest.objects", "object path is unsafe")
    if path in object_map or not isinstance(entry.get("sha256"), str) or not __import__("re").fullmatch(r"[0-9a-f]{64}", entry["sha256"]):
        fail("handoff-manifest.objects", "object digest is invalid or duplicated")
    object_map[path] = entry["sha256"]

all_files = {p.relative_to(root).as_posix() for p in root.rglob("*") if p.is_file() and not p.is_symlink()}
if any(p.is_symlink() for p in root.rglob("*")):
    fail("handoff", "symlink entries are forbidden")
if "handoff-manifest.json" not in all_files or set(object_map) | {"handoff-manifest.json"} != all_files:
    fail("handoff-manifest", "handoff contains an unexpected or missing file")
for path, expected in object_map.items():
    actual = __import__("hashlib").sha256((root / path).read_bytes()).hexdigest()
    if actual != expected:
        fail(f"handoff-manifest.objects.{path}", "sealed bytes digest mismatch")

binding_path = root / "binding.json"
decision_path = root / "decision/go-no-go.json"
event_paths = list(root.glob("run-status-events/*.json"))
if set(object_map) != {"binding.json", "decision/go-no-go.json", *(p.relative_to(root).as_posix() for p in event_paths)} or len(event_paths) != 1:
    fail("handoff", "publication-only object allowlist is invalid")
binding = load(binding_path, "binding.json")
decision = load(decision_path, "decision/go-no-go.json")
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

if binding.get("releaseCommitSha") != commit or binding.get("sourceCommitSha") != commit:
    fail("binding.releaseCommitSha", "mismatch")
if decision.get("sourceCommitSha") != commit:
    fail("decision.sourceCommitSha", "mismatch")
if binding.get("ociIndexDigest") != digest or decision.get("ociIndexDigest") != digest:
    fail("decision.ociIndexDigest", "mismatch")
if binding.get("bindingId") != manifest.get("bindingId") or decision.get("bindingId") != binding.get("bindingId") or event.get("bindingId") != binding.get("bindingId"):
    fail("bindingId", "mismatch")
if manifest.get("qualificationRunId") != binding.get("qualificationRunId") or manifest.get("sealedEventId") != event.get("eventId"):
    fail("handoff-manifest", "sealed identity mismatch")
if event_paths[0].stem != event.get("eventId") or event.get("canonicalization") != {"algorithm": "RFC8785-JCS", "version": 1} or event.get("previousRunStatusEventDigestSha256") is not None:
    fail("run-status-event", "terminal event schema mismatch")
event_digest = event.get("eventDigestSha256")
unsigned = {key: value for key, value in event.items() if key != "eventDigestSha256"}
if not isinstance(event_digest, str) or __import__("hashlib").sha256(json.dumps(unsigned, ensure_ascii=False, sort_keys=True, separators=(",", ":")).encode("utf-8")).hexdigest() != event_digest:
    fail("run-status-event.eventDigestSha256", "digest mismatch")
decision_digests = event.get("decisionDigests")
if not isinstance(decision_digests, dict) or set(decision_digests) != {"evidenceIndexSha256", "goNoGoSha256", "phase4ManifestSha256"} or any(not isinstance(v, str) or not __import__("re").fullmatch(r"[0-9a-f]{64}", v) for v in decision_digests.values()):
    fail("run-status-event.decisionDigests", "digest set is invalid")
if decision.get("authorizationDigestSha256") != binding.get("authorizationDigestSha256"):
    fail("decision.authorizationDigestSha256", "must match binding")

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
