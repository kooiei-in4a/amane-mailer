# ADR 0013: 管理画面の脅威モデル・公開範囲・PII 取り扱い方針

- **Status:** Accepted
- **Date:** 2026-06-24
- **適用範囲:** Amane.Mailer 管理画面

## Context

Amane.Mailer は `mail_request` の永続化 payload に宛先、件名、HTML 本文、テキスト本文、reply-to、metadata を保持する。これらはメール配送のために必要だが、管理画面で閲覧できるようにすると PII と本文内容を扱う強い権限面が新しく生まれる。

管理画面は障害対応、再送、キャンセル、DB 状態確認、backup/checkpoint などの運用を楽にする一方、以下のリスクを持つ。

- 認証なし、または公開範囲の誤設定によるインターネット露出
- 管理者アカウントの漏洩による宛先・件名・本文の閲覧
- 一覧画面、ログ、スクリーンショット、画面共有からの PII 漏洩
- HTML 本文プレビューによる XSS、外部リソース読み込み、追跡ピクセル発火
- 再送・キャンセル・DB 操作の誤操作、または操作証跡の欠落

本 ADR は後続の Admin UI AOT 互換性検証、Admin API・認証境界・StaticFiles 配信基盤、一覧画面、管理操作監査ログ（[#6](https://github.com/kooiei-in4a/amane-mailer/issues/6)）、DB 操作の実装前提を凍結する。

## Decision

### D-01. 管理画面はデフォルト無効

`/admin` は `MAILER_ADMIN_ENABLED=true` が明示された場合のみ有効にする。既定値は `false` とし、設定未指定・誤記・無効値では fail-closed にする。

`MAILER_ADMIN_ENABLED=false` の場合、管理画面の静的ファイル、Admin API、認証エンドポイント、管理操作 CLI 連携 API は登録しない。reverse proxy 側で誤ってルーティングされてもアプリ内で 404 になることを期待値とする。

### D-02. インターネット直接公開は想定しない

Amane.Mailer 管理画面は VPN、LAN、SSH tunnel、または認証済み reverse proxy 配下の運用者用画面とする。裸のインターネットへ直接公開する構成は採用しない。

インターネット到達可能な reverse proxy を経由する場合でも、少なくとも以下を運用前提に含める。

- HTTPS 終端
- 管理画面パスへの IP allowlist または VPN 相当の到達制限
- proxy から Mailer への内部ネットワーク接続
- `X-Forwarded-*` の信頼境界を compose / Caddy / nginx 設定で固定

公開インターネット向けに管理画面を提供する必要が出た場合は、OIDC、MFA、レート制限、WAF、セッション保護、監査要件を含む別 ADR を起こす。

### D-03. 管理画面の request local address allowlist を制限できるようにする

管理画面の公開先は `AMANE_ADMIN_ALLOWED_LOCAL_ADDRESS` で request-time に制限できるようにする。これは `/admin` request の `Connection.LocalIpAddress` allowlist であり、Kestrel の socket bind / listener 設定ではない。既定値は `127.0.0.1` とし、`Connection.LocalIpAddress` が取得できない場合は fail-closed にする。旧 `AMANE_ADMIN_BIND` / `MAILER_ADMIN_BIND` は deprecated alias として扱う。

現行実装では `Program.cs` で管理画面専用 listener を分けず、`ASPNETCORE_URLS` と deploy / Docker 側の `ports`、reverse proxy、Docker network、firewall による到達制限を実際の network 境界として扱う。Docker 内で `ASPNETCORE_URLS=http://+:8080` を使う場合でも、host 側 publish address と proxy ACL を管理画面基盤の受け入れ条件に含める。

`0.0.0.0` / `::` は任意の local listener address を許可する allowlist 値であり、外部公開 bind を意味しない。ただし管理画面へ到達できる network 面を広げる構成で使う場合は、`AMANE_ADMIN_ENABLED=true` と別に明示設定を必要とし、運用ドキュメントで host publish、reverse proxy、network ACL を確認してから使う。

### D-04. 認証方式は Cookie + PBKDF2 パスワードを第一候補にする

ブラウザ UX、自己完結性、reverse proxy 依存の低さを優先し、アプリ内の Cookie session + PBKDF2 パスワード認証を採用する。Native AOT 互換性を検証し、互換性に問題がないことを管理画面基盤の実装前提にする。

管理者アカウントは Consumer アプリの `admin_users` とは共有しない。Mailer は自身の SQLite DB に管理者・権限・セッションの正本を持ち、Consumer 側のロール・所有権とは独立して運用する。

最低要件は以下とする。

- パスワード hash は PBKDF2-HMAC-SHA256 以上、salt はユーザーごと、反復回数は設定可能。既定値と production 下限は 600,000 回以上とし、AOT 互換性・負荷検証で上げることはできるが下げない
- session cookie は `HttpOnly`, `Secure`, `SameSite=Strict`
- session は絶対有効期限とアイドルタイムアウトを持つ。初期値は絶対 12 時間、アイドル 30 分とし、短縮は可能だが production での無期限化は禁止する
- パスワード変更、管理者無効化、権限変更、tenant scope 変更時は対象管理者の既存 session を即時失効する
- 同時 session 数は上限を設定可能にし、production では管理者ごとの無制限同時 session を禁止する
- ログイン成功、ログイン失敗、ログアウト、セッション失効、ログイン試行制限の発火を監査対象にする
- ログイン失敗はアカウント名や存在有無を漏らさない汎用エラーにする
- ログイン失敗には IP / account 単位の rate limit、progressive delay、または一時 lockout を必須とする
- CSRF が成立する state-changing API は CSRF token または same-site cookie 前提の追加ヘッダを必須にする

Basic 認証は開発・一時検証・reverse proxy 側の追加防御としては許可するが、アプリ内の主認証にはしない。`X-Remote-User` など reverse proxy 認証ヘッダだけで管理者を成立させる方式、Bearer token 単独方式は MVP のブラウザ管理画面では採用しない。

### D-05. 一覧画面の PII はデフォルトでマスクする

送信依頼一覧では、宛先メールアドレスと件名をデフォルトでマスク表示する。状態、作成日時、更新日時、tenant、source_service、purpose、retry count、最終エラー種別など、運用判断に必要で PII ではない情報を優先して表示する。

一覧で宛先・件名の非マスク表示が必要な運用では、`MAILER_ADMIN_PII_LIST_MODE=visible` の明示設定を必要とする。既定値は `masked` とし、設定未指定では非マスクにしない。非マスク表示を許す環境では、D-02 の到達制限と D-08 の監査を必須とする。

マスク表示の最低要件は以下とする。

- メールアドレスは local-part と domain の一部だけを残す、または完全マスクする
- 件名は先頭数文字だけ、または完全マスクする
- payload JSON 全体を一覧レスポンスに含めない
- API レスポンスにもマスク済みフィールドと非マスク権限を分ける

### D-06. 本文はデフォルト非表示、展開は明示操作にする

`html_body` と `text_body` は詳細画面でもデフォルト非表示にする。本文を見るには管理者が明示的に「本文表示」操作を行う。

本文表示の最低要件は以下とする。

- 本文表示操作は監査ログに必ず記録する
- HTML 本文はそのまま親 DOM に挿入しない
- HTML プレビューを行う場合は sandboxed iframe、script 禁止、外部リソース読み込み禁止、またはサニタイズ済み表示に限定する
- text body は HTML escape して表示する
- 本文をサーバーログ、ブラウザ console、監査ログ metadata に出力しない

### D-07. PII 表示と操作権限を分離する

管理画面の初期ロールは単一 admin でもよいが、実装境界として「一覧を見る権限」「非マスク PII を見る権限」「本文を見る権限」「再送・キャンセルする権限」「DB 操作する権限」を分離できる形にする。

MVP で単一ロールに畳む場合でも、API と service 層では capability 名を分け、将来のロール分割時に本文表示・DB 操作を切り離せるようにする。

### D-08. 管理操作監査ログは必須

以下のイベントは監査ログへ記録する。

| 分類 | イベント |
|------|----------|
| 認証 | login success, login failure, logout, session expired |
| 認証防御 | login rate limited, account temporarily locked |
| PII 閲覧 | list unmasked, recipient/subject reveal, body reveal |
| 配送操作 | retry requested, cancel requested |
| DB 操作 | checkpoint requested, backup requested, backup completed, backup failed |
| 設定・保守 | admin enabled startup summary, DB ops enabled startup summary |

監査ログには actor、時刻、正規化した source IP または proxy forwarded IP、user agent summary、対象 `mail_request_id`、操作結果、エラーコードを記録する。内部管理画面のインシデント調査では raw IP が必要になるため、source IP は監査保持期間内に限って記録を許容する。組織ポリシー上 raw IP を残せない環境では `MAILER_ADMIN_AUDIT_HASH_NETWORK_IDENTIFIERS=true` で source IP と user agent を keyed hash にできるようにする。

宛先、件名、本文、payload JSON、完全な user agent、`X-Forwarded-For` 全 chain のような PII または過剰な長期識別子は原則として入れない。必要な場合はハッシュ化、分類値、または短い理由コードにする。

監査ログは append-only とし、通常 UI から任意削除できない。保持期間は `MAILER_ADMIN_AUDIT_RETENTION_DAYS` で設定し、既定値は 180 日とする。保持期限を過ぎた監査ログは sweep または明示 CLI で削除できる。保持期間を 30 日未満にする設定は、ローカル開発以外では禁止する。

監査ログの正本は Mailer SQLite DB 内に置く。管理操作は低頻度であり、配送 payload の永続化より高い write load を想定しない。改ざん耐性や外部保全が必要な環境では、同じイベントを PII を含まない構造化ログとして stdout にも出し、ログ収集基盤で追加保持できるようにする。ただし stdout だけを正本にする構成は、ログ収集基盤の有無に依存するため MVP の標準とはしない。

### D-09. DB 操作は明示 opt-in のみ許可する

管理画面から SQLite `checkpoint` / `backup` を実行する機能は、`MAILER_ADMIN_DB_OPS_ENABLED=true` が明示された場合だけ有効にする。既定値は `false` とする。

DB 操作の最低要件は以下とする。

- `MAILER_ADMIN_ENABLED=true` だけでは DB 操作を有効化しない
- DB 操作 API は管理画面認証に加えて DB ops capability を要求する
- backup は全 tenant の PII を含む service-wide 操作であるため、tenant 限定管理者には許可しない。break-glass 管理者または全 tenant scope を持つ管理者だけが実行できる
- backup の出力先は設定済み allowlist 配下に限定する
- backup 実行結果と保存先種別を監査ログに記録するが、絶対パスや secret を過剰に出さない
- checkpoint / backup 中の二重実行を防止する

DB 操作を UI で提供しない構成でも、CLI による `db checkpoint` / `db backup` は既存運用として存続できる。

### D-10. 共有 Mailer のテナント可視範囲を制限する

共有 Mailer は develop、staging、production の複数 tenant を同一 SQLite DB に保持できる。管理画面は tenant を第一級の認可境界として扱い、管理者ごとに許可された `tenant_id` だけを一覧・詳細・本文表示・再送・キャンセル・DB 集計の対象にする。

既定では管理者作成時に少なくとも 1 つの tenant scope を明示する。develop / staging 管理者が production tenant を閲覧できる状態、または production 管理者が非 production の検証メール本文を閲覧できる状態を暗黙には作らない。全 tenant を横断できる break-glass 管理者は明示作成のみ許可し、ログインと PII 閲覧を通常管理者より強く監査する。

tenant scope は管理画面の認可境界であり、SQLite ファイルやプロセスの物理分離ではない。ADR 0012 D-04a の shared Mailer service を前提とする限り、noisy neighbor、DB ファイル破損、service-wide backup などのリスクは共有される。production / staging / develop の物理分離を原則化する場合は、本 ADR ではなく ADR 0012 または運用 ADR を更新する。

### D-11. SQLite deployment の管理画面は単一 Mailer プロセスを前提にする

ADR 0012 D-04a と `infra/deploy/compose.yml` の形に合わせ、現在の SQLite deployment の管理画面は単一 Native AOT container / 単一 Mailer プロセスで運用する。管理 API や管理画面だけを水平スケールさせる構成は現在の運用対象外とする。

session、login throttle、tenant scope、監査ログ、DB 操作ロックの正本は Mailer SQLite DB に置く。単一プロセス内のメモリキャッシュや `SemaphoreSlim` は最適化には使えるが、正本にはしない。将来 Mailer API / Admin API を複数プロセスで動かす場合は、session 失効確認、login throttle、checkpoint / backup の二重実行防止を SQLite transaction または分散ロックで成立させる設計に更新する。

PBKDF2 の 600,000 回以上という下限は AOT 互換性・負荷検証で低リソース環境の CPU 負荷も測る。検証では、ログイン失敗の連打が配送 worker、HTTP readiness、healthcheck に与える影響を確認し、必要ならログイン endpoint の並列数制限を管理画面基盤要件に含める。

## Alternatives Considered

| 案 | 却下理由 |
|----|----------|
| 管理画面を常時有効にする | Mailer は配送 payload に PII を保持するため、設定漏れで閲覧面が露出するリスクが大きい。fail-closed を優先する |
| インターネット直接公開を前提にする | MFA/OIDC/WAF/専用レート制限などが必要になり、MVP の運用者向け画面として過剰。VPN/LAN/reverse proxy 配下に限定する |
| Basic 認証を主認証にする | 実装は単純だが、ログアウト、失敗監査、UX、将来の権限分離が弱い。HTTPS と proxy 設定への依存も強い |
| reverse proxy 認証ヘッダだけを信頼する | AOT 問題は避けやすいが、proxy 設定ミスが即認証バイパスになる。追加防御としてはよいが主認証にはしない |
| Bearer token 単独方式 | API には単純で AOT 互換性も高いが、ブラウザ管理画面の UX が悪く、token 漏洩時の操作証跡も弱い |
| 一覧で宛先・件名を常時表示する | 障害対応は楽になるが、画面共有やスクリーンショットでの漏洩面が大きい。デフォルトはマスクし、必要環境だけ明示 opt-in にする |
| 本文を詳細画面で即表示する | 本文は最も強い PII 面であり、HTML XSS/外部リソース発火のリスクもある。明示展開と監査を必須にする |
| DB 操作を管理画面で常時許可する | checkpoint / backup は運用上有用だが、誤操作・保存先漏洩・負荷集中のリスクがある。明示 opt-in と監査を必須にする |

## Consequences

- _positive:_ 管理画面は既定で存在しないため、Mailer を配送 API と worker として運用するだけの環境では新しい閲覧面が増えない。
- _positive:_ Cookie + PBKDF2 を第一候補として AOT 互換性を検証すればよく、管理画面基盤は fail-closed、request local address allowlist、CSRF、監査 hook を基盤要件にできる。
- _positive:_ 一覧画面は PII マスクと本文非表示を前提に設計でき、非マスク表示や本文表示は明示操作として扱える。
- _positive:_ 管理操作監査ログの永続化（[#6](https://github.com/kooiei-in4a/amane-mailer/issues/6)）は監査対象イベントと保持期間の初期値を本 ADR から引ける。
- _positive:_ DB 操作は `MAILER_ADMIN_DB_OPS_ENABLED=true` を必須前提にできる。
- _negative:_ 障害対応時に宛先・件名・本文を即時確認できず、1 クリック以上の操作と監査ログが増える。
- _negative:_ request local address allowlist と reverse proxy / network ACL による到達制限が必要になり、compose と運用手順の確認項目が増える。
- _negative:_ PII マスク、本文 sandbox、監査保持 sweep など、単純な CRUD 管理画面より実装範囲が広がる。
- _negative:_ 管理者・session・tenant scope・login throttle の永続化が Mailer DB に追加され、単純な reverse proxy 認証よりデータモデルと migration が増える。
- _negative:_ tenant scope を厳格にすると、共有 Mailer 障害時に横断確認できる担当者が限られる。break-glass 管理者の発行・保管・監査手順が必要になる。
- _negative:_ 監査ログを SQLite DB に保持するため、管理操作が多い環境では Mailer DB の write load とファイルサイズを監視する必要がある。
- _negative:_ 現在の SQLite deployment は単一 Mailer プロセス前提のため、水平スケールで管理画面を動かすには session / throttle / DB 操作ロックを再設計する必要がある。
- _neutral:_ tenant scope は誤閲覧を防ぐが、環境ごとの物理 DB 分離を提供しない。production と非 production を同一 shared Mailer に同居させる運用リスクは ADR 0012 側の判断として残る。
- _operational:_ PII 表示を許す環境では、画面共有、録画、スクリーンショット、ブラウザ拡張、端末ロック、共有アカウント利用を運用ルールで制限する。非マスク一覧や本文表示は「便利機能」ではなく「監査される例外操作」として扱う。
- _operational:_ backup ファイルは Mailer DB と同等以上に PII を含む。保存先権限、暗号化、世代管理、転送経路、削除確認を運用 runbook に含める。

## 実装ステータス（2026-06-27 時点）

本 ADR は方針・目標・最低要件を定める。以下の項目は現時点で未実装であり、
ADR の「目標」と「現在の実装」を混同しないよう明記する。

| 項目 | ADR 参照 | 現状 | 追跡 |
|------|----------|------|------|
| durable login throttle | D-04 | in-memory `ConcurrentDictionary` のみ（プロセス再起動でリセット） | — |
| durable server-side session store・session 即時失効 | D-04 | cookie auth のみ（server-side store なし、無効化・認証情報変更時の即時失効なし） | — |
| 管理者ごとの tenant scope | D-10 | 未実装（単一 `AMANE_ADMIN_USERNAME` / `AMANE_ADMIN_PASSWORD_HASH`） | — |
| 管理操作監査ログの SQLite 永続化 | D-08 | body view（fail-closed）と login success/failure を `admin_audit_events` に永続化（stdout にもミラー）。logout / session expired / login rate limited、retention sweep、network identifier の hash 化は未実装 | [#6](https://github.com/kooiei-in4a/amane-mailer/issues/6) |

上記未実装項目が残る間、Admin UI は **内部ネットワーク向け・experimental** な位置づけとして運用する（D-02 に準拠）。

## References

- [ADR 0012: メール送信マイクロサービス](0012-mail-via-mailer-microservice.md)
- [ADR 0013 Supplement: Admin UI Native AOT Compatibility](0013-admin-ui-aot-compatibility-supplement.md)
