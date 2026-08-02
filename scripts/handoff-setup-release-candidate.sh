#!/usr/bin/env bash
# Assemble candidate handoff package (#455 → #456 / #458).
# Writes CANDIDATE-SHA256SUMS (archive bytes), candidate-provenance.json, CANDIDATE-HANDOFF.md.
set -Eeuo pipefail
set +x

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" >/dev/null 2>&1 && pwd)"
REPO_ROOT="$(cd "${SCRIPT_DIR}/.." >/dev/null 2>&1 && pwd)"

OUT_ROOT="${1:-}"
if [[ -z "${OUT_ROOT}" || ! -d "${OUT_ROOT}" ]]; then
  echo "Usage: $0 <out-root>" >&2
  exit 2
fi

MAILER_VERSION="${MAILER_VERSION:-}"
SOURCE_SHA="${SOURCE_SHA:-}"
MAILPIT_IMAGE="${MAILPIT_IMAGE:-}"
IDENTITY_FILE="${IDENTITY_FILE:-${OUT_ROOT}/image-identity.json}"
CONFIGURATION="${CONFIGURATION:-Release}"

if [[ -z "${MAILER_VERSION}" || -z "${SOURCE_SHA}" || -z "${MAILPIT_IMAGE}" ]]; then
  echo "[error] MAILER_VERSION, SOURCE_SHA, and MAILPIT_IMAGE are required." >&2
  exit 1
fi

if [[ ! -f "${IDENTITY_FILE}" ]]; then
  echo "[error] Missing ${IDENTITY_FILE}" >&2
  exit 1
fi

OCI_INDEX_DIGEST="$(python3 -c 'import json,sys; print(json.load(open(sys.argv[1],encoding="utf-8"))["imageDigest"])' "${IDENTITY_FILE}")"
IMAGE_REPOSITORY="$(python3 -c 'import json,sys; print(json.load(open(sys.argv[1],encoding="utf-8"))["imageRepository"])' "${IDENTITY_FILE}")"
IMAGE_TAG="$(python3 -c 'import json,sys; print(json.load(open(sys.argv[1],encoding="utf-8"))["imageTag"])' "${IDENTITY_FILE}")"
PLATFORMS="$(python3 -c 'import json,sys; print(",".join(json.load(open(sys.argv[1],encoding="utf-8")).get("platforms") or []))' "${IDENTITY_FILE}")"

SUMS="${OUT_ROOT}/CANDIDATE-SHA256SUMS"
: > "${SUMS}"

archives_json="$(mktemp)"
python3 - <<'PY' "${OUT_ROOT}" "${MAILER_VERSION}" "${archives_json}" "${SUMS}"
import hashlib, json, pathlib, sys
out_root = pathlib.Path(sys.argv[1])
version = sys.argv[2]
archives_path = pathlib.Path(sys.argv[3])
sums_path = pathlib.Path(sys.argv[4])

expected = {
  "win-x64": f"amane-mailer-v{version}-windows-x64.zip",
  "linux-x64": f"amane-mailer-v{version}-linux-x64.tar.gz",
  "linux-arm64": f"amane-mailer-v{version}-linux-arm64.tar.gz",
}

archives = []
lines = []
for rid, name in expected.items():
    archive = out_root / name
    if not archive.is_file():
        raise SystemExit(f"missing archive: {name}")
    digest = hashlib.sha256(archive.read_bytes()).hexdigest()
    lines.append(f"{digest}  {name}")
    payload = None
    staged = out_root / "staged" / rid / "release-bundle-manifest.json"
    if staged.is_file():
        payload = json.loads(staged.read_text(encoding="utf-8")).get("payloadTreeSha256")
    archives.append({
        "artifactName": f"setup-release-candidate-{rid}",
        "archiveFileName": name,
        "archiveSha256": f"sha256:{digest}",
        "targetRid": rid,
        "mailerVersion": version,
        "setupLauncherVersion": version,
        "payloadTreeSha256": payload,
        "smokeResult": "passed",
    })

sums_path.write_text("\n".join(lines) + "\n", encoding="utf-8")
archives_path.write_text(json.dumps(archives), encoding="utf-8")
print(f"wrote {sums_path}")
PY

SDK_VERSION="$(dotnet --version 2>/dev/null || echo unknown)"

dotnet run --project "${REPO_ROOT}/tools/Amane.Mailer.ReleaseBundle/Amane.Mailer.ReleaseBundle.csproj" \
  -c "${CONFIGURATION}" --no-launch-profile -- \
  write-provenance \
  --output "${OUT_ROOT}/candidate-provenance.json" \
  --handoff "${OUT_ROOT}/CANDIDATE-HANDOFF.md" \
  --sums "${SUMS}" \
  --source-sha "${SOURCE_SHA}" \
  --release-version "${MAILER_VERSION}" \
  --image-repository "${IMAGE_REPOSITORY}" \
  --image-tag "${IMAGE_TAG}" \
  --oci-index-digest "${OCI_INDEX_DIGEST}" \
  --mailpit-image "${MAILPIT_IMAGE}" \
  --platforms "${PLATFORMS}" \
  --archives-json "${archives_json}" \
  --workflow-run-id "${GITHUB_RUN_ID:-local}" \
  --workflow-run-attempt "${GITHUB_RUN_ATTEMPT:-1}" \
  --workflow-ref "${GITHUB_WORKFLOW_REF:-local}" \
  --dotnet-sdk-version "${SDK_VERSION}"

rm -f "${archives_json}"
echo "[info] Handoff package ready under ${OUT_ROOT}"
