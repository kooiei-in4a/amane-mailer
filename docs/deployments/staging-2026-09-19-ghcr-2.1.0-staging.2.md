# ステージング更新記録 — GHCR 2.1.0-staging.2

この文書は、2026-09-19 に実施した Amane Mailer `2.1.0-staging.2` の
Build Once / GHCR publication / staging VPS image-only deployment / live acceptance の
value-free evidence を記録します。

正式な public release の正本は
[`docs/agent-workflows/release.md`](../agent-workflows/release.md)、
staging image publication は
[`docs/ops/staging-release.md`](../ops/staging-release.md)、
VPS deployment は
[`docs/ops/staging-deployment.md`](../ops/staging-deployment.md)
を参照してください。

API Key、ACS connection string、Caddy password/hash、PAT、age private identity、
recipient、mail body 等の secret / PII は記録していません。

## 結果

```yaml
environment: staging
public_url: https://stg.mailer.amanesystem.net/
deployment_date_utc: 2026-09-19

previous_version: 2.1.0-staging.1
previous_source_commit: 6429158ab64b72755085d6acb310534eb224a083
previous_digest: sha256:bd2e93926cf96e1981ce813060bcc3691ea37c2cfaccdb3e290fbcefff94b154

source_commit: 4767834bc8646897980089b13afe27a4298dc37d
version: 2.1.0-staging.2
platform: linux/amd64
ghcr_digest: sha256:e2f0913f48547d74441ac46a3ee90aa285018ad8b25292fa4fff011e29800c05
ghcr_version_tag: ghcr.io/kooiei-in4a/amane-mailer:v2.1.0-staging.2
ghcr_sha_tag: ghcr.io/kooiei-in4a/amane-mailer:sha-4767834bc8646897980089b13afe27a4298dc37d

development_vm_build_image_id: sha256:2f0436aa231877a6a4da967909d623bf26ee92d4cd80e3a4375163b12973a339

compose_project: amane-mailer-staging
deploy_directory: /srv/apps/amane-mailer-staging
application_host_port: none
migration: skipped_no_schema_change
vps_source_build: false

rollback_directory: /srv/apps/amane-mailer-staging/rollback/20260919T114756Z-pre-2.1.0-staging.2
backup_file: /srv/apps/amane-mailer-staging/rollback/20260919T114756Z-pre-2.1.0-staging.2/mailer-state-pre-2.1.0-staging.2-20260919T114756Z.tar.age
backup_bytes: 379160
backup_sha256: 82d7fb68a17f6138f2b01c7a126fe46c11b73d7f78f629f5b3bf9d256f3a7947
plaintext_archive_retained: false

caddy_changed: false
compose_files_changed: false
shared_edge_changed: false
other_services_changed: false

healthz: pass
readyz: pass
edge_boundary: pass
setup_status_regression: pass
e2e_before_restart: delivered
e2e_after_restart: delivered
status_at_handoff: staging_acceptance_complete
```

## Source authority

`2.1.0-staging.2` のsourceは exact `main`:

```text
4767834bc8646897980089b13afe27a4298dc37d
```

です。

この revision の main post-merge CI は SUCCESS でした。

staging.1 source
`6429158ab64b72755085d6acb310534eb224a083` から target source までの
`Data/Migrations/*.sql` を Fresh 比較し、変更がないことを確認したため、
今回migrationは実行していません。

## Build Once / GHCR publication

Development VMで exact revision の committed filesだけをbuild inputとして使用し、
`linux/amd64` を1回だけbuildしました。

local validation:

- restore: PASS
- build: PASS
- test: PASS
- worktree clean before/after
- container `--help`: PASS
- `/healthz`: 200 / healthy=true
- fresh managed-v2 `/readyz`: 503 / ready=false / reason=uninitialized

Build Once artifact:

```text
OCI digest:
sha256:e2f0913f48547d74441ac46a3ee90aa285018ad8b25292fa4fff011e29800c05

OCI version:
2.1.0-staging.2

OCI revision:
4767834bc8646897980089b13afe27a4298dc37d

OCI source:
https://github.com/kooiei-in4a/amane-mailer
```

既存tag collisionをfail-closedで確認後、保存済みOCI layoutをrebuildせずGHCRへpublicationしました。

version tag / immutable SHA tag は同一digestを指します。

```text
ghcr.io/kooiei-in4a/amane-mailer:v2.1.0-staging.2
ghcr.io/kooiei-in4a/amane-mailer:sha-4767834bc8646897980089b13afe27a4298dc37d
```

両方のregistry read-back digest:

```text
sha256:e2f0913f48547d74441ac46a3ee90aa285018ad8b25292fa4fff011e29800c05
```

## VPS deployment

deployment前のFresh stateは:

- Mailer: exactly one
- current version: `2.1.0-staging.1`
- current revision: `6429158ab64b72755085d6acb310534eb224a083`
- host-published Mailer port: none
- data mount: `/srv/apps/amane-mailer-staging/data -> /app/data`
- shared edge: existing `amane-platform-edge`
- `/healthz`: 200
- `/readyz`: 200
- `/admin` / `/setup`: Caddy Basic Auth boundary
- unauthenticated `/api`: Mailer 401 / Basic challengeなし

target digestをdowntime前にpullし、platform / OCI labels / RepoDigestを確認しました。

deployment:

1. rollback record作成
2. running MailerでWAL checkpoint
3. Mailerだけ停止
4. SQLite sidecar不在確認
5. DB / canonical ACS secret / committed spoolをcold pointでage暗号化backup
6. candidate `.env` を作成
7. `MAILER_IMAGE_REFERENCE` / `MAILER_IMAGE_TAG` の2項目だけ変更
8. effective Compose preflight
9. migration skip
10. `--no-deps --no-build --pull never --force-recreate mailer`
11. container / public acceptance

Compose files、Caddyfile、shared edge、他service、data path、network topologyは変更していません。

## #782 / PR #783 live acceptance

`/admin/setup-status` をstaging.2で確認しました。

- Deployment: Browser Setup
- Mailer version: `2.1.0-staging.2`
- Credential loaded: yes
- Provider summary: acs
- Live sending: yes
- Sender: current managed Sender（masked）
- `credential-missing` regression: not observed

`/admin/ops` でも:

- Overall readiness: Ready
- Schema migrated: yes
- DB connection: yes
- Worker running: yes
- Sweep running: yes
- Live sending: enabled
- Provider preflight: configured / safe

を確認しました。

## Existing Consumer E2E

既存Consumer API Keyを使用しました。

restart前:

```text
mail_request_id:
55a9b4e1-1292-4bfa-982f-789f958a779b

accepted
-> processing
-> delivered
```

その後 Mailerだけrestartし、

- version: `2.1.0-staging.2`
- revision: `4767834bc8646897980089b13afe27a4298dc37d`
- health: healthy
- `/healthz`: 200
- `/readyz`: 200
- OOM: false
- host port: none

を再確認しました。

restart後、同じ既存Consumer API Keyで再送信:

```text
mail_request_id:
ea67ca0b-4a2c-4945-b159-4f75ea8a971a

accepted
-> processing
-> delivered
```

これにより、staging.2へのimage-only update後もmanaged configurationとexisting Consumer identityが
restartをまたいで維持されることを確認しました。

## Release readiness handoff

staging.2 acceptanceは完了しました。

次のrelease gateはcanonical:

```text
prepare-version -Version 2.1.0
```

です。

このstaging deploymentでは Git tag、GitHub Release、Contracts NuGet、`latest`、
`release/current-public.json` を変更していません。
