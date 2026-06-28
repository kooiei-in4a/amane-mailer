#!/usr/bin/env bash
# Verifies a pushed Mailer image manifest and every configured runtime platform.
set -Eeuo pipefail
set +x

require_env() {
  local name="$1"
  if [ -z "${!name:-}" ]; then
    echo "[error] required environment variable is empty: ${name}" >&2
    exit 2
  fi
}

require_cmd() {
  if ! command -v "$1" >/dev/null 2>&1; then
    echo "[error] missing required command: $1" >&2
    exit 2
  fi
}

split_platforms() {
  local raw="$1"
  local item
  IFS=',' read -r -a PLATFORMS <<< "$raw"
  for i in "${!PLATFORMS[@]}"; do
    item="${PLATFORMS[$i]}"
    item="${item#"${item%%[![:space:]]*}"}"
    item="${item%"${item##*[![:space:]]}"}"
    if [ -z "$item" ]; then
      echo "[error] IMAGE_PLATFORMS contains an empty platform entry: ${raw}" >&2
      exit 2
    fi
    PLATFORMS[$i]="$item"
  done
}

digest_from_inspect() {
  awk '/^Digest:/ { print $2; exit }'
}

collect_manifest_metadata() {
  local inspect_raw_file="$1"
  shift
  python3 - "$inspect_raw_file" "$@" <<'PY'
import json
import sys

path = sys.argv[1]
expected_platforms = sys.argv[2:]

with open(path, encoding="utf-8") as handle:
    document = json.load(handle)

manifests = document.get("manifests")
if not isinstance(manifests, list):
    print("image inspect raw output did not contain a manifest list", file=sys.stderr)
    sys.exit(3)

runtime_by_platform = {}
attestation_by_subject = {}

for manifest in manifests:
    annotations = manifest.get("annotations") or {}
    digest = manifest.get("digest") or ""
    if annotations.get("vnd.docker.reference.type") == "attestation-manifest":
        subject = annotations.get("vnd.docker.reference.digest")
        if subject and digest:
            attestation_by_subject.setdefault(subject, []).append(digest)
        continue

    platform = manifest.get("platform") or {}
    os_name = platform.get("os")
    architecture = platform.get("architecture")
    variant = platform.get("variant")
    if os_name and architecture and digest:
        base_platform_name = f"{os_name}/{architecture}"
        runtime_by_platform.setdefault(base_platform_name, digest)
        if variant:
            runtime_by_platform[f"{base_platform_name}/{variant}"] = digest

missing_runtime = []
missing_attestation = []
for platform_name in expected_platforms:
    runtime_digest = runtime_by_platform.get(platform_name)
    if runtime_digest is None:
        missing_runtime.append(platform_name)
        continue

    attestation_digests = attestation_by_subject.get(runtime_digest, [])
    if not attestation_digests:
        missing_attestation.append(platform_name)
        continue

    print(f"{platform_name}\t{runtime_digest}\t{','.join(attestation_digests)}")

if missing_runtime:
    print(
        "missing runtime manifest for platform(s): " + ", ".join(missing_runtime),
        file=sys.stderr,
    )
    sys.exit(4)

if missing_attestation:
    print(
        "missing attestation manifest for platform(s): " + ", ".join(missing_attestation),
        file=sys.stderr,
    )
    sys.exit(5)
PY
}

verify_container_for_platform() {
  local platform="$1"
  local platform_key="${platform//\//-}"
  local container_id=""
  local config_dir="${WORK_DIR}/config-${platform_key}"
  local actual_files="${WORK_DIR}/config-${platform_key}.actual"
  local expected_files="${WORK_DIR}/config.expected"
  local source_label revision_label version_label

  echo "[verify] runtime smoke: ${platform}"
  docker run --rm --platform "$platform" "$IMAGE_REF" --help

  mkdir -p "$config_dir"
  container_id="$(docker create --platform "$platform" "$IMAGE_REF" --help)"
  CONTAINERS_TO_CLEAN+=("$container_id")
  docker cp "$container_id:/app/config/mailer/." "$config_dir"

  source_label="$(docker inspect "$container_id" --format '{{ index .Config.Labels "org.opencontainers.image.source" }}')"
  revision_label="$(docker inspect "$container_id" --format '{{ index .Config.Labels "org.opencontainers.image.revision" }}')"
  version_label="$(docker inspect "$container_id" --format '{{ index .Config.Labels "org.opencontainers.image.version" }}')"

  docker rm -f "$container_id" >/dev/null
  container_id=""

  if [ "$source_label" != "$EXPECTED_SOURCE" ]; then
    echo "[error] ${platform} OCI source label mismatch: ${source_label}" >&2
    exit 1
  fi

  if [ "$revision_label" != "$EXPECTED_REVISION" ]; then
    echo "[error] ${platform} OCI revision label mismatch: ${revision_label}" >&2
    exit 1
  fi

  if [ "$version_label" != "$EXPECTED_VERSION" ]; then
    echo "[error] ${platform} OCI version label mismatch: ${version_label}" >&2
    exit 1
  fi

  printf '%s\n' \
    tenants.example.json \
    tenants.local-acs.json.example \
    tenants.schema.json \
    > "$expected_files"
  find "$config_dir" -maxdepth 1 -type f -printf '%f\n' | sort > "$actual_files"
  diff -u "$expected_files" "$actual_files"
}

require_env IMAGE_DIGEST
require_env IMAGE_REF
require_env VERSION_REF
require_env IMAGE_PLATFORMS

if [ -z "${EXPECTED_SOURCE:-}" ] && [ -n "${GITHUB_SERVER_URL:-}" ] && [ -n "${GITHUB_REPOSITORY:-}" ]; then
  EXPECTED_SOURCE="${GITHUB_SERVER_URL}/${GITHUB_REPOSITORY}"
fi
EXPECTED_REVISION="${EXPECTED_REVISION:-${REVISION:-}}"
EXPECTED_VERSION="${EXPECTED_VERSION:-${RELEASE_TAG:-}}"

require_env EXPECTED_SOURCE
require_env EXPECTED_REVISION
require_env EXPECTED_VERSION

require_cmd awk
require_cmd diff
require_cmd docker
require_cmd find
require_cmd python3

split_platforms "$IMAGE_PLATFORMS"

WORK_DIR="$(mktemp -d)"
CONTAINERS_TO_CLEAN=()
cleanup() {
  for container_id in "${CONTAINERS_TO_CLEAN[@]}"; do
    docker rm -f "$container_id" >/dev/null 2>&1 || true
  done
  rm -rf "$WORK_DIR"
}
trap cleanup EXIT

inspect_raw_file="${WORK_DIR}/inspect.raw.json"
metadata_file="${WORK_DIR}/platform-metadata.tsv"

echo "[verify] inspecting image tags"
inspect_text="$(docker buildx imagetools inspect "$IMAGE_REF")"
version_inspect_text="$(docker buildx imagetools inspect "$VERSION_REF")"
inspect_raw="$(docker buildx imagetools inspect --raw "$IMAGE_REF")"
printf '%s\n' "$inspect_text"
printf '%s\n' "$inspect_raw" > "$inspect_raw_file"

sha_tag_digest="$(printf '%s\n' "$inspect_text" | digest_from_inspect)"
version_tag_digest="$(printf '%s\n' "$version_inspect_text" | digest_from_inspect)"

if [ -z "$sha_tag_digest" ] || [ -z "$version_tag_digest" ]; then
  echo "[error] Could not resolve digest for ${IMAGE_REF} or ${VERSION_REF}." >&2
  exit 1
fi

if [ "$sha_tag_digest" != "$IMAGE_DIGEST" ]; then
  echo "[error] Build digest ${IMAGE_DIGEST} does not match ${IMAGE_REF} digest ${sha_tag_digest}." >&2
  exit 1
fi

if [ "$version_tag_digest" != "$IMAGE_DIGEST" ]; then
  echo "[error] Build digest ${IMAGE_DIGEST} does not match ${VERSION_REF} digest ${version_tag_digest}." >&2
  exit 1
fi

collect_manifest_metadata "$inspect_raw_file" "${PLATFORMS[@]}" > "$metadata_file"

runtime_summary=""
attestation_summary=""
markdown_rows=""

while IFS=$'\t' read -r platform runtime_digest attestation_digests; do
  verify_container_for_platform "$platform"
  runtime_summary="${runtime_summary}${platform} ${runtime_digest}"$'\n'
  attestation_summary="${attestation_summary}${platform} ${attestation_digests}"$'\n'
  markdown_rows="${markdown_rows}| \`${platform}\` | \`${runtime_digest}\` | \`${attestation_digests}\` |"$'\n'
done < "$metadata_file"

if [ -n "${GITHUB_OUTPUT:-}" ]; then
  {
    echo "digest=${IMAGE_DIGEST}"
    echo "platform=${IMAGE_PLATFORMS}"
    echo "attestation_manifest=present"
    echo "runtime_manifests<<EOF"
    printf '%s' "$runtime_summary"
    echo "EOF"
    echo "attestation_manifests<<EOF"
    printf '%s' "$attestation_summary"
    echo "EOF"
  } >> "$GITHUB_OUTPUT"
fi

if [ -n "${GITHUB_STEP_SUMMARY:-}" ]; then
  {
    echo "## Published image"
    echo
    echo "- Image: \`${IMAGE_REF}\`"
    echo "- Release tag: \`${EXPECTED_VERSION}\`"
    echo "- SHA tag revision: \`${EXPECTED_REVISION}\`"
    echo "- Index digest: \`${IMAGE_DIGEST}\`"
    echo "- Release tag digest: \`${version_tag_digest}\`"
    echo "- Platforms: \`${IMAGE_PLATFORMS}\`"
    echo "- OCI source label: \`${EXPECTED_SOURCE}\`"
    echo "- OCI revision label: \`${EXPECTED_REVISION}\`"
    echo "- OCI version label: \`${EXPECTED_VERSION}\`"
    echo "- SBOM/provenance: \`sbom: true\` and \`provenance: true\`; attestation manifests present"
    echo
    echo "| Platform | Runtime manifest digest | Attestation manifest digest(s) |"
    echo "| --- | --- | --- |"
    printf '%s' "$markdown_rows"
    echo
    echo '```'
    printf '%s\n' "$inspect_text"
    echo '```'
  } >> "$GITHUB_STEP_SUMMARY"
fi
