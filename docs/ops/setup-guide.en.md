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

[release-image-smoke](release-image-smoke.en.md) defaults to a currently published release tag (for example `v1.0.1`). Running it as-is does **not** verify v1.1.0.

### Configurations that cannot be completed today (honest boundaries)

The modes below are still useful as a **target taxonomy**, but they cannot be finished with only the current canonical deploy templates / CLIs. Do not treat them as completable setup paths yet.

| Gap | Current state | Mode availability | Diagnostic treatment |
|-----|---------------|-------------------|----------------------|
| Production ACS file-secret registration | `admin provider register-acs` accepts only the exact confirmation phrase **`Staging`**. Never tell a production operator to type `Staging` while doing production work (that destroys the safety check) | Mode 4 live send is **Blocked** (deploy-shaped prep remains Available) | Live-send completion is `[FAIL]` + `[ACTION]` waiting for a canonical procedure |
| Platform-owned sender | The same CLI also writes `platform-sender.json`, which is **not** used by the current tenant ACS send path | Do not treat it as evidence that tenant live send is ready | Not grounds for tenant live-send completion |
| Production ACS + Queue (mode 5) | [bounce ingestion runbook](bounce-ingestion-runbook.en.md) requires `MAILER_BOUNCE_INGESTION`, Queue credentials, and Queue name, but current [`infra/deploy/compose.yml`](../../infra/deploy/compose.yml) does **not** pass them in `environment` / volumes. Setting variables only in the host shell does not inject them into the container | **Target only** until deploy-template wiring exists | Completion is `[FAIL]` + `[ACTION]` waiting for compose wiring |

## Mode availability vs result codes (keep them separate)

Whether a configuration can be finished today (the mode-table column) is a different layer from diagnostic CLI result codes. Later setup doctor / verification CLIs ([#425](https://github.com/kooiei-in4a/amane-mailer/issues/425)–[#428](https://github.com/kooiei-in4a/amane-mailer/issues/428)) must use the result-code meanings below. Existing smoke scripts mainly emit `[PASS]` / `[FAIL]`.

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
| No production-safe secret registration path | Blocked (live send) | `[FAIL]` + `[ACTION]` |
| Bounce env / Queue secret not wired in compose | Target only | `[FAIL]` + `[ACTION]` |
| Queue poller runs but Event Grid arrival unconfirmed | (depends on mode) | `[WARN]` or `[ACTION]` |
| Published v1.1.0 image not verified | (depends on mode) | `[WARN]` or `[ACTION]` |

Do not include secret values, plaintext recipients, connection strings, or raw provider errors in results. Report only which setting key or capability is missing.

A quiet `mail_provider_queue_poll_failed_total` alone is **not** proof that Event Grid → Queue wiring works (the poller can run with no events arriving → `[WARN]` / `[ACTION]`).

## Choose a configuration mode

Answer these questions and pick **exactly one** mode.

1. Reach first delivery on Docker without sending real mail → **local Mailpit**
2. Bring up a deploy-shaped stack without ACS live send → **staging ACS no-send**
3. Explicitly validate ACS connectivity / sender on staging for a short window → **staging ACS verification**
4. Send production mail with an approved sender (bounce ingestion not required yet) → **production ACS** (current CLI cannot finish secret registration; see table)
5. Production send plus Delivery Report ingestion via Queue → **production ACS + Event Grid / Storage Queue** (**target**; current deploy template unsupported)

| Mode | Intended use | provider | `live_sending` | bounce mode | Completable with current sources? | Primary sources |
|------|--------------|----------|----------------|-------------|-----------------------------------|-----------------|
| local Mailpit | First delivery, local smoke | `mailpit` | `false` | `off` (default) | **Available** | [Zero-Admin first-mail quickstart](first-mail-quickstart.en.md), [local Docker runbook](local-mailer-docker-runbook.en.md) |
| staging ACS no-send | Deploy-shaped start, token / migrate checks; no live send | `acs` (or as in JSON) | `false` | usually `off` | **Available** (no live send) | [local deploy rehearsal](local-deploy-rehearsal-runbook.en.md), [config README](../../config/mailer/README.en.md) |
| staging ACS verification | **Explicit** ACS / approved-sender validation | `acs` | `true` only during the validation (dedicated tenant / recipients) | usually `off` | **Available** (Staging) | [register-acs CLI](register-acs-cli-runbook.en.md) (**Staging only**), [config README](../../config/mailer/README.en.md), drill guide |
| production ACS | Production delivery target | `acs` | `true` (approved only) | `off` allowed | Deploy shape / config **Available**; live send **Blocked** (no production-confirmed secret registration) | [deploy `.env.example`](../../infra/deploy/.env.example), [compose.yml](../../infra/deploy/compose.yml), [config README](../../config/mailer/README.en.md) |
| production ACS + Queue | Production delivery + hard-bounce suppression target | `acs` | `true` | **`queue` only** | **Target only** | Target setting names in [bounce ingestion runbook](bounce-ingestion-runbook.en.md); compose wiring needs separate work |

## provider / `live_sending` / bounce mode

| Combination | Live email | Accept / persist | Notes |
|-------------|------------|------------------|-------|
| `mailpit` + `live_sending=false` | No (to Mailpit) | Yes | Local default; safe first check |
| `acs` + `live_sending=false` | **Does not send** | Yes (blocked by the live-send gate) | Staging no-send; may surface `LIVE_SENDING_DISABLED` |
| `acs` + `live_sending=true` | **Sends** | Yes | Requires approved sender + registered ACS secret |
| bounce `off` | — | — | v1.0-compatible default; no ingestion |
| bounce `queue` | — | — | v1.1.0 adopted path; Storage Queue Pull only. **Runtime supports it; deploy compose does not wire it yet** |
| bounce `webhook` | — | — | **Not implemented (#304). Startup fails. Do not adopt** |

`MAILER_PROVIDER` / `Mailer__Provider` overrides provider for **all** tenants. Avoid unintended overrides ([config README](../../config/mailer/README.en.md)).

### Boundary between ACS secret and platform-owned sender

| Artifact | What it is for | Where it can be used today |
|----------|----------------|----------------------------|
| Tenant ACS delivery connection string (file) | File referenced by deploy `ACS_CONNECTION_STRING_FILE` | **Staging** can register via [register-acs CLI](register-acs-cli-runbook.en.md). **Production confirmation is not supported** |
| `platform-sender.json` | System Admin platform-owned sender identity | Written by the same CLI, but **unused by the current tenant send path**. Not evidence that tenant live send is ready |

Do not instruct production operators to type `Staging` into the confirmation prompt.

## Responsibility boundaries

| Component | Owns | Does not own |
|-----------|------|--------------|
| **ACS Email** | Accepting send operations; emitting Delivery Reports | Mailer DB suppression lists |
| **Event Grid** | Subscribing to ACS Delivery Reports and delivering them to a **Storage Queue** | HTTPS Push into Mailer (not used in v1.1.0) |
| **Storage Queue** | Temporary at-least-once event storage | Correlation, suppression, PII masking |
| **Mailer** | Accepting mail requests, Worker delivery, Queue Pull, correlation, `mail_suppressions`, Admin / metrics | Auto-creating Azure resources; forcing real bounces; silently injecting env vars missing from the deploy template |

Keep **ACS and Queue separated per environment** (dev / staging / production). Mixing them can mis-correlate `provider_message_id` ([bounce runbook](bounce-ingestion-runbook.en.md)).

## Safety boundaries: local / staging / production

| | local | staging | production |
|--|-------|---------|------------|
| Live send | No (Mailpit) | Default no; verification only when explicit | Approved only; production-confirmed secret registration is outside the current CLI |
| token / `tenant_id` | example / local-only | non-production only | production-only; never share with staging |
| ACS secret | local drill may use bare env (see runbook) | file secret (`register-acs`, confirmation `Staging`) | file secret required, but **current register-acs cannot confirm production** |
| Admin | optional, internal network | optional, reachability limits required | optional, reachability limits required (no direct internet exposure) |
| bounce Queue | usually unnecessary | [#427](https://github.com/kooiei-in4a/amane-mailer/issues/427) planned for per-environment read-only config checks. [#428](https://github.com/kooiei-in4a/amane-mailer/issues/428) is Staging E2E only | Target only; current compose unwired |
| Done means | health + first Mailpit delivery, etc. | start + preflight + optional explicit verification | deploy-shaped prep possible; **canonical live-send completion waits on the secret-registration gap**. Real bounce not required |

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
- [ ] (mode 3) Can run `admin provider register-acs` on the deploy host (interactive TTY, secret directory permissions, confirmation phrase `Staging`)
- [ ] (mode 5 target) Can subscribe Delivery Reports via Event Grid with a **Storage Queue** endpoint
- [ ] (mode 5 / Target only) Can pass Queue credentials **into the Mailer container via compose** (upstream `compose.yml` alone cannot → completion is `[FAIL]` + `[ACTION]`)

### Secrets (location only; never record values)

- [ ] Tenant Bearer token (environment variable; never plaintext in JSON)
- [ ] (Staging ACS live) file secret written by `register-acs` for `ACS_CONNECTION_STRING_FILE`
- [ ] (production ACS) file secret is required per [`.env.example`](../../infra/deploy/.env.example). **Production confirmation on the registration CLI is unsupported** → live-send completion is `[FAIL]` + `[ACTION]` (do not invent a workaround)
- [ ] (mode 5 / Target only) Queue connection string or file. If compose is unwired, the container cannot read host-only env → completion is `[FAIL]` + `[ACTION]`
- [ ] (metrics enabled) scrape bearer
- [ ] (Admin enabled) Admin secrets such as password hash

### Network / runtime

- [ ] Docker (local / rehearsal) or deploy-host compose networking
- [ ] Mailer HTTP (health / ready); Mailpit UI/API for local
- [ ] Production reachability boundary (reverse proxy / firewall; no direct Admin exposure)
- [ ] (mode 5 target) **Outbound** reachability from Mailer to Storage Queue (no public HTTPS ingress required)

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
| `production-acs` | Production deploy shape (live-send completion is Blocked) |
| `production-queue` | Production + Queue target (Target only) |

Output uses the result codes above (PASS / FAIL / WARN / ACTION) and ends with `Summary: PASS=… FAIL=… WARN=… ACTION=…`. Exit code `1` when any check is FAIL.

- Never prints secret values, tokens, connection strings, recipient plaintext, or raw provider errors
- Does not run DB migrate, start containers, live-send mail, or Azure configuration checks ([#427](https://github.com/kooiei-in4a/amane-mailer/issues/427))
- ACS directory write verification remains `admin provider check-acs-preflight` (doctor uses read-only safety checks only)
- Compose validation is suggested as **ACTION**: run `docker compose config --quiet` on the host

On deploy hosts, run the same command inside the Mailer image with your env / volume wiring (for example `docker compose run --rm mailer setup doctor --mode staging-no-send` — follow the canonical deploy service names and profiles).

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
2. Setup: [register-acs CLI runbook](register-acs-cli-runbook.en.md) (interactive only; never pass secrets as CLI arguments; confirmation phrase is **`Staging` only**, per that runbook)
3. Verification: your org-approved drill / procedure (for example [mail-05a drill guide](drills/mail-05a-drill-guide.html)). A dedicated ACS send-check CLI is planned in [#426](https://github.com/kooiei-in4a/amane-mailer/issues/426)
4. After validation, decide whether to return staging to `live_sending=false` (do not leave a WARN-worthy state)

**Done when:** the explicit validation message is processed via ACS as expected. **A real bounce is not required.** Presence of `platform-sender.json` is not evidence that tenant live send is complete.

### 4. production ACS

**Honest current scope:** You can prepare the deploy template and configuration. The current `register-acs` CLI is **Staging-confirmation only**, so it is not a production-safe file-secret registration path (and this guide will not tell you to type `Staging` as a workaround).

**Order**

1. Preflight: production-only tokens / tenants; approved sender; metrics bearer as needed ([deploy `.env.example`](../../infra/deploy/.env.example))
2. Setup (what you can do): prepare the host in the shape of deploy compose ([infra/deploy/compose.yml](../../infra/deploy/compose.yml)); align tenant JSON / tokens / metrics / Admin with the [config README](../../config/mailer/README.en.md)
3. Setup (backup, optional): [Backup operations](backup-operations.en.md), [Restore procedure](restore-procedure.en.md), [Restore verification](restore-verification.en.md)
4. Setup (ACS secret): compose expects `ACS_CONNECTION_STRING_FILE`. **There is no production-confirmed registration CLI / runbook this entry point can link today** → live send is **Blocked**; diagnosis is `[FAIL]` + `[ACTION]`. Do not reuse register-acs for production work
5. Verification (before the secret gap is closed): limit checks to `/healthz` `/readyz`, no-send, or accept-only paths that do **not** require live send. Published-image smoke: [release-image-smoke](release-image-smoke.en.md) (**default tag is a published release; it is not a v1.1.0 verification** → unpublished v1.1.0 verification is `[WARN]` / `[ACTION]`)
6. Even if bounce is not needed, canonical production live-send completion waits on the secret-registration gap

**Done when (today):** deploy shape, tenant / env preflight, and health/ready may be `[PASS]`. Production live-send completion is **Blocked**, so report `[FAIL]` + `[ACTION]` (do not call that state WARN / “usable with caveats”).

### 5. production ACS + Event Grid / Storage Queue (target)

**Hard limit:** In addition to the mode-4 gap, current [`infra/deploy/compose.yml`](../../infra/deploy/compose.yml) / [`.env.example`](../../infra/deploy/.env.example) do not pass bounce env vars or Queue secret mounts. Host-only variables are not injected into the container. **Do not complete this mode with the current templates alone.**

**Target understanding (needs runtime deploy wiring elsewhere)**

1. Keep ACS / Event Grid / Queue **production-isolated**. Do not create a Push webhook (#304)
2. Azure side: Delivery Report → Event Grid → **Storage Queue**
3. Mailer side: pass `MAILER_BOUNCE_INGESTION=queue`, Queue credentials, and Queue name from the [bounce ingestion runbook](bounce-ingestion-runbook.en.md) **through compose (or an approved override)**
4. Use a **v1.1.0-line image** (migration `011`) and re-verify against the published image after release

**Results you can assign today**

- Deploy template unwired → mode is **Target only**; completion is `[FAIL]` + `[ACTION]`
- Do not treat a quiet poll-failure metric as Event Grid wiring success (unconfirmed arrival is `[WARN]` / `[ACTION]`)
- [#427](https://github.com/kooiei-in4a/amane-mailer/issues/427) is planned as **read-only configuration checks for the selected environment** (including dev / staging / production). It is not Staging-only
- [#428](https://github.com/kooiei-in4a/amane-mailer/issues/428) is planned as a **Staging-only** Delivery Report E2E / pre-production wiring check. Do not treat #428 results as evidence that production was exercised. Production Queue execution and production test sends are non-goals of #428
- **Real bounce is not a completion criterion**

**Done when (today):** nowhere—Target only. Keep #427 per-environment config evidence, #428 Staging E2E evidence, and production completion separate.

## Where to look when something fails

| Example symptom | See |
|-----------------|-----|
| tenant / token / `LIVE_SENDING_DISABLED` / missing provider config | [config README troubleshooting](../../config/mailer/README.en.md#tenant--env-troubleshooting) |
| local start / Admin / Mailpit | [local Docker runbook](local-mailer-docker-runbook.en.md) |
| deploy-shaped compose / migrate / network | [local deploy rehearsal](local-deploy-rehearsal-runbook.en.md) |
| Staging ACS secret registration failure | [register-acs CLI](register-acs-cli-runbook.en.md) (Staging only) |
| bounce / unmatched / Queue poll (runtime description) | [bounce ingestion](bounce-ingestion-runbook.en.md), [metrics-and-alerts](metrics-and-alerts.en.md) |
| backup / restore | [Backup operations](backup-operations.en.md), [Restore procedure](restore-procedure.en.md), [Restore verification](restore-verification.en.md) |
| published image smoke (published tags) | [release-image-smoke](release-image-smoke.en.md) |

## Planned verification helpers (#425 excepted)

| Issue | Planned | Boundary |
|-------|---------|----------|
| [#425](https://github.com/kooiei-in4a/amane-mailer/issues/425) | read-only setup doctor | **Available** (see “setup doctor” above) |
| [#426](https://github.com/kooiei-in4a/amane-mailer/issues/426) | ACS-only live send check CLI | Staging-oriented plan (follow that issue) |
| [#427](https://github.com/kooiei-in4a/amane-mailer/issues/427) | read-only Event Grid / Storage Queue configuration check | **For the selected environment** (not Staging-only). Config check only; does not prove event arrival |
| [#428](https://github.com/kooiei-in4a/amane-mailer/issues/428) | Delivery Report Queue arrival E2E (message ID correlation; real bounce not required) | **Staging-only** pre-production wiring check. Production Queue / production test send are non-goals |

Use setup doctor (#425) together with existing preflight scripts, smokes, and manual runbook checks until #426–#428 land.

## Non-goals of this entry point

- Azure resource auto-creation
- Wiring bounce into deploy compose or extending register-acs for production (separate issues)
- Copying full existing runbooks into this file
- Documenting v1.2.0 Consumer bounce API / webhook contracts
- Adopting Event Grid Push (#304)
- Workarounds that ask production operators to type `Staging`
- Publishing real credentials, tenants, or private paths
