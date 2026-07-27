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

1. Environment: `Staging` (exact match; visible input. Ctrl+C → `REJECTED_CANCELLED` / exit `2`)
2. Intent: `MAILER-ACS-TEST-SEND` (visible input. Ctrl+C → exit `2`)
3. ACS connection string twice (**hidden**) only when no secret file is available (Ctrl+C → exit `2`)
4. Sender email (bare; **hidden** so it does not remain in the PTY transcript. Ctrl+C → exit `2`)
5. Recipient email (bare; **hidden**. Ctrl+C → exit `2`)
6. Fully qualified absolute path for the message ID handoff file (when `MAILER_ACS_TEST_SEND_MESSAGE_ID_FILE` is unset; visible input). **If the file already exists, reject before any ACS call** (`REJECTED_MESSAGE_ID_HANDOFF_PATH_EXISTS`). Operators must inspect/remove it and retry

Display-name input is not collected (wire path uses sender email only).

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
| `REJECTED_MESSAGE_ID_HANDOFF_PATH_INVALID` | Handoff path is not fully qualified |
| `REJECTED_MESSAGE_ID_HANDOFF_PATH_EXISTS` | Handoff file already exists (reject before send to prevent stale UUID reuse) |
| `FAILED_ACS_AUTHENTICATION` | Auth / credential failure (401/403). Display: `[FAIL] ACS authentication` |
| `FAILED_ACS_NETWORK` | Network reachability failure (DNS/TCP). Auth not judged. Display: `[FAIL] ACS network reachability` (no auth PASS/FAIL line) |
| `FAILED_ACS_SENDER_REJECTED` | Structured ACS error code clearly names sender/domain |
| `FAILED_ACS_SEND_REQUEST` | Other send-request failures (generic 4xx, etc.) |
| `FAILED_ACS_OPERATION` | LRO finished but not Succeeded |
| `FAILED_ACS_TIMEOUT` | Timeout / 429 / 5xx |
| `FAILED_ACS_MESSAGE_ID_INVALID` | After LRO success, message ID is not a matching UUID / is `NOT_SET` (no file written) |

Exit codes:

| Code | Meaning |
|------|---------|
| `0` | Send operation completed + UUID handoff written |
| `1` | ACS-side failure or message ID validation failure |
| `2` | Input/precondition rejection. **Ctrl+C during any interactive prompt** (environment / intent / secret / PII / handoff path) is `REJECTED_CANCELLED` → `2`. Redirected stdin and existing handoff also use `2` |
| `130` | Cooperative cancel **during ACS I/O only** (`RunCancellableCliAsync` maps token-linked `OperationCanceledException`) |

## 6. Message ID handoff (for #428)

On success, write **one `D`-format UUID line that matches the caller-supplied operation id** (no email / subject / body / secret). `NOT_SET`, non-UUID, or mismatched values fail with `FAILED_ACS_MESSAGE_ID_INVALID` and do not create/overwrite the file.

If a file already exists at the handoff path, reject with `REJECTED_MESSAGE_ID_HANDOFF_PATH_EXISTS` before any ACS call (prevents reusing a previous UUID). No auto-delete or overwrite.

- Later Delivery Report E2E (#428) is expected to reuse this file or the same `IAcsTestSendClient` path.
- Do not paste handoff file contents into evidence, issues, or PRs.

## 7. Real PTY smoke (dev / CI)

Does not contact real ACS. On Linux:

```bash
dotnet build src/Amane.Mailer/Amane.Mailer.csproj
python3 scripts/pty-smoke-test-acs-send.py
```

Covers environment rejection, intent rejection, TTY secret mismatch, non-echo sender/recipient + invalid handoff before send, interactive Ctrl+C (exit `2`), and redirected stdin rejection. The CI `build-test` job runs the same script. Exit `130` during ACS I/O is covered by unit tests (cooperative cancellation).

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
