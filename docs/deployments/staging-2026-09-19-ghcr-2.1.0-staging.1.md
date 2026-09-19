# ステージング更新記録 — GHCR 2.1.0-staging.1

この文書は、2026-09-19 に実施した Amane Mailer staging image release / VPS deployment の
value-free evidence を記録します。

現行手順:

- [staging image release](../ops/staging-release.md)
- [staging VPS deployment](../ops/staging-deployment.md)

正式な public release の正本は
[\`docs/agent-workflows/release.md\`](../agent-workflows/release.md) です。

API Key、ACS connection string、Caddy password/hash、PAT、age private identity、recipient、
mail body 等のsecret/PIIは記録していません。

## 結果

\`\`\`yaml
environment: staging
public_url: https://stg.mailer.amanesystem.net/
deployment_date_utc: 2026-09-19

integration_pr: 778
previous_version: 2.0.2
previous_source_commit: e7092096414b7a864e0cfa688a28e3ef359a1f63
previous_digest: sha256:40db12100086d2dae60b7b9c102fb352cf55b5c08919a094ab2c03ba621d9125

source_commit: 6429158ab64b72755085d6acb310534eb224a083
version: 2.1.0-staging.1
platform: linux/amd64
ghcr_digest: sha256:bd2e93926cf96e1981ce813060bcc3691ea37c2cfaccdb3e290fbcefff94b154
ghcr_version_tag: ghcr.io/kooiei-in4a/amane-mailer:v2.1.0-staging.1
ghcr_sha_tag: ghcr.io/kooiei-in4a/amane-mailer:sha-6429158ab64b72755085d6acb310534eb224a083

development_vm_build_image_id: sha256:7a0d8be5c42ecb606ac6897d5193f57c8a96790b137903410163908c81a592df
vps_local_image_id: sha256:bd2e93926cf96e1981ce813060bcc3691ea37c2cfaccdb3e290fbcefff94b154

compose_project: amane-mailer-staging
deploy_directory: /srv/apps/amane-mailer-staging
application_host_port: none
migration: skipped_no_schema_change
vps_source_build: false

backup_file: /srv/apps/amane-mailer-staging/rollback/20260919T021806Z-pre-2.1.0-staging.1/mailer-state-pre-2.1.0-staging.1-20260919T021806Z.tar.age
backup_bytes: 379160
backup_sha256: 074f0ad94d6ec4d4f48a5eb74e2109578aa2e5cfb28598d793d096565a47efb5

caddy_changed: false
compose_files_changed: false
other_services_changed: false

healthz: pass
readyz: pass
edge_boundary: pass
e2e_send: delivered
mail_request_id: 5d3417b9-bfc3-412c-a9e6-a4cca69f353b
status_at_handoff: healthy_ready_and_delivery_verified
\`\`\`

## Source integration

\`develop\` は PR #778 で \`main\` へ統合しました。

- pre-merge head: \`f963efa5e4c47b15b4ca372cbe871d38a45ea7a4\`
- pre-merge CI #1549: SUCCESS
- merge commit / release source:
  \`6429158ab64b72755085d6acb310534eb224a083\`

merge後の \`main\` は上記SHAと一致していました。

この staging image publication では Git tag、GitHub Release、Contracts NuGet、
\`release/current-public.json\`、\`latest\` を変更していません。

## Development VM build

Development VMで exact \`origin/main\` の detached worktreeを作成し、次を確認しました。

- restore: PASS
- build: PASS
- test: PASS
- worktree clean
- platform: \`linux/amd64\`
- committed filesだけをbuild inputとして使用
- version/SHA local tagsは同一Build Once artifact
- OCI version/revision/source labels一致
- container \`--help\`: PASS
- \`/healthz\`: 200 / healthy
- fresh managed-v2 \`/readyz\`: 503 / \`reason=uninitialized\`

最初の buildx invocation は Native AOT native code generation中に operator harnessから中断され、
image / tar / digestを生成していません。その後、同一version / source SHAで完成したartifactが
本記録のBuild Once artifactです。

Build VMで得た local image IDは
\`sha256:7a0d8be5c42ecb606ac6897d5193f57c8a96790b137903410163908c81a592df\`、
publication authorityとなるOCI digestは
\`sha256:bd2e93926cf96e1981ce813060bcc3691ea37c2cfaccdb3e290fbcefff94b154\`
でした。

## GHCR publication

既存のformal release scriptは prerelease versionを受理しないため、repository fileは変更せず、
保存済み OCI layout を pinned crane で直接publishしました。

publication前に両tagの不存在を確認し、rebuildせず次を作成しました。

\`\`\`text
ghcr.io/kooiei-in4a/amane-mailer:v2.1.0-staging.1
ghcr.io/kooiei-in4a/amane-mailer:sha-6429158ab64b72755085d6acb310534eb224a083
\`\`\`

両tagのregistry digestは同一:

\`\`\`text
sha256:bd2e93926cf96e1981ce813060bcc3691ea37c2cfaccdb3e290fbcefff94b154
\`\`\`

OCI labels:

\`\`\`text
org.opencontainers.image.version=2.1.0-staging.1
org.opencontainers.image.revision=6429158ab64b72755085d6acb310534eb224a083
org.opencontainers.image.source=https://github.com/kooiei-in4a/amane-mailer
\`\`\`

temporary Docker credential directoryは削除しました。
GHCR write PAT自体はDevelopment VMのoperator-owned credentialとしてowner-onlyで継続保管する
運用へ統一しました。

## VPS Fresh state

deployment前のlive構成は次でした。

\`\`\`text
Compose project:
  amane-mailer-staging

directory:
  /srv/apps/amane-mailer-staging

Compose files:
  compose.yml
  compose.shared-staging.yml
  compose.image-digest.yml

shared edge:
  amane-platform-edge

current Mailer:
  version 2.0.2
  revision e7092096414b7a864e0cfa688a28e3ef359a1f63
  digest sha256:40db12100086d2dae60b7b9c102fb352cf55b5c08919a094ab2c03ba621d9125
  host port none
\`\`\`

public baseline:

- \`/healthz\`: 200 / healthy
- \`/readyz\`: 200 / ready
- \`/admin\`: 401 + Caddy Basic challenge
- \`/setup\`: 401 + Caddy Basic challenge
- unauthenticated \`/api\`: 401 / Basic challengeなし

persistent managed stateにはSQLite DB、canonical ACS secret、empty committed/staging attachment spoolが
存在しました。

## Backup

Development VM側で reusable age identityを作成し、private identityはoperator側にowner-onlyで保存しました。
VPSにはpublic recipientだけを使用しました。

deployment前に:

1. running MailerでWAL checkpoint
2. Mailer停止
3. SQLite sidecar消失確認
4. DB / canonical ACS secret / committed spoolを同一cold pointで取得
5. plaintext tarをdiskへ残さずageへstream

を実施しました。

encrypted snapshot:

\`\`\`text
/srv/apps/amane-mailer-staging/rollback/20260919T021806Z-pre-2.1.0-staging.1/
  .env
  identity.txt
  mailer-state-pre-2.1.0-staging.1-20260919T021806Z.tar.age
  deployment.txt
\`\`\`

backup SHA-256:

\`\`\`text
074f0ad94d6ec4d4f48a5eb74e2109578aa2e5cfb28598d793d096565a47efb5
\`\`\`

これはstaging update rollback用のlocal encrypted snapshotであり、VPS障害までを対象にする
offsite DR backupではありません。

## VPS deployment

target digestをMailer停止前に明示pullし、platform / RepoDigest / OCI labelsを確認しました。

初回preflightではDevelopment VMのDocker local image IDとVPS pull後のlocal image IDを
一致させる誤ったguardによりfail-fastしました。この時点ではMailer停止、backup、\`.env\`変更、
container recreationは未実施でした。

確認後、deployment authorityを **GHCR digest + platform + OCI labels** に修正しました。
Build VM local image IDとVPS local image IDは一致条件にしません。

実際のapplyでは:

1. current public stateを再確認
2. target digestがVPS上に存在することを再確認（再buildなし）
3. pre-stop checkpoint
4. Mailer停止
5. encrypted cold backup
6. candidate \`.env\` 作成
7. effective Compose preflight
8. \`MAILER_IMAGE_REFERENCE\` と \`MAILER_IMAGE_TAG\` のみtargetへ変更
9. migration差分なしのためmigration skip
10. \`--no-deps --no-build --pull never --force-recreate mailer\`
11. container health / running image identity確認
12. public acceptance

としました。

Caddyfile、Compose files、shared edge、他サービスは変更していません。

## Post-deploy acceptance

deployment後:

- container health: PASS
- target digest: PASS
- target OCI version/revision: PASS
- \`/healthz\`: PASS
- \`/readyz\`: PASS
- Admin/Setup/API edge boundary: PASS
- unrelated infrastructure unchanged: PASS

official PowerShell smoke clientで実メールを送信し、

\`\`\`text
POST -> 202 accepted
status -> processing
status -> delivered
\`\`\`

まで確認しました。

途中、保存済み候補API Keyで3回 \`401 UNAUTHORIZED\` になりました。
managed API Keyは plaintext one-time revealであり、invalid / unknown / revoked keyとdisabled Senderは
同じ401になります。正しいmanaged API Keyを使用した実行ではdeliveryまで成功しました。
今後は不明な候補keyを連続試行せず、必要ならsmoke専用keyを新規発行します。

## 次回に省略できる初回作業

今回、初回の運用経路確立のため次を調査・整備しました。

- actual VPS Compose topology探索
- shared Caddy topology探索
- image authority探索
- persistent state / backup boundary探索
- age tooling導入とreusable identity作成
- prerelease versionと既存formal release toolingの制約確認
- Build VM / VPS local image ID差異の整理

次回の日常staging updateでは、これらをゼロから再設計しません。
Fresh current-state確認後、\`staging-release.md\` と \`staging-deployment.md\` の固定contractに沿って
Build Once → GHCR exact digest → cold backup → image-only recreate → E2E send smoke を実施します。
