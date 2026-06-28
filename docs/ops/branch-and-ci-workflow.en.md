# Branch strategy and CI weighting

This repository accumulates work on `develop` and releases to `main` in batched PRs.
CI is weighted by branch path so repeated pushes on feature branches stay fast.

## Branch flow

```
feature/**, fix/**  → (PR) → develop  ← accumulate multiple features
                               ↓ integration check with docker compose
develop  → (PR, release-gate CI) → main
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
| PR targeting `main` | Release-gate CI (Native AOT, amd64 Docker, compose smoke, OpenAPI) |
| Push to `main`, `workflow_dispatch` | Final CI (above + arm64 Docker) |

Release-gate CI includes:

- `Restore, build, and test`
- `Native AOT publish smoke`
- `Docker build smoke (linux/amd64)` and aggregate `Docker build smoke`
- `Local compose fresh data dir`
- `OpenAPI validation`

The `linux/arm64` Docker build (QEMU emulation) is the slowest Docker smoke path,
so it runs only on `main` push and `workflow_dispatch` final CI.

### Intended trade-off

Native AOT and Docker smoke are minimized on `develop` and feature branches.
Native AOT or amd64 Docker failures may therefore appear **for the first time on
a PR to `main`**. arm64 Docker failures may appear **for the first time on the
post-merge push to `main`**. That is intentional: release PRs stay faster while
the final `main` commit still receives multi-arch Docker coverage.

Ambiguous paths fail secure toward final CI (for example `workflow_dispatch`).

### concurrency

`concurrency.group: ci-${{ github.workflow }}-${{ github.ref }}` with `cancel-in-progress: true` cancels in-progress runs for the same ref on rapid pushes. Push and pull_request use different refs and therefore different groups.

### Branch protection alignment

Required status check job names are unchanged.

- Standalone jobs (Native AOT, compose smoke, etc.): **Skipped** on light paths; GitHub treats skipped required checks as successful.
- Aggregate job `Docker build smoke` (`docker-build-smoke-required`): **Always runs** even when the matrix is skipped, and returns success when the matrix result is skipped. This avoids a `needs` + matching `if` skip chain that can leave required checks pending.
- On PRs to `main`, the amd64 matrix runs and pass/fail is reflected in the aggregate job.
- On pushes to `main` and `workflow_dispatch`, the amd64 / arm64 matrix runs.

### develop protection policy

`develop` has a lighter ruleset than `main`. Because `develop` is the integration
branch, PR reviews, Native AOT, Docker smoke, OpenAPI, and CodeQL are not required
there. The only required status check is `Restore, build, and test`.

Direct pushes to `develop` are reserved for maintainer setup and maintenance
checks. Normal feature work uses PRs from `feature/**` or `fix/**` into `develop`.
