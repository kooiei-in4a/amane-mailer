[日本語](vps-dogfood-smoke.md)

# VPS dogfood smoke checklist (Issue #733 / PR2)

This checklist is the reproducible operator procedure for validating the v2
Consumer API on the PR1 VPS managed-v2 deployment. The two official clients are
the [Python smoke client](../../examples/consumer-python/README.md) and the
[PowerShell smoke client](../../scripts/smoke/send-mail.ps1).

This PR and its CI **do not perform an ACS live send**. The A1/A2/B1 send steps
below are instructions for a later run by an operator who has explicitly
approved the purpose, recipients, senders, and time window. Starting a client
does not create Senders or API keys, automate Admin login, restart Docker, or
enable live sending.

## 0. Go / no-go

Before starting, confirm:

- [ ] This checklist is Issue #733 PR2 smoke/dogfood work; use the dedicated PR3 backup/restore runbooks separately.
- [ ] The purpose, approved recipient, approved sender, execution window, and stop owner for any ACS live send are explicit.
- [ ] On production, the public Mailer URL, management URL, operator CIDR, and ACS environment agree.
- [ ] Recipient, API key, bootstrap token, ACS connection string, and password values will not be recorded in this document, shell history, an issue, chat, or CI logs.
- [ ] If the run is cancelled, do not start the client and do not enable `live_sending`.

Labels such as `A1 real send` do not claim that this PR performed the send. If
an executed run needs evidence, record only value-free facts such as image
digest, time, HTTP status/code, Mailer's `mail_request_id`, and delivery status.
Do not record recipient or message content.

## 1. Prerequisites and fresh VPS

- [ ] Docker Engine and a Compose plugin supporting `!override` / `!reset` are installed.
- [ ] DNS/TLS and host firewall policy separate the public API from operator-only management paths as intended.
- [ ] A verified immutable image tag or digest has been selected. Do not use an unverified `latest`.
- [ ] `MAILER_DATA_PATH` is persistent, and ACS/bounce secret directories are mode `0700`.
- [ ] On a fresh state, do not create `tenants.json`, `MAIL_SERVICE_TOKEN*`, or legacy `MAILER_PROVIDER`.

The [VPS dogfood deployment (PR1)](vps-dogfood-deployment.en.md) is authoritative
for the security boundary, fixed proxy network, unpublished Mailer port, and
management CIDR. Do not use `down -v`: it can delete the Mailer database and
Caddy certificate state.

## 2. Deploy, migrate, and bootstrap setup

1. Use the PR1 `infra/deploy/.env.vps-dogfood.example` and Caddyfile to create
   uncommitted deploy-host configuration. Verify the image, hostname, operator
   CIDR, data path, and protected secret paths.
2. Without displaying secret values, verify that rendered Compose has no host
   Mailer port `8080` publish, legacy tenant mount, `MAIL_SERVICE_TOKEN*`, or
   `MAILER_PROVIDER`.
3. Run config validation, migration, and startup with the profile explicit.

```bash
docker compose --env-file .env \
  -f compose.yml -f compose.vps-dogfood.yml \
  --profile vps-dogfood config --quiet

docker compose --env-file .env \
  -f compose.yml -f compose.vps-dogfood.yml \
  --profile vps-dogfood run --rm mailer-migrate

docker compose --env-file .env \
  -f compose.yml -f compose.vps-dogfood.yml \
  --profile vps-dogfood up -d
```

4. Check `/healthz` and `/readyz`. With fresh state, `/readyz` `503` before setup is expected.
5. Display the bootstrap token once from inside the container and enter it in
   the browser through an operator TTY. Do not copy it into shell history,
   logs, issues, chat, or CI artifacts.

```bash
docker compose --env-file .env \
  -f compose.yml -f compose.vps-dogfood.yml \
  --profile vps-dogfood exec mailer /app/Amane.Mailer setup bootstrap show
```

6. From the operator-only HTTPS route, complete `/setup` in this order:

   `bootstrap authentication → ACS provider secret → first Admin → first Sender → finalize`

   Enter the ACS connection string in the password field. Do not put it in an
   environment variable, URL, or CLI argument. Finalize commits durable managed
   state. Restart Mailer and verify that `/readyz` becomes ready.

7. After setup, as the instance owner, confirm in `/admin/ops` that `Provider
   preflight` is `configured / safe` and `live_sending` is `disabled`. `/readyz`
   plus this Admin display are the canonical preflight for tenant runtime on a
   VPS managed-v2 deployment. `setup doctor --mode production-acs` checks the
   legacy/setup-bundle mode that uses tenants.json; it is not evidence for a
   managed-v2 fresh VPS that intentionally has no tenant JSON.

`admin provider register-acs` is a separate platform-owned sender registration
path; it is not a replacement for the tenant runtime ACS secret entered through
`/setup/provider`. If that path is also used, follow the exact `Production`
confirmation and preflight in the [register-acs CLI runbook](register-acs-cli-runbook.en.md),
and never pass a secret as a CLI argument. Success of that command alone is not
evidence that a tenant real send has been verified.

## 3. Prepare Senders and API keys

In `/admin/senders`, use the first-run Sender as **Sender A** and create a
separate **Sender B**. Use ACS-approved sender addresses.

| Target | Admin action | Purpose |
|---|---|---|
| Sender A | Confirm the first-run Sender | Owner of A1/A2 |
| Key A1 | Create API key named `A1` on A | Revoke target |
| Key A2 | Create API key named `A2` on A | Continuity after revoke |
| Sender B | Create a separate Sender | Owner of B1 |
| Key B1 | Create API key named `B1` on B | Sender identity isolation |

- [ ] Senders A and B are enabled.
- [ ] Each API key plaintext was saved safely once, immediately after creation.
- [ ] Plaintext was not copied into the Admin list, logs, ticket, chat, or shell history. Admin does not reveal it again.
- [ ] `live_sending` remains disabled until provider preflight and the send approval are confirmed.

## 4. Explicit live sending and the official clients

Only for an approved ACS live send, an instance owner checks `Live sending` in
`/admin/ops`, confirms that provider preflight is safe, and enables it with the
explicit confirmation. This is a delivery gate, not proof that setup is
complete. After the run, or when stopping, disable it according to the
organization's procedure.

The official clients follow the v2 standard deployment contract for Base URLs:
VPS and remote endpoints require `https://`. Plain HTTP is reserved for local
or no-send fixture tests using `localhost`, `127.0.0.1`, or `::1`; the clients
reject remote HTTP before acquiring the API key. There is no `--allow-insecure`
or `ALLOW_INSECURE_HTTP` escape hatch.

### Python

Python needs no additional package. If `MAILER_API_KEY` is not set, a hidden
TTY prompt is shown. A non-interactive runner should inject it from a secret
manager into the environment. The values below are documentation placeholders;
real runs use approved values through a safe channel.

```bash
export MAILER_BASE_URL='https://mailer.example.invalid/'
export MAILER_RECIPIENT_EMAIL='approved-recipient@example.invalid'
export MAILER_SUBJECT='Amane Mailer intentional smoke'
export MAILER_TEXT_BODY='Intentional operator smoke request.'

# Use a hidden prompt or secret-manager injection for MAILER_API_KEY.
python3 examples/consumer-python/send_mail.py
```

### PowerShell

Run on PowerShell 5.1+ or 7+. The API key cannot be passed as a parameter. If
`MAILER_API_KEY` is absent, the client uses `Read-Host -AsSecureString`.

```powershell
$env:MAILER_BASE_URL = 'https://mailer.example.invalid/'
$env:MAILER_RECIPIENT_EMAIL = 'approved-recipient@example.invalid'
$env:MAILER_SUBJECT = 'Amane Mailer intentional smoke'
$env:MAILER_TEXT_BODY = 'Intentional operator smoke request.'

# Use a hidden prompt or secret-manager injection for MAILER_API_KEY.
pwsh -NoProfile -ExecutionPolicy Bypass -File .\scripts\smoke\send-mail.ps1
```

Unless `--request-id` / `-RequestId` is supplied, each invocation generates a
new UUID. Do not fix the ID except for an intentional idempotent retry or
conflict rehearsal.

Client outcomes are:

- `POST 202` with `accepted` / `already_accepted` means acceptance only; it is not delivery success.
- `queued` / `processing` are polled with a bounded deadline.
- Only `delivered` returns exit code `0`; `failed`, `dead_lettered`, `cancelled`, and `delivery_unknown` are terminal but return `1`.
- 401/403/404/409/429/503, timeout, redirect, and unknown status return `1`; output contains only HTTP status and safe error code.
- `delivery_unknown` is not proof of non-delivery. Do not resend the same ID; assess duplicate risk before a business resend with a new ID.

### Run order for A1 / A2 / B1

1. Use A1 as `MAILER_API_KEY` and run one client invocation. Confirm `202`, then `delivered`.
2. Safely replace A1 in the process environment with A2. Run one new-UUID send and confirm `delivered`.
3. Use B1 for one new-UUID send and confirm `delivered`.
4. In `/admin/mail-requests`, confirm that Admin has instance-wide visibility of the A1/A2/B1 requests and delivery states. Do not copy recipient/body values into evidence. The Admin view confirms operational visibility and sender ownership; it does not replace the API key.

## 5. Revoke and isolation

1. In `/admin/senders` → Sender A → Key A1, perform the irreversible revoke confirmation.
2. Run the client with A1 and a **new UUID**. Expect `HTTP 401` / `UNAUTHORIZED` and exit `1`. Do not reuse an old ID to test revocation.
3. Run a new-UUID send with A2 and confirm `delivered`.
4. Run a new-UUID send with B1 and confirm `delivered`.
5. Confirm in the instance-wide Admin view that A1's 401 did not disable A2/B1 and that Sender A/B request ownership is not mixed.

## 6. Restart and persistence

After the live sends finish, or after disabling `live_sending` when stopping,
restart Mailer without deleting data.

```bash
docker compose --env-file .env \
  -f compose.yml -f compose.vps-dogfood.yml \
  --profile vps-dogfood restart mailer

docker compose --env-file .env \
  -f compose.yml -f compose.vps-dogfood.yml \
  --profile vps-dogfood ps
```

- [ ] Wait for `/readyz` to become ready.
- [ ] Admin Senders A/B, active A2/B1, revoked A1, `live_sending`, and provider configuration remain in SQLite managed state.
- [ ] A new-UUID send with A2 still reaches `delivered`.
- [ ] A new-UUID send with B1 still reaches `delivered`.
- [ ] Do not use `docker compose down -v`, delete volumes, or reinitialize the database. Backup/restore is covered separately by the [PR3 restore verification runbook](restore-verification.en.md).

## 7. Runtime rate-limit proof

Do not create a new limiter. Obtain the deterministic proof for the existing
runtime limiter from these existing local/test tests. No ACS is needed.

```bash
dotnet test tests/Amane.Mailer.Tests/Amane.Mailer.Tests.csproj \
  -c Release --filter 'FullyQualifiedName~SenderApiKeyIdentityTests.Authentication_attempt_limiter_rejects_after_fixed_window_budget'

dotnet test tests/Amane.Mailer.Tests/Amane.Mailer.Tests.csproj \
  -c Release --filter 'FullyQualifiedName~FirstRunSetupTests.Setup_auth_rate_limits_repeated_invalid_tokens_at_http_endpoint'
```

Expected results:

- `/api/*`: invalid/unknown API keys from the same remote IP return `401` for 20 attempts, then `429` / `AUTHENTICATION_RATE_LIMITED` on attempt 21.
- `/setup` bootstrap auth: invalid bootstrap tokens return `401` for 20 attempts, then `429`; the correct token immediately after the limit is also `429`.
- `/admin` password login uses a separate SQLite-backed login throttle and must not be confused with the API limiter. If needed, also run `MailerAdminSessionThrottleAuditTests.Login_throttle_survives_process_restart_simulation`.

If 21 invalid requests are made against a real VPS endpoint, use no real key
and do it only on staging or in an explicitly approved maintenance window. The
fixed window suppresses authentication attempts from the same source IP; do not
disrupt normal production operation for this check. The `/setup` HTTP path also
requires CSRF and a bootstrap workflow session, so the WebApplicationFactory
test above is the canonical proof instead of a hand-written loop on a VPS.

## 8. Secret and log exposure proof

Verify each item without copying values into evidence:

- [ ] **API key plaintext** does not appear in client stdout/stderr, ordinary exceptions, timeout, 401/409/429 output, or process argv. There is no `--api-key` parameter.
- [ ] **Authorization header** does not appear in console output, Admin UI, container logs, or CI output. The client sets it only as an internal HTTP header.
- [ ] **Bootstrap token** is handled only in the temporary operator TTY after `setup bootstrap show`; it is not stored in logs, command arguments, URLs, or artifacts.
- [ ] **Provider secret** is handled only through `/setup/provider` or the approved hidden-input/protected-file registration path; it is not put in `.env`, tenant JSON, CLI arguments, or logs.
- [ ] **Recipient / subject / body** use environment or secret-manager injection during a real run so they do not appear in CLI argv or evidence. Limit Admin PII views to necessary operators.
- [ ] **Container log** is inspected on screen before/after the run for absence of secret/header/body. Do not paste raw logs into an issue, chat, CI artifact, or shared file.
- [ ] **Process argv** is inspected with `ps` or Windows process inspection while the client runs. Confirm no API key, Authorization, bootstrap token, provider secret, or real recipient is present. Do not expand secret environment values into another command's arguments.

The automated no-send proof uses canary values against a fixture and checks that
API key, recipient, subject, and body do not appear in client stdout/stderr, and
that no v1 fields (`tenant_id`, `source_service`, `payload_hash`) are sent.

```bash
PYTHONDONTWRITEBYTECODE=1 python3 scripts/smoke/test_send_mail.py
pwsh -NoProfile -ExecutionPolicy Bypass -File scripts/smoke/send-mail-self-test.ps1
```

## 9. Local and CI boundary

- [ ] Python and PowerShell CI self-tests use only temporary local HTTP fixtures; they do not connect to ACS, Mailpit, a VPS, a production recipient, or a real API key.
- [ ] If ACS live-send evidence is needed, record it as a separate approved operator run using this checklist.
- [ ] Never treat `delivery_unknown` as success, proof of non-delivery, or a safe retry.
- [ ] Backup, restore, volume migration, release, tag, and issue closure are not completion criteria for this PR.

The required stop state is that the `live_sending` intent is explicit, operator-
owned secrets have not leaked into logs or arguments, and the Mailer database and
Caddy volumes have not been deleted.
