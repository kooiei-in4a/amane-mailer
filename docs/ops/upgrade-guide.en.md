[日本語](upgrade-guide.md)

# Upgrade / Rollback Guide

This is the canonical operational entry point for upgrading an existing
self-hosted Amane Mailer deployment to the current public release. Use the
[setup entry point](setup-guide.en.md) for a first installation. This guide orders
release identity, compatibility, backup, migration, rollout, verification, and
rollback decisions while delegating detailed operations to their authoritative
runbooks.

The procedure uses a Manual / Hardened deployment based on
`infra/deploy/compose.yml`. Easy Setup Managed bundle changes remain governed by
the `ACTIVE` / integrity contract in [ADR 0021](../adr/0021-easy-setup-boundaries.md),
but a configuration bundle rollback does not restore SQLite, Admin state, mail
data, or provider side effects. Do not substitute setup re-application or Admin
re-bootstrap for an image or schema upgrade.

## Authorities and stop conditions

| Decision | Authority |
|----------|-----------|
| Current public version / tag / platforms / release-record path | [`release/current-public.json`](../../release/current-public.json) |
| Target commit, image digest, immutable tag, platforms, and publication evidence | The release record referenced by `current-public.json` |
| Compatibility, breaking changes, and migration inventory across releases | Applicable sections of [`CHANGELOG.md`](../../CHANGELOG.md) and each [`docs/releases/`](../releases/) record in the path |
| Public artifact verification method | [Release artifact verification](release-artifact-verification.en.md) |
| Migration behavior | [Service-spec migration checksum policy](../service-spec.en.md#migration-checksum-policy) and `Data/Migrations/*.sql` in the target image |
| Backup / restore | [Backup operations](backup-operations.en.md), [Restore procedure](restore-procedure.en.md), and [Restore verification](restore-verification.en.md) |
| Liveness / readiness | [Service spec](../service-spec.en.md) and the target-release runtime |

An individual file under `docs/releases/`, README, or the setup guide is not a
second authority for the current release. Always start with
`release/current-public.json`. From the repository root, inspect it without
copying a fixed version into this procedure:

```bash
node - <<'NODE'
const current = require('./release/current-public.json');
console.log(`version=${current.version}`);
console.log(`tag=${current.tag}`);
console.log(`platforms=${current.platforms.join(',')}`);
console.log(`releaseRecord=${current.releaseRecord}`);
NODE
```

Stop before making changes if any of these are true:

- the running image version / immutable identity, target release, or target
  platform cannot be established;
- the release records / CHANGELOG between the deployed and target releases do
  not provide the required compatibility or migration information;
- the release record and registry disagree on commit, tag, digest, or platform;
- no pre-upgrade backup or usable age identity for the selected backup is available;
- the previous immutable image, private configuration / secrets, or database
  restore path needed for rollback is unavailable.

The 1.x SemVer compatibility guarantee applies to the public HTTP contract and
Contracts package. It does not guarantee a database schema downgrade or an
upgrade that skips arbitrary releases.

## 1. Establish the target and compatibility

1. Record the running immutable image reference, version, compose / configuration
   identity, and database data path in a private change record. Do not paste
   secret values or private paths into public issues or logs.
2. Read `release/current-public.json`, then open the release record it names.
3. In order, read the applicable CHANGELOG sections and release records after the
   deployed release through the target. Review public-contract, runtime-semantic,
   configuration, platform, and migration-inventory changes.
4. Verify the release record's commit, digest, and immutable
   `sha-<git-sha>` tag with the [artifact verification runbook](release-artifact-verification.en.md).
   Deploy a verified digest or immutable SHA tag rather than a mutable version
   tag or `latest`.
5. For a path containing migrations, confirm that the starting schema and ordered
   inventory documented by the release records / CHANGELOG apply to the current
   database. Do not infer a direct upgrade path when none is documented.

`infra/deploy/compose.yml` combines `MAILER_IMAGE_REPOSITORY` and
`MAILER_IMAGE_TAG`, so this template can set `MAILER_IMAGE_TAG` to the immutable
`sha-<releaseCommitSha>` tag in the release record. If other deployment tooling
accepts a digest reference, pin the digest verified against that record.

## 2. Prepare the backup and rollback plan

Under [Backup operations](backup-operations.en.md), take the final upgrade backup
after quiescing callers and immediately before stopping the old Mailer.
`backup-mailer.sh` uses the running Mailer's SQLite online backup API; do not
directly copy a live WAL database file. When a managed-v2 full recovery point is
needed, run `backup-instance-state.sh` after graceful shutdown to capture the
database, canonical provider secret, and committed spool at one cold point. The
full script does not stop or start services.

Confirm in advance that:

- the matching age identity and its recovery copy are available;
- operator-owned state outside the database—such as `tenants.json`, `.env`, the
  compose template, file secrets, and any Managed root—is preserved in private storage;
  treatment of Caddy named volumes and external bounce secrets excluded from the
  managed-v2 archive is also decided;
- an isolated environment and time are available to run
  [Restore verification](restore-verification.en.md) after taking the final backup
  and before migrating the target;
- the previous immutable image reference and configuration compatible with that
  release remain available.

Decide the rollback approver, how to stop callers, and the decision deadline in
advance. A production database restore is destructive and must not run without
the explicit approval required by the [restore procedure](restore-procedure.en.md).

The backup snapshot time is the rollback recovery point. Database updates after
that time are absent from the restored database even if normal traffic has not
resumed. Quiescing callers reduces new acceptance; it does not pause existing
queued / in-flight work, Worker / Sweep, webhook / Admin / retention database
updates, or provider calls. Provider side effects between the snapshot and
completed Mailer shutdown are not undone by a database restore. Before approving
a restore, reconcile request state and provider outcomes for this interval and
explicitly accept both the database updates that will be lost and the external
side effects that will remain. Do not assume that retrying a request with an
ambiguous provider outcome is safe.

## 3. Roll out

Run the following from the Mailer compose directory on the deployment host. Use
private values for real paths, image identities, and secrets.

1. Stop new requests from callers. Under the environment's operating policy,
   inspect queued / in-flight state and record the intended recovery point.
2. With the old Mailer still running, take the final online database backup. Confirm the
   new encrypted `mailer-*.db.age`, any required offsite upload, and that no
   plaintext `.db` remains. Record the snapshot time.
3. Immediately complete graceful shutdown of the old Mailer. If a managed-v2
   full recovery point is required, run `backup-instance-state.sh` here and
   confirm `mailer-state-*.tar.age` exists with no plaintext `.tar` remaining.
   Record any database updates or provider operations completed after the
   snapshot for reconciliation.

```bash
docker compose --env-file .env -f compose.yml stop mailer
```

4. Preserve the saved old private configuration / image identity, change
   `MAILER_IMAGE_TAG` in `.env` to the verified target immutable SHA tag, and
   pull the target image.
5. Run [Restore verification](restore-verification.en.md) against the final database/full backup
   with the target image in an isolated disposable environment. This exercises
   normal `mailer-migrate` plus health / readiness checks. If it fails, stop
   without changing the production database and either explicitly restore and
   restart the saved old image / configuration or select another verified backup.
6. Classify the schema read-only with the target image.

```bash
docker compose --env-file .env -f compose.yml --profile ops pull mailer-migrate mailer
docker compose --env-file .env -f compose.yml --profile ops run --rm \
  mailer-migrate db migrate --status --format json
```

For an existing deployment, normally only `Current` or `Behind` is a candidate
to continue. `AheadOrUnsupported` or an unexpected `DatabaseAbsent` is a stop.
`Unknown` also remains a stop until path / mount / image / schema, SQLite open,
or I/O problems are resolved, except for the known legacy bootstrap below.

### Legacy checksum bootstrap (limited exception)

The [service-spec migration checksum policy](../service-spec.en.md#migration-checksum-policy)
defines one path for an existing `schema_migrations` table created before the
checksum column existed: the first checksum-aware `db migrate` adds the column
and backfills the applied migration versions from the bundled files. Read-only
`--status` can classify this database as `Unknown`.

Proceed from `Unknown` to a normal migration only when all of these are true:

- before target migration, the operator inspected a disposable copy of the final
  backup read-only and confirmed that the source release and database have an
  existing `schema_migrations` table but no checksum column, matching the
  documented legacy schema;
- applied versions are the contiguous prefix expected by the target image, and
  database path / mount, SQLite open / I/O, permissions, unknown applied versions,
  migration gaps, and other possible causes of `Unknown` are excluded;
- restore verification of the final pre-upgrade backup succeeded with the target
  image, including its normal migration, and the operator explicitly approved
  using that image's bundled SQL as the first trust anchor because no historical
  checksum exists.

Stop if this cannot be proven. Do not manually add or backfill the checksum
column or values; only the approved target image's normal `db migrate` performs
the bootstrap.

7. For `Behind` or an approved legacy checksum bootstrap, run exactly one
   migration runner from the target image. With `Current`, the same command
   completes as up to date. Never run concurrent migration runners.
8. Run read-only status again after migration and require `Current`.
9. Start the target Mailer only after migration and status verification succeed.

```bash
docker compose --env-file .env -f compose.yml --profile ops run --rm mailer-migrate
docker compose --env-file .env -f compose.yml --profile ops run --rm \
  mailer-migrate db migrate --status --format json
docker compose --env-file .env -f compose.yml up -d --wait mailer
```

SQL migrations are a numerically ordered, forward-only bundle. The runner checks
applied versions and byte-level checksums. Do not bypass a missing historical
migration or checksum mismatch by editing SQL, reformatting a migration, or
changing database metadata. Select the correct image / SQL files or restore a backup.

## 4. Health, readiness, and operational verification

Before restoring normal traffic, run at least:

```bash
docker compose --env-file .env -f compose.yml exec -T mailer /app/Amane.Mailer healthcheck
MAILER_HTTP_PORT="$(sed -n 's/^MAILER_HTTP_PORT=//p' .env | tail -n 1 | sed "s/^['\"]//;s/['\"]$//")"
MAILER_HTTP_PORT="${MAILER_HTTP_PORT:-8080}"
docker compose --env-file .env -f compose.yml exec -T mailer \
  curl -fsS "http://localhost:${MAILER_HTTP_PORT}/healthz"
docker compose --env-file .env -f compose.yml exec -T mailer \
  curl -fsS "http://localhost:${MAILER_HTTP_PORT}/readyz"
docker compose --env-file .env -f compose.yml exec -T mailer /app/Amane.Mailer db stats
```

- `/healthz` reports process liveness only.
- CLI `healthcheck` and `/readyz` check the migration versions / checksums required
  by the target binary and, when the Worker is enabled, Worker / Sweep running
  state and heartbeat freshness.
- `/readyz` does not revalidate provider / ACS configuration and does not by
  itself prove provider reachability, delivery, or overall upgrade success. Some
  invalid provider configurations fail closed at startup.

Also perform configuration and contract checks required by the release record /
CHANGELOG, plus a no-send or explicitly approved delivery check appropriate for
the environment. If Admin is enabled, verify login, Mail Requests, and Dead
Letters through the approved access path. Resume callers gradually only after all
checks pass, while monitoring readiness, queue / failure metrics, and provider results.

## 5. Rollback decisions and execution boundary

| State | Action |
|-------|--------|
| Artifact / configuration / pull / status check fails before migration | Abort the change. If `.env` already targets the new release, explicitly restore the saved old private configuration and immutable image identity, start the old image, and recheck health / readiness. |
| New runtime startup or verification fails with no migration applied | Keep callers stopped, restore the previous immutable image / configuration, and recheck health / readiness. |
| Startup, readiness, or operational verification fails after migration | Do not run the old binary against the forward-migrated database. Reconcile post-snapshot database updates and provider side effects, approve the loss window, then restore the **pre-upgrade database backup** together with the previous compatible image / configuration by following the [restore procedure](restore-procedure.en.md). |
| Rollback is needed after traffic resumes | Stop callers first and reconcile every request / provider outcome since the snapshot. The loss window starts at the backup snapshot, not at traffic resumption; explicitly approve that entire interval before choosing the incident procedure. |

Schema downgrade is not guaranteed, and no reverse migration is provided. A
restore's database loss window starts at the backup snapshot, and provider side
effects are not undone by restore. Present the reconciliation result and scope to
the approver, return `.env` and compose state to the previous compatible image /
configuration first, and then restore the selected pre-upgrade backup through the
[restore procedure](restore-procedure.en.md). Keep callers disabled until CLI
`healthcheck`, `/healthz`, `/readyz`, `db stats`, and environment-specific checks pass.

Restoring configuration alone does not undo SQLite, Admin state, mail data, or
provider side effects. Do not use `docker compose down -v`, deletion of the data
directory, or manual migration-metadata edits as rollback procedures.

## Change record

Without exposing private values, record these in the operator's private change /
incident record:

- previous and target immutable image identities;
- target release record and verified artifact digest / platform;
- backup artifact, restore-verification result, and age-identity availability;
- backup snapshot time, post-snapshot changes / provider outcomes through shutdown,
  reconciliation, and approval;
- migration status and execution result;
- health / readiness / environment-specific verification results;
- traffic stop / resume, rollback decision, approvals, and unresolved items.
