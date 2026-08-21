#!/usr/bin/env bash
# Fixture self-test for the pre-login promotion validators.
set -Eeuo pipefail
set +x

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" >/dev/null 2>&1 && pwd)"
ROOT="$(mktemp -d)"
trap 'rm -rf "${ROOT}"' EXIT

OCI_PARENT="${ROOT}/oci-artifact"
OCI="${OCI_PARENT}/oci"
HANDOFF="${ROOT}/handoff"
SHARED_QUALIFICATION="${SCRIPT_DIR}/fixtures/qualification-handoff/production-shape/artifact"
EXPECTED_PRODUCER="${SCRIPT_DIR}/fixtures/qualification-handoff/production-shape/expected-producer-identity.json"
QUAL_ARTIFACT="${ROOT}/qualification-artifact"
QUAL_SEALED="${ROOT}/qualification-sealed"
QUAL_NO_GO_ARTIFACT="${ROOT}/qualification-no-go-artifact"
QUAL_NO_GO_SEALED="${ROOT}/qualification-no-go-sealed"
mkdir -p "${OCI}/blobs/sha256" "${HANDOFF}" "${QUAL_ARTIFACT}"
cp -a "${SHARED_QUALIFICATION}/." "${QUAL_ARTIFACT}/"

COMMIT="0123456789abcdef0123456789abcdef01234567"
RUN_ID="12345"
ATTEMPT="1"
VERSION="9.8.7"
REPOSITORY="ghcr.io/example/amane-mailer"
ARCHIVE_DIGEST_ARM="sha256:$(printf '1%.0s' {1..64})"
ARCHIVE_DIGEST_LINUX="sha256:$(printf '2%.0s' {1..64})"
ARCHIVE_DIGEST_WIN="sha256:$(printf '3%.0s' {1..64})"
export OCI_PARENT OCI HANDOFF COMMIT RUN_ID ATTEMPT VERSION REPOSITORY
export ARCHIVE_DIGEST_ARM ARCHIVE_DIGEST_LINUX ARCHIVE_DIGEST_WIN

DIGEST="$(python3 - <<'PY'
import hashlib
blob = b'{"schemaVersion":2,"manifests":[]}'
print('sha256:' + hashlib.sha256(blob).hexdigest())
PY
)"
export DIGEST
CANDIDATE_ID="$(python3 - <<'PY'
import hashlib
import os
parts = [os.environ["COMMIT"], os.environ["RUN_ID"], os.environ["ATTEMPT"], os.environ["DIGEST"],
         os.environ["ARCHIVE_DIGEST_ARM"], os.environ["ARCHIVE_DIGEST_LINUX"], os.environ["ARCHIVE_DIGEST_WIN"]]
print(hashlib.sha256("|".join(parts).encode()).hexdigest())
PY
)"
export CANDIDATE_ID

python3 - <<'PY'
import json, os
from pathlib import Path

parent = Path(os.environ["OCI_PARENT"])
oci = Path(os.environ["OCI"])
handoff = Path(os.environ["HANDOFF"])
digest = os.environ["DIGEST"]
index_blob = b'{"schemaVersion":2,"manifests":[]}'
(oci / "blobs" / "sha256" / digest[7:]).write_bytes(index_blob)
(oci / "oci-layout").write_text('{"imageLayoutVersion":"1.0.0"}\n', encoding="utf-8")
(oci / "index.json").write_text(json.dumps({"schemaVersion": 2, "manifests": [{
    "mediaType": "application/vnd.oci.image.index.v1+json",
    "digest": digest, "size": len(index_blob),
}]}) + "\n", encoding="utf-8")

identity = {
    "imageRepository": os.environ["REPOSITORY"], "imageTag": "sha-" + os.environ["COMMIT"],
    "imageDigest": digest, "sourceCommitSha": os.environ["COMMIT"],
    "mailerVersion": os.environ["VERSION"], "platforms": ["linux/amd64", "linux/arm64"],
}
(parent / "image-identity.json").write_text(json.dumps(identity) + "\n", encoding="utf-8")
(handoff / "image-identity.json").write_text(json.dumps(identity) + "\n", encoding="utf-8")
(parent / "oci-index.digest").write_text(digest + "\n", encoding="utf-8")
(parent / "buildx-metadata.json").write_text(json.dumps({
    "containerimage.descriptor": {"mediaType": "application/vnd.oci.image.index.v1+json", "digest": digest, "size": len(index_blob)},
    "containerimage.digest": digest,
}) + "\n", encoding="utf-8")
(handoff / "candidate-provenance.json").write_text(json.dumps({
    "schemaVersion": 1, "sourceCommitSha": os.environ["COMMIT"], "releaseVersion": os.environ["VERSION"],
    "workflowRunId": os.environ["RUN_ID"], "workflowRunAttempt": os.environ["ATTEMPT"],
    "imageRepository": os.environ["REPOSITORY"], "imageTag": "sha-" + os.environ["COMMIT"],
    "ociIndexDigest": digest, "ociPlatforms": ["linux/amd64", "linux/arm64"],
    "archives": [
        {"targetRid": "linux-arm64", "archiveSha256": os.environ["ARCHIVE_DIGEST_ARM"]},
        {"targetRid": "linux-x64", "archiveSha256": os.environ["ARCHIVE_DIGEST_LINUX"]},
        {"targetRid": "win-x64", "archiveSha256": os.environ["ARCHIVE_DIGEST_WIN"]},
    ],
}) + "\n", encoding="utf-8")
PY

expect_pass() {
  local name="$1"
  shift
  "$@" >/tmp/promotion-validator-pass.out 2>/tmp/promotion-validator-pass.err \
    || { cat /tmp/promotion-validator-pass.err >&2; exit 1; }
  echo "[PASS] ${name}"
}
expect_fail() {
  local name="$1"
  shift
  "$@" >/tmp/promotion-validator-fail.out 2>/tmp/promotion-validator-fail.err \
    && { echo "expected failure: ${name}" >&2; exit 1; } || true
  echo "[PASS] negative: ${name}"
}

expect_pass "candidate OCI handoff validation" bash "${SCRIPT_DIR}/validate-candidate-oci-handoff.sh" \
  --oci-root "${OCI}" --handoff-root "${HANDOFF}" --expected-digest "${DIGEST}" \
  --release-version "${VERSION}" --release-commit-sha "${COMMIT}" \
  --candidate-run-id "${RUN_ID}" --candidate-run-attempt "${ATTEMPT}" \
  --candidate-id "${CANDIDATE_ID}" --repository "${REPOSITORY}"

expect_fail "bad OCI digest" bash "${SCRIPT_DIR}/validate-candidate-oci-handoff.sh" \
  --oci-root "${OCI}" --handoff-root "${HANDOFF}" \
  --expected-digest "sha256:$(printf 'd%.0s' {1..64})" \
  --release-version "${VERSION}" --release-commit-sha "${COMMIT}" \
  --candidate-run-id "${RUN_ID}" --candidate-run-attempt "${ATTEMPT}" \
  --candidate-id "${CANDIDATE_ID}" --repository "${REPOSITORY}"

mapfile -t QUALIFICATION_IDENTITY < <(python3 - "${QUAL_ARTIFACT}/binding.json" <<'PY'
import json
import sys
from pathlib import Path

binding = json.loads(Path(sys.argv[1]).read_text(encoding="utf-8"))
for field in ("candidateId", "qualificationRunId", "releaseCommitSha", "ociIndexDigest"):
    print(binding[field])
PY
)
[[ "${#QUALIFICATION_IDENTITY[@]}" -eq 4 ]] || { echo "shared qualification identity is incomplete" >&2; exit 1; }
QUAL_CANDIDATE_ID="${QUALIFICATION_IDENTITY[0]}"
QUALIFICATION_RUN_ID="${QUALIFICATION_IDENTITY[1]}"
QUAL_RELEASE_COMMIT="${QUALIFICATION_IDENTITY[2]}"
QUAL_OCI_DIGEST="${QUALIFICATION_IDENTITY[3]}"

expect_pass "production qualification prepare" python3 "${SCRIPT_DIR}/prepare-qualification-handoff.py" \
  --artifact-root "${QUAL_ARTIFACT}" \
  --expected-producer-identity "${EXPECTED_PRODUCER}" \
  --sealed-root "${QUAL_SEALED}"

expect_pass "sealed-only strict validation" bash "${SCRIPT_DIR}/validate-qualification-handoff.sh" \
  --root "${QUAL_SEALED}" --candidate-id "${QUAL_CANDIDATE_ID}" \
  --qualification-run-id "${QUALIFICATION_RUN_ID}" --release-commit-sha "${QUAL_RELEASE_COMMIT}" \
  --expected-digest "${QUAL_OCI_DIGEST}"

mkdir -p "${QUAL_NO_GO_ARTIFACT}"
cp -a "${QUAL_ARTIFACT}/." "${QUAL_NO_GO_ARTIFACT}/"
python3 - "${QUAL_NO_GO_ARTIFACT}" <<'PY'
import hashlib
import json
import sys
from pathlib import Path

root = Path(sys.argv[1])
decision_path = root / "decision/go-no-go.json"
decision = json.loads(decision_path.read_text(encoding="utf-8"))
decision["machineVerdict"] = "NO_GO"
decision_path.write_text(json.dumps(decision, indent=2, sort_keys=True) + "\n", encoding="utf-8")

manifest_path = root / "handoff-manifest.json"
manifest = json.loads(manifest_path.read_text(encoding="utf-8"))
for entry in manifest["objects"]:
    if entry["path"] == "decision/go-no-go.json":
        entry["sha256"] = hashlib.sha256(decision_path.read_bytes()).hexdigest()
        break
else:
    raise SystemExit("shared manifest decision object is missing")
manifest_path.write_text(json.dumps(manifest, indent=2, sort_keys=True) + "\n", encoding="utf-8")
PY

expect_pass "NO_GO test-copy qualification prepare" python3 "${SCRIPT_DIR}/prepare-qualification-handoff.py" \
  --artifact-root "${QUAL_NO_GO_ARTIFACT}" \
  --expected-producer-identity "${EXPECTED_PRODUCER}" \
  --sealed-root "${QUAL_NO_GO_SEALED}"

expect_fail "qualification decision NO_GO" bash "${SCRIPT_DIR}/validate-qualification-handoff.sh" \
  --root "${QUAL_NO_GO_SEALED}" --candidate-id "${QUAL_CANDIDATE_ID}" \
  --qualification-run-id "${QUALIFICATION_RUN_ID}" --release-commit-sha "${QUAL_RELEASE_COMMIT}" \
  --expected-digest "${QUAL_OCI_DIGEST}"

echo "[info] qualified promotion validator self-test passed"
echo "finalResult=PASS"
