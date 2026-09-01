#!/usr/bin/env bash
# Shared preflight for release smoke (issue #506).
# Validates artifact identity, compose project, compose file, tools, and local
# Docker endpoint before any docker mutation. Sources must set +Eeuo pipefail.
set -o pipefail

RELEASE_SMOKE_PREFLIGHT_ERROR=2

release_smoke_preflight_log_error() {
  printf '[error] %s\n' "$1" >&2
}

release_smoke_preflight_fail() {
  release_smoke_preflight_log_error "$1"
  exit "${RELEASE_SMOKE_PREFLIGHT_ERROR}"
}

release_smoke_preflight_trim() {
  local value="$1"
  value="${value#"${value%%[![:space:]]*}"}"
  value="${value%"${value##*[![:space:]]}"}"
  printf '%s' "$value"
}

release_smoke_preflight_validate_tag() {
  local tag="$1"
  tag="$(release_smoke_preflight_trim "$tag")"
  if [[ -z "$tag" ]]; then
    release_smoke_preflight_fail 'MAILER_IMAGE_TAG must not be empty'
  fi
  if [[ "$tag" == "latest" ]]; then
    release_smoke_preflight_fail 'MAILER_IMAGE_TAG=latest is not allowed for release smoke'
  fi
  if [[ ! "$tag" =~ ^[A-Za-z0-9_][A-Za-z0-9_.-]{0,127}$ ]]; then
    release_smoke_preflight_fail 'MAILER_IMAGE_TAG has invalid Docker tag syntax'
  fi
}

release_smoke_preflight_validate_digest() {
  local digest="$1"
  digest="$(release_smoke_preflight_trim "$digest")"
  if [[ -z "$digest" ]]; then
    release_smoke_preflight_fail 'MAILER_IMAGE_DIGEST must not be empty'
  fi
  if [[ ! "$digest" =~ ^sha256:[0-9a-f]{64}$ ]]; then
    release_smoke_preflight_fail 'MAILER_IMAGE_DIGEST must match sha256:<64-lowercase-hex>'
  fi
}

release_smoke_preflight_resolve_artifact() {
  local repository="${MAILER_IMAGE_REPOSITORY:-ghcr.io/kooiei-in4a/amane-mailer}"
  local tag digest
  tag="$(release_smoke_preflight_trim "${MAILER_IMAGE_TAG:-}")"
  digest="$(release_smoke_preflight_trim "${MAILER_IMAGE_DIGEST:-}")"
  local tag_set=0 digest_set=0

  if [[ -n "$tag" ]]; then
    tag_set=1
  fi
  if [[ -n "$digest" ]]; then
    digest_set=1
  fi

  if [[ "$tag_set" -eq 0 && "$digest_set" -eq 0 ]]; then
    release_smoke_preflight_fail 'MAILER_IMAGE_TAG or MAILER_IMAGE_DIGEST is required (exactly one)'
  fi
  if [[ "$tag_set" -eq 1 && "$digest_set" -eq 1 ]]; then
    release_smoke_preflight_fail 'MAILER_IMAGE_TAG and MAILER_IMAGE_DIGEST are mutually exclusive'
  fi

  if [[ "$tag_set" -eq 1 ]]; then
    release_smoke_preflight_validate_tag "$tag"
    MAILER_IMAGE_REFERENCE="${repository}:${tag}"
    return 0
  fi

  release_smoke_preflight_validate_digest "$digest"
  MAILER_IMAGE_REFERENCE="${repository}@${digest}"
}

release_smoke_preflight_validate_project_name() {
  local project="${1:-amane-mailer-release-smoke}"
  project="$(release_smoke_preflight_trim "$project")"

  if [[ -z "$project" ]]; then
    release_smoke_preflight_fail 'RELEASE_SMOKE_PROJECT must not be empty'
  fi
  if [[ "$project" == "." || "$project" == ".." ]]; then
    release_smoke_preflight_fail 'RELEASE_SMOKE_PROJECT is invalid'
  fi
  if [[ "$project" == *"/"* || "$project" == *"\\"* ]]; then
    release_smoke_preflight_fail 'RELEASE_SMOKE_PROJECT is invalid'
  fi
  if [[ "$project" =~ [[:space:]] ]]; then
    release_smoke_preflight_fail 'RELEASE_SMOKE_PROJECT is invalid'
  fi
  if [[ ! "$project" =~ ^amane-mailer-release-smoke(-[a-z0-9][a-z0-9-]{0,40})?$ ]]; then
    release_smoke_preflight_fail 'RELEASE_SMOKE_PROJECT is invalid'
  fi

  RELEASE_SMOKE_PROJECT="$project"
}

release_smoke_preflight_validate_compose_file() {
  local compose_file="$1"
  if [[ -z "$compose_file" || ! -f "$compose_file" ]]; then
    release_smoke_preflight_fail 'release smoke compose file is missing'
  fi
  RELEASE_SMOKE_COMPOSE_FILE="$compose_file"
}

release_smoke_preflight_require_tools() {
  local missing=()
  command -v docker >/dev/null 2>&1 || missing+=("docker")
  command -v curl >/dev/null 2>&1 || missing+=("curl")
  command -v awk >/dev/null 2>&1 || missing+=("awk")
  if [[ "${#missing[@]}" -gt 0 ]]; then
    release_smoke_preflight_log_error "missing required tools: ${missing[*]}"
    exit "${RELEASE_SMOKE_PREFLIGHT_ERROR}"
  fi
  if ! docker compose version >/dev/null 2>&1; then
    release_smoke_preflight_fail "'docker compose' plugin is not available"
  fi
}

release_smoke_preflight_is_local_endpoint() {
  local endpoint="$1"
  case "$endpoint" in
    unix://*|npipe://*) return 0 ;;
    *) return 1 ;;
  esac
}

release_smoke_preflight_inspect_context_endpoint() {
  local context_name="${1:-}"
  local endpoint=""
  if [[ -n "$context_name" ]]; then
    endpoint="$(docker context inspect "$context_name" --format '{{.Endpoints.docker.Host}}' 2>/dev/null || true)"
  else
    endpoint="$(docker context inspect --format '{{.Endpoints.docker.Host}}' 2>/dev/null || true)"
  fi
  printf '%s' "$endpoint"
}

release_smoke_preflight_validate_docker_endpoint() {
  local endpoint=""

  if [[ -n "${DOCKER_CONTEXT:-}" ]]; then
    endpoint="$(release_smoke_preflight_inspect_context_endpoint "${DOCKER_CONTEXT}")"
    if [[ -z "$endpoint" ]]; then
      release_smoke_preflight_fail 'remote Docker endpoint is not allowed for release smoke'
    fi
  elif [[ -n "${DOCKER_HOST:-}" ]]; then
    endpoint="${DOCKER_HOST}"
  else
    endpoint="$(release_smoke_preflight_inspect_context_endpoint "")"
    if [[ -z "$endpoint" ]]; then
      release_smoke_preflight_fail 'remote Docker endpoint is not allowed for release smoke'
    fi
  fi

  if ! release_smoke_preflight_is_local_endpoint "$endpoint"; then
    release_smoke_preflight_fail 'remote Docker endpoint is not allowed for release smoke'
  fi
}

release_smoke_preflight_run() {
  local repo_root="$1"
  local compose_file="${2:-${repo_root}/infra/docker/docker-compose.release-smoke.yml}"

  release_smoke_preflight_resolve_artifact
  export MAILER_IMAGE_REFERENCE

  release_smoke_preflight_validate_project_name "${RELEASE_SMOKE_PROJECT:-amane-mailer-release-smoke}"
  export RELEASE_SMOKE_PROJECT

  release_smoke_preflight_validate_compose_file "$compose_file"
  release_smoke_preflight_require_tools
  release_smoke_preflight_validate_docker_endpoint
}

release_smoke_compose_argv() {
  printf '%s\n' docker compose -p "$RELEASE_SMOKE_PROJECT" -f "$RELEASE_SMOKE_COMPOSE_FILE"
}
