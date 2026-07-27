[日本語](setup-guide.md)

# Amane Mailer setup entry point

This is the **single entry point** for first-time Amane Mailer setup. Choose one configuration mode, gather what you need, then follow the existing runbooks in order.

This document is the source of truth for decisions, order, safety boundaries, and shared terminology. It does not copy detailed procedures; it links to each runbook and the config README.

Parent tracking: [#423](https://github.com/kooiei-in4a/amane-mailer/issues/423) · This issue: [#424](https://github.com/kooiei-in4a/amane-mailer/issues/424)

## Role of existing docs (do not duplicate)

| Document | Role | Relation to this entry |
|----------|------|------------------------|
| [README](../../README.en.md) | Repository front door | One click to reach this guide |
| [Zero-Admin first-mail quickstart](first-mail-quickstart.en.md) | Shortest **local Mailpit** path | Mode 1 procedure source of truth |
| [local Docker runbook](local-mailer-docker-runbook.en.md) ([bash](local-mailer-docker-runbook-bash.en.md)) | Extra local smoke (idempotency, Admin, etc.) | Mode 1 extension |
| [local deploy rehearsal](local-deploy-rehearsal-runbook.en.md) | Deploy-shaped stack rehearsal | Mode 2 procedure source of truth |
| [register-acs CLI](register-acs-cli-runbook.en.md) | ACS file-secret registration (exact `Staging` or `Production` confirmation) | Mode 3 uses `Staging`; mode 4 uses `Production`. Do not mix confirmation phrases |
| [test-acs-send CLI](test-acs-send-cli-runbook.en.md) | Staging-only ACS standalone live-send check | Mode 3 verification source |
| [bounce ingestion](bounce-ingestion-runbook.en.md) | Queue Pull runtime settings / operations | Mode 5 setting-name source of truth; pass via deploy compose |
| [event-grid config check](event-grid-config-check-runbook.en.md) | Read-only Event Grid / Queue configuration check | Per environment; does not prove arrival |
| [verify-delivery-report](verify-delivery-report-runbook.en.md) | Delivery Report Queue arrival E2E | **Staging only**. Not production evidence |
| [config README](../../config/mailer/README.en.md) | tenant / env / preflight | Config shape source for all modes |
| [release-image-smoke](release-image-smoke.en.md) | Published-image smoke | For published tags; not a `v1.1.0` check while that tag is missing |

## Before you start (safety)

- Do not paste secrets, connection strings, real tenant tokens, sender/recipient addresses, PII, or raw provider errors into docs, issues, logs, or chat.
- Use placeholders only (`replace-with-*`, `local-mail-service-token`).
- Event Grid **Push** webhooks ([#304](https://github.com/kooiei-in4a/amane-mailer/issues/304)) are **not** the v1.1.0 adopted transport. Do not follow Push as the setup path.
- The v1.1.0 bounce transport is **Storage Queue Pull only** (`MAILER_BOUNCE_INGESTION=queue`).
- **Generating a real bounce is not a normal setup completion criterion.**

### About the published v1.1.0 image

Bounce ingestion (including migration `011`) may already exist in source, but while the public GitHub release / GHCR tag `v1.1.0` is missing, treat **final verification against the published image as not done**. If you follow procedures with a local build or develop-derived artifact, record that in your ops notes. Re-verify against the published image after v1.1.0 release / publish / post-promote sync completes.

[release-image-smoke](release-image-smoke.en.md) defaults to a currently published release tag (for example `v1.0.1`). Running it as-is does **not** verify v1.1.0.

### Configurations that cannot be completed today (honest boundaries)

The item below remains for clarity, but it is **not** grounds that tenant live send is complete.

| Gap | Current state | Mode availability | Diagnostic treatment |
|-----|---------------|-------------------|----------------------|
| Platform-owned sender | `register-acs` also writes `platform-sender.json`, which is **not** used by the current tenant ACS send path | Do not treat it as evidence that tenant live send is ready | Not grounds for tenant live-send completion |

Production ACS (mode 4) file-secret registration is **Available** via `admin provider register-acs` with exact **`Production`** confirmation. Never tell a production operator to type `Staging` while doing production work: the CLI accepts it as a **staging** registration (not production evidence), and `setup doctor --mode production-acs` reports `[FAIL]` when `platform-sender.json` `environment` is `staging`.

Production ACS + Queue (mode 5) is **Available**: [`infra/deploy/compose.yml`](../../infra/deploy/compose.yml) / [`.env.example`](../../infra/deploy/.env.example) pass `MAILER_BOUNCE_INGESTION`, Queue name, and Queue connection (file) into the container. Host-shell-only variables still do not reach the container.

## Mode availability vs result codes (keep them separate)

Whether a configuration can be finished today (the mode-table column) is a different layer from diagnostic CLI result codes. Setup doctor / verification CLIs ([#425](https://github.com/kooiei-in4a/amane-mailer/issues/425)–[#428](https://github.com/kooiei-in4a/amane-mailer/issues/428)) use the result-code meanings below. Existing smoke scripts mainly emit `[PASS]` / `[FAIL]`.

### Mode availability (what the sources support today)

| Value | Meaning |
|-------|---------|
| **Available** | Completable with current canonical runbooks / CLIs / deploy templates |
| **Blocked** | Desired mode, but a required path is missing so it cannot be finished now |
| **Target only** | Taxonomy / target description only; do not mark complete with current templates |

### Result codes (diagnostic output)

| Code | Meaning | What to do next |
|------|---------|-----------------|
| **PASS** | Machine-verified; the check matches the intended configuration | Continue to the next check or mode |
| **FAIL** | An inconsistency that blocks setup progress, or a missing required precondition | Stop. Includes unblockable required gaps (not “usable with caveats”) |
| **WARN** | **Operable**, but a human must confirm or judge risk | Record and confirm manually. Do not use WARN for “cannot finish” |
| **ACTION** | Next safe human step (the tool will not auto-fix) | Follow the indicated steps. Do not invent missing procedures |

Examples:

| State | Mode availability | Diagnostic |
|-------|-------------------|------------|
| Production ACS secret not registered (including wrong confirmation phrase) | Available (procedure exists) | `[FAIL]` or `[ACTION]` (`Production` confirmation on register-acs) |
| Bounce mode / Queue secret / Queue name missing (mode 5) | Available (procedure exists) | `[FAIL]` or `[ACTION]` (settings via compose) |
| Queue poller runs but Event Grid arrival unconfirmed | (depends on mode) | `[WARN]` or `[ACTION]` |
| Published v1.1.0 image not verified | (depends on mode) | `[WARN]` or `[ACTION]` |

Do not include secret values, plaintext recipients, connection strings, or raw provider errors in results. Report only which setting key or capability is missing.

A quiet `mail_provider_queue_poll_failed_total` alone is **not** proof that Event Grid → Queue wiring works (the poller can run with no events arriving → `[WARN]` / `[ACTION]`).

## Choose a configuration mode

Answer these questions and pick **exactly one** mode.

1. Reach first delivery on Docker without sending real mail → **local Mailpit**
2. Bring up a deploy-shaped stack without ACS live send → **staging ACS no-send**
3. Explicitly validate ACS connectivity / sender on staging for a short window → **staging ACS verification**
4. Send production mail with an approved sender (bounce ingestion not required yet) → **production ACS**
5. Production send plus Delivery Report ingestion via Queue → **production ACS + Event Grid / Storage Queue**

| Mode | Intended use | provider | `live_sending` | bounce mode | Completable with current sources? | Primary sources |
|------|--------------|----------|----------------|-------------|-----------------------------------|-----------------|
| local Mailpit | First delivery, local smoke | `mailpit` | `false` | `off` (default) | **Available** | [Zero-Admin first-mail quickstart](first-mail-quickstart.en.md), [local Docker runbook](local-mailer-docker-runbook.en.md) |
| staging ACS no-send | Deploy-shaped start, token / migrate checks; no live send | `acs` (or as in JSON) | `false` | usually `off` | **Available** (no live send) | [local deploy rehearsal](local-deploy-rehearsal-runbook.en.md), [config README](../../config/mailer/README.en.md) |
| staging ACS verification | **Explicit** ACS / approved-sender validation | `acs` | `true` only during the validation (dedicated tenant / recipients) | usually `off` | **Available** (Staging) | [register-acs CLI](register-acs-cli-runbook.en.md) (confirmation **`Staging`**), [test-acs-send CLI](test-acs-send-cli-runbook.en.md), [config README](../../config/mailer/README.en.md) |
| production ACS | Production delivery | `acs` | `true` (approved only) | `off` allowed | **Available** | [register-acs CLI](register-acs-cli-runbook.en.md) (confirmation **`Production`**), [deploy `.env.example`](../../infra/deploy/.env.example), [compose.yml](../../infra/deploy/compose.yml), [config README](../../config/mailer/README.en.md) |
| production ACS + Queue | Production delivery + hard-bounce suppression | `acs` | `true` | **`queue` only** | **Available** | [bounce ingestion runbook](bounce-ingestion-runbook.en.md), [deploy `.env.example`](../../infra/deploy/.env.example), [compose.yml](../../infra/deploy/compose.yml), [register-acs CLI](register-acs-cli-runbook.en.md) (confirmation **`Production`**) |

## provider / `live_sending` / bounce mode

| Combination | Live email | Accept / persist | Notes |
|-------------|------------|------------------|-------|
| `mailpit` + `live_sending=false` | No (to Mailpit) | Yes | Local default; safe first check |
| `acs` + `live_sending=false` | **Does not send** | Yes (blocked by the live-send gate) | Staging no-send; may surface `LIVE_SENDING_DISABLED` |
| `acs` + `live_sending=true` | **Sends** | Yes | Requires approved sender + registered ACS secret |
| bounce `off` | — | — | v1.0-compatible default; no ingestion |
| bounce `queue` | — | — | v1.1.0 adopted path; Storage Queue Pull only. Pass settings through deploy compose |
| bounce `webhook` | — | — | **Not implemented (#304). Startup fails. Do not adopt** |

`MAILER_PROVIDER` / `Mailer__Provider` overrides provider for **all** tenants. Avoid unintended overrides ([config README](../../config/mailer/README.en.md)).

### Boundary between ACS secret and platform-owned sender

| Artifact | What it is for | Where it can be used today |
|----------|----------------|----------------------------|
| Tenant ACS delivery connection string (file) | File referenced by deploy `ACS_CONNECTION_STRING_FILE` | Register via [register-acs CLI](register-acs-cli-runbook.en.md) with exact **`Staging`** or **`Production`** confirmation |
| `platform-sender.json` | System Admin platform-owned sender identity | Written by the same CLI, but **unused by the current tenant send path**. Not evidence that tenant live send is ready |

Do not instruct production operators to type `Staging` into the confirmation prompt.

## Responsibility boundaries

| Component | Owns | Does not own |
|-----------|------|--------------|
| **ACS Email** | Accepting send operations; emitting Delivery Reports | Mailer DB suppression lists |
| **Event Grid** | Subscribing to ACS Delivery Reports and delivering them to a **Storage Queue** | HTTPS Push into Mailer (not used in v1.1.0) |
| **Storage Queue** | Temporary at-least-once event storage | Correlation, suppression, PII masking |
| **Mailer** | Accepting mail requests, Worker delivery, Queue Pull, correlation, `mail_suppressions`, Admin / metrics | Auto-creating Azure resources; forcing real bounces; treating host-shell-only env as container config |

Keep **ACS and Queue separated per environment** (dev / staging / production). Mixing them can mis-correlate `provider_message_id` ([bounce runbook](bounce-ingestion-runbook.en.md)).

## Safety boundaries: local / staging / production

| | local | staging | production |
|--|-------|---------|------------|
| Live send | No (Mailpit) | Default no; verification only when explicit | Approved only; `register-acs` with exact `Production` confirmation |
| token / `tenant_id` | example / local-only | non-production only | production-only; never share with staging |
| ACS secret | local drill may use bare env (see runbook) | file secret (`register-acs`, confirmation `Staging`) | file secret (`register-acs`, confirmation **`Production`**; never reuse `Staging`) |
| Admin | optional, internal network | optional, reachability limits required | optional, reachability limits required (no direct internet exposure) |
| bounce Queue | usually unnecessary | [#427](https://github.com/kooiei-in4a/amane-mailer/issues/427) `setup check-event-grid` for per-environment read-only config checks. [#428](https://github.com/kooiei-in4a/amane-mailer/issues/428) is Staging E2E only | Available; pass `queue` + Queue name + file secret via compose |
| Done means | health + first Mailpit delivery, etc. | start + preflight + optional explicit verification | deploy shape + production-confirmed secret registration + approved live send. Real bounce not required |

## Shared checklist (information, access, secrets, network)

Confirm readiness only; do not write down secret values.

### Information

- [ ] Configuration mode (exactly one from the table). For modes 4 / 5, acknowledge the gaps above
- [ ] Tenant JSON location (copy of an example; **do not commit** real files)
- [ ] Each tenant `token_env` name and where the matching environment variable is set
- [ ] Effective provider (tenant JSON or `MAILER_PROVIDER`)
- [ ] Intended `live_sending` (`false` / explicit `true`)
- [ ] Bounce mode (`off`, or `queue` as a target only)
- [ ] Whether Admin / metrics / backup are enabled (defaults off or as in runbooks)

### Azure capabilities required (mode 2+, exact IAM role names follow your org)

- [ ] Can inspect the ACS Email resource and approved sender / domain
- [ ] (mode 3) Can run `admin provider register-acs` on the deploy host (interactive TTY, secret directory permissions, confirmation phrase **`Staging`**)
- [ ] (mode 4) Can run the same CLI (confirmation phrase **`Production`**; do not type `Staging` for production work)
- [ ] (mode 5) Can subscribe Delivery Reports via Event Grid with a **Storage Queue** endpoint
- [ ] (mode 5) Can pass Queue credentials **into the Mailer container via compose** (`.env` + secret file mount; host shell alone is not enough)

### Secrets (location only; never record values)

- [ ] Tenant Bearer token (environment variable; never plaintext in JSON)
- [ ] (Staging ACS live) file secret written by `register-acs` (confirmation `Staging`) for `ACS_CONNECTION_STRING_FILE`
- [ ] (production ACS) file secret written by `register-acs` (confirmation **`Production`**) on the same path
- [ ] (mode 5) Place the Queue connection string at `${MAILER_BOUNCE_QUEUE_SECRET_HOST_PATH}/queue_connection_string` (do not record the value; compose mounts it as a file)
- [ ] (mode 5) Set `MAILER_BOUNCE_INGESTION=queue` and `MAILER_BOUNCE_QUEUE_NAME` in `.env`
- [ ] (metrics enabled) scrape bearer
- [ ] (Admin enabled) Admin secrets such as password hash

### Network / runtime

- [ ] Docker (local / rehearsal) or deploy-host compose networking
- [ ] Mailer HTTP (health / ready); Mailpit UI/API for local
- [ ] Production reachability boundary (reverse proxy / firewall; no direct Admin exposure)
- [ ] (mode 5) **Outbound** reachability from Mailer to Storage Queue (no public HTTPS ingress required)

## setup doctor (read-only diagnostics)

Before setup or after a failed start, run read-only diagnostics for local configuration and host prerequisites. The command does **not** change config files, the DB, containers, or Azure resources.

```bash
dotnet Amane.Mailer.dll setup doctor --mode <mode> [--compose-file <path>]
```

| `--mode` | Use case |
|----------|----------|
| `local-mailpit` | First local Mailpit reachability |
| `staging-no-send` | Deploy-shaped stack, no live send |
| `staging-verification` | Explicit Staging ACS validation |
| `production-acs` | Production deploy shape (`register-acs` uses **`Production`** confirmation) |
| `production-queue` | Production + Queue (`queue` settings via compose) |

Output uses the result codes above (PASS / FAIL / WARN / ACTION) and ends with `Summary: PASS=… FAIL=… WARN=… ACTION=…`. Exit code `1` when any check is FAIL.

- Never prints secret values, tokens, connection strings, recipient plaintext, or raw provider errors
- Does not run DB migrate, start containers, or live-send mail. Azure Event Grid / Queue configuration checks use the separate `setup check-event-grid` command ([#427](https://github.com/kooiei-in4a/amane-mailer/issues/427) / [event-grid-config-check-runbook.en.md](event-grid-config-check-runbook.en.md))
- ACS directory write verification remains `admin provider check-acs-preflight` (doctor uses read-only safety checks only)
- Compose validation is suggested as **ACTION**: run `docker compose config --quiet` on the host

On deploy hosts, prefer running setup doctor **on the host** (with the same env / compose files the containers will use) so Docker CLI and published host ports are meaningful. If you run the command inside the Mailer container, Docker availability and loopback port checks are reported as WARN / ACTION because they only reflect the container namespace.

## Execution order (all modes)

1. **Preflight** — choose mode, complete the checklist, run **setup doctor** (above), validate tenant / env shape ([config README Preflight](../../config/mailer/README.en.md#preflight))
2. **Setup** — follow the mode’s primary runbooks to start / register (do not force completion where gaps remain)
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
4. ACS secret registration is not required yet; connectivity validation is mode 3

**Done when:** the stack is healthy / ready and no live mail was sent.

### 3. staging ACS verification

**Prerequisite:** a mode-2-shaped deploy stack is running. Validation is **explicit only**. Scope is **Staging**.

**Order**

1. Preflight: dedicated tenant / recipients / approved sender; keep `live_sending=true` short-lived and scoped
2. Setup: [register-acs CLI runbook](register-acs-cli-runbook.en.md) (interactive only; never pass secrets as CLI arguments; mode 3 confirmation phrase is **`Staging` only**)
3. Setup doctor (re-run): `setup doctor --mode staging-verification`. Confirm `[PASS] platform_sender_environment` (expected `staging`). On mismatch, `[FAIL]` — do not proceed to live send
4. Verification: [ACS standalone live-send CLI](test-acs-send-cli-runbook.en.md) (`admin provider test-acs-send`; Staging + `MAILER-ACS-TEST-SEND`; does not go through Mailer API / Worker). Optional org drill: [mail-05a drill guide](drills/mail-05a-drill-guide.html)
5. After validation, decide whether to return staging to `live_sending=false` (do not leave a WARN-worthy state)

**Done when:** the explicit validation message is processed via ACS as expected. **A real bounce is not required.** Presence of `platform-sender.json` is not evidence that tenant live send is complete.

### 4. production ACS

**Scope:** In addition to the deploy template and configuration, `admin provider register-acs` with exact **`Production`** confirmation registers the file secret. Do not suggest typing `Staging` as a production workaround: `Staging` is accepted as a **staging** registration and is not production evidence; `setup doctor --mode production-acs` reports `[FAIL]` when `environment` mismatches.

**Order**

1. Preflight: production-only tokens / tenants; approved sender; metrics bearer as needed ([deploy `.env.example`](../../infra/deploy/.env.example))
2. Setup doctor (before registration): `setup doctor --mode production-acs` ([#425](https://github.com/kooiei-in4a/amane-mailer/issues/425)). Production registration guidance is `[ACTION] production_register_acs` (environment match is not evaluated yet because `platform-sender` does not exist)
3. Setup (stack): prepare the host in the shape of deploy compose ([infra/deploy/compose.yml](../../infra/deploy/compose.yml)); align tenant JSON / tokens / metrics / Admin with the [config README](../../config/mailer/README.en.md)
4. Setup (backup, optional): [Backup operations](backup-operations.en.md), [Restore procedure](restore-procedure.en.md), [Restore verification](restore-verification.en.md)
5. Setup (ACS secret): [register-acs CLI runbook](register-acs-cli-runbook.en.md) (confirmation phrase **`Production`**; never pass secrets as CLI arguments)
6. Setup doctor (re-run): `setup doctor --mode production-acs`. Confirm `[PASS] platform_sender_environment` (expected `production`) before live send. A `Staging` confirmation registration fails here
7. Verification: `/healthz` `/readyz`, and explicit live send with an approved sender. Published-image smoke: [release-image-smoke](release-image-smoke.en.md) (**default tag is a published release; it is not a v1.1.0 verification** → unpublished v1.1.0 verification is `[WARN]` / `[ACTION]`)
8. If bounce ingestion is needed, continue to mode 5 (otherwise you may stop here)

**Done when:** deploy shape, tenant / env preflight, `Production`-confirmed secret registration, post-registration doctor `platform_sender_environment` PASS, health/ready, and approved live send can be `[PASS]`. Unpublished `v1.1.0` image verification remains a soft residual (`[WARN]` / `[ACTION]`).

### 5. production ACS + Event Grid / Storage Queue

**Scope:** In addition to mode 4, [`infra/deploy/compose.yml`](../../infra/deploy/compose.yml) / [`.env.example`](../../infra/deploy/.env.example) pass bounce Queue settings into the Mailer container. Host-shell-only variables still do not reach the container. Do not create a Push webhook (#304).

**Order**

1. Preflight: same production-only tokens / tenants / approved sender as mode 4, plus production-isolated ACS / Event Grid / Storage Queue
2. Setup doctor (before registration): `setup doctor --mode production-queue` ([#425](https://github.com/kooiei-in4a/amane-mailer/issues/425))
3. Setup (stack + ACS): follow mode 4 (deploy compose, `Production` register-acs, doctor re-run)
4. Setup (bounce): follow [bounce ingestion runbook](bounce-ingestion-runbook.en.md); set `MAILER_BOUNCE_INGESTION=queue` and `MAILER_BOUNCE_QUEUE_NAME` in `.env`, and place the Queue connection string at `${MAILER_BOUNCE_QUEUE_SECRET_HOST_PATH}/queue_connection_string` (never pass secrets as CLI arguments)
5. Setup (Azure): Delivery Report → Event Grid → **Storage Queue** (not Push). Use `setup check-event-grid` ([#427](https://github.com/kooiei-in4a/amane-mailer/issues/427)) for a read-only configuration check
6. Setup doctor (re-run): `setup doctor --mode production-queue`. Confirm `[PASS] compose_bounce_wiring` / `mode_bounce_queue` / `bounce_queue`
7. Verification: `/healthz` `/readyz`, approved live send. Staging Delivery Report arrival is `setup verify-delivery-report` ([#428](https://github.com/kooiei-in4a/amane-mailer/issues/428)) — not production evidence. Unpublished `v1.1.0` verification remains `[WARN]` / `[ACTION]`

**How to score results**

- Do not treat a quiet poll-failure metric as Event Grid wiring success (unconfirmed arrival is `[WARN]` / `[ACTION]`)
- [#427](https://github.com/kooiei-in4a/amane-mailer/issues/427) provides **read-only configuration checks for the selected environment** (including dev / staging / production). It is not Staging-only
- [#428](https://github.com/kooiei-in4a/amane-mailer/issues/428) is **Staging-only**. Do not treat #428 results as evidence that production was exercised
- **Real bounce is not a completion criterion**

**Done when:** mode 4 completion plus compose-wired `queue` settings, Queue file secret, Queue name, and Event Grid → Queue configuration checks can be `[PASS]` / human-confirmed. Unpublished `v1.1.0` image verification remains a soft residual.

## Where to look when something fails

| Example symptom | See |
|-----------------|-----|
| tenant / token / `LIVE_SENDING_DISABLED` / missing provider config | [config README troubleshooting](../../config/mailer/README.en.md#tenant--env-troubleshooting), setup doctor in this guide |
| local start / Admin / Mailpit | [local Docker runbook](local-mailer-docker-runbook.en.md) |
| deploy-shaped compose / migrate / network | [local deploy rehearsal](local-deploy-rehearsal-runbook.en.md) |
| Staging / Production ACS secret registration failure | [register-acs CLI](register-acs-cli-runbook.en.md) (match confirmation phrase to the environment) |
| Staging ACS standalone send triage | [test-acs-send CLI](test-acs-send-cli-runbook.en.md) (Staging only) |
| Event Grid / Queue configuration mismatch | [event-grid config check](event-grid-config-check-runbook.en.md) (read-only) |
| Staging Delivery Report not arriving in Queue | [verify-delivery-report](verify-delivery-report-runbook.en.md) (Staging only; real bounce not required) |
| bounce / unmatched / Queue poll (runtime description) | [bounce ingestion](bounce-ingestion-runbook.en.md), [metrics-and-alerts](metrics-and-alerts.en.md) |
| backup / restore | [Backup operations](backup-operations.en.md), [Restore procedure](restore-procedure.en.md), [Restore verification](restore-verification.en.md) |
| published image smoke (published tags) | [release-image-smoke](release-image-smoke.en.md) |

## Verification helpers (availability)

| Issue | Capability | Boundary |
|-------|------------|----------|
| [#425](https://github.com/kooiei-in4a/amane-mailer/issues/425) | read-only setup doctor | **Available** (see “setup doctor” above) |
| [#426](https://github.com/kooiei-in4a/amane-mailer/issues/426) | ACS-only live send check CLI | **Available** — [test-acs-send-cli-runbook.en.md](test-acs-send-cli-runbook.en.md) (Staging only) |
| [#427](https://github.com/kooiei-in4a/amane-mailer/issues/427) | read-only Event Grid / Storage Queue configuration check (`setup check-event-grid`) | **Available** — [event-grid-config-check-runbook.en.md](event-grid-config-check-runbook.en.md) (selected environment; does not prove arrival) |
| [#428](https://github.com/kooiei-in4a/amane-mailer/issues/428) | Delivery Report Queue arrival E2E (message ID correlation; real bounce not required) | **Available** — [verify-delivery-report-runbook.en.md](verify-delivery-report-runbook.en.md) (**Staging only**. Production Queue / production test send are non-goals) |

For the setup entry point, use the CLIs above plus existing preflight / smoke / manual runbook checks.

## Non-goals of this entry point

- Azure resource auto-creation
- Copying full existing runbooks into this file
- Documenting v1.2.0 Consumer bounce API / webhook contracts
- Adopting Event Grid Push (#304)
- Workarounds that ask production operators to type `Staging`
- Publishing real credentials, tenants, or private paths
