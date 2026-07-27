# ACS Delivery Report Queue arrival E2E CLI runbook

> Command: `setup verify-delivery-report`
> Parent: [#423](https://github.com/kooiei-in4a/amane-mailer/issues/423) / [#428](https://github.com/kooiei-in4a/amane-mailer/issues/428)
> Sources of truth: [ADR 0020](../adr/0020-bounce-ingestion-and-suppression.md), [setup guide](setup-guide.en.md), [#426 test-acs-send](test-acs-send-cli-runbook.en.md), [#427 check-event-grid](event-grid-config-check-runbook.en.md)

## 1. Purpose

In Staging, send a normal ACS test message and confirm with **read-only peek** that a matching `Microsoft.Communication.EmailDeliveryReportReceived` event arrives in Storage Queue via Event Grid.

- **Staging only.** Production live send and Production Queue checks are out of scope.
- Does not require real bounces, invalid recipients, or suppression register/remove.
- Does not verify Mailer `provider_event_inbox` ingestion.

## 2. Safety boundary

- Require exact `Staging` and fixed phrase `MAILER-VERIFY-DELIVERY-REPORT` before any send.
- Never accept ACS/Queue connection strings, sender, or recipient as command-line arguments.
- Queue access is **Peek / GetProperties only**. No Receive, Delete, or visibility change.
- Never print message IDs, recipient, sender, subject, body, raw event JSON, provider raw errors, or connection strings.
- Do not create, update, or delete Azure resources, Event Grid subscriptions, Queues, or RBAC.

## 3. Prerequisites

1. [#427](https://github.com/kooiei-in4a/amane-mailer/issues/427) `setup check-event-grid ... --environment staging` exited `0` (WARN/ACTION allowed; no FAIL).
2. Staging ACS approved sender and a dedicated test recipient.
3. A dedicated or low-backlog Staging Queue. If the Mailer bounce poller consumes the same queue, pause it or use a dedicated queue to avoid races.
4. Prefer ACS secret file (`ACS_CONNECTION_STRING_FILE` or `MAILER_ACS_SECRET_DIRECTORY/acs_connection_string`).
5. Prefer Queue connection string file (`MAILER_BOUNCE_QUEUE_CONNECTION_STRING_FILE`) and queue name (`MAILER_BOUNCE_QUEUE_NAME`).

## 4. Execution

A real TTY is required. Redirected stdin / compose `-T` is rejected.

```bash
export ACS_CONNECTION_STRING_FILE=/path/to/acs_connection_string
export MAILER_BOUNCE_QUEUE_CONNECTION_STRING_FILE=/path/to/queue_connection_string
export MAILER_BOUNCE_QUEUE_NAME=staging-acs-delivery-reports
# optional (defaults: timeout 180s, poll 5s; caps: timeout 30-600, poll 1-30)
export MAILER_VERIFY_DELIVERY_REPORT_TIMEOUT_SECONDS=180
export MAILER_VERIFY_DELIVERY_REPORT_POLL_INTERVAL_SECONDS=5

dotnet Amane.Mailer.dll setup verify-delivery-report
```

Interactive steps:

1. Environment: `Staging` (exact match; Ctrl+C → exit `2`)
2. Intent: `MAILER-VERIFY-DELIVERY-REPORT`
3. ACS connection string (hidden double-entry only when no secret file)
4. Sender / Recipient (hidden)
5. Queue connection string (hidden double-entry only when no file/env)
6. Queue name (visible only when unset). Obvious production-looking names (`prod` / `production`) are rejected

Send content reuses the same fixed synthetic subject / text body as #426 via `IAcsTestSendClient`.

## 5. Interpreting results

Values themselves are never printed.

```text
[PASS] ACS authentication
[PASS] Send request accepted
[PASS] ACS send operation completed
[PASS] Delivery Report observed in Storage Queue
[PASS] Event correlated to the test send
[PASS|FAIL|WARN] Delivery status classified
[ACTION] Confirm receipt in the test mailbox
success: operation=verify_delivery_report result=SUCCESS
```

| Judgment | Meaning |
|----------|---------|
| ACS send operation | ACS completed the send operation (#426 equivalent) |
| Delivery Report observed | A Delivery Report was visible in the queue |
| Event correlated | Exact message ID match (no normalization) |
| Delivery status classified | `Delivered` → PASS; `Failed` / `Bounced` → FAIL; other → WARN. Independent of wiring |
| mailbox ACTION | Human mailbox confirmation |

If wiring is confirmed, exit `0` even when delivery status is `Failed` (wiring PASS, delivery FAIL).

On timeout, ACS success and Event Grid non-confirmation are reported separately.

If queue backlog exceeds the peek window (32) and the target cannot be confirmed, emit WARN / ACTION and do not PASS.

## 6. Exit codes

| Code | Meaning |
|------|---------|
| `0` | ACS send completed and Delivery Report correlated (wiring confirmed) |
| `1` | ACS failure, Queue auth failure, timeout, backlog-inconclusive, etc. |
| `2` | Staging / phrase / input rejection, or Ctrl+C during prompts |
| `130` | Cooperative cancel during ACS / Queue I/O |

## 7. Non-goals

- Generating real bounces
- Suppression register / remove
- Mailer inbox ingestion
- Production execution
- Creating or modifying Event Grid / Queue resources
- Deleting queue messages
- Saving raw event evidence

## 8. Related

- [日本語](verify-delivery-report-runbook.md)
- [test-acs-send-cli-runbook.en.md](test-acs-send-cli-runbook.en.md)
- [event-grid-config-check-runbook.en.md](event-grid-config-check-runbook.en.md)
- [bounce-ingestion-runbook.en.md](bounce-ingestion-runbook.en.md)
