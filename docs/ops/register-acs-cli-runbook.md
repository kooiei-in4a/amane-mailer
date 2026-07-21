# ACS secret / platform-owned sender 登録 CLI runbook

> 対象: `admin provider register-acs` / `admin provider check-acs-preflight`
> action ID: `MAILER-ACS-INPUT-01`

## 1. 目的

このcommandは、ACS connection stringと、System Admin platform-owned mail用のsender情報（email・display name）を、対話入力だけで安全に登録するための非公開one-shot CLIである。

- ACS connection stringは deploy-time secret file（`acs_connection_string`）だけに保存する。tenant JSON、DB、amane-flow側のsecret経路には一切保存しない。
- platform-owned sender情報は新規・tenant非依存の`platform-sender.json`に保存する。既存tenantへの割当てや偽tenantの作成は行わない。
- このcommand単体では System Admin確認メールの実送信は完了しない。`platform-sender.json`をruntime送信経路へ組み込むのは、正式なplatform-owned mail request契約（MAIL-PLATFORM-01）の責務である。

## 2. 事前準備（deploy host）

1. `MAILER_ACS_SECRET_HOST_PATH`（既定 `./secrets/acs`）と`MAILER_PLATFORM_SENDER_HOST_PATH`（既定 `./config/platform-sender`）に対応するhost directoryを作成する。
2. Mailer runtime imageの実行時UID/GIDを確認する（chiseled imageの非rootユーザー）。
   `docker inspect <image> --format '{{.Config.User}}'`で確認する。公開イメージ
   `ghcr.io/kooiei-in4a/amane-mailer:v0.3.0` では実測で `Config.User=1654`
   （uid/gid `1654`、passwd 上の名前は `app`）。**デプロイするタグごとに必ず再確認する**
   （base image / `APP_UID` の更新で変わり得るため、他タグの値を推測で流用しない）。
3. 両directoryをそのUID/GIDのowner、mode `0700`で作成する。group/otherに一切の権限を与えない。
   v0.3.0 イメージを使う場合の例: `chown 1654:1654` のあと `chmod 0700`。
4. secret値そのものは、このrunbookや承認記録に転記しない。

## 3. 実行

```bash
docker compose --env-file .env -f compose.yml --profile acs-admin run --rm mailer-acs-admin
```

`-T`を付けない。secretの非表示入力は実TTYを要求するため、`-T`（no-TTY）付きで実行すると`REJECTED_INPUT_REDIRECTED`で拒否される。

非対話のpreflightだけを確認したい場合（secretを一切要求しない、繰り返し実行してよい）:

```bash
docker compose --env-file .env -f compose.yml --profile acs-admin run --rm mailer-acs-admin admin provider check-acs-preflight
```

## 4. 対話手順

1. 対象環境名の確認: 完全一致の`Staging`だけを受理する（`staging`・`STAGING`・他の綴りは全て拒否）。
2. 実行意図確認: 固定phrase `MAILER-ACS-REGISTER`の入力を求める。
3. ACS connection string: 非表示入力で2回。一致しない場合は書き込まない。
4. sender email: bare email形式のみ受理する。
5. sender display name: 1〜200文字、制御文字不可。

成功時は`SUCCESS`、拒否時はcanonical result code（例: `REJECTED_ALREADY_REGISTERED`）だけを出力する。secret値・raw exceptionは出力しない。

## 5. 排他制御

`preflight`通過後から2ファイルのcommit完了まで、同一secret directory内の lock file（`.register-acs.lock`）で排他制御する。同時に2つ目のcommandを実行すると`REJECTED_CONCURRENT_EXECUTION`で即座に拒否される。

lockはOS levelのadvisory lock（`FileShare.None`）であり、lock fileの存在そのものではなく、process が実際に open しているかどうかで判定する。processが異常終了した場合もOSが自動的にlockを解放するため、staleなlock fileが残っていても次回実行を妨げない。

## 6. 部分書き込みからの復元

ACS secretとplatform-sender.jsonの2ファイルは、prepare→commit A→commit Bの順で書き込む。commit Bが失敗した場合、commit Aは自動的にrollback（削除）され、`REJECTED_PARTIAL_WRITE_ROLLED_BACK`を返す。

rollback自体が失敗する極めて狭い window（例: filesystem障害）が万一発生した場合、次回実行時のpreflightが「片方だけ登録済み」の状態を`REJECTED_PARTIAL_STATE`として検知し、自動修復せずに停止する。この場合:

1. 秘密値を表示せずに、両ファイルの存在有無だけを確認する。
2. 意図しない残存ファイルと判断した場合は、対象ファイルを手動で削除してから再実行する。
3. 判断に迷う場合は、実行者・実行日時・観測したcanonical result codeを記録した上で承認者に確認する。

## 7. 実PTYでのCLI動作確認（開発・CI向け）

`scripts/pty-smoke-register-acs.py`は合成値だけを使い、実PTY(擬似端末)経由でCLIを起動して
成功・再実行拒否・partial state拒否・secretが端末出力へ現れないことを確認するスクリプトである。
Linux上で次のように実行する。

```bash
dotnet build src/Amane.Mailer/Amane.Mailer.csproj
python3 scripts/pty-smoke-register-acs.py
```

`Console.ReadKey(intercept: true)`によるsecret非表示入力は、unit test（fake console）では
検証できない実端末固有の挙動であるため、このスクリプトで別途確認する。

## 8. Sanitized evidenceとして記録してよいもの

- command名、実行environment区分、canonical result code。
- 成功・拒否の別。

記録しないもの:

- ACS connection string、sender emailの実値。
- raw exception、stack trace、対話入力の内容。

## 9. Verified on（Docker 確認）

2026-07-21、Windows + WSL2 + Docker Engine で確認:

- 使い捨て `.env`（`.env.example` 由来、image=`ghcr.io/kooiei-in4a/amane-mailer:v0.3.0`）で `infra/deploy/compose.yml` に対し `docker compose ... config` を実行。
- マウント境界（compose config + `docker inspect` Mounts）: `mailer` の ACS secret は `:ro` / `RW=false`。`mailer-acs-admin` の ACS secret と platform-sender は read-write（`RW=true`）。`mailer-migrate` には ACS / platform-sender マウントなし。
- Image UID: `docker inspect ghcr.io/kooiei-in4a/amane-mailer:v0.3.0 --format '{{.Config.User}}'` → `1654`。ホスト dir は Linux ネイティブな WSL パス上で `1654:1654` / `0700` を準備（Windows `/mnt/c` の drvfs では `chown` / 実質的な `chmod 0700` 不可）。
- 登録: 公開 `v0.3.0` には `admin provider register-acs` が含まれないため、ブランチからビルドしたローカル image `amane-mailer:pr206-verify`（`Config.User=1654`）で確認。compose profile `acs-admin` の `check-acs-preflight` → `SUCCESS`。実 PTY 経由で `docker compose ... run mailer-acs-admin` に合成入力のみを供給した interactive `register-acs` → `SUCCESS`。`acs_connection_string` と `platform-sender.json` が mode `600` uid/gid `1654` で出現（内容は記録しない）。検証後に合成ファイルを削除。
