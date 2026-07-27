[日本語](setup-guide.md)

# Amane Mailer setup entry point

This is the **single entry point** for first-time Amane Mailer setup. Choose one configuration mode, gather what you need, then follow the existing runbooks in order.

This document is the source of truth for decisions, order, safety boundaries, and shared terminology. It does not copy detailed procedures; it links to each runbook and the config README.

Parent tracking: [#423](https://github.com/kooiei-in4a/amane-mailer/issues/423) · This issue: [#424](https://github.com/kooiei-in4a/amane-mailer/issues/424)

## Before you start (safety)

- Do not paste secrets, connection strings, real tenant tokens, sender/recipient addresses, PII, or raw provider errors into docs, issues, logs, or chat.
- Use placeholders only (`replace-with-*`, `local-mail-service-token`).
- Event Grid **Push** webhooks ([#304](https://github.com/kooiei-in4a/amane-mailer/issues/304)) are **not** the v1.1.0 adopted transport. Do not follow Push as the setup path.
- The v1.1.0 bounce transport is **Storage Queue Pull only** (`MAILER_BOUNCE_INGESTION=queue`).
- **Generating a real bounce is not a normal setup completion criterion.**

### About the published v1.1.0 image

Bounce ingestion (including migration `011`) may already exist in source, but while the public GitHub release / GHCR tag `v1.1.0` is missing, treat **final verification against the published image as not done**. If you follow procedures with a local build or develop-derived artifact, record that in your ops notes. Re-verify against the published image after v1.1.0 release / publish / post-promote sync completes.

## Result codes (PASS / FAIL / WARN / ACTION)

Use the same meanings in later setup doctor / verification CLIs ([#425](https://github.com/kooiei-in4a/amane-mailer/issues/425)–[#428](https://github.com/kooiei-in4a/amane-mailer/issues/428)). Existing smoke scripts mainly emit `[PASS]` / `[FAIL]`.

| Code | Meaning | What to do next |
|------|---------|-----------------|
| **PASS** | The check matches the intended configuration | Continue to the next check or mode |
| **FAIL** | A required precondition is missing; continuing risks a wrong or unsafe live send | Stop and fix using the linked runbook / config docs |
| **WARN** | Not strictly required, but risky or discouraged for operations | Record it and decide whether to resolve before production |
| **ACTION** | The tool will not auto-fix; a human must perform an explicit step | Follow the indicated runbook steps yourself |

Do not include secret values, plaintext recipients, connection strings, or raw provider errors in results. Report only which setting key or capability is missing.

## Choose a configuration mode

Answer these questions and pick **exactly one** mode.

1. Reach first delivery on Docker without sending real mail → **local Mailpit**
2. Bring up a deploy-shaped stack without ACS live send → **staging ACS no-send**
3. Explicitly validate ACS connectivity / sender on staging for a short window → **staging ACS verification**
4. Send production mail with an approved sender (bounce ingestion not required yet) → **production ACS**
5. Production send plus Delivery Report ingestion via Queue → **production ACS + Event Grid / Storage Queue**

| Mode | Intended use | provider | `live_sending` | bounce mode | Primary sources |
|------|--------------|----------|----------------|-------------|-----------------|
| local Mailpit | First delivery, local smoke | `mailpit` | `false` | `off` (default) | [Zero-Admin first-mail quickstart](first-mail-quickstart.en.md), [local Docker runbook](local-mailer-docker-runbook.en.md) |
| staging ACS no-send | Deploy-shaped start, token / migrate checks; no live send | `acs` (or as in JSON) | `false` | usually `off` | [local deploy rehearsal](local-deploy-rehearsal-runbook.en.md), [config README](../../config/mailer/README.en.md) |
| staging ACS verification | **Explicit** ACS / approved-sender validation | `acs` | `true` only during the validation (dedicated tenant / recipients) | usually `off` | [register-acs CLI](register-acs-cli-runbook.en.md), [config README](../../config/mailer/README.en.md), drill guide |
| production ACS | Production delivery | `acs` | `true` (approved only) | `off` allowed | [deploy `.env.example`](../../infra/deploy/.env.example), [register-acs CLI](register-acs-cli-runbook.en.md) |
| production ACS + Queue | Production delivery + hard-bounce suppression | `acs` | `true` | **`queue` only** | Above plus [bounce ingestion runbook](bounce-ingestion-runbook.en.md) |

## provider / `live_sending` / bounce mode

| Combination | Live email | Accept / persist | Notes |
|-------------|------------|------------------|-------|
| `mailpit` + `live_sending=false` | No (to Mailpit) | Yes | Local default; safe first check |
| `acs` + `live_sending=false` | **Does not send** | Yes (blocked by the live-send gate) | Staging no-send; may surface `LIVE_SENDING_DISABLED` |
| `acs` + `live_sending=true` | **Sends** | Yes | Requires approved sender + registered ACS secret |
| bounce `off` | — | — | v1.0-compatible default; no ingestion |
| bounce `queue` | — | — | v1.1.0 adopted path; Storage Queue Pull only |
| bounce `webhook` | — | — | **Not implemented (#304). Startup fails. Do not adopt** |

`MAILER_PROVIDER` / `Mailer__Provider` overrides provider for **all** tenants. Avoid unintended overrides ([config README](../../config/mailer/README.en.md)).

## Responsibility boundaries

| Component | Owns | Does not own |
|-----------|------|--------------|
| **ACS Email** | Accepting send operations; emitting Delivery Reports | Mailer DB suppression lists |
| **Event Grid** | Subscribing to ACS Delivery Reports and delivering them to a **Storage Queue** | HTTPS Push into Mailer (not used in v1.1.0) |
| **Storage Queue** | Temporary at-least-once event storage | Correlation, suppression, PII masking |
| **Mailer** | Accepting mail requests, Worker delivery, Queue Pull, correlation, `mail_suppressions`, Admin / metrics | Auto-creating Azure resources; forcing real bounces |

Keep **ACS and Queue separated per environment** (dev / staging / production). Mixing them can mis-correlate `provider_message_id` ([bounce runbook](bounce-ingestion-runbook.en.md)).

## Safety boundaries: local / staging / production

| | local | staging | production |
|--|-------|---------|------------|
| Live send | No (Mailpit) | Default no; verification only when explicit | Approved senders only |
| token / `tenant_id` | example / local-only | non-production only | production-only; never share with staging |
| ACS secret | local drill may use bare env (see runbook) | file secret (`register-acs`) | file secret only |
| Admin | optional, internal network | optional, reachability limits required | optional, reachability limits required (no direct internet exposure) |
| bounce Queue | usually unnecessary | usually unnecessary | mode 5 only; environment isolation required |
| Done means | health + first Mailpit delivery, etc. | start + preflight + optional explicit verification | delivery confirmation; **real bounce not required** |

## Shared checklist (information, access, secrets, network)

Confirm readiness only; do not write down secret values.

### Information

- [ ] Configuration mode (exactly one from the table above)
- [ ] Tenant JSON location (copy of an example; **do not commit** real files)
- [ ] Each tenant `token_env` name and where the matching environment variable is set
- [ ] Effective provider (tenant JSON or `MAILER_PROVIDER`)
- [ ] Intended `live_sending` (`false` / explicit `true`)
- [ ] Bounce mode (`off` or `queue`)
- [ ] Whether Admin / metrics / backup are enabled (defaults off or as in runbooks)

### Azure capabilities required (mode 2+, exact IAM role names follow your org)

- [ ] Can inspect the ACS Email resource and approved sender / domain
- [ ] (mode 5) Can subscribe Delivery Reports via Event Grid with a **Storage Queue** endpoint
- [ ] (mode 5) Can supply Queue credentials to Mailer (connection string or file)
- [ ] (modes 3–5) Can run `admin provider register-acs` on the deploy host (interactive TTY, secret directory permissions)

### Secrets (location only; never record values)

- [ ] Tenant Bearer token (environment variable; never plaintext in JSON)
- [ ] (ACS live) file secret via `ACS_CONNECTION_STRING_FILE`, or temporary local-drill env within runbook boundaries
- [ ] (mode 5) `MAILER_BOUNCE_QUEUE_CONNECTION_STRING` or `*_FILE` (never log)
- [ ] (metrics enabled) scrape bearer
- [ ] (Admin enabled) Admin secrets such as password hash

### Network / runtime

- [ ] Docker (local / rehearsal) or deploy-host compose networking
- [ ] Mailer HTTP (health / ready); Mailpit UI/API for local
- [ ] Production reachability boundary (reverse proxy / firewall; no direct Admin exposure)
- [ ] (mode 5) **Outbound** reachability from Mailer to Storage Queue (no public HTTPS ingress required)

## Execution order (all modes)

1. **Preflight** — choose mode, complete the checklist, validate tenant / env shape ([config README Preflight](../../config/mailer/README.en.md#preflight))
2. **Setup** — follow the mode’s primary runbooks to start / register
3. **Verification** — health / ready, accept, and mode-appropriate delivery or no-send checks using the result codes above
4. **Troubleshooting** — on FAIL / WARN, use “Where to look when something fails” below. No auto-repair (ACTION)

## One path per mode

### 1. local Mailpit

**Order**

1. Preflight: Docker running, ports free (quickstart prerequisites)
2. Setup / Verification: [Zero-Admin first-mail quickstart](first-mail-quickstart.en.md) (automated smoke: `scripts/local-first-mail-smoke.ps1` / `.sh`)
3. Extra smoke (idempotency, conflict, Admin, etc.): [local Mailer Docker runbook](local-mailer-docker-runbook.en.md) / [bash edition](local-mailer-docker-runbook-bash.en.md)

**Done when:** `[PASS]` for health / ready / first Mailpit delivery. ACS, bounce, and real bounces are not required.

### 2. staging ACS no-send

**Order**

1. Preflight: [config README](../../config/mailer/README.en.md) and shared-example tenants; keep `live_sending=false`
2. Setup: [local deploy rehearsal](local-deploy-rehearsal-runbook.en.md) (do not commit `infra/deploy` `.env` / `tenants.json`)
3. Verification: compose health, migrate, `/healthz` `/readyz`. Do not live-send (follow rehearsal guidance for optional no-send smoke)
4. Production ACS secret registration is not required yet; connectivity validation is mode 3

**Done when:** the stack is healthy / ready and no live mail left Mailpit/ACS unexpectedly.

### 3. staging ACS verification

**Prerequisite:** a mode-2-shaped deploy stack is running. Validation is **explicit only**.

**Order**

1. Preflight: dedicated tenant / recipients / approved sender; keep `live_sending=true` short-lived and scoped
2. Setup: [register-acs CLI runbook](register-acs-cli-runbook.en.md) (interactive only; never pass secrets as CLI arguments)
3. Verification: your org-approved drill / procedure (for example [mail-05a drill guide](drills/mail-05a-drill-guide.html)). A dedicated ACS send-check CLI is planned in [#426](https://github.com/kooiei-in4a/amane-mailer/issues/426)
4. After validation, decide whether to return staging to `live_sending=false` (do not leave a WARN-worthy state)

**Done when:** the explicit validation message is processed via ACS as expected. **A real bounce is not required.**

### 4. production ACS

**Order**

1. Preflight: production-only tokens / tenants; approved sender; metrics bearer as needed ([deploy `.env.example`](../../infra/deploy/.env.example))
2. Setup: deploy compose ([infra/deploy/compose.yml](../../infra/deploy/compose.yml)), [register-acs](register-acs-cli-runbook.en.md), backup settings if required (backup runbooks)
3. Verification: `/healthz` `/readyz`, accept/deliver on the approved path. For published-image smoke see [release-image-smoke](release-image-smoke.en.md) (use the then-current public release tag)
4. If bounce ingestion is not needed, stop here with bounce `off`

**Done when:** production delivery behaves as expected. Queue configuration is optional.

### 5. production ACS + Event Grid / Storage Queue

**Prerequisite:** mode 4 complete. Use a **v1.1.0-line image** (including migration `011`). While public `v1.1.0` is incomplete, published-image final verification remains pending.

**Order**

1. Preflight: ACS / Event Grid / Queue are **production-isolated**. Do not create a Push webhook
2. Setup (Azure): Delivery Report → Event Grid → **Storage Queue** (cloud-side ops are the detailed source of truth; Mailer only Pulls the Queue)
3. Setup (Mailer): set `MAILER_BOUNCE_INGESTION=queue` and Queue connection settings per the [bounce ingestion runbook](bounce-ingestion-runbook.en.md). If deploy templates do not wire every variable yet, pass them on the host using the env names in that runbook (this issue does not change compose)
4. Verification: Mailer starts; Queue poll-failure metrics are not steadily rising; follow Admin / metrics guidance. Read-only Event Grid / Queue checks and Delivery Report E2E are planned in [#427](https://github.com/kooiei-in4a/amane-mailer/issues/427) / [#428](https://github.com/kooiei-in4a/amane-mailer/issues/428)
5. **Do not require a real bounce to finish setup.** Confirming normal Delivery Report arrival is enough (#428 planned)

**Done when:** Mailer runs with bounce mode `queue` and can poll an environment-isolated Queue. Writing suppressions from a real bounce is optional ops confirmation, not required.

## Where to look when something fails

| Example symptom | See |
|-----------------|-----|
| tenant / token / `LIVE_SENDING_DISABLED` / missing provider config | [config README troubleshooting](../../config/mailer/README.en.md#tenant--env-troubleshooting) |
| local start / Admin / Mailpit | [local Docker runbook](local-mailer-docker-runbook.en.md) |
| deploy-shaped compose / migrate / network | [local deploy rehearsal](local-deploy-rehearsal-runbook.en.md) |
| ACS secret registration failure | [register-acs CLI](register-acs-cli-runbook.en.md) |
| bounce / unmatched / Queue poll | [bounce ingestion](bounce-ingestion-runbook.en.md), [metrics-and-alerts](metrics-and-alerts.en.md) |
| published image smoke | [release-image-smoke](release-image-smoke.en.md) |

## Planned verification helpers (not done yet)

| Issue | Planned |
|-------|---------|
| [#425](https://github.com/kooiei-in4a/amane-mailer/issues/425) | read-only setup doctor |
| [#426](https://github.com/kooiei-in4a/amane-mailer/issues/426) | ACS-only live send check CLI |
| [#427](https://github.com/kooiei-in4a/amane-mailer/issues/427) | read-only Event Grid / Storage Queue configuration check |
| [#428](https://github.com/kooiei-in4a/amane-mailer/issues/428) | Delivery Report Queue arrival E2E (message ID correlation; real bounce not required) |

Until those land, continue with existing preflight scripts, smokes, and manual runbook checks.

## Non-goals of this entry point

- Implementing setup CLI / doctor / Azure resource auto-creation
- Copying full existing runbooks into this file
- Documenting v1.2.0 Consumer bounce API / webhook contracts
- Adopting Event Grid Push (#304)
- Publishing real credentials, tenants, or private paths
