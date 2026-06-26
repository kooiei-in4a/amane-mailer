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
before building the `MailDeliveryResult`. As a result, the
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

When the Admin UI is enabled and an authenticated admin opens a stored
`html_body`, `text_body`, or `metadata_json` field, Amane Mailer writes a
structured audit log event named `AdminMailRequestBodyViewed`.

The event records only the admin username, mail request id, field name, and
remote address. It must not include the message body, recipient address,
subject, or metadata values.
