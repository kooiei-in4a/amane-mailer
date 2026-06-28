# ブランチ戦略と CI 重み付け

本リポジトリは `develop` で機能を蓄積し、まとまった単位で `main` へリリースする運用です。
CI はブランチ経路ごとに重みを分け、feature ブランチの反復 push では待ち時間を短くします。

## ブランチフロー

```
feature/**, fix/**  →（PR）→ develop  ← 機能を複数蓄積
                              ↓ docker compose で統合確認
develop  →（PR・release gate CI）→ main
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
| `main` 向け PR | release gate CI（Native AOT、amd64 Docker、compose smoke、OpenAPI） |
| `main` への push、`workflow_dispatch` | 最終 CI（上記 + arm64 Docker） |

release gate CI に含まれる job:

- `Restore, build, and test`
- `Native AOT publish smoke`
- `Docker build smoke (linux/amd64)` および集約 job `Docker build smoke`
- `Local compose fresh data dir`
- `OpenAPI validation`

`linux/arm64` の Docker build（QEMU エミュレーション）は時間が長いため、`main` への push と
`workflow_dispatch` の最終 CI のみで実行します。

### 設計上のトレードオフ

`develop` や feature ブランチでは Native AOT と Docker smoke は最小限に抑えます。
Native AOT や amd64 Docker の失敗は **初めて `main` 向け PR を出したとき** に検出される場合があります。
arm64 Docker の失敗は **`main` への merge 後 push** で初検出される場合があります。
release PR の待ち時間を短くしつつ、最終的な `main` コミットでは multi-arch Docker を確認するための
意図的なトレードオフです。

判定に迷う経路は fail-secure で最終 CI 側に倒します（例: `workflow_dispatch`）。

### concurrency

`concurrency.group: ci-${{ github.workflow }}-${{ github.ref }}` と `cancel-in-progress: true` により、
同一 ref への連続 push では進行中 run がキャンセルされます。push と pull_request は ref が異なるため別 group になります。

### branch protection との整合

required status checks の job 名は変更していません。

- 単体 job（Native AOT、compose smoke 等）: 軽量経路では **Skipped**。GitHub は skipped job を required check 上成功扱いにします。
- 集約 job `Docker build smoke`（`docker-build-smoke-required`）: matrix 本体が skip されても **常に実行** し、matrix が skipped のときは success を返します。`needs` と同一 `if` による skip 連鎖で required check が pending のままになるのを防ぐためです。
- `main` 向け PR では amd64 matrix が実行され、成否が集約 job に反映されます。
- `main` への push と `workflow_dispatch` では amd64 / arm64 matrix が実行されます。

### develop protection 方針

`develop` には `main` より軽い ruleset を付けます。`develop` は機能統合の実験場なので
PR review、Native AOT、Docker smoke、OpenAPI、CodeQL は required にしません。ただし最低限の
品質ゲートとして required status check は `Restore, build, and test` のみ必須にします。

`develop` への直接 push は初期作成や保守確認など maintainer の確認目的に限ります。通常の機能開発は
`feature/**` または `fix/**` から `develop` への PR で行います。
