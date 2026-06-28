# Branch strategy and CI weighting

This repository accumulates work on `develop` and releases to `main` in batched PRs.
CI is weighted by branch path so repeated pushes on feature branches stay fast.

## Branch flow

```
feature/**, fix/**  → (PR) → develop  ← accumulate multiple features
                               ↓ integration check with docker compose
develop  → (PR, full CI) → main
                               ↓ sync main back into develop (manual merge)
                             tag → release
```

- Use `feature/<topic>` or `fix/<topic>` for work branches (no agent-specific names).
- Stack small PRs into `develop` and verify integration locally or with docker compose.
- Merge to `main` only in release-sized PRs. After each merge, **always** sync `main` back into `develop` (steps below).

### Sync develop after a main merge (manual)

Automation is tracked as a future issue. Maintainers run this manually for now:

```bash
git fetch origin
git checkout develop
git pull origin develop
git merge origin/main
# resolve conflicts if any, then
git push origin develop
```

## CI weighting

A single workflow [`.github/workflows/ci.yml`](../../.github/workflows/ci.yml) uses job-level `if` conditions.
Job names are unchanged so branch protection required status checks stay aligned.

| Trigger | Jobs run |
|---------|----------|
| Push to `feature/**` / `fix/**` | `Restore, build, and test` only |
| Push to `develop`, or PR targeting `develop` | Above + `OpenAPI validation` |
| Push to `main`, PR targeting `main`, `workflow_dispatch` | Full CI (all jobs below) |

Full CI includes:

- `Restore, build, and test`
- `Native AOT publish smoke`
- `Docker build smoke (linux/amd64)` / `Docker build smoke (linux/arm64)` and aggregate `Docker build smoke`
- `Local compose fresh data dir`
- `OpenAPI validation`

The `linux/arm64` Docker build (QEMU emulation) runs on full CI only.

### Intended trade-off

Native AOT and multi-arch Docker do not run on `develop` or feature branches.
Failures may therefore appear **for the first time on a PR to `main`**. That is intentional: the release gate is full CI on `main` PRs.

Ambiguous paths fail secure toward full CI (for example `workflow_dispatch`).

### concurrency

`concurrency.group: ci-${{ github.workflow }}-${{ github.ref }}` with `cancel-in-progress: true` cancels in-progress runs for the same ref on rapid pushes. Push and pull_request use different refs and therefore different groups.

### Branch protection alignment

Required status check job names are unchanged.

- Standalone jobs (Native AOT, compose smoke, etc.): **Skipped** on light paths; GitHub treats skipped required checks as successful.
- Aggregate job `Docker build smoke` (`docker-build-smoke-required`): **Always runs** even when the matrix is skipped, and returns success when the matrix result is skipped. This avoids a `needs` + matching `if` skip chain that can leave required checks pending.
- On PRs to `main`, the matrix runs and pass/fail is reflected normally.

Ruleset changes, if needed, are out of scope for the CI weighting change itself.
