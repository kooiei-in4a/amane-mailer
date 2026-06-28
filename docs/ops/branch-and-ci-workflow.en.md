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

### Main Protection Ruleset Snapshot

As of 2026-06-29 JST, the `main protection` ruleset is active. The classic
branch protection API returns `Branch not protected` because `main` is protected
by a repository ruleset.

- Target: `refs/heads/main`
- Pull request rule:
  - `required_approving_review_count: 0`
  - unresolved review threads must be resolved before merge
  - CODEOWNERS review is not required
  - last-push approval is not required
- Required status checks:
  - `Restore, build, and test`
  - `Native AOT publish smoke`
  - `Docker build smoke`
  - `OpenAPI validation`
  - `Analyze (actions)`
  - `Analyze (csharp)`
  - `Analyze (javascript-typescript)`
  - `Local compose fresh data dir`
- Additional rules: required signatures, non-fast-forward block, deletion block

This repository currently keeps required review count at 0 for solo-maintainer
operation. In exchange, maintainers must use this checklist for PRs into `main`
and for release review:

- Keep PRs release-sized, and confirm diff scope plus related issue / release record coverage.
- Confirm required checks match current workflow job names and are all successful or intentionally skipped-success.
- Confirm `docs/ops/public-repository-p0-evidence.md` and the release record cover artifact digests, NuGet package / symbols, security evidence, and known unverifiable items.
- For workflow, release, deployment, Contracts / OpenAPI, Admin security, provider error handling, secret, or PII changes, write an explicit self-review and keep the PR in Draft if another review is needed.
- Because CODEOWNERS currently points to the single repository owner, requiring CODEOWNERS review would not add an independent reviewer by itself. Revisit required review count 1, CODEOWNERS review, and last-push approval when an external reviewer is available.

### develop protection policy

`develop` has a lighter ruleset than `main`. Because `develop` is the integration
branch, PR reviews, Native AOT, Docker smoke, OpenAPI, and CodeQL are not required
there. The only required status check is `Restore, build, and test`.

Direct pushes to `develop` are reserved for maintainer setup and maintenance
checks. Normal feature work uses PRs from `feature/**` or `fix/**` into `develop`.
