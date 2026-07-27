# ACS standalone live-send verification CLI runbook

> Command: `admin provider test-acs-send`
> Parent: [#423](https://github.com/kooiei-in4a/amane-mailer/issues/423) / [#426](https://github.com/kooiei-in4a/amane-mailer/issues/426)
> Related: [register-acs CLI](register-acs-cli-runbook.en.md), [setup entry](setup-guide.en.md) mode 3

## 1. Purpose

Verify, in one shot, that ACS accepts and completes a test send using only the ACS connection string, sender, and a test recipient — without starting Mailer API, Worker, Event Grid, Storage Queue, or bounce processing.

- Initial scope is **Staging only**. Production live send is out of scope.
- Only a fixed synthetic subject / text body is sent (no arbitrary body, attachments, or bulk).
- ACS operation success and mailbox arrival are separate judgments (arrival is a human ACTION).

## 2. Safety boundary

- Require environment confirmation (exact `Staging`) and fixed phrase `MAILER-ACS-TEST-SEND` before any send.
- Do not accept connection string, access key, sender, or recipient as command-line arguments.
- Prefer an existing secret file; fall back to interactive TTY non-echo double entry only when needed.
- Do not print secrets, sender, recipient, subject, body, message ID, or provider raw errors to stdout / stderr / logs.
- Do not modify the DB, tenant JSON, `platform-sender.json`, or the existing ACS secret (read-only).
- Classify and sanitize provider exceptions; return canonical result codes only.

## 3. Prerequisites

1. Staging ACS test sender (approved Email Domain) and a test recipient mailbox.
2. Prefer a registered `acs_connection_string` from `admin provider register-acs`, or deploy `ACS_CONNECTION_STRING_FILE`.
3. Choose an absolute path for the message ID handoff file (or set `MAILER_ACS_TEST_SEND_MESSAGE_ID_FILE`).
4. Do not run with Production credentials or Production recipients.

## 4. Execution

A real TTY is required. Redirected stdin / compose `-T` is rejected.

```bash
export ACS_CONNECTION_STRING_FILE=/path/to/acs_connection_string
export MAILER_ACS_TEST_SEND_MESSAGE_ID_FILE=/path/to/acs-test-send-message-id.txt

dotnet Amane.Mailer.dll admin provider test-acs-send
```

Interactive steps:

1. Environment: `Staging` (exact match)
2. Intent: `MAILER-ACS-TEST-SEND`
3. ACS connection string twice (hidden) only when no secret file is available
4. Sender email (bare)
5. Recipient email (bare)
6. Sender display name (optional; empty to skip; wire path uses sender email)
7. Absolute message ID handoff path (when env is unset)

## 5. Interpreting results

Success example (values themselves are never printed):

```text
[PASS] ACS authentication
[PASS] Send request accepted
[PASS] ACS send operation completed
[PASS] Message ID handoff file written
[ACTION] Confirm receipt in the test mailbox
success: operation=test_acs_send result=SUCCESS
```

| Line | Meaning |
|------|---------|
| `[PASS] ACS authentication` | Credentials allowed the send request to start |
| `[PASS] Send request accepted` | ACS accepted the send request |
| `[PASS] ACS send operation completed` | ACS long-running operation reached Succeeded |
| `[PASS] Message ID handoff file written` | Provider message ID written as UUID-only to the handoff file |
| `[ACTION] Confirm receipt...` | Mailbox arrival is a human check |

Failures use `failed: operation=test_acs_send result=<CODE>` or `rejected: ...`. Common codes:

| Code | Meaning |
|------|---------|
| `REJECTED_ENVIRONMENT_MISMATCH` | Not exact Staging |
| `REJECTED_INTENT_MISMATCH` | Phrase mismatch |
| `REJECTED_INPUT_REDIRECTED` | Non-interactive stdin |
| `REJECTED_SECRET_MISMATCH` / `REJECTED_INVALID_CONNECTION_STRING` | Secret input problems |
| `FAILED_ACS_AUTHENTICATION` | Auth / credential failure |
| `FAILED_ACS_SENDER_REJECTED` | Sender / domain rejection |
| `FAILED_ACS_SEND_REQUEST` | Send request failure |
| `FAILED_ACS_OPERATION` | LRO finished but not Succeeded |
| `FAILED_ACS_TIMEOUT` | Timeout / transient failure |

Exit codes: `0` success, `1` ACS-side failure, `2` input/precondition rejection, `130` cooperative Ctrl+C cancel.

## 6. Message ID handoff (for #428)

On success, write **one UUID line only** to the handoff file (no email / subject / body / secret).

- Later Delivery Report E2E (#428) is expected to reuse this file or the same `IAcsTestSendClient` path.
- Do not paste handoff file contents into evidence, issues, or PRs.

## 7. Real PTY smoke (dev / CI)

Does not contact real ACS. On Linux:

```bash
dotnet build src/Amane.Mailer/Amane.Mailer.csproj
python3 scripts/pty-smoke-test-acs-send.py
```

Covers environment rejection, intent rejection, TTY secret mismatch, redirected stdin rejection, and non-leakage of secrets / emails.

## 8. What may / must not be recorded

May record: command name, Staging, canonical result code, PASS/FAIL/ACTION distinction.

Must not record: connection string, access key, sender, recipient, subject, body, message ID, provider raw error, stack traces.

## 9. Non-goals

- Mailer API / Worker send verification
- Production send
- Arbitrary subject / body / attachment / bulk
- Event Grid / Queue / bounce verification
- Creating or changing ACS resources or Email Domains
- Credential persistence / rotation
