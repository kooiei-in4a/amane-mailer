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
QUAL="${ROOT}/qualification"
mkdir -p "${OCI}/blobs/sha256" "${HANDOFF}" "${QUAL}/decision" "${QUAL}/run-status-events"

COMMIT="0123456789abcdef0123456789abcdef01234567"
RUN_ID="12345"
ATTEMPT="1"
QUALIFICATION_RUN_ID="qualification-$(printf 'c%.0s' {1..64})"
VERSION="9.8.7"
REPOSITORY="ghcr.io/example/amane-mailer"
ARCHIVE_DIGEST_ARM="sha256:$(printf '1%.0s' {1..64})"
ARCHIVE_DIGEST_LINUX="sha256:$(printf '2%.0s' {1..64})"
ARCHIVE_DIGEST_WIN="sha256:$(printf '3%.0s' {1..64})"
export OCI_PARENT OCI HANDOFF QUAL COMMIT RUN_ID ATTEMPT QUALIFICATION_RUN_ID VERSION REPOSITORY
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
qual = Path(os.environ["QUAL"])
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
(qual / "binding.json").write_text(json.dumps({
    "candidateId": os.environ["CANDIDATE_ID"], "qualificationRunId": os.environ["QUALIFICATION_RUN_ID"],
    "releaseCommitSha": os.environ["COMMIT"],
}) + "\n", encoding="utf-8")
(qual / "decision" / "go-no-go.json").write_text(json.dumps({
    "candidateId": os.environ["CANDIDATE_ID"], "qualificationRunId": os.environ["QUALIFICATION_RUN_ID"],
    "sourceCommitSha": os.environ["COMMIT"], "ociIndexDigest": digest,
    "machineVerdict": "GO_ELIGIBLE", "humanDecision": "APPROVE", "runSealed": True,
    "issueFreshnessCheck": {"matchedBinding": True},
}) + "\n", encoding="utf-8")
(qual / "run-status-events" / "sealed.json").write_text(json.dumps({
    "candidateId": os.environ["CANDIDATE_ID"], "qualificationRunId": os.environ["QUALIFICATION_RUN_ID"],
    "status": "sealed", "runStatusEventSequence": 1,
}) + "\n", encoding="utf-8")
PY

expect_pass() {
  "$@" >/tmp/promotion-validator-pass.out 2>/tmp/promotion-validator-pass.err \
    || { cat /tmp/promotion-validator-pass.err >&2; exit 1; }
}
expect_fail() {
  "$@" >/tmp/promotion-validator-fail.out 2>/tmp/promotion-validator-fail.err \
    && { echo "expected failure" >&2; exit 1; } || true
}

expect_pass bash "${SCRIPT_DIR}/validate-candidate-oci-handoff.sh" \
  --oci-root "${OCI}" --handoff-root "${HANDOFF}" --expected-digest "${DIGEST}" \
  --release-version "${VERSION}" --release-commit-sha "${COMMIT}" \
  --candidate-run-id "${RUN_ID}" --candidate-run-attempt "${ATTEMPT}" \
  --candidate-id "${CANDIDATE_ID}" --repository "${REPOSITORY}"

expect_fail bash "${SCRIPT_DIR}/validate-candidate-oci-handoff.sh" \
  --oci-root "${OCI}" --handoff-root "${HANDOFF}" \
  --expected-digest "sha256:$(printf 'd%.0s' {1..64})" \
  --release-version "${VERSION}" --release-commit-sha "${COMMIT}" \
  --candidate-run-id "${RUN_ID}" --candidate-run-attempt "${ATTEMPT}" \
  --candidate-id "${CANDIDATE_ID}" --repository "${REPOSITORY}"

expect_pass bash "${SCRIPT_DIR}/validate-qualification-handoff.sh" \
  --root "${QUAL}" --candidate-id "${CANDIDATE_ID}" \
  --qualification-run-id "${QUALIFICATION_RUN_ID}" --release-commit-sha "${COMMIT}" \
  --expected-digest "${DIGEST}"

python3 -c "from pathlib import Path; p=Path(r'${QUAL}/decision/go-no-go.json'); p.write_text(p.read_text(encoding='utf-8').replace('GO_ELIGIBLE','NO_GO'), encoding='utf-8')"
expect_fail bash "${SCRIPT_DIR}/validate-qualification-handoff.sh" \
  --root "${QUAL}" --candidate-id "${CANDIDATE_ID}" \
  --qualification-run-id "${QUALIFICATION_RUN_ID}" --release-commit-sha "${COMMIT}" \
  --expected-digest "${DIGEST}"

echo "[info] qualified promotion validator self-test passed"
echo "finalResult=PASS"
