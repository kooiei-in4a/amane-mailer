# Event Grid / Storage Queue configuration check CLI runbook

> Command: `setup check-event-grid`
> Parent: [#423](https://github.com/kooiei-in4a/amane-mailer/issues/423) / [#427](https://github.com/kooiei-in4a/amane-mailer/issues/427)
> Sources of truth: [ADR 0020](../adr/0020-bounce-ingestion-and-suppression.md), [bounce ingestion runbook](bounce-ingestion-runbook.en.md), [setup guide](setup-guide.en.md)

## 1. Purpose

Verify, without mutating Azure resources, that the Event Grid subscription and Storage Queue used for ACS Email Delivery Reports match the v1.1.0 Pull bounce-ingestion assumptions.

This command only inspects configuration. It does not prove Delivery Report arrival or read queue message bodies (arrival belongs to [#428](https://github.com/kooiei-in4a/amane-mailer/issues/428) / the bounce runbook).

## 2. Chosen approach

No Azure Resource Manager SDK is added to Mailer. The command runs allowlisted read-only Azure CLI queries against an operator `az login` session.

| Option | Decision |
|--------|----------|
| Add Azure ARM SDKs | Rejected (Native AOT/trim, binary size, credential chain, maintenance) |
| Invoke Azure CLI (chosen) | No new NuGet packages; credentials stay in `az login`; CI uses fixtures / fake runner |

Core logic is not duplicated into Bash/PowerShell.

## 3. Prerequisites

1. Azure CLI on PATH
2. `az login` with read access to the target ACS / Event Grid / Storage resources
3. Resource names/IDs only — never secrets

Do not supply:

- ACS connection strings / access keys
- Storage account keys / connection strings
- access tokens / Bearer values
- sender or recipient emails

## 4. Run

```bash
dotnet Amane.Mailer.dll setup check-event-grid \
  --subscription <id-or-name> \
  --resource-group <rg> \
  --acs-name <acs-name> \
  --event-subscription <subscription-name> \
  --storage-account <storage-account> \
  --queue-name <queue-name> \
  --environment <dev|staging|production>
```

Use either `--acs-name` or `--acs-resource-id` (exactly one).

`--subscription` is passed to each query; the command does not run `az account set`.

## 5. Interpreting results

Same vocabulary as `setup doctor`.

| Code | Meaning |
|------|---------|
| `PASS` | Queried configuration matches the expectation |
| `FAIL` | Missing resource or clear mismatch |
| `WARN` | Cannot fully machine-verify (RBAC, network, arrival) |
| `ACTION` | Operator follow-up in Portal/CLI |

Output ends with `Summary: PASS=… FAIL=… WARN=… ACTION=…`. Any `FAIL` yields exit code `1`. Usage errors yield `2`.

Typical failures:

- Missing event subscription
- Source ACS mismatch
- Non-Storage Queue destination
- Destination queue/storage mismatch
- Missing `Microsoft.Communication.EmailDeliveryReportReceived` (`All` alone is not accepted; the type must be listed explicitly)
- Push webhook destination (not valid for v1.1.0; #304)
- Cloud Events delivery schema
- Environment naming mix heuristic

Typical WARN / ACTION (never auto-PASS):

- Event Grid → Queue RBAC / managed identity
- Storage firewall / private endpoint
- Actual Delivery Report arrival (out of scope here)

## 6. Safety / read-only guarantee

- No Azure create / update / delete
- No automatic repair of role assignments, subscriptions, queues, network, or firewall
- Azure CLI calls are allowlisted read queries only
- Raw JSON, raw errors, tokens, keys, and emails are not printed
- Subscription GUIDs in resource IDs are masked as `***`

## 7. Testing and real Azure

- CI uses a fake Azure CLI runner / fixture JSON (no live Azure auth required)
- Real Azure manual smoke is maintainer-owned. If skipped, document why and residual risk in the PR

## 8. Related

- [bounce-ingestion-runbook.en.md](bounce-ingestion-runbook.en.md)
- [setup-guide.en.md](setup-guide.en.md)
- ADR 0020