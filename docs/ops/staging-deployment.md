# Staging VPS deployment

GHCR へ公開済みの Amane Mailer staging image を、既存 VPS staging 環境へ
**source build せず、exact digest で更新する**ための手順です。

Development VM での staging image release は
[`staging-release.md`](staging-release.md) を参照してください。
2026-09-19 の実績は
[`docs/deployments/staging-2026-09-19-ghcr-2.1.0-staging.1.md`](../deployments/staging-2026-09-19-ghcr-2.1.0-staging.1.md)
に記録しています。

## 現在確認済みの staging topology

次は 2026-09-19 の実績です。**将来の current state として決め打ちせず、deployment 前に Fresh 確認してください。**

```text
VPS alias / host role: stg-mailer-01
deploy directory:       /srv/apps/amane-mailer-staging
Compose project:        amane-mailer-staging

Compose:
  compose.yml
  compose.shared-staging.yml
  compose.image-digest.yml

shared edge project:
  amane-platform-edge

Mailer:
  host port publishなし
  shared edge networkへ参加
  data: /srv/apps/amane-mailer-staging/data
```

## Repository-owned shared-edge profile

Issue #771 以降、shared platform edge に Mailer を接続するRepository側の標準overlayは
[`infra/deploy/compose.shared-edge.yml`](../../infra/deploy/compose.shared-edge.yml) です。
host用の非secret設定例は
[`infra/deploy/.env.shared-edge.example`](../../infra/deploy/.env.shared-edge.example) を参照します。

このprofileが所有するのはMailer側の接続境界だけです。

- Mailerはhost portをpublishしない。
- Mailerは `internal` と既存external shared-edge networkだけへ参加する。
- `mailer-migrate` は `internal` のみ。
- managed-v2ではlegacy tenant/token/provider inputsをoverlayで除去する。
- forwarded headersは設定したshared-edge proxy IPv4 addressだけをtrustする。
- Admin/Setupの `Connection.LocalIpAddress` 判定を維持するため、shared edge上のMailer IPv4 addressを固定する。
- Caddy/TLS/DNS/Basic Auth/JP allow-listはplatform edge側の責任であり、このprofileでは作成しない。

必要なshared-edge固有値は次の4つです。

```text
MAILER_SHARED_EDGE_NETWORK_NAME=<existing external Docker network>
MAILER_SHARED_EDGE_ALIAS=mailer
MAILER_SHARED_EDGE_PROXY_IPV4_ADDRESS=<platform edge proxy IPv4>
MAILER_SHARED_EDGE_MAILER_IPV4_ADDRESS=<reserved Mailer IPv4 on that network>
```

2026-09-19に構築済みのstaging hostは、このRepository profile追加前に作成された
`compose.shared-staging.yml` を使用しています。このIssueではlive VPSを変更しません。
通常のimage-only deploymentでCompose topologyを暗黙に差し替えず、将来の再構築または
明示的なprofile reconciliation時にRepository-owned profileをauthorityとして比較・採用します。

image authority は `.env` の次の値です。

```text
MAILER_IMAGE_REFERENCE=ghcr.io/kooiei-in4a/amane-mailer@sha256:<digest>
MAILER_IMAGE_TAG=sha-<source-revision>
MAILER_PULL_POLICY=never
```

`compose.image-digest.yml` は `mailer`、`mailer-migrate`、`mailer-acs-admin` の image を
`${MAILER_IMAGE_REFERENCE}` で上書きします。

## 原則

- VPS は source repository を必要としない。
- VPS で image build しない。
- target image は apply 前に explicit pull する。
- registry digest と OCI labels を確認してから downtime に入る。
- `pull_policy: never` と `--no-build --pull never` を維持する。
- 通常の staging image update で Caddy や shared edge を変更しない。
- Compose files は変更せず、image authority の `.env` 値だけを candidate 更新する。
- Mailer 以外の Compose project / container を変更しない。
- deployment 前に rollback identity と backup を固定する。

## 1. Fresh preflight

最低限次を確認します。

- deploy directory / Compose project / Compose files
- running Mailer container が exactly one
- current image digest / OCI version / OCI revision
- restart count / OOM state
- Mailer の host port が publish されていないこと
- data mount / ACS secret mount / bounce secret mount
- shared edge container identity
- live Caddyfile SHA-256
- public `/healthz` / `/readyz`
- `/admin` / `/setup` / unauthenticated `/api` の edge/auth boundary
- `.env` の current image authority
- effective Compose の target service image / pull policy / ports / networks

`MAIL_SERVICE_TOKEN` 未設定 warning は、base Compose の interpolation により表示される場合があります。
warning だけで fail とせず、shared-edge overlay の `!reset null` が effective Compose で
legacy token / provider 値を除去していることを確認します。現在の2026-09-19 hostでは
historical `compose.shared-staging.yml`、Repository標準では `compose.shared-edge.yml` が該当します。

## 2. Target image を downtime 前に pull

approved target は digest ref を使います。

```text
ghcr.io/kooiei-in4a/amane-mailer@sha256:<approved-digest>
```

pull 後に次を検証します。

- `RepoDigests` に approved digest がある
- platform = `linux/amd64`
- OCI version = approved staging version
- OCI revision = approved source SHA
- OCI source = `https://github.com/kooiei-in4a/amane-mailer`

**Development VM の local image ID との一致を要求しません。**

private package authenticationが必要な場合は read-only credential と temporary `DOCKER_CONFIG` を使います。
write credential を VPS に置きません。

## 3. Pre-deploy rollback record

downtime 前に次を保存します。

- old version
- old source revision
- old digest
- current `.env` copy
- Compose files SHA-256
- Caddyfile SHA-256
- target version / revision / digest
- change timestamp

rollback directory は deploy directory 配下の protected operator-owned path を使用します。

## 4. SQLite checkpoint と cold backup

Mailer は SQLite managed state に加えて provider secret と committed attachment spool を持つため、
Sample02 より backup boundary が広いです。

backup unit:

```text
mailer.db
secrets/acs/acs_connection_string
attachment-spool/committed/
```

手順:

1. running Mailer で `db checkpoint` を実行する。
2. Mailer を停止する。
3. `mailer` / `mailer-migrate` / `mailer-acs-admin` が running でないことを確認する。
4. `mailer.db-wal` / `mailer.db-shm` / `mailer.db-journal` が残っていないことを確認する。
5. ACS secret が regular file / non-empty / owner-only であることを確認する。
6. DB + ACS secret + committed spool を同じ cold point から archive する。
7. operator が管理する age recipient で暗号化する。
8. backup bytes / SHA-256 / path を記録する。

staging の pre-deploy rollback snapshot では、tar を stdout から `age` へ直接 pipe して
plaintext tar を disk に残さない方式を使用できます。

age private identity は VPS に置かず、operator / Development VM 側に owner-only で保管します。
VPS には public recipient だけを入力します。

この local encrypted snapshot は image update の rollback point です。
VPS 全体の障害をカバーする offsite DR backup の代替ではありません。

## 5. Migration 判定

old source revision と target source revision の間で `Data/Migrations/*.sql` を Fresh 比較します。

- migration変更なし: migrationを実行せず image-only update
- migration変更あり: migration compatibility と rollbackを別途reviewし、matching pre-update backupを必須にする

schema変更がある場合、旧 image へ戻すだけでは rollback できないことがあります。
`PRAGMA user_version` を手動で下げたり、post-update state を暗黙に破棄してはいけません。

## 6. Candidate .env

通常の image-only staging update では次の2値だけを更新します。

```text
MAILER_IMAGE_REFERENCE=<approved digest ref>
MAILER_IMAGE_TAG=sha-<approved source revision>
```

変更しないもの:

- `compose.yml`
- current hostの `compose.shared-staging.yml`（明示reconciliationまでは変更しない）
- Repository標準の `compose.shared-edge.yml`（新規構築・明示reconciliation時のauthority）
- `compose.image-digest.yml`
- Caddyfile
- shared edge
- data / secret paths
- resource limits
- network topology

candidate `.env` で effective Compose を render し、少なくとも次を preflight します。

- `mailer` / `mailer-migrate` / `mailer-acs-admin` が approved digest ref
- `pull_policy: never`
- Mailer host portなし
- Mailer network = expected `edge` + `internal`
- migration service host portなし

preflight PASS後に candidateをactive `.env` へ切り替えます。

## 7. Recreate

migration変更がない場合は Mailer だけを更新します。

```bash
docker compose ... up -d \
  --no-deps \
  --no-build \
  --pull never \
  --force-recreate \
  mailer
```

target imageをすでに明示pullしているため、apply中にimplicit pullさせません。

## 8. Acceptance

container:

- running
- healthy
- expected digest ref
- expected OCI version / revision
- host portなし

public:

- `/healthz` = 200 / `healthy=true`
- `/readyz` = 200 / `ready=true`
- JP operator sourceの `/admin` = expected Basic Auth boundary
- JP operator sourceの `/setup` = expected Basic Auth boundary
- unauthenticated `/api` = Mailer 401、Basic challengeなし
- initialized Browser managed-v2 の `/admin/setup-status` = canonical managed stateと整合し、configured providerを `credential-missing` と誤表示しない

さらに、Compose files / Caddyfile SHA と shared edge container identity が preflight から変わっていないことを確認します。

## 9. E2E send smoke

image deploy の最終確認として、既存の公式 client で **1通だけ**実送信し、
`accepted → processing → delivered` を確認します。

- PowerShell: `scripts/smoke/send-mail.ps1`
- Python: `examples/consumer-python/send_mail.py`

API Key は one-time plaintextです。保存済みkeyが不明なら、Adminから smoke用managed API Keyを
新規発行し、作成直後のplaintextだけを安全に使用します。

invalid / unknown / revoked key は同じ `401 UNAUTHORIZED` になるため、候補keyを連続して試しません。
authentication rate limitを無用に消費しないでください。

smoke専用keyが不要になったらrevokeします。live sendingをsmokeのためだけにenableした場合は、
operator policyに従ってdisabledへ戻します。

## 10. Rollback

schema変更がない通常のimage-only update:

```text
saved old .env
→ old digest authorityを復元
→ --no-build --pull never で Mailer recreate
→ health / ready / edge acceptance
```

schema変更がある場合はimage rollbackだけでなく、matching cold backupのrestoreを検討します。

自動 rollback を組む場合も、Mailer以外のproject、Caddy、shared edgeを変更しません。

## 11. Cleanup / retention

削除する:

- temporary `DOCKER_CONFIG`
- temporary candidate files
- temporary HTTP response files

継続保存してよい:

- Development VM の GHCR PAT（owner-only）
- operator側 age private identity（owner-only）
- deployment record
- rollback `.env`
- encrypted pre-deploy snapshot（retention policy内）

日常の次回deploymentでは、初回に実施した topology探索、age key新規生成、
backup方式の再設計は不要です。Fresh current-state確認だけを行い、上記の固定contractに沿って進めます。