#!/usr/bin/env bash
# Generate one Easy Setup release-candidate host RID archive (#455).
# Stages a single RID tree using tools/Amane.Mailer.ReleaseBundle (not product CLI).
# Does not embed the OCI layout into the host archive. Never tags / pushes GHCR.
set -Eeuo pipefail
set +x

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" >/dev/null 2>&1 && pwd)"
REPO_ROOT="$(cd "${SCRIPT_DIR}/.." >/dev/null 2>&1 && pwd)"

RID="${1:-}"
if [[ -z "${RID}" ]]; then
  echo "Usage: $0 <win-x64|linux-x64|linux-arm64>" >&2
  exit 2
fi

OUT_ROOT="${OUT_ROOT:-${REPO_ROOT}/artifacts/setup-release-candidate}"
MAILER_VERSION="${MAILER_VERSION:-}"
LAUNCHER_VERSION="${LAUNCHER_VERSION:-${MAILER_VERSION}}"
IMAGE_REPOSITORY="${IMAGE_REPOSITORY:-ghcr.io/kooiei-in4a/amane-mailer}"
IMAGE_TAG="${IMAGE_TAG:-}"
SOURCE_SHA="${SOURCE_SHA:-}"
MAILPIT_IMAGE="${MAILPIT_IMAGE:-}"
SKIP_HOST_PUBLISH="${SKIP_HOST_PUBLISH:-0}"
CREATE_ARCHIVES="${CREATE_ARCHIVES:-1}"
CONFIGURATION="${CONFIGURATION:-Release}"
IDENTITY_FILE="${IDENTITY_FILE:-${OUT_ROOT}/image-identity.json}"

export DOTNET_ROOT="${DOTNET_ROOT:-${HOME}/.dotnet}"
export PATH="${DOTNET_ROOT}:${PATH}"

if [[ -z "${SOURCE_SHA}" ]]; then
  SOURCE_SHA="$(git -C "${REPO_ROOT}" rev-parse HEAD)"
fi

if [[ -z "${MAILER_VERSION}" ]]; then
  echo "[error] MAILER_VERSION (major.minor.patch) is required." >&2
  exit 1
fi

if [[ ! "${MAILER_VERSION}" =~ ^[0-9]+\.[0-9]+\.[0-9]+$ ]]; then
  echo "[error] MAILER_VERSION must be major.minor.patch only (got: ${MAILER_VERSION})." >&2
  exit 1
fi

if [[ -z "${MAILPIT_IMAGE}" ]]; then
  echo "[error] MAILPIT_IMAGE is required as repo@sha256:<64 lowercase hex>." >&2
  exit 1
fi

if [[ ! "${MAILPIT_IMAGE}" =~ ^[^@[:space:]]+@sha256:[a-f0-9]{64}$ ]]; then
  echo "[error] MAILPIT_IMAGE must match repo@sha256:<64 lowercase hex>." >&2
  exit 1
fi

if [[ ! -f "${IDENTITY_FILE}" ]]; then
  echo "[error] OCI identity file missing: ${IDENTITY_FILE}" >&2
  echo "[error] Host archives must consume image-identity.json (digest/tag/repo only), not the full OCI layout." >&2
  exit 1
fi

# Bind host packaging to the OCI identity produced for this source SHA / version / platforms.
dotnet run --project "${REPO_ROOT}/tools/Amane.Mailer.ReleaseBundle/Amane.Mailer.ReleaseBundle.csproj" \
  -c "${CONFIGURATION}" --no-launch-profile -- \
  assert-image-identity \
  --identity "${IDENTITY_FILE}" \
  --source-sha "${SOURCE_SHA}" \
  --mailer-version "${MAILER_VERSION}"

OCI_INDEX_DIGEST="$(python3 -c 'import json,sys; d=json.load(open(sys.argv[1],encoding="utf-8")); assert isinstance(d.get("imageDigest"), str) and d["imageDigest"].startswith("sha256:"); print(d["imageDigest"])' "${IDENTITY_FILE}")"
IDENTITY_REPO="$(python3 -c 'import json,sys; print(json.load(open(sys.argv[1],encoding="utf-8")).get("imageRepository") or "")' "${IDENTITY_FILE}")"
IDENTITY_TAG="$(python3 -c 'import json,sys; print(json.load(open(sys.argv[1],encoding="utf-8")).get("imageTag") or "")' "${IDENTITY_FILE}")"
if [[ -n "${IDENTITY_REPO}" ]]; then
  IMAGE_REPOSITORY="${IDENTITY_REPO}"
fi
if [[ -z "${IMAGE_TAG}" ]]; then
  IMAGE_TAG="${IDENTITY_TAG:-sha-${SOURCE_SHA}}"
fi

mkdir -p "${OUT_ROOT}"
STAGING_PARENT="$(mktemp -d "${OUT_ROOT}/staging-parent.XXXXXX")"
PUBLISH_ROOT="${OUT_ROOT}/publish"
mkdir -p "${PUBLISH_ROOT}"

cleanup() {
  rm -rf "${STAGING_PARENT}"
}
trap cleanup EXIT

echo "[info] Candidate RID=${RID} out=${OUT_ROOT}"
echo "[info] sourceCommitSha=${SOURCE_SHA}"
echo "[info] mailerVersion=${MAILER_VERSION}"
echo "[info] ociIndexDigest=${OCI_INDEX_DIGEST}"

publish_dir="${PUBLISH_ROOT}/${RID}"
if [[ "${SKIP_HOST_PUBLISH}" != "1" ]]; then
  echo "[info] Publishing host Native AOT for ${RID}"
  rm -rf "${publish_dir}"
  mkdir -p "${publish_dir}"
  # Use -p: (not /p:) so Git for Windows / MSYS does not rewrite leading '/'
  # into a path and strip the MSBuild switch (Candidate attempt 2 MSB1008).
  publish_args=(
    publish
    "${REPO_ROOT}/src/Amane.Mailer/Amane.Mailer.csproj"
    -c "${CONFIGURATION}"
    -r "${RID}"
    --self-contained
    -o "${publish_dir}"
    "-p:PublishAot=true"
    "-p:IlcTreatWarningsAsErrors=true"
    "-p:Version=${MAILER_VERSION}"
    "-p:InformationalVersion=${MAILER_VERSION}+${SOURCE_SHA}"
    "-p:IncludeSourceRevisionInInformationalVersion=false"
  )
  dotnet "${publish_args[@]}"
fi

if [[ "${RID}" == "win-x64" ]]; then
  host_bin="${publish_dir}/Amane.Mailer.exe"
else
  host_bin="${publish_dir}/Amane.Mailer"
fi
if [[ ! -f "${host_bin}" ]]; then
  echo "[error] Host binary missing for ${RID}: ${host_bin}" >&2
  exit 1
fi

dotnet run --project "${REPO_ROOT}/tools/Amane.Mailer.ReleaseBundle/Amane.Mailer.ReleaseBundle.csproj" \
  -c "${CONFIGURATION}" --no-launch-profile -- \
  assert-binary-version \
  --binary "${host_bin}" \
  --expected-core "${MAILER_VERSION}"

out_dir="${STAGING_PARENT}/${RID}"
echo "[info] Staging candidate tree for ${RID} under ${STAGING_PARENT}"

dotnet run --project "${REPO_ROOT}/tools/Amane.Mailer.ReleaseBundle/Amane.Mailer.ReleaseBundle.csproj" \
  -c "${CONFIGURATION}" --no-launch-profile -- \
  stage \
  --output "${out_dir}" \
  --staging-parent "${STAGING_PARENT}" \
  --rid "${RID}" \
  --host-binary "${host_bin}" \
  --source-sha "${SOURCE_SHA}" \
  --mailer-version "${MAILER_VERSION}" \
  --launcher-version "${LAUNCHER_VERSION}" \
  --image-repository "${IMAGE_REPOSITORY}" \
  --image-tag "${IMAGE_TAG}" \
  --oci-index-digest "${OCI_INDEX_DIGEST}" \
  --deploy-compose "${REPO_ROOT}/infra/deploy/compose.yml" \
  --image-digest-overlay "${REPO_ROOT}/infra/deploy/compose.image-digest.yml" \
  --recorded-metadata-overlay "${REPO_ROOT}/infra/deploy/compose.recorded-metadata.yml" \
  --mailpit-overlay "${REPO_ROOT}/infra/deploy/compose.mailpit.yml" \
  --env-example "${REPO_ROOT}/infra/deploy/.env.example" \
  --tenants-example "${REPO_ROOT}/config/mailer/tenants.example.json" \
  --tenants-schema "${REPO_ROOT}/config/mailer/tenants.schema.json" \
  --tenants-local-acs-example "${REPO_ROOT}/config/mailer/tenants.local-acs.json.example" \
  --license "${REPO_ROOT}/LICENSE" \
  --mailpit-image "${MAILPIT_IMAGE}" \
  --project-name-prefix amane

bash "${SCRIPT_DIR}/scan-setup-release-bundle.sh" "${out_dir}"

archive_name="$(python3 - <<PY
version="${MAILER_VERSION}"
rid="${RID}"
label = version if version.startswith("v") else "v" + version
names = {
  "win-x64": f"amane-mailer-{label}-windows-x64.zip",
  "linux-x64": f"amane-mailer-{label}-linux-x64.tar.gz",
  "linux-arm64": f"amane-mailer-{label}-linux-arm64.tar.gz",
}
print(names.get(rid, f"amane-mailer-{label}-{rid}.tar.gz"))
PY
)"
archive_path="${OUT_ROOT}/${archive_name}"
rm -f "${archive_path}"

if [[ "${CREATE_ARCHIVES}" == "1" ]]; then
  if [[ "${RID}" == "win-x64" ]]; then
    if command -v powershell.exe >/dev/null 2>&1; then
      powershell.exe -NoProfile -Command \
        "Compress-Archive -Path '${out_dir}' -DestinationPath '${archive_path}' -Force"
    elif command -v zip >/dev/null 2>&1; then
      (cd "${STAGING_PARENT}" && zip -qr "${archive_path}" "${RID}")
    else
      echo "[error] zip or powershell required to archive win-x64." >&2
      exit 1
    fi
  else
    # Preserve executable bits in the archive (smoke verifies without chmod).
    tar -C "${STAGING_PARENT}" -czf "${archive_path}" "${RID}"
  fi
  echo "[info] Wrote ${archive_path}"
  ARCHIVE_SHA="sha256:$(sha256sum "${archive_path}" | awk '{print $1}')"
  echo "${ARCHIVE_SHA}" > "${archive_path}.sha256"
  echo "${ARCHIVE_SHA}  ${archive_name}" > "${OUT_ROOT}/${archive_name}.sha256.txt"
  echo "archiveSha256=${ARCHIVE_SHA}"

  bash "${SCRIPT_DIR}/smoke-setup-release-bundle.sh" \
    "${archive_path}" \
    "${ARCHIVE_SHA}" \
    "${RID}" \
    "${MAILER_VERSION}"
fi

# Persist staged tree for optional local inspection (not uploaded as host+oci merge).
STAGED_COPY="${OUT_ROOT}/staged/${RID}"
rm -rf "${STAGED_COPY}"
mkdir -p "${OUT_ROOT}/staged"
cp -a "${out_dir}" "${STAGED_COPY}"

echo "[info] Candidate RID ${RID} generation complete (no tag / no GHCR push)."
