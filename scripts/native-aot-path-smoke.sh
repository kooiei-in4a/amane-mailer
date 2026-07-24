#!/usr/bin/env bash
# Native AOT black-box smoke for low-frequency paths (issue #286).
#
# Runs against a published linux-x64 Amane.Mailer binary (not the GHCR image).
# Complements scripts/release-smoke.sh, which covers core API + Mailpit on the
# release image and does not exercise Admin, webhook HTTPS tenant load, or
# db backup on the AOT binary.
#
# Paths (labels appear in [PASS]/[FAIL] lines):
#   cli:admin-hash-password  — PBKDF2 hash CLI on the AOT binary
#   cli:db-migrate           — SQLite migrate CLI
#   cli:db-backup            — online SQLite backup CLI
#   http:admin-login-get     — Admin enabled; GET /admin/login
#   http:admin-login-post    — Admin cookie sign-in (CSRF + password hash)
#   http:webhook-https-ready — HTTPS webhook tenant config loads; /readyz 200
#
# Non-goals (remain JIT tests / manual / release-smoke):
#   ACS live send, full signed webhook delivery e2e (SSRF blocks loopback;
#   public receivers are not CI-stable), multi-arch, release-image Mailpit flow.
#
# Dependencies: bash, curl, awk; python3 optional (for free-port selection).
#
# Config via environment (all optional except MAILER_BIN when not defaulted):
#   MAILER_BIN                 path to Amane.Mailer (required)
#   AOT_PATH_SMOKE_HTTP_PORT   default 18580 (or free port if python3 available)
#   AOT_PATH_SMOKE_KEEP        set to 1 to keep work dir and leave process running
set -Eeuo pipefail
set +x

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" >/dev/null 2>&1 && pwd)"
REPO_ROOT="$(cd "$SCRIPT_DIR/.." >/dev/null 2>&1 && pwd)"

MAILER_BIN="${MAILER_BIN:-}"
KEEP="${AOT_PATH_SMOKE_KEEP:-0}"
HTTP_PORT="${AOT_PATH_SMOKE_HTTP_PORT:-}"

PASS_COUNT=0
FAIL_COUNT=0
WORK_DIR=""
MAILER_PID=""
COOKIE_JAR=""

log()  { printf '%s\n' "$*"; }
pass() { PASS_COUNT=$((PASS_COUNT + 1)); printf '[PASS] %s\n' "$1"; }
fail() { FAIL_COUNT=$((FAIL_COUNT + 1)); printf '[FAIL] %s -- %s\n' "$1" "$2" >&2; }

require_deps() {
  local missing=()
  command -v curl >/dev/null 2>&1 || missing+=("curl")
  command -v awk >/dev/null 2>&1 || missing+=("awk")
  if [ "${#missing[@]}" -gt 0 ]; then
    log "[error] missing required tools: ${missing[*]}"
    exit 2
  fi
}

pick_http_port() {
  if [ -n "$HTTP_PORT" ]; then
    return
  fi
  if command -v python3 >/dev/null 2>&1; then
    HTTP_PORT="$(python3 - <<'PY'
import socket
s = socket.socket()
s.bind(("127.0.0.1", 0))
print(s.getsockname()[1])
s.close()
PY
)"
  else
    HTTP_PORT="18580"
  fi
}

cleanup() {
  local status=$?
  if [ -n "${MAILER_PID:-}" ] && kill -0 "$MAILER_PID" >/dev/null 2>&1; then
    if [ "$KEEP" = "1" ]; then
      log "[cleanup] AOT_PATH_SMOKE_KEEP=1; leaving Mailer pid $MAILER_PID running."
    else
      kill "$MAILER_PID" >/dev/null 2>&1 || true
      wait "$MAILER_PID" >/dev/null 2>&1 || true
    fi
  fi
  if [ "$KEEP" != "1" ] && [ -n "${WORK_DIR:-}" ] && [ -d "$WORK_DIR" ]; then
    rm -rf "$WORK_DIR"
  elif [ "$KEEP" = "1" ] && [ -n "${WORK_DIR:-}" ]; then
    log "[cleanup] work dir kept at $WORK_DIR"
  fi
  exit "$status"
}
trap cleanup EXIT

dump_failure_diagnostics() {
  # CI runners discard WORK_DIR on EXIT; print tails so failures remain in job logs.
  if [ -z "${WORK_DIR:-}" ] || [ ! -d "$WORK_DIR" ]; then
    return
  fi
  local file
  for file in mailer.log migrate.err migrate-webhook.err backup.err hash.err login-post.out; do
    if [ -f "$WORK_DIR/$file" ] && [ -s "$WORK_DIR/$file" ]; then
      log ""
      log "[diagnostics] $file (tail)"
      tail -n 80 "$WORK_DIR/$file" || true
    fi
  done
}

finish() {
  log ""
  log "Native AOT path smoke: ${PASS_COUNT} passed, ${FAIL_COUNT} failed"
  if [ "$FAIL_COUNT" -gt 0 ]; then
    dump_failure_diagnostics
    exit 1
  fi
}

require_mailer_bin() {
  if [ -z "$MAILER_BIN" ]; then
    log "[error] MAILER_BIN is required (path to published Amane.Mailer)."
    exit 2
  fi
  if [ ! -x "$MAILER_BIN" ]; then
    log "[error] MAILER_BIN is not executable: $MAILER_BIN"
    exit 2
  fi
}

write_tenants_json() { # path include_webhook(0|1)
  local path="$1"
  local include_webhook="$2"
  if [ "$include_webhook" = "1" ]; then
    cat >"$path" <<'EOF'
{
  "version": 1,
  "environment": "develop",
  "tenants": [
    {
      "tenant_id": "00000000-0000-0000-0000-000000000101",
      "name": "aot-path-smoke",
      "source_services": ["example-service"],
      "default_from": {
        "email": "noreply@example.invalid",
        "display_name": "AOT Path Smoke"
      },
      "token_env": "MAIL_SERVICE_TOKEN",
      "provider": "mailpit",
      "live_sending": false,
      "metadata_max_bytes": 4096,
      "retry": {
        "max_attempts": 3,
        "initial_delay_seconds": 1,
        "max_delay_seconds": 2
      },
      "webhook": {
        "url": "https://example.com/internal/mailer/webhooks",
        "secret_env": "AOT_PATH_SMOKE_WEBHOOK_SECRET",
        "allowed_host_suffixes": ["example.com"]
      }
    }
  ]
}
EOF
  else
    cat >"$path" <<'EOF'
{
  "version": 1,
  "environment": "develop",
  "tenants": [
    {
      "tenant_id": "00000000-0000-0000-0000-000000000101",
      "name": "aot-path-smoke",
      "source_services": ["example-service"],
      "default_from": {
        "email": "noreply@example.invalid",
        "display_name": "AOT Path Smoke"
      },
      "token_env": "MAIL_SERVICE_TOKEN",
      "provider": "mailpit",
      "live_sending": false,
      "metadata_max_bytes": 4096,
      "retry": {
        "max_attempts": 3,
        "initial_delay_seconds": 1,
        "max_delay_seconds": 2
      }
    }
  ]
}
EOF
  fi
}

run_cli_hash_password() {
  local password="$1"
  local out_file="$2"
  local err_file="$3"
  # Prompt text goes to stderr; hash goes to stdout.
  printf '%s\n%s\n' "$password" "$password" | "$MAILER_BIN" admin hash-password >"$out_file" 2>"$err_file"
}

extract_csrf_token() {
  local html_file="$1"
  awk '
    BEGIN { marker = "name=\"__RequestVerificationToken\" value=\"" }
    {
      idx = index($0, marker)
      if (idx > 0) {
        rest = substr($0, idx + length(marker))
        end = index(rest, "\"")
        if (end > 1) {
          print substr(rest, 1, end - 1)
          exit
        }
      }
    }
  ' "$html_file"
}

wait_for_http() { # url expected_status timeout_seconds
  local url="$1"
  local expected="$2"
  local timeout_seconds="$3"
  local started
  started="$(date +%s)"
  local status=""
  while true; do
    status="$(curl -sS -o /dev/null -w '%{http_code}' --max-time 2 "$url" 2>/dev/null || true)"
    if [ "$status" = "$expected" ]; then
      return 0
    fi
    if [ "$(( $(date +%s) - started ))" -ge "$timeout_seconds" ]; then
      printf '%s' "${status:-000}"
      return 1
    fi
    sleep 0.25
  done
}

start_mailer() { # tenants_path db_path password_hash log_file
  local tenants_path="$1"
  local db_path="$2"
  local password_hash="$3"
  local log_file="$4"
  # Published wwwroot lives next to the binary. Without an explicit content root,
  # ASP.NET resolves wwwroot from the process cwd and Admin static mapping aborts.
  local content_root
  content_root="$(cd "$(dirname "$MAILER_BIN")" && pwd)"

  env \
    ASPNETCORE_ENVIRONMENT=Development \
    ASPNETCORE_CONTENTROOT="$content_root" \
    ASPNETCORE_URLS="http://127.0.0.1:${HTTP_PORT}" \
    "ConnectionStrings__Mailer=Data Source=${db_path}" \
    "MAILER_TENANTS_PATH=${tenants_path}" \
    MAIL_SERVICE_TOKEN=local-mail-service-token \
    MAILER_PROVIDER=mailpit \
    MAILPIT_SMTP_HOST=127.0.0.1 \
    MAILPIT_SMTP_PORT=1025 \
    MAILPIT_SMTP_USE_SSL=false \
    ACS_CONNECTION_STRING= \
    MAILER_METRICS_BEARER_TOKEN=local-metrics-scrape-token \
    Mailer__Worker__Enabled=false \
    AMANE_ADMIN_ENABLED=true \
    AMANE_ADMIN_USERNAME=admin \
    "AMANE_ADMIN_PASSWORD_HASH=${password_hash}" \
    AMANE_ADMIN_ALLOW_HTTP=true \
    AMANE_ADMIN_ALLOWED_LOCAL_ADDRESS=127.0.0.1 \
    AOT_PATH_SMOKE_WEBHOOK_SECRET=local-aot-path-smoke-webhook-secret \
    "$MAILER_BIN" >"$log_file" 2>&1 &
  MAILER_PID=$!
}

stop_mailer() {
  if [ -n "${MAILER_PID:-}" ] && kill -0 "$MAILER_PID" >/dev/null 2>&1; then
    kill "$MAILER_PID" >/dev/null 2>&1 || true
    wait "$MAILER_PID" >/dev/null 2>&1 || true
  fi
  MAILER_PID=""
}

main() {
  require_deps
  require_mailer_bin
  pick_http_port

  WORK_DIR="$(mktemp -d "${TMPDIR:-/tmp}/amane-aot-path-smoke.XXXXXX")"
  COOKIE_JAR="$WORK_DIR/cookies.txt"
  local db_path="$WORK_DIR/mailer.db"
  local backup_path="$WORK_DIR/backups/mailer-backup.db"
  local tenants_plain="$WORK_DIR/tenants.plain.json"
  local tenants_webhook="$WORK_DIR/tenants.webhook.json"
  local hash_out="$WORK_DIR/hash.out"
  local hash_err="$WORK_DIR/hash.err"
  local login_html="$WORK_DIR/login.html"
  local server_log="$WORK_DIR/mailer.log"
  local password="aot-path-smoke-password"
  local base_url="http://127.0.0.1:${HTTP_PORT}"
  local hash=""

  mkdir -p "$WORK_DIR/backups"
  write_tenants_json "$tenants_plain" 0
  write_tenants_json "$tenants_webhook" 1

  log "Native AOT path smoke"
  log "  MAILER_BIN=$MAILER_BIN"
  log "  HTTP_PORT=$HTTP_PORT"
  log "  WORK_DIR=$WORK_DIR"
  log ""

  # --- cli:admin-hash-password ---
  if run_cli_hash_password "$password" "$hash_out" "$hash_err"; then
    hash="$(tr -d '\r\n' <"$hash_out")"
    if [[ "$hash" == pbkdf2:sha256:* ]]; then
      pass "cli:admin-hash-password"
    else
      fail "cli:admin-hash-password" "stdout was not a pbkdf2:sha256 hash"
      hash=""
    fi
  else
    fail "cli:admin-hash-password" "exit code $? (see $hash_err)"
    hash=""
  fi

  if [ -z "${hash:-}" ]; then
    fail "cli:db-migrate" "skipped; no password hash from admin hash-password"
    fail "cli:db-backup" "skipped; no password hash from admin hash-password"
    fail "http:admin-login-get" "skipped; no password hash from admin hash-password"
    fail "http:admin-login-post" "skipped; no password hash from admin hash-password"
    fail "http:webhook-https-ready" "skipped; no password hash from admin hash-password"
    finish
  fi

  # --- cli:db-migrate ---
  if env \
    "ConnectionStrings__Mailer=Data Source=${db_path}" \
    "MAILER_TENANTS_PATH=${tenants_plain}" \
    MAIL_SERVICE_TOKEN=local-mail-service-token \
    "$MAILER_BIN" db migrate >"$WORK_DIR/migrate.out" 2>"$WORK_DIR/migrate.err"; then
    if [ -f "$db_path" ]; then
      pass "cli:db-migrate"
    else
      fail "cli:db-migrate" "migrate succeeded but database file was not created"
    fi
  else
    fail "cli:db-migrate" "exit code $? (see $WORK_DIR/migrate.err)"
  fi

  # --- cli:db-backup ---
  if [ -f "$db_path" ]; then
    if env \
      "ConnectionStrings__Mailer=Data Source=${db_path}" \
      "MAILER_TENANTS_PATH=${tenants_plain}" \
      MAIL_SERVICE_TOKEN=local-mail-service-token \
      "$MAILER_BIN" db backup "$backup_path" >"$WORK_DIR/backup.out" 2>"$WORK_DIR/backup.err"; then
      if [ -f "$backup_path" ] && [ -s "$backup_path" ]; then
        pass "cli:db-backup"
      else
        fail "cli:db-backup" "backup command succeeded but destination missing or empty"
      fi
    else
      fail "cli:db-backup" "exit code $? (see $WORK_DIR/backup.err)"
    fi
  else
    fail "cli:db-backup" "skipped; migrate did not create a database"
  fi

  # --- http:admin + webhook HTTPS (single process with webhook tenant) ---
  if [ ! -f "$db_path" ]; then
    fail "http:admin-login-get" "skipped; no database"
    fail "http:admin-login-post" "skipped; no database"
    fail "http:webhook-https-ready" "skipped; no database"
    finish
  fi

  # Fresh DB for host so migrate-on-start / credential sync see clean state.
  rm -f "$db_path" "${db_path}-wal" "${db_path}-shm"
  if ! env \
    "ConnectionStrings__Mailer=Data Source=${db_path}" \
    "MAILER_TENANTS_PATH=${tenants_webhook}" \
    MAIL_SERVICE_TOKEN=local-mail-service-token \
    AOT_PATH_SMOKE_WEBHOOK_SECRET=local-aot-path-smoke-webhook-secret \
    "$MAILER_BIN" db migrate >"$WORK_DIR/migrate-webhook.out" 2>"$WORK_DIR/migrate-webhook.err"; then
    fail "http:webhook-https-ready" "pre-host migrate failed (see $WORK_DIR/migrate-webhook.err)"
    fail "http:admin-login-get" "skipped; pre-host migrate failed"
    fail "http:admin-login-post" "skipped; pre-host migrate failed"
    finish
  fi

  start_mailer "$tenants_webhook" "$db_path" "$hash" "$server_log"

  local ready_status=""
  if ready_status="$(wait_for_http "$base_url/readyz" "200" 30)"; then
    pass "http:webhook-https-ready"
  else
    fail "http:webhook-https-ready" "readyz returned '${ready_status:-none}' within 30s (see $server_log)"
  fi

  # One GET stores both the HTML (CSRF field) and antiforgery cookie for POST.
  rm -f "$COOKIE_JAR"
  local login_code
  login_code="$(
    curl -sS -c "$COOKIE_JAR" -b "$COOKIE_JAR" -o "$login_html" -w '%{http_code}' \
      --max-time 5 "$base_url/admin/login" || true
  )"
  if [ "$login_code" = "200" ] && grep -q '__RequestVerificationToken' "$login_html"; then
    pass "http:admin-login-get"
  else
    fail "http:admin-login-get" "GET /admin/login status=${login_code:-none}"
  fi

  local csrf
  csrf="$(extract_csrf_token "$login_html" || true)"
  if [ -n "$csrf" ]; then
    local post_code
    post_code="$(
      curl -sS -c "$COOKIE_JAR" -b "$COOKIE_JAR" -o "$WORK_DIR/login-post.out" -w '%{http_code}' \
        --max-time 30 \
        -X POST "$base_url/admin/api/login" \
        -H 'Content-Type: application/x-www-form-urlencoded' \
        --data-urlencode "__RequestVerificationToken=${csrf}" \
        --data-urlencode "username=admin" \
        --data-urlencode "password=${password}" \
        || true
    )"
    if [ "$post_code" = "302" ] || [ "$post_code" = "303" ]; then
      if grep -qiE 'amane-admin-auth|__Host-amane-admin-auth' "$COOKIE_JAR"; then
        pass "http:admin-login-post"
      else
        fail "http:admin-login-post" "redirect ${post_code} but auth cookie missing"
      fi
    else
      fail "http:admin-login-post" "POST /admin/api/login status=${post_code:-none}"
    fi
  else
    fail "http:admin-login-post" "CSRF token not found on login page"
  fi

  stop_mailer
  finish
}

main "$@"
