#!/usr/bin/env bash
# Generate Easy Setup release-candidate host bundles (#455).
# Stages Windows x64 / Linux x64 / Linux arm64 trees, optional archives,
# checksums, and handoff metadata. Never tags, never pushes GHCR, never
# creates a GitHub Release (#458 owns publish).
set -Eeuo pipefail
set +x

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" >/dev/null 2>&1 && pwd)"
REPO_ROOT="$(cd "${SCRIPT_DIR}/.." >/dev/null 2>&1 && pwd)"

OUT_ROOT="${OUT_ROOT:-${REPO_ROOT}/artifacts/setup-release-candidate}"
MAILER_VERSION="${MAILER_VERSION:-1.2.0-candidate}"
LAUNCHER_VERSION="${LAUNCHER_VERSION:-${MAILER_VERSION}}"
IMAGE_REPOSITORY="${IMAGE_REPOSITORY:-ghcr.io/kooiei-in4a/amane-mailer}"
IMAGE_TAG="${IMAGE_TAG:-}"
SOURCE_SHA="${SOURCE_SHA:-}"
MAILPIT_IMAGE="${MAILPIT_IMAGE:-}"
SKIP_OCI_BUILD="${SKIP_OCI_BUILD:-0}"
SKIP_HOST_PUBLISH="${SKIP_HOST_PUBLISH:-0}"
CREATE_ARCHIVES="${CREATE_ARCHIVES:-1}"
CONFIGURATION="${CONFIGURATION:-Release}"
OCI_PLATFORM="${OCI_PLATFORM:-linux/amd64}"
RIDS="${RIDS:-linux-x64,linux-arm64,win-x64}"

export DOTNET_ROOT="${DOTNET_ROOT:-${HOME}/.dotnet}"
export PATH="${DOTNET_ROOT}:${PATH}"

if [[ -z "${SOURCE_SHA}" ]]; then
  SOURCE_SHA="$(git -C "${REPO_ROOT}" rev-parse HEAD)"
fi
if [[ -z "${IMAGE_TAG}" ]]; then
  IMAGE_TAG="sha-${SOURCE_SHA}"
fi

mkdir -p "${OUT_ROOT}"
STAGED_ROOT="${OUT_ROOT}/staged"
OCI_DIR="${OUT_ROOT}/oci"
PUBLISH_ROOT="${OUT_ROOT}/publish"
mkdir -p "${STAGED_ROOT}" "${PUBLISH_ROOT}"

echo "[info] Candidate output root: ${OUT_ROOT}"
echo "[info] sourceCommitSha=${SOURCE_SHA}"
echo "[info] mailerVersion=${MAILER_VERSION}"
echo "[info] imageTag=${IMAGE_TAG}"

if [[ "${SKIP_OCI_BUILD}" != "1" ]]; then
  bash "${SCRIPT_DIR}/build-candidate-oci-image.sh" "${OCI_DIR}" "${OCI_PLATFORM}"
else
  if [[ ! -f "${OCI_DIR}/index.json" ]]; then
    echo "[error] SKIP_OCI_BUILD=1 but ${OCI_DIR}/index.json is missing." >&2
    exit 1
  fi
fi

OCI_INDEX_DIGEST="sha256:$(sha256sum "${OCI_DIR}/index.json" | awk '{print $1}')"
echo "[info] ociIndexDigest=${OCI_INDEX_DIGEST}"

# Prefer an already-built framework binary for staging CLI; fall back to `dotnet run`.
STAGE_BIN=""
if [[ -x "${REPO_ROOT}/artifacts/publish/aot-linux-x64/Amane.Mailer" ]]; then
  STAGE_BIN="${REPO_ROOT}/artifacts/publish/aot-linux-x64/Amane.Mailer"
fi

stage_one() {
  local rid="$1"
  local host_bin="$2"
  local out_dir="${STAGED_ROOT}/${rid}"

  local args=(
    setup stage-release-bundle
    --output "${out_dir}"
    --rid "${rid}"
    --host-binary "${host_bin}"
    --source-sha "${SOURCE_SHA}"
    --mailer-version "${MAILER_VERSION}"
    --launcher-version "${LAUNCHER_VERSION}"
    --image-repository "${IMAGE_REPOSITORY}"
    --image-tag "${IMAGE_TAG}"
    --oci-index-digest "${OCI_INDEX_DIGEST}"
    --deploy-compose "${REPO_ROOT}/infra/deploy/compose.yml"
    --image-digest-overlay "${REPO_ROOT}/infra/deploy/compose.image-digest.yml"
    --recorded-metadata-overlay "${REPO_ROOT}/infra/deploy/compose.recorded-metadata.yml"
    --mailpit-overlay "${REPO_ROOT}/infra/deploy/compose.mailpit.yml"
    --env-example "${REPO_ROOT}/infra/deploy/.env.example"
    --tenants-example "${REPO_ROOT}/config/mailer/tenants.example.json"
    --tenants-schema "${REPO_ROOT}/config/mailer/tenants.schema.json"
    --tenants-local-acs-example "${REPO_ROOT}/config/mailer/tenants.local-acs.json.example"
    --oci-layout "${OCI_DIR}"
    --project-name-prefix amane
  )

  if [[ -n "${MAILPIT_IMAGE}" ]]; then
    args+=(--mailpit-image "${MAILPIT_IMAGE}")
  fi

  if [[ -n "${STAGE_BIN}" ]]; then
    "${STAGE_BIN}" "${args[@]}"
  else
    dotnet run --project "${REPO_ROOT}/src/Amane.Mailer/Amane.Mailer.csproj" \
      -c "${CONFIGURATION}" --no-launch-profile -- \
      "${args[@]}"
  fi
}

IFS=',' read -r -a RID_LIST <<< "${RIDS}"
for rid in "${RID_LIST[@]}"; do
  publish_dir="${PUBLISH_ROOT}/${rid}"
  if [[ "${SKIP_HOST_PUBLISH}" != "1" ]]; then
    echo "[info] Publishing host Native AOT for ${rid}"
    rm -rf "${publish_dir}"
    mkdir -p "${publish_dir}"
    dotnet publish "${REPO_ROOT}/src/Amane.Mailer/Amane.Mailer.csproj" \
      -c "${CONFIGURATION}" \
      -r "${rid}" \
      --self-contained \
      -o "${publish_dir}" \
      /p:PublishAot=true \
      /p:IlcTreatWarningsAsErrors=true \
      /p:Version="${MAILER_VERSION}" \
      /p:InformationalVersion="${LAUNCHER_VERSION}"
  fi

  if [[ "${rid}" == "win-x64" ]]; then
    host_bin="${publish_dir}/Amane.Mailer.exe"
  else
    host_bin="${publish_dir}/Amane.Mailer"
  fi
  if [[ ! -f "${host_bin}" ]]; then
    echo "[error] Host binary missing for ${rid}: ${host_bin}" >&2
    exit 1
  fi

  echo "[info] Staging candidate tree for ${rid}"
  stage_one "${rid}" "${host_bin}"

  if [[ "${CREATE_ARCHIVES}" == "1" ]]; then
    archive_name="$(python3 - <<PY
version="${MAILER_VERSION}"
rid="${rid}"
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
    if [[ "${rid}" == "win-x64" ]]; then
      (cd "${STAGED_ROOT}" && zip -qr "${archive_path}" "${rid}")
    else
      tar -C "${STAGED_ROOT}" -czf "${archive_path}" "${rid}"
    fi
    echo "[info] Wrote ${archive_path}"
  fi
done

bash "${SCRIPT_DIR}/scan-setup-release-bundle.sh" "${STAGED_ROOT}"
bash "${SCRIPT_DIR}/smoke-setup-release-bundle.sh" "${STAGED_ROOT}"
bash "${SCRIPT_DIR}/handoff-setup-release-candidate.sh" "${OUT_ROOT}" "${SOURCE_SHA}" "${OCI_INDEX_DIGEST}" "${MAILER_VERSION}"

echo "[info] Candidate generation complete (no tag / no GHCR push / no GitHub Release)."
