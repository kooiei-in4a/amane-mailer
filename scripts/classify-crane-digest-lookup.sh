#!/usr/bin/env bash
# Fail-close crane digest / tag lookup classification.
# Sourced by promote-release-latest (and unit-tested in isolation).
# Does not push, copy, login, or depend on candidate-OCI promotion plumbing.
#
# classify_crane_digest_lookup <crane_bin> <ref>
#   prints: PRESENT|<digest> | ABSENT| | UNKNOWN|
#   PRESENT only when exit 0 and stdout is exactly one line matching ^sha256:[0-9a-f]{64}$.
#   ABSENT only when registry clearly reports missing manifest/name.
#   Auth / network / TLS / timeout / 5xx / rate-limit / parse / tool / malformed success -> UNKNOWN.
# stderr from crane is redacted before classification (no credential echo).

redact_registry_err() {
  sed -E \
    -e 's/[Bb]earer[[:space:]]+[A-Za-z0-9._~+\/=-]+/Bearer [REDACTED]/g' \
    -e 's/(password|token|authorization|GITHUB_TOKEN|GHCR_TOKEN)[=:][^[:space:]]+/\1=[REDACTED]/gI' \
    -e 's/ghp_[A-Za-z0-9]+/ghp_[REDACTED]/g' \
    -e 's/gho_[A-Za-z0-9]+/gho_[REDACTED]/g'
}

classify_crane_digest_lookup() {
  local crane_bin="$1"
  local ref="$2"
  local errf out rc err
  if [[ -z "${crane_bin}" || -z "${ref}" ]]; then
    printf '%s\n' 'UNKNOWN|'
    return 0
  fi
  errf="$(mktemp)"
  set +e
  out="$("${crane_bin}" digest "${ref}" 2>"${errf}")"
  rc=$?
  set -e
  if [[ "${rc}" -eq 0 ]]; then
    rm -f "${errf}"
    # Fail-close: do not take head -n 1. Multiline / garbage / short / non-sha256 => UNKNOWN.
    out="$(printf '%s' "${out}" | tr -d '\r')"
    if [[ "${out}" =~ ^sha256:[0-9a-f]{64}$ ]]; then
      printf 'PRESENT|%s\n' "${out}"
      return 0
    fi
    printf '%s\n' 'UNKNOWN|'
    return 0
  fi
  err="$(redact_registry_err < "${errf}" || true)"
  rm -f "${errf}"
  # Explicit UNKNOWN classes (auth / transport / server / tool). Never ABSENT.
  if echo "${err}" | grep -Eiq \
    'unauthorized|authentication required|denied|forbidden|[[:space:]]401([[:space:]]|$)|[[:space:]]403([[:space:]]|$)|[[:space:]]429([[:space:]]|$)|rate.?limit|too many requests|[[:space:]]5[0-9]{2}([[:space:]]|$)|dial tcp|connection refused|i/o timeout|context deadline|tls:|x509:|certificate|no such host|network is unreachable|temporary failure|server misbehaving|EOF|http2:|could not parse reference|server gave HTTP response to HTTPS|timed out|timeout'; then
    printf '%s\n' 'UNKNOWN|'
    return 0
  fi
  if echo "${err}" | grep -Eiq 'MANIFEST_UNKNOWN|NAME_UNKNOWN|manifest unknown|name unknown'; then
    printf '%s\n' 'ABSENT|'
    return 0
  fi
  printf '%s\n' 'UNKNOWN|'
  return 0
}
