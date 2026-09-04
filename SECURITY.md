# Security Policy

## Supported Versions

Starting with 1.0.0, Amane Mailer follows semantic versioning for the public HTTP
contract and `Amane.Mailer.Contracts` package. Only the latest patch release of
the current minor version receives security fixes.

The `Yes (latest release)` row is maintained from
[`release/current-public.json`](release/current-public.json). Older rows are
historical support records and must not be interpreted as the current public
release.

| Version | Supported          |
| ------- | ------------------ |
| 1.3.8   | Yes (latest release) |
| 1.2.0   | No                   |
| 1.1.0   | No                 |
| 1.0.1   | No                 |
| 1.0.0   | No                 |
| 0.9.2   | No                 |
| 0.9.1   | No                 |
| 0.9.0   | No                 |
| 0.4.x   | No                 |
| 0.3.x   | No                 |
| 0.2.x   | No                 |
| 0.1.x   | No                 |
| < 0.1.0 | No                 |

## Reporting a Vulnerability

**Do not open public GitHub Issues for security vulnerabilities.**

Please report security issues via
[GitHub's private vulnerability reporting](https://docs.github.com/en/code-security/security-advisories/guidance-on-reporting-and-writing/privately-reporting-a-security-vulnerability)
for this repository.

If GitHub private vulnerability reporting is unavailable to you, email
**kouichirou.ie@in4a.jp** with the subject line `[Amane Mailer Security]`.
Include reproduction steps, affected version, and impact assessment.

## Response Timeline

This project is maintained by a solo developer. Timelines below are
best-effort goals, not SLA guarantees.

| Stage                | Target       |
| -------------------- | ------------ |
| Initial response     | 7 days       |
| Triage / severity    | 14 days      |
| Fix release          | 90 days      |
| Public advisory      | After fix    |

If a reported issue is accepted, you will be credited in the advisory unless
you request otherwise.

## Scope

This policy covers:

- Amane Mailer service source code (`src/Amane.Mailer`)
- Contracts NuGet package (`src/Amane.Mailer.Contracts`)
- Published Docker images on GHCR (`ghcr.io/kooiei-in4a/amane-mailer`)
- Deploy-time compose templates (`infra/deploy`)

Out of scope:

- Host-level infrastructure, rclone configuration, age key management
- Reverse proxy setup and TLS termination
- Third-party dependencies (report upstream; mention here if relevant to Amane Mailer)

## General Guidelines

- Do not commit real secrets, tokens, connection strings, or database files.
- See `.gitignore` and `.dockerignore` for patterns that are already excluded.
- Keep `ACS_CONNECTION_STRING` empty in checked-in files.
- Do not commit registered ACS secret files under `infra/deploy/secrets/` or
  registered `platform-sender.json` under `infra/deploy/config/platform-sender/`.
  Staging/Production register those via `admin provider register-acs` (file-based
  `ACS_CONNECTION_STRING_FILE` only; see `docs/ops/register-acs-cli-runbook.md`).
- Use placeholder values (`replace-with-*`) in examples and templates.

## Provider Error Sanitization

ACS/Mailpit delivery exceptions can embed connection strings, access keys, SAS
tokens, bearer credentials, URL query secrets, and recipient email addresses.
Raw provider exception text is never persisted, logged, or shown in the Admin UI.

The delivery layer (`AcsMailDeliveryProvider`, `MailpitMailDeliveryProvider`)
routes every raw exception message through `ProviderErrorSanitizer.Sanitize`
before building the `MailDeliveryResult`. The worker also re-runs the sanitizer
immediately before persisting or logging delivery failures as defense-in-depth.
As a result, the
`mail_requests.last_error_message` / `mail_attempts.error_message` columns,
stdout logs, and the Admin UI all consume a single sanitized summary.

The sanitizer:

- Masks credential assignments (`endpoint=`, `accesskey=`, `token=`,
  `password=`, `SharedAccessKey=`, etc.) and URL query strings.
- Masks bearer tokens and email addresses.
- Collapses multi-line text to one line and truncates overlong messages.

The classification `error_code` uses the stable taxonomy in
`MailDeliveryErrorCodes` (for example `ACS_REQUEST_FAILED`,
`ACS_SEND_FAILED`, `SEND_TIMEOUT`, `PROVIDER_NETWORK`, or
`PROVIDER_UNKNOWN`). Exception type names from ACS/MailKit are not persisted
as `error_code`. Rows written before this taxonomy may still contain legacy
type-name codes; new failures use the stable codes only. No database rewrite
is performed.

Unknown provider exceptions map to `PROVIDER_UNKNOWN` with `retryable: false`.
The worker does not schedule further attempts for non-retryable failures, so
`PROVIDER_UNKNOWN` ends the delivery as `Failed` on that attempt (it is not
retried up to `max_attempts`, and it is not dead-lettered). Transient
network/timeout/protocol buckets remain retryable; auth/TLS failures are not.
Raw provider responses are intentionally not stored anywhere.

## Mail Request Metadata

Mailer applies a **docs-first** policy for `metadata` on
`POST /internal/mail-requests`:

- **Keys** containing `token`, `password`, `secret`, or `url` (case-insensitive)
  are rejected with `INVALID_METADATA` (422). Oversized metadata is also rejected.
- **Values** are stored exactly as sent. Mailer does not scan or scrub metadata
  values for secrets, URL query parameters, or token-like content.
- Accepted metadata is persisted in SQLite, included in backups, and may be
  displayed in the Admin UI when operators view stored mail request fields.

Consumers must not place secrets, bearer tokens, passwords, or reset-link query
secrets in metadata values even when the key name is allowed. `subject`, body
fields, `reply_to`, and `metadata` may contain PII; treat the mail payload and
Mailer database as sensitive data.

See `docs/api/openapi.yaml`, `src/Amane.Mailer.Contracts/README.md`, and
`docs/service-spec.md` for the full contract description.

## Admin Audit Logging

Admin operation audit events are persisted to the Mailer SQLite database
(`admin_audit_events` table) as the source of truth, so the trail survives
restart and deployment (ADR 0013 D-08). Each event is also mirrored to a
structured stdout log as a secondary channel.

Persisted events:

| Event type | When | Persistence policy |
| --- | --- | --- |
| `mail_request.body_viewed` | An authenticated admin opens a stored `html_body`, `text_body`, or `metadata_json` field | **Fail closed** — if the audit event cannot be persisted, the body view is denied with HTTP 500 and the content is not returned. |
| `auth.login_succeeded` | A successful admin login | **Best effort** — a persistence failure is logged but does not block the auth flow. |
| `auth.login_failed` | A rejected admin login | **Best effort** — bounded per IP/account by the login throttle. |
| `auth.logout` | A successful explicit admin logout | **Best effort** |
| `auth.session_expired` | A server-side session rejected for absolute or idle expiry | **Best effort** — deduplicated per session id for five minutes |
| `auth.account_temporarily_locked` | Login failures reached the throttle threshold | **Best effort** |
| `auth.login_rate_limited` | A login attempt rejected while the throttle lock is active | **Best effort** |

Each row records only the event type, actor, timestamp, source IP, a truncated
user-agent summary, the target reference (type / id / field name), the result,
and an optional error code. It must never include the message body, recipient
address, subject, metadata values, or payload JSON. For a failed login the
actor is the submitted username, length-bounded and never accompanied by the
password.

The body-view event keeps its dedicated structured stdout log
(`AdminMailRequestBodyViewed`) in addition to the database row.

Admin audit retention sweep uses `MAILER_ADMIN_AUDIT_RETENTION_DAYS` (default
180 days). Rows older than the configured retention are deleted on worker startup
and on a daily timer. Explicit purge:
`dotnet Amane.Mailer.dll db admin-audit purge --older-than-days <days>`.
Retention under 30 days is allowed only when `ASPNETCORE_ENVIRONMENT=Development`.
Purge output and sweep logs include counts and day thresholds only — no actor,
target, or mail payload fields. When
`MAILER_ADMIN_AUDIT_HASH_NETWORK_IDENTIFIERS=true`, auth audit `source_ip` values
and login throttle keys store keyed HMAC-SHA256 hashes instead of raw IP addresses;
see the runbook section **Admin audit identifier hash key rotation**.
`admin_audit_events` table is part of the Mailer SQLite database and is
therefore included in `Amane.Mailer db backup` output.

## Admin UI Security Scope

The Admin UI is an **internal-network-only, experimental** operational tool
(see [ADR 0013](docs/adr/0013-admin-threat-model-and-pii-policy.md)).
Do not expose `/admin` directly to the public internet.
Restrict access via a reverse proxy, VPN, firewall, or Docker port publish
limits before enabling the admin UI in any non-local environment.

Current implementation limits:

- **Audit log**: Body-view and auth events (login, logout, session expired, account
  locked, login rate limited) are persisted to `admin_audit_events` (stdout mirror).
  Retention sweep and `db admin-audit purge` remove rows older than
  `MAILER_ADMIN_AUDIT_RETENTION_DAYS` (default 180 days).
- **Login throttle**: SQLite-backed with in-memory cache; survives process restart.
- **Session store / revocation**: Server-side sessions in SQLite with credential-epoch
  invalidation on password hash change, explicit logout, expiry, and concurrent-session
  limit enforcement (default three sessions per admin).
- **Tenant scope**: Per-admin tenant scope is implemented (ADR 0014 Phase 2).
  Scoped admins are limited to explicitly assigned `tenant_id` values in
  `admin_user_tenant_scopes`. Break-glass admins (`is_break_glass=1`) can
  access all tenants and receive enhanced audit events. When two or more
  effective tenants exist (`tenants.json` count and distinct `mail_requests.tenant_id`,
  whichever is larger) and Admin is enabled, startup fails closed unless at
  least one enabled scoped or break-glass admin exists.
- **Bootstrap admin**: `AMANE_ADMIN_USERNAME` / `AMANE_ADMIN_PASSWORD_HASH`
  seeds the first `admin_users` row on empty database creation with all
  configured tenant scopes (`is_break_glass=false`). This is **not** break-glass
  access and does not receive break-glass audit treatment. In shared
  multi-tenant production, do not rely on the bootstrap admin for ongoing
  operations; provision scoped admins per tenant boundary. Use `admin user create`
  with `admin hash-password` for scoped / break-glass provisioning.
