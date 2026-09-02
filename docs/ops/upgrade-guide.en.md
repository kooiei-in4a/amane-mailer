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

While the pre-upgrade Mailer is still running, execute `backup-mailer.sh` as
documented in [Backup operations](backup-operations.en.md). It must use Mailer's
SQLite online backup API; do not directly copy a live WAL database file.

Confirm all of the following:

- a new encrypted `mailer-*.db.age` exists and no plaintext `.db` remains;
- offsite upload succeeded where required;
- the matching age identity and its recovery copy are available;
- operator-owned state outside the database—such as `tenants.json`, `.env`, the
  compose template, file secrets, and any Managed root—is preserved in private storage;
- [Restore verification](restore-verification.en.md) has succeeded for the backup selected for rollback;
- the previous immutable image reference and configuration compatible with that
  release remain available.

Decide the rollback approver, how to stop callers, and the decision deadline in
advance. A production database restore is destructive and must not run without
the explicit approval required by the [restore procedure](restore-procedure.en.md).

## 3. Roll out

Run the following from the Mailer compose directory on the deployment host. Use
private values for real paths, image identities, and secrets.

1. Preserve the previous private configuration, change `MAILER_IMAGE_TAG` in
   `.env` to the verified target immutable SHA tag, and pull the target image.
   Pulling alone does not replace the running container.
2. Stop new requests from callers and allow the old Mailer to finish graceful shutdown.
3. Classify the schema read-only with the target image.

```bash
docker compose --env-file .env -f compose.yml --profile ops pull mailer-migrate mailer
docker compose --env-file .env -f compose.yml stop mailer
docker compose --env-file .env -f compose.yml --profile ops run --rm \
  mailer-migrate db migrate --status --format json
```

For an existing deployment, only `Current` or `Behind` is a candidate to
continue. `AheadOrUnsupported`, `Unknown`, or an unexpected `DatabaseAbsent` can
indicate a path, mount, image, or schema mismatch. Stop and do not run `db migrate`.

4. If the result is `Behind`, run exactly one migration runner from the target
   image. With `Current`, the same command completes as up to date. Never run
   concurrent migration runners.
5. Start the target Mailer only after migration succeeds.

```bash
docker compose --env-file .env -f compose.yml --profile ops run --rm mailer-migrate
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
| Artifact / configuration / pull / status check fails before migration | Abort the change and reverify with the old image / configuration unchanged. |
| New runtime startup or verification fails with no migration applied | Keep callers stopped, restore the previous immutable image / configuration, and recheck health / readiness. |
| Startup, readiness, or operational verification fails after migration | Do not run the old binary against the forward-migrated database. With explicit approval, restore the **pre-upgrade database backup** together with the previous compatible image / configuration by following the [restore procedure](restore-procedure.en.md). |
| Rollback is needed after traffic resumes | Stop callers first. A pre-upgrade restore excludes database state accepted after the upgrade; assess request / provider side effects and the data-loss window before choosing the incident procedure. |

Schema downgrade is not guaranteed, and no reverse migration is provided. For a
restore, first return `.env` and compose state to the previous compatible image /
configuration, then restore the selected pre-upgrade backup through the
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
- migration status and execution result;
- health / readiness / environment-specific verification results;
- traffic stop / resume, rollback decision, approvals, and unresolved items.
