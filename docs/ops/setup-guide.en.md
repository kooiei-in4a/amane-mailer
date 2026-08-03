[日本語](setup-guide.md)

# Amane Mailer setup entry point

This is the **single entry point** for first-time Amane Mailer setup. Choose Easy Setup (recommended), Manual Deployment, or Hardened Deployment; gather what you need; then follow the linked runbooks. This document is the source of truth for **judgment, order, and safety boundaries**. It does not copy detailed procedures or embed candidate-specific SHA / digest / checksum values.

Parent tracking: [#445](https://github.com/kooiei-in4a/amane-mailer/issues/445) · This issue: [#457](https://github.com/kooiei-in4a/amane-mailer/issues/457) · Design authority: [ADR 0021](../adr/0021-easy-setup-boundaries.md) ([#446](https://github.com/kooiei-in4a/amane-mailer/issues/446))

Use placeholders only (`replace-with-*`, `example.invalid`, synthetic UUIDs / paths). Do not paste real secrets, tokens, connection strings, sender/recipient addresses, PII, or private host paths into docs, issues, logs, or chat.

## Document roles

| Document | Role |
|----------|------|
| [README](../../README.en.md) / [README.ja](../../README.md) | Minimal repository front door → this guide |
| **This setup guide** | Judgment, path selection, order, safety boundaries (authority) |
| Ops runbooks under `docs/ops/` | Detailed procedures (link; do not copy full text here) |
| [ADR 0021](../adr/0021-easy-setup-boundaries.md) | Easy Setup design authority |
| [setup-release-bundle](setup-release-bundle.en.md) | Maintainer packaging / candidate handoff |
| [implementation-status](../implementation-status.json) | Tracked feature status (Easy Setup is `implemented` in v1.2.0) |
| [v1.2.0 release record](../releases/v1.2.0.md) | Published identities / digests / migrations / smoke evidence |
| Candidate `README-SETUP.md` | Minimal extract entry; links back to this guide at the candidate `sourceCommitSha` |

## Path selection

| Path | When to choose | Notes |
|------|----------------|-------|
| **Easy Setup (recommended)** | Windows Docker Desktop or Linux Docker Engine / VPS; modes 1–4 | Host `setup assistant` / optional non-interactive Main apply. Mode 5 is Manual. |
| **Manual Deployment** | You prefer existing runbooks / CLI without Managed bundles | Modes 1–5 remain available. Current published image is **v1.2.0** (prior v1.1.0 remains available) |
| **Hardened Deployment** | Strict file-secret / owner-only / no Managed metadata | Easy Setup assistant is **not** used. Manual contract foundation. |

---

## Easy Setup (recommended)

Easy Setup wraps existing `.env` / `tenants.json` / file-secret / deploy compose contracts with a host-local Web or terminal assistant ([ADR 0021](../adr/0021-easy-setup-boundaries.md)). It is **`implemented` in v1.2.0** ([#445](https://github.com/kooiei-in4a/amane-mailer/issues/445) / [#458](https://github.com/kooiei-in4a/amane-mailer/issues/458)). Use Manual paths when you prefer not to activate Managed bundles.

### Platform starts

| Environment | First command (from the extracted host bundle or install layout) |
|-------------|-------------------------------------------------------------------|
| Windows Docker Desktop | `Amane.Mailer.exe setup assistant` |
| Linux GUI + Docker Engine | `./Amane.Mailer setup assistant` |
| Headless Linux / VPS | `./Amane.Mailer setup assistant --no-browser` or `./Amane.Mailer setup assistant --terminal` |
| SSH to VPS | Prefer `--terminal`, or open an SSH tunnel to the assistant loopback port and use a local browser. **A browser alone on the VPS does not complete setup.** |
| Offline / GitHub unavailable | `Amane.Mailer setup assistant --help` then `--terminal` (Windows: `Amane.Mailer.exe`) |
| Non-interactive Main only | `Amane.Mailer setup apply --config <absolute-path> --non-interactive` |

Exact CLIs (do not invent alternatives):

```text
Amane.Mailer setup assistant [--port <n>] [--no-browser] [--terminal]
Amane.Mailer setup apply --config <absolute-path> --non-interactive
```

Optional `--port` selects the localhost Web listen port. `--no-browser` skips opening a browser. `--terminal` runs the interactive terminal UI.

### Candidate consumption (verify methods)

When consuming an Easy Setup **release-candidate** host bundle (not a published GitHub Release):

#### Release-candidate qualification (#456)

Verify methods only — **do not** treat any fixed digest in this guide as authoritative:

- Outer `CANDIDATE-SHA256SUMS` verifies the **archive** before / as you extract.
- Inner `FILES-SHA256SUMS` verifies **extracted files**.
- Read `release-bundle-manifest.json` for `sourceCommitSha`, image digest, and schema ranges.
- `payloadTreeSha256` in the manifest is a staged payload tree digest. It is **not** the archive checksum.
- If handoff materials disagree, **stop**. Handoff is qualification-only (maintainer #456), not a per-user “production verified” stamp.

Packaging maintainer steps: [setup-release-bundle](setup-release-bundle.en.md). Operator judgment stays in this guide.

#### Published release users

For published **v1.2.0**, use GitHub Release checksums / the [release record](../releases/v1.2.0.md) / the public image digest (<https://github.com/kooiei-in4a/amane-mailer/releases/tag/v1.2.0>). Do not confuse candidate handoff with published release verification. Host archives for Windows x64 / Linux x64 / Linux arm64 are attached.

### Managed boundaries

- Immutable configuration bundles; **`ACTIVE` is the sole activation authority** (`bundleId` + monotonic `activationGeneration`).
- **Configuration fingerprint** covers non-secret configuration identity only. **Bundle integrity** covers sealed secret-valued env and file secrets. Do not treat fingerprint match as full secret-inclusive bundle match.
- **Recorded** metadata vs **effective** runtime inspection are separate; metadata is not a send authority.
- Do **not** mix Managed Setup and Manual Deployment on the same root (no Managed `ACTIVE` / metadata alongside an ad-hoc Manual `.env` as dual authorities).
- Privileged host administrators who can rewrite seals and secrets together are **out of scope** for Easy Setup protections.

#### Managed backup boundaries

| Target | Treatment |
|--------|-----------|
| SQLite database | Obtain via the [Backup operations](backup-operations.en.md) path **separately**. Config rollback does not restore it |
| Managed root | Preserve `bundles` / `state` (`ACTIVE`) / `verification` / `sealing` as **one generation** |
| External / manual-only | Data path, backup settings, rclone config, etc. stay **outside** Managed switching and are managed separately |
| Docs / logs | Never include secret values or private host paths |

Do not copy full procedures here: [Backup operations](backup-operations.en.md), [Restore procedure](restore-procedure.en.md), [Restore verification](restore-verification.en.md).

#### Managed failure / recovery

| Situation | Treatment |
|-----------|-----------|
| Previous `ACTIVE` present | Atomic switch back → recreate containers → fingerprint / integrity / verification must succeed before calling rollback successful |
| Previous `ACTIVE` absent | **FreshFailed** — do not present as a successful rollback |
| Lock / `TX.stamp` / incomplete `ACTIVE` / FINALIZED mismatch | Not success; recovery or manual intervention |
| Migration, Admin SQLite, mail data, provider side effects | **Out of config rollback scope** |
| `docker compose down -v` / DB migration rollback | **Do not guide** |

#### Secret detection scope and limits

| Contract | Meaning |
|----------|---------|
| Non-secret fingerprint | Does **not** include secret values |
| Secrets in a finalized Managed bundle | Integrity-seal targets (values stay off public surfaces) |
| Wrong mount / substitution | Runtime may detect via mount attestation |
| Fingerprint match alone | Does **not** mean full secret-inclusive bundle match |
| Privileged host rewriting seal + secrets together | Out of Easy Setup protection scope |

### Deployment states

| State | Meaning |
|-------|---------|
| **Configuration applied** | Managed bundle committed via `ACTIVE` |
| **Send-ready** | Applied bundle meets send-ready conditions (effective / doctor / readiness / fingerprint / integrity / verification record aligned) |
| **Deployment operational verification** | Operator proved live send via the normal Mailer path. **Not recorded by Easy Setup** — Manual verification is required if you need it |
| **Release Production operational verification** | Maintainer #456 product qualification. **Not** a per-user environment status |

Staging test vs Production: keep ACS / Queue / tokens separated per environment. Do not treat Staging drills as Production evidence.

### Admin (optional; default disabled)

- Admin enablement is **optional** and **default disabled**. It is an independent transaction **after** Main setup succeeds.
- Bootstrap only via interactive Web or terminal assistant — **not** non-interactive.
- Non-interactive Main apply keeps Admin disabled. If non-interactive input requests Admin enablement → **FAIL** (do not silently ignore); use the interactive assistant.
- Do not accept plaintext passwords via file, redirected stdin, or CLI arguments. Password hash file input is out of scope for v1.2.0.
- Supported DB states: **fresh** and **managed same-user** reapply. Existing Manual / unsupported Admin state → Manual path.
- Config bundle rollback ≠ SQLite Admin state rollback (`admin_config` / `admin_users` / sessions may remain).
- Bootstrap success includes login and `/admin/setup-status` display. Admin setup status does **not** run doctor, test send, or Docker operations.

#### Admin access profiles

| Profile | Conditions (summary) |
|---------|----------------------|
| **Local Development** | `ASPNETCORE_ENVIRONMENT=Development`; loopback-only host publish; `AMANE_ADMIN_ALLOW_HTTP=true`; localhost access; `Connection.LocalIpAddress` matches `AMANE_ADMIN_ALLOWED_LOCAL_ADDRESS` |
| **Production HTTPS** | Approved HTTPS reverse proxy already exists; `AMANE_ADMIN_ALLOW_HTTP=false`; Secure / `__Host-` cookies; server-side local address matches allowed local address; no direct internet Admin exposure |

When the reverse proxy terminates TLS and forwards plain HTTP to Mailer, set `ASPNETCORE_FORWARDEDHEADERS_ENABLED=true` in compose / `external.env` so `X-Forwarded-Proto` makes Admin antiforgery treat the request as HTTPS. Enable only behind that trusted proxy boundary.

`AMANE_ADMIN_ALLOWED_LOCAL_ADDRESS` matches **`Connection.LocalIpAddress`** (server-side), **not** the client source IP.

Easy Setup does **not** build reverse proxies, certificates, or DNS. If no HTTPS Production path exists, **keep Admin disabled**. Main setup can still succeed.

### Modes, support matrix, setup ≠ upgrade

| Modes 1–4 | Easy Setup formal targets |
|-----------|---------------------------|
| Mode 5 (production ACS + Event Grid / Storage Queue) | **Manual / not Easy Setup** |
| Windows Docker Desktop / Linux Docker Engine / VPS | Formal |
| NAS | Best-effort |
| Remote Docker / Kubernetes / Podman / macOS formal distribution | Out of scope |
| Consumer bounced Webhook [#307](https://github.com/kooiei-in4a/amane-mailer/issues/307) | Out of v1.2.0 (v1.5.0+) |

**Setup is not upgrade.** Easy Setup targets first-time / managed setup. Product upgrades for existing Manual / Hardened deployments pull the published image and apply SQLite migrations on the normal runtime path (not a silent Admin re-bootstrap).

**v1.1.0 → v1.2.0 DB migrations (INCLUDE):** take a backup first; the runtime applies (omission / `none` is not allowed):

- `012_provider_event_inbox_details.sql`
- `013_provider_queue_dead_letters.sql`

Identities: [docs/releases/v1.2.0.md](../releases/v1.2.0.md).

### Backup / rollback / recovery (high level)

- Prefer the Managed backup / failure-recovery / secret-detection tables above.
- Prefer documented backup of DB and operator-owned secrets / config; exclude what runbooks exclude.
- Do **not** use `docker compose down -v` as a casual rollback (destroys volumes).
- Do **not** treat DB migration rollback as a supported Easy Setup recovery.
- Details: [Backup operations](backup-operations.en.md), [Restore procedure](restore-procedure.en.md), [Restore verification](restore-verification.en.md).

### Easy Setup troubleshooting pointers

- Assistant will not start / bind: confirm host binary, Docker Desktop/Engine local context, loopback-only expectations.
- VPS “browser only” attempts: switch to `--terminal` or SSH tunnel.
- Non-interactive Admin enable request: expected **FAIL** — use interactive assistant.
- Fingerprint match but secrets wrong / remounted: integrity / mount attestation may still fail; fingerprint alone is insufficient.
- More Manual failure pointers: see [Troubleshooting](#troubleshooting) below.

### Doc defect return (#456 → #457)

If qualification (#456) finds a documentation defect in this guide or candidate `README-SETUP.md`:

1. Fix in [#457](https://github.com/kooiei-in4a/amane-mailer/issues/457) (this document / packaging generator).
2. Regenerate the candidate from the **new merge SHA**.
3. Re-run affected qualification scenarios.
4. Do **not** paste the #456 Hard gate table into this guide (#456 owns that table).

---

## Manual Deployment

Manual Deployment remains a first-class path. The sections below keep the mode 1–5 runbook order and availability meanings. **The current recommended published image is v1.2.0.** Feature-boundary notes that originated in v1.1.0 (for example bounce Queue adoption) remain as historical facts.

Container one-shot effective inspection (`Amane.Mailer setup inspect-effective --format json`, [#447](https://github.com/kooiei-in4a/amane-mailer/issues/447)) is implemented for Managed hosts. stdout is JSON only. recorded / effective / mountAttestation are separate; the one-shot never claims final `bundleIntegrity=matched` by itself. Host assistant / ACTIVE apply do not delete these Manual procedures.

### Role of existing Manual docs (do not duplicate)

| Document | Role | Relation to Manual entry |
|----------|------|------------------------|
| [Zero-Admin first-mail quickstart](first-mail-quickstart.en.md) | Shortest **local Mailpit** path | Mode 1 procedure source of truth |
| [local Docker runbook](local-mailer-docker-runbook.en.md) ([bash](local-mailer-docker-runbook-bash.en.md)) | Extra local smoke (idempotency, Admin, etc.) | Mode 1 extension |
| [local deploy rehearsal](local-deploy-rehearsal-runbook.en.md) | Deploy-shaped stack rehearsal | Mode 2 procedure source of truth |
| [register-acs CLI](register-acs-cli-runbook.en.md) | ACS file-secret registration (exact `Staging` or `Production` confirmation) | Mode 3 uses `Staging`; mode 4 uses `Production`. Do not mix confirmation phrases |
| [test-acs-send CLI](test-acs-send-cli-runbook.en.md) | Staging-only ACS standalone live-send check | Mode 3 verification source |
| [bounce ingestion](bounce-ingestion-runbook.en.md) | Queue Pull runtime settings / operations | Mode 5 setting-name source of truth; pass via deploy compose |
| [event-grid config check](event-grid-config-check-runbook.en.md) | Read-only Event Grid / Queue configuration check | Per environment; does not prove arrival |
| [verify-delivery-report](verify-delivery-report-runbook.en.md) | Delivery Report Queue arrival E2E | **Staging only**. Not production evidence |
| [config README](../../config/mailer/README.en.md) | tenant / env / preflight | Config shape source for all modes |
| [release-image-smoke](release-image-smoke.en.md) | Published-image smoke | For published tags; default is `v1.2.0` |

### Before you start (safety)

- Do not paste secrets, connection strings, real tenant tokens, sender/recipient addresses, PII, or raw provider errors into docs, issues, logs, or chat.
- Use placeholders only (`replace-with-*`, `local-mail-service-token`).
- Event Grid **Push** webhooks ([#304](https://github.com/kooiei-in4a/amane-mailer/issues/304)) are **not** the v1.1.0 adopted transport. Do not follow Push as the setup path.
- The v1.1.0 bounce transport is **Storage Queue Pull only** (`MAILER_BOUNCE_INGESTION=queue`).
- **Generating a real bounce is not a normal setup completion criterion.**

### About the published image (current v1.2.0)

**Current recommendation:** public GitHub release / GHCR tag `v1.2.0` for both Easy Setup and Manual paths.
Evidence: [docs/releases/v1.2.0.md](../releases/v1.2.0.md) (including release-image smoke) and
<https://github.com/kooiei-in4a/amane-mailer/releases/tag/v1.2.0>.

When upgrading from a v1.1.0 deployment, take a DB backup before pulling the image. Expect runtime
migrations `012_provider_event_inbox_details.sql` and `013_provider_queue_dead_letters.sql`
(INCLUDE; omission is not allowed).

**Prior release:** `v1.1.0` (through migration `011`) evidence remains in
[docs/releases/v1.1.0.md](../releases/v1.1.0.md).
If you follow procedures with a local build or develop-derived artifact, record that in your ops notes.

[release-image-smoke](release-image-smoke.en.md) defaults to the published release tag (`v1.2.0`).

### Configurations that cannot be completed today (honest boundaries)

The item below remains for clarity, but it is **not** grounds that tenant live send is complete.

| Gap | Current state | Mode availability | Diagnostic treatment |
|-----|---------------|-------------------|----------------------|
| Platform-owned sender | `register-acs` also writes `platform-sender.json`, which is **not** used by the current tenant ACS send path | Do not treat it as evidence that tenant live send is ready | Not grounds for tenant live-send completion |

Production ACS (mode 4) file-secret registration is **Available** via `admin provider register-acs` with exact **`Production`** confirmation. Never tell a production operator to type `Staging` while doing production work: the CLI accepts it as a **staging** registration (not production evidence), and `setup doctor --mode production-acs` reports `[FAIL]` when `platform-sender.json` `environment` is `staging`.

Production ACS + Queue (mode 5) is **Available**: [`infra/deploy/compose.yml`](../../infra/deploy/compose.yml) / [`.env.example`](../../infra/deploy/.env.example) pass `MAILER_BOUNCE_INGESTION`, Queue name, and Queue connection (file) into the container. Host-shell-only variables still do not reach the container.

### Mode availability vs result codes (keep them separate)

Whether a configuration can be finished today (the mode-table column) is a different layer from diagnostic CLI result codes. Setup doctor / verification CLIs ([#425](https://github.com/kooiei-in4a/amane-mailer/issues/425)–[#428](https://github.com/kooiei-in4a/amane-mailer/issues/428)) use the result-code meanings below. Existing smoke scripts mainly emit `[PASS]` / `[FAIL]`.

#### Mode availability (what the sources support today)

| Value | Meaning |
|-------|---------|
| **Available** | Completable with current canonical runbooks / CLIs / deploy templates |
| **Blocked** | Desired mode, but a required path is missing so it cannot be finished now |
| **Target only** | Taxonomy / target description only; do not mark complete with current templates |

#### Result codes (diagnostic output)

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
| Published v1.2.0 image not verified | (depends on mode) | See the [v1.2.0 release record](../releases/v1.2.0.md). Hosts not yet on that image: `[WARN]` / `[ACTION]` |

Do not include secret values, plaintext recipients, connection strings, or raw provider errors in results. Report only which setting key or capability is missing.

A quiet `mail_provider_queue_poll_failed_total` alone is **not** proof that Event Grid → Queue wiring works (the poller can run with no events arriving → `[WARN]` / `[ACTION]`).

### Choose a configuration mode

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

### provider / `live_sending` / bounce mode

| Combination | Live email | Accept / persist | Notes |
|-------------|------------|------------------|-------|
| `mailpit` + `live_sending=false` | No (to Mailpit) | Yes | Local default; safe first check |
| `acs` + `live_sending=false` | **Does not send** | Yes (blocked by the live-send gate) | Staging no-send; may surface `LIVE_SENDING_DISABLED` |
| `acs` + `live_sending=true` | **Sends** | Yes | Requires approved sender + registered ACS secret |
| bounce `off` | — | — | v1.0-compatible default; no ingestion |
| bounce `queue` | — | — | v1.1.0 adopted path; Storage Queue Pull only. Pass settings through deploy compose |
| bounce `webhook` | — | — | **Not implemented (#304). Startup fails. Do not adopt** |

`MAILER_PROVIDER` / `Mailer__Provider` overrides provider for **all** tenants. Avoid unintended overrides ([config README](../../config/mailer/README.en.md)).

#### Boundary between ACS secret and platform-owned sender

| Artifact | What it is for | Where it can be used today |
|----------|----------------|----------------------------|
| Tenant ACS delivery connection string (file) | File referenced by deploy `ACS_CONNECTION_STRING_FILE` | Register via [register-acs CLI](register-acs-cli-runbook.en.md) with exact **`Staging`** or **`Production`** confirmation |
| `platform-sender.json` | System Admin platform-owned sender identity | Written by the same CLI, but **unused by the current tenant send path**. Not evidence that tenant live send is ready |

Do not instruct production operators to type `Staging` into the confirmation prompt.

### Responsibility boundaries

| Component | Owns | Does not own |
|-----------|------|--------------|
| **ACS Email** | Accepting send operations; emitting Delivery Reports | Mailer DB suppression lists |
| **Event Grid** | Subscribing to ACS Delivery Reports and delivering them to a **Storage Queue** | HTTPS Push into Mailer (not used in v1.1.0) |
| **Storage Queue** | Temporary at-least-once event storage | Correlation, suppression, PII masking |
| **Mailer** | Accepting mail requests, Worker delivery, Queue Pull, correlation, `mail_suppressions`, Admin / metrics | Auto-creating Azure resources; forcing real bounces; treating host-shell-only env as container config |

Keep **ACS and Queue separated per environment** (dev / staging / production). Mixing them can mis-correlate `provider_message_id` ([bounce runbook](bounce-ingestion-runbook.en.md)).

### Safety boundaries: local / staging / production

| | local | staging | production |
|--|-------|---------|------------|
| Live send | No (Mailpit) | Default no; verification only when explicit | Approved only; `register-acs` with exact `Production` confirmation |
| token / `tenant_id` | example / local-only | non-production only | production-only; never share with staging |
| ACS secret | local drill may use bare env (see runbook) | file secret (`register-acs`, confirmation `Staging`) | file secret (`register-acs`, confirmation **`Production`**; never reuse `Staging`) |
| Admin | optional, internal network | optional, reachability limits required | optional, reachability limits required (no direct internet exposure) |
| bounce Queue | usually unnecessary | [#427](https://github.com/kooiei-in4a/amane-mailer/issues/427) `setup check-event-grid` for per-environment read-only config checks. [#428](https://github.com/kooiei-in4a/amane-mailer/issues/428) is Staging E2E only | Available; pass `queue` + Queue name + file secret via compose |
| Done means | health + first Mailpit delivery, etc. | start + preflight + optional explicit verification | deploy shape + production-confirmed secret registration + approved live send. Real bounce not required |

### Shared checklist (information, access, secrets, network)

Confirm readiness only; do not write down secret values.

#### Information

- [ ] Configuration mode (exactly one from the table). For modes 4 / 5, acknowledge production-specific safety boundaries (dedicated tokens / ACS·Queue isolation, no Push). Treat published image `v1.2.0` as canonical ([release record](../releases/v1.2.0.md))
- [ ] Tenant JSON location (copy of an example; **do not commit** real files)
- [ ] Each tenant `token_env` name and where the matching environment variable is set
- [ ] Effective provider (tenant JSON or `MAILER_PROVIDER`)
- [ ] Intended `live_sending` (`false` / explicit `true`)
- [ ] Bounce mode (`off` or `queue`)
- [ ] Whether Admin / metrics / backup are enabled (defaults off or as in runbooks)

#### Azure capabilities required (mode 2+, exact IAM role names follow your org)

- [ ] Can inspect the ACS Email resource and approved sender / domain
- [ ] (mode 3) Can run `admin provider register-acs` on the deploy host (interactive TTY, secret directory permissions, confirmation phrase **`Staging`**)
- [ ] (mode 4) Can run the same CLI (confirmation phrase **`Production`**; do not type `Staging` for production work)
- [ ] (mode 5) Can subscribe Delivery Reports via Event Grid with a **Storage Queue** endpoint
- [ ] (mode 5) Can pass Queue credentials **into the Mailer container via compose** (`.env` + secret file mount; host shell alone is not enough)

#### Secrets (location only; never record values)

- [ ] Tenant Bearer token (environment variable; never plaintext in JSON)
- [ ] (Staging ACS live) file secret written by `register-acs` (confirmation `Staging`) for `ACS_CONNECTION_STRING_FILE`
- [ ] (production ACS) file secret written by `register-acs` (confirmation **`Production`**) on the same path
- [ ] (mode 5) Place the Queue connection string at `${MAILER_BOUNCE_QUEUE_SECRET_HOST_PATH}/queue_connection_string` (do not record the value; compose mounts it as a file)
- [ ] (mode 5) Set `MAILER_BOUNCE_INGESTION=queue` and `MAILER_BOUNCE_QUEUE_NAME` in `.env`
- [ ] (metrics enabled) scrape bearer
- [ ] (Admin enabled) Admin secrets such as password hash

#### Network / runtime

- [ ] Docker (local / rehearsal) or deploy-host compose networking
- [ ] Mailer HTTP (health / ready); Mailpit UI/API for local
- [ ] Production reachability boundary (reverse proxy / firewall; no direct Admin exposure)
- [ ] (mode 5) **Outbound** reachability from Mailer to Storage Queue (no public HTTPS ingress required)

### setup doctor (read-only diagnostics)

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

### Execution order (all modes)

1. **Preflight** — choose mode, complete the checklist, run **setup doctor** (above), validate tenant / env shape ([config README Preflight](../../config/mailer/README.en.md#preflight))
2. **Setup** — follow the mode’s primary runbooks to start / register (do not force completion where gaps remain)
3. **Verification** — health / ready, accept, and mode-appropriate delivery or no-send checks using the result codes above
4. **Troubleshooting** — on FAIL / WARN, use [Troubleshooting](#troubleshooting) below. No auto-repair (ACTION)

### One path per mode

#### 1. local Mailpit

**Order**

1. Preflight: Docker running, ports free (quickstart prerequisites)
2. Setup / Verification: [Zero-Admin first-mail quickstart](first-mail-quickstart.en.md) (automated smoke: `scripts/local-first-mail-smoke.ps1` / `.sh`)
3. Extra smoke (idempotency, conflict, Admin, etc.): [local Mailer Docker runbook](local-mailer-docker-runbook.en.md) / [bash edition](local-mailer-docker-runbook-bash.en.md)

**Done when:** `[PASS]` for health / ready / first Mailpit delivery. ACS, bounce, and real bounces are not required.

#### 2. staging ACS no-send

**Order**

1. Preflight: [config README](../../config/mailer/README.en.md) and shared-example tenants; keep `live_sending=false`
2. Setup: [local deploy rehearsal](local-deploy-rehearsal-runbook.en.md) (do not commit `infra/deploy` `.env` / `tenants.json`)
3. Verification: compose health, migrate, `/healthz` `/readyz`. Do not live-send (follow rehearsal guidance for optional no-send smoke)
4. ACS secret registration is not required yet; connectivity validation is mode 3

**Done when:** the stack is healthy / ready and no live mail was sent.

#### 3. staging ACS verification

**Prerequisite:** a mode-2-shaped deploy stack is running. Validation is **explicit only**. Scope is **Staging**.

**Order**

1. Preflight: dedicated tenant / recipients / approved sender; keep `live_sending=true` short-lived and scoped
2. Setup: [register-acs CLI runbook](register-acs-cli-runbook.en.md) (interactive only; never pass secrets as CLI arguments; mode 3 confirmation phrase is **`Staging` only**)
3. Setup doctor (re-run): `setup doctor --mode staging-verification`. Confirm `[PASS] platform_sender_environment` (expected `staging`). On mismatch, `[FAIL]` — do not proceed to live send
4. Verification: [ACS standalone live-send CLI](test-acs-send-cli-runbook.en.md) (`admin provider test-acs-send`; Staging + `MAILER-ACS-TEST-SEND`; does not go through Mailer API / Worker). Optional org drill: [mail-05a drill guide](drills/mail-05a-drill-guide.html)
5. After validation, decide whether to return staging to `live_sending=false` (do not leave a WARN-worthy state)

**Done when:** the explicit validation message is processed via ACS as expected. **A real bounce is not required.** Presence of `platform-sender.json` is not evidence that tenant live send is complete.

#### 4. production ACS

**Scope:** In addition to the deploy template and configuration, `admin provider register-acs` with exact **`Production`** confirmation registers the file secret. Do not suggest typing `Staging` as a production workaround: `Staging` is accepted as a **staging** registration and is not production evidence; `setup doctor --mode production-acs` reports `[FAIL]` when `environment` mismatches.

**Order**

1. Preflight: production-only tokens / tenants; approved sender; metrics bearer as needed ([deploy `.env.example`](../../infra/deploy/.env.example))
2. Setup doctor (before registration): `setup doctor --mode production-acs` ([#425](https://github.com/kooiei-in4a/amane-mailer/issues/425)). Production registration guidance is `[ACTION] production_register_acs` (environment match is not evaluated yet because `platform-sender` does not exist)
3. Setup (stack): prepare the host in the shape of deploy compose ([infra/deploy/compose.yml](../../infra/deploy/compose.yml)); align tenant JSON / tokens / metrics / Admin with the [config README](../../config/mailer/README.en.md)
4. Setup (backup, optional): [Backup operations](backup-operations.en.md), [Restore procedure](restore-procedure.en.md), [Restore verification](restore-verification.en.md)
5. Setup (ACS secret): [register-acs CLI runbook](register-acs-cli-runbook.en.md) (confirmation phrase **`Production`**; never pass secrets as CLI arguments)
6. Setup doctor (re-run): `setup doctor --mode production-acs`. Confirm `[PASS] platform_sender_environment` (expected `production`) before live send. A `Staging` confirmation registration fails here
7. Verification: `/healthz` `/readyz`, and explicit live send with an approved sender. Published-image smoke: [release-image-smoke](release-image-smoke.en.md) (default tag `v1.2.0`; evidence in the [v1.2.0 release record](../releases/v1.2.0.md))
8. If bounce ingestion is needed, continue to mode 5 (otherwise you may stop here)

**Done when:** deploy shape, tenant / env preflight, `Production`-confirmed secret registration, post-registration doctor `platform_sender_environment` PASS, health/ready, and approved live send can be `[PASS]`. Published image is `v1.2.0` ([release record](../releases/v1.2.0.md)).

#### 5. production ACS + Event Grid / Storage Queue

**Scope:** In addition to mode 4, [`infra/deploy/compose.yml`](../../infra/deploy/compose.yml) / [`.env.example`](../../infra/deploy/.env.example) pass bounce Queue settings into the Mailer container. Host-shell-only variables still do not reach the container. Do not create a Push webhook (#304). This mode is **Manual** for Easy Setup (not assistant-automated).

**Order**

1. Preflight: same production-only tokens / tenants / approved sender as mode 4, plus production-isolated ACS / Event Grid / Storage Queue
2. Setup doctor (before registration): `setup doctor --mode production-queue` ([#425](https://github.com/kooiei-in4a/amane-mailer/issues/425))
3. Setup (stack + ACS): follow mode 4 (deploy compose, `Production` register-acs, doctor re-run)
4. Setup (bounce): follow [bounce ingestion runbook](bounce-ingestion-runbook.en.md); set `MAILER_BOUNCE_INGESTION=queue` and `MAILER_BOUNCE_QUEUE_NAME` in `.env`, and place the Queue connection string at `${MAILER_BOUNCE_QUEUE_SECRET_HOST_PATH}/queue_connection_string` (never pass secrets as CLI arguments)
5. Setup (Azure): Delivery Report → Event Grid → **Storage Queue** (not Push). Use `setup check-event-grid` ([#427](https://github.com/kooiei-in4a/amane-mailer/issues/427)) for a read-only configuration check
6. Setup doctor (re-run): `setup doctor --mode production-queue`. Confirm `[PASS] compose_bounce_wiring` / `mode_bounce_queue` / `bounce_queue`
7. Verification: `/healthz` `/readyz`, approved live send. Staging Delivery Report arrival is `setup verify-delivery-report` ([#428](https://github.com/kooiei-in4a/amane-mailer/issues/428)) — not production evidence. Published image is `v1.2.0` ([release record](../releases/v1.2.0.md))

**How to score results**

- Do not treat a quiet poll-failure metric as Event Grid wiring success (unconfirmed arrival is `[WARN]` / `[ACTION]`)
- [#427](https://github.com/kooiei-in4a/amane-mailer/issues/427) provides **read-only configuration checks for the selected environment** (including dev / staging / production). It is not Staging-only
- [#428](https://github.com/kooiei-in4a/amane-mailer/issues/428) is **Staging-only**. Do not treat #428 results as evidence that production was exercised
- **Real bounce is not a completion criterion**

**Done when:** mode 4 completion plus compose-wired `queue` settings, Queue file secret, Queue name, and Event Grid → Queue configuration checks can be `[PASS]` / human-confirmed. Published image is `v1.2.0` ([release record](../releases/v1.2.0.md)).

### Manual verification helpers (availability)

| Issue | Capability | Boundary |
|-------|------------|----------|
| [#425](https://github.com/kooiei-in4a/amane-mailer/issues/425) | read-only setup doctor | **Available** (see “setup doctor” above) |
| [#426](https://github.com/kooiei-in4a/amane-mailer/issues/426) | ACS-only live send check CLI | **Available** — [test-acs-send-cli-runbook.en.md](test-acs-send-cli-runbook.en.md) (Staging only) |
| [#427](https://github.com/kooiei-in4a/amane-mailer/issues/427) | read-only Event Grid / Storage Queue configuration check (`setup check-event-grid`) | **Available** — [event-grid-config-check-runbook.en.md](event-grid-config-check-runbook.en.md) (selected environment; does not prove arrival) |
| [#428](https://github.com/kooiei-in4a/amane-mailer/issues/428) | Delivery Report Queue arrival E2E (message ID correlation; real bounce not required) | **Available** — [verify-delivery-report-runbook.en.md](verify-delivery-report-runbook.en.md) (**Staging only**. Production Queue / production test send are non-goals) |

For Manual setup, use the CLIs above plus existing preflight / smoke / manual runbook checks.

---

## Hardened Deployment

Use Hardened Deployment when you need strict host controls and will **not** use the Easy Setup assistant.

- Build on the **Manual** contract (modes, runbooks, file secrets, compose).
- Do **not** create Managed root / `ACTIVE` / Easy Setup metadata.
- Prefer file secrets with owner-only permissions; keep `.env`, tenants, secrets, DB, and backups in **separate** storage locations as your policy requires.
- No remote Docker, Docker socket delegation into the Mailer container, or arbitrary Compose stacks outside the documented deploy template.
- Production Admin: HTTPS only; `AMANE_ADMIN_ALLOW_HTTP=false`.
- TLS-terminating reverse proxy → Mailer HTTP upstream: `ASPNETCORE_FORWARDEDHEADERS_ENABLED=true` (compose contract; trusted proxy only).

CLI examples (exact):

```text
Amane.Mailer setup doctor --mode <mode>
Amane.Mailer admin provider register-acs
Amane.Mailer admin hash-password
Amane.Mailer admin user create --username <name> --password-hash <pbkdf2> --tenant-id <uuid>
Amane.Mailer db backup <absolute-path>
Amane.Mailer db checkpoint
```

Treat `password-hash` as sensitive: do not paste it into docs, logs, or issues. It may remain in shell history or process listings. Admin details stay in existing Admin / local Docker runbooks — do not present break-glass as the default path.

---

## Troubleshooting

| Example symptom | See |
|-----------------|-----|
| Easy Setup start / VPS / non-interactive Admin FAIL | [Easy Setup troubleshooting](#easy-setup-troubleshooting-pointers) |
| tenant / token / `LIVE_SENDING_DISABLED` / missing provider config | [config README troubleshooting](../../config/mailer/README.en.md#tenant--env-troubleshooting), setup doctor in Manual Deployment |
| local start / Admin / Mailpit | [local Docker runbook](local-mailer-docker-runbook.en.md) |
| deploy-shaped compose / migrate / network | [local deploy rehearsal](local-deploy-rehearsal-runbook.en.md) |
| Staging / Production ACS secret registration failure | [register-acs CLI](register-acs-cli-runbook.en.md) (match confirmation phrase to the environment) |
| Staging ACS standalone send triage | [test-acs-send CLI](test-acs-send-cli-runbook.en.md) (Staging only) |
| Event Grid / Queue configuration mismatch | [event-grid config check](event-grid-config-check-runbook.en.md) (read-only) |
| Staging Delivery Report not arriving in Queue | [verify-delivery-report](verify-delivery-report-runbook.en.md) (Staging only; real bounce not required) |
| bounce / unmatched / Queue poll (runtime description) | [bounce ingestion](bounce-ingestion-runbook.en.md), [metrics-and-alerts](metrics-and-alerts.en.md) |
| backup / restore | [Backup operations](backup-operations.en.md), [Restore procedure](restore-procedure.en.md), [Restore verification](restore-verification.en.md) |
| published image smoke (published tags) | [release-image-smoke](release-image-smoke.en.md) |
| candidate packaging / handoff | [setup-release-bundle](setup-release-bundle.en.md) |

## Non-goals of this entry point

- Runtime implementation changes in this documentation issue
- Marketing site
- Per-NAS product manuals
- Credential / password rotation guides
- Reverse proxy / certificate / DNS auto-configuration
- Non-interactive Admin bootstrap
- Password hash file input for Admin bootstrap
- Recording deployment operational verification inside Easy Setup
- Exhaustive external secret-manager product guides
- Azure resource auto-creation
- Copying full existing runbooks into this file
- Documenting Consumer bounce API / webhook contracts for v1.2.0 (#307 is later)
- Adopting Event Grid Push (#304)
- Workarounds that ask production operators to type `Staging`
- Publishing real credentials, tenants, or private paths
- Embedding #456 Hard gate tables or candidate-specific digest values
