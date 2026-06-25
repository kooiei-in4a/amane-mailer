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

## Admin Audit Logging

When the Admin UI is enabled and an authenticated admin opens a stored
`html_body`, `text_body`, or `metadata_json` field, Amane Mailer writes a
structured audit log event named `AdminMailRequestBodyViewed`.

The event records only the admin username, mail request id, field name, and
remote address. It must not include the message body, recipient address,
subject, or metadata values.
