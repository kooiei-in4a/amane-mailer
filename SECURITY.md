# Security Policy

## Supported Versions

Amane Mailer is pre-1.0 software. Only the latest patch release of the current
minor version receives security fixes.

| Version | Supported          |
| ------- | ------------------ |
| 0.1.1   | Yes (latest patch) |
| < 0.1.1 | No                 |

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

The classification `error_code` (for example `ACS_REQUEST_FAILED`,
`ACS_SEND_FAILED`, `SEND_TIMEOUT`, or the exception type name) is preserved so
operators can still triage failures. Raw provider responses are intentionally
not stored anywhere.

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

Not yet implemented (tracked for follow-up): retention sweep
(`MAILER_ADMIN_AUDIT_RETENTION_DAYS`). When
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
  Retention sweep is not yet implemented.
- **Login throttle**: SQLite-backed with in-memory cache; survives process restart.
- **Session store / revocation**: Server-side sessions in SQLite with credential-epoch
  invalidation on password hash change, explicit logout, expiry, and concurrent-session
  limit enforcement (default three sessions per admin).
- **Tenant scope**: No per-admin tenant scope. A single
  `AMANE_ADMIN_USERNAME` / `AMANE_ADMIN_PASSWORD_HASH` credential has
  access to all tenants.
