# ブランチ戦略と CI 重み付け

本リポジトリは `develop` で機能を蓄積し、まとまった単位で `main` へリリースする運用です。
CI はブランチ経路ごとに重みを分け、feature ブランチの反復 push では待ち時間を短くします。

## ブランチフロー

```
feature/**, fix/**  →（PR）→ develop  ← 機能を複数蓄積
                              ↓ docker compose で統合確認
develop  →（PR・フル CI）→ main
                              ↓ main を develop に同期し戻す（手動 merge）
                            tag → release
```

- 作業ブランチ名は `feature/<topic>` または `fix/<topic>` を使います（エージェント固有名は使いません）。
- `develop` へは小さな PR を積み重ね、ローカルまたは deploy 相当の docker compose で統合確認します。
- `main` へは release 単位の PR のみ。マージ後は **必ず** `main` を `develop` に同期し戻します（下記手順）。

### main マージ後の develop 同期（手動）

自動化は将来 issue で扱います。現時点では maintainer が手動で実行します。

```bash
git fetch origin
git checkout develop
git pull origin develop
git merge origin/main
# 競合があれば解消してから
git push origin develop
```

## CI 重み付け

単一 workflow [`.github/workflows/ci.yml`](../../.github/workflows/ci.yml) 内で job-level `if` により分岐します。
job 名は branch protection の required status checks と一致させるため変更していません。

| トリガー | 実行 job |
|----------|----------|
| `feature/**` / `fix/**` への push | `Restore, build, and test` のみ |
| `develop` への push、または `develop` 向け PR | 上記 + `OpenAPI validation` |
| `main` への push、`main` 向け PR、`workflow_dispatch` | フル CI（下記すべて） |

フル CI に含まれる job:

- `Restore, build, and test`
- `Native AOT publish smoke`
- `Docker build smoke (linux/amd64)` / `Docker build smoke (linux/arm64)` および集約 job `Docker build smoke`
- `Local compose fresh data dir`
- `OpenAPI validation`

`linux/arm64` の Docker build（QEMU エミュレーション）はフル CI のみです。

### 設計上のトレードオフ

`develop` や feature ブランチでは Native AOT と multi-arch Docker は走りません。
そのため **初めて `main` 向け PR を出したとき** に AOT / arm64 で初検出される失敗があり得ます。
release 前の最終ゲートを `main` PR のフル CI に置く意図的なトレードオフです。

判定に迷う経路は fail-secure でフル CI 側に倒します（例: `workflow_dispatch`）。

### concurrency

`concurrency.group: ci-${{ github.workflow }}-${{ github.ref }}` と `cancel-in-progress: true` により、
同一 ref への連続 push では進行中 run がキャンセルされます。push と pull_request は ref が異なるため別 group になります。

### branch protection との整合

required status checks の job 名は変更していません。`develop` 向け PR では重い job は **Skipped** となり、
GitHub は skipped job を required check 上成功扱いにします。`main` 向け PR では従来どおり全 required job が実行されます。
ruleset 自体の変更が必要な場合は別 issue で扱います。
