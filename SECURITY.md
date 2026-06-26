# Security Policy

## Reporting a Vulnerability

**Do not open public GitHub Issues for security vulnerabilities.**

If you discover a security issue, please report it via
[GitHub's private vulnerability reporting](https://docs.github.com/en/code-security/security-advisories/guidance-on-reporting-and-writing/privately-reporting-a-security-vulnerability)
for this repository.

## Scope

This policy covers the Amane Mailer service code, its Contracts NuGet package,
Docker images, and the deploy-time compose templates in this repository.

Host-level infrastructure, rclone configuration, age key management, and
reverse proxy setup are outside this repository's scope.

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

Each row records only the event type, actor, timestamp, source IP, a truncated
user-agent summary, the target reference (type / id / field name), the result,
and an optional error code. It must never include the message body, recipient
address, subject, metadata values, or payload JSON. For a failed login the
actor is the submitted username, length-bounded and never accompanied by the
password.

The body-view event keeps its dedicated structured stdout log
(`AdminMailRequestBodyViewed`) in addition to the database row.

Not yet implemented (tracked for follow-up): logout / session-expired /
login-rate-limited events, retention sweep
(`MAILER_ADMIN_AUDIT_RETENTION_DAYS`), and optional hashing of network
identifiers (`MAILER_ADMIN_AUDIT_HASH_NETWORK_IDENTIFIERS`). The
`admin_audit_events` table is part of the Mailer SQLite database and is
therefore included in `Amane.Mailer db backup` output.

## Admin UI Security Scope

The Admin UI is an **internal-network-only, experimental** operational tool
(see [ADR 0013](docs/adr/0013-admin-threat-model-and-pii-policy.md)).
Do not expose `/admin` directly to the public internet.
Restrict access via a reverse proxy, VPN, firewall, or Docker port publish
limits before enabling the admin UI in any non-local environment.

Current implementation limits:

- **Audit log**: Body-view and login success/failure events are persisted to
  the `admin_audit_events` SQLite table (and mirrored to stdout). Logout,
  session-expired, and login-rate-limited events, plus a retention sweep, are
  not yet implemented. See the Admin Audit Logging section above.
- **Login throttle**: In-memory only; resets on process restart
  (no durable throttle).
- **Session store / revocation**: No durable server-side session store
  (cookie auth only). Immediate revocation of existing sessions on admin
  disable or credential change is not implemented. Sessions remain valid
  until the default idle timeout (30 min) or default absolute lifetime (12 h)
  expires.
- **Tenant scope**: No per-admin tenant scope. A single
  `AMANE_ADMIN_USERNAME` / `AMANE_ADMIN_PASSWORD_HASH` credential has
  access to all tenants.
