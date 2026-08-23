#!/usr/bin/env bash
# Local fixture self-test for the read-only public image verifier.
set -Eeuo pipefail

SCRIPT_DIR="$(cd "$(dirname "$BASH_SOURCE")" >/dev/null 2>&1 && pwd)"
REPO_ROOT="$(cd "$SCRIPT_DIR/.." >/dev/null 2>&1 && pwd)"
TARGET="$REPO_ROOT/scripts/verify-published-release-image.sh"
PYTHON_TARGET="$REPO_ROOT/scripts/verify-published-release-image.py"
FIXTURE_DIR="$REPO_ROOT/scripts/test-fixtures/verify-published-release-image"

bash -n "$TARGET" "$FIXTURE_DIR/crane" "$FIXTURE_DIR/docker"
python3 -c 'import ast, pathlib, sys; ast.parse(pathlib.Path(sys.argv[1]).read_text(encoding="utf-8"))' "$PYTHON_TARGET"
grep -F -- 'public-consumer-verification' "$PYTHON_TARGET" >/dev/null
grep -F -- 'release-image-publication' "$PYTHON_TARGET" >/dev/null
grep -F -- 'pull' "$PYTHON_TARGET" >/dev/null
grep -F -- 'digestImageHelp' "$PYTHON_TARGET" >/dev/null

if grep -nE 'docker (build|login|push)|buildx build|crane push|crane copy' "$TARGET" "$PYTHON_TARGET"; then
  echo '[error] public verification must not build, login, or publish' >&2
  exit 1
fi

case "$(uname -s)" in
  MINGW*|MSYS*|CYGWIN*)
    echo 'verify-published-release-image-self-test: PASS (static checks; fixture execution requires POSIX tools)'
    exit 0
    ;;
esac

WORK_DIR="$(mktemp -d)"
trap 'rm -rf "$WORK_DIR"' EXIT
cp "$FIXTURE_DIR/crane" "$WORK_DIR/crane"
cp "$FIXTURE_DIR/docker" "$WORK_DIR/docker"
chmod +x "$WORK_DIR/crane" "$WORK_DIR/docker"

SOURCE_SHA=0000000000000000000000000000000000000000
DIGEST=sha256:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa
IDENTITY_FILE="$WORK_DIR/identity.json"
BUILD_REPORT_FILE="$WORK_DIR/report.json"
REPRO_REPORT_FILE="$WORK_DIR/repro.json"
python3 - "$IDENTITY_FILE" "$BUILD_REPORT_FILE" "$REPRO_REPORT_FILE" "$SOURCE_SHA" "$DIGEST" <<'PY'
import json
import sys
from pathlib import Path

identity_path, build_path, repro_path, source_sha, digest = sys.argv[1:]
Path(identity_path).write_text(json.dumps({
    "schemaVersion": 1,
    "sourceCommitSha": source_sha,
    "releaseVersion": "9.9.9",
    "platform": "linux/amd64",
    "image": {"digest": digest},
    "checks": {"os": True, "architecture": True, "source_label": True},
}) + "\n", encoding="utf-8")
Path(build_path).write_text(json.dumps({
    "schemaVersion": 1,
    "sourceCommitSha": source_sha,
    "releaseVersion": "9.9.9",
    "platform": "linux/amd64",
    "smoke": {"containerHelp": "PASS", "healthz": "PASS", "readyz": "PASS"},
}) + "\n", encoding="utf-8")
Path(repro_path).write_text(json.dumps({
    "schemaVersion": 1,
    "sourceCommitSha": source_sha,
    "releaseVersion": "9.9.9",
    "platform": "linux/amd64",
    "expectedDigest": digest,
    "observedDigest": digest,
    "digestMatch": True,
}) + "\n", encoding="utf-8")
PY

GITHUB_REPOSITORY=kooiei-in4a/amane-mailer \
GITHUB_RUN_ID=local-self-test \
GITHUB_RUN_ATTEMPT=1 \
GITHUB_WORKFLOW='Publish Release Image' \
GITHUB_WORKFLOW_REF='kooiei-in4a/amane-mailer/.github/workflows/publish-release-image.yml@refs/heads/main' \
GITHUB_REF=refs/heads/main \
bash "$TARGET" \
  --repository ghcr.io/kooiei-in4a/amane-mailer \
  --expected-digest "$DIGEST" \
  --release-version 9.9.9 \
  --release-commit-sha "$SOURCE_SHA" \
  --crane "$WORK_DIR/crane" \
  --docker "$WORK_DIR/docker" \
  --report-file "$WORK_DIR/public.json" \
  --evidence-file "$WORK_DIR/evidence.json" \
  --identity-file "$IDENTITY_FILE" \
  --build-report "$BUILD_REPORT_FILE" \
  --reproducibility-report "$REPRO_REPORT_FILE"

python3 - "$WORK_DIR/public.json" "$WORK_DIR/evidence.json" "$DIGEST" <<'PY'
import json
import sys
from pathlib import Path

public_path, evidence_path, digest = sys.argv[1:]
public = json.loads(Path(public_path).read_text(encoding="utf-8"))
evidence = json.loads(Path(evidence_path).read_text(encoding="utf-8"))
assert public["status"] == "PASS"
assert public["checks"]["tagDigestsMatch"] is True
assert public["checks"]["linuxAmd64Pull"] is True
assert public["checks"]["digestImageHelp"] is True
assert evidence["schemaVersion"] == 1
assert evidence["evidenceType"] == "release-image-publication"
assert evidence["workflowRunAttempt"] == 1
assert evidence["image"]["publishedDigest"] == digest
assert evidence["image"]["verifiedDigests"]["versionTag"] == digest
assert evidence["image"]["verifiedDigests"]["immutableShaTag"] == digest
assert evidence["checks"]["buildSmoke"]["status"] == "PASS"
assert evidence["checks"]["noCacheReproducibility"]["status"] == "PASS"
assert evidence["checks"]["publicConsumerVerification"]["status"] == "PASS"
assert evidence["recordedAtUtc"].endswith("Z")
PY

echo 'verify-published-release-image-self-test: PASS'
