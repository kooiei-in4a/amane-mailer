# バックアップ運用

Mailer SQLite DB のバックアップ取得・保管・暗号化に関する運用手順です。

## 取得方法

| 方法 | 用途 |
|------|------|
| `dotnet Amane.Mailer.dll db backup <absolute-path>` | コンテナ exec / cron からの CLI バックアップ（従来どおり） |
| Admin UI `/admin/ops` | `AMANE_ADMIN_DB_OPS_ENABLED=true` かつ break-glass / 全 effective tenant scope 管理者のみ |

CLI と Admin のどちらも **オンライン SQLite Backup API** を使い、稼働中の DB から取得します。

## Admin 経由 backup の注意

- **既定では無効**です。`AMANE_ADMIN_ENABLED=true` だけでは DB 操作は有効になりません。`AMANE_ADMIN_DB_OPS_ENABLED=true` を明示してください（fallback: `MAILER_ADMIN_DB_OPS_ENABLED`）。
- backup は **service-wide** 操作です。全 tenant の mail payload（宛先・件名・本文・metadata 等）を含みます。**PII を平文で含む**ため、保存先のアクセス制御・暗号化・転送経路を runbook どおりに運用してください。
- Admin backup の出力先は **固定ディレクトリ**のみです。UI/API からパスを指定できません。
  - 既定: mailer DB と同じ親ディレクトリ配下の `backups/`
  - 上書き: `AMANE_ADMIN_DB_BACKUP_DIRECTORY`（fallback: `MAILER_ADMIN_DB_BACKUP_DIRECTORY`）に **絶対パス**を設定
  - ファイル名: `mailer-<UTC-timestamp>.db`（例: `mailer-20260704T045100Z.db`）
- 操作は `admin_audit_events` に記録されます（`db_ops.backup_requested` / `db_ops.backup_completed` / `db_ops.backup_failed`）。監査ログには **絶対パスは記録しません**（保存先種別のみ）。
- checkpoint / backup は **同時実行不可**です。実行中に 409 Conflict になります。
- 本番では `infra/deploy/backup-mailer.sh` による age 暗号化 + offsite 転送を推奨します。Admin backup は緊急時の平文スナップショット取得向けです。

## 関連ドキュメント

- [restore-verification.md](restore-verification.md) — リストア検証
- [restore-procedure.md](restore-procedure.md) — 本番リストア手順
- [ADR 0013 D-09](../adr/0013-admin-threat-model-and-pii-policy.md) — Admin DB 操作の threat model
