# Amane Mailer Release Workflow for AI Agents

Use this workflow for maintainer-authorized Amane Mailer releases. The default release mode is a **full service release**: Git tag, GHCR image, `Amane.Mailer.Contracts` NuGet package, OpenAPI version, CHANGELOG, release record, and GitHub Release all represent one `X.Y.Z` release identity.

Partial publication such as OCI-only is allowed only when the maintainer explicitly requests it in the current session. Do not silently narrow a full release into an image-only release.

## Inputs

Required before any release mutation:

- Target version: explicit `X.Y.Z`; never infer it.
- Target GitHub issue: required.
- Canonical branch: `main` unless the maintainer explicitly states otherwise.
- Release mode: `FULL_SERVICE` by default.
- Human authorization: required for each side-effecting release boundary.

Primary release artifacts for a full service release:

- Git tag `vX.Y.Z`.
- `src/Amane.Mailer.Contracts/Amane.Mailer.Contracts.csproj` `<Version>X.Y.Z</Version>`.
- `docs/api/openapi.yaml` `info.version: "X.Y.Z"`.
- `CHANGELOG.md` entry for `X.Y.Z`.
- `docs/releases/vX.Y.Z.md` release record.
- GHCR `ghcr.io/kooiei-in4a/amane-mailer:vX.Y.Z`.
- Immutable GHCR tag `ghcr.io/kooiei-in4a/amane-mailer:sha-<releaseCommitSha>`.
- NuGet `Amane.Mailer.Contracts X.Y.Z` plus symbol package.
- GitHub Release `vX.Y.Z`.

The Git tag target, GHCR OCI revision/source, NuGet SourceLink revision, and version-preparation source must describe the same release source identity.

## Hard Rules

- Read this workflow before inventing any ad-hoc release procedure.
- Never publish from a dirty, diverged, ambiguous, or stale local repository.
- Refresh remote state before every irreversible boundary.
- Once `releaseCommitSha` is frozen, do not silently change source identity.
- Do not create or move an existing release tag to a different commit.
- Do not overwrite existing GHCR version or immutable SHA tags.
- Do not republish an existing NuGet package version.
- Do not create or move GHCR `latest` except through the guarded `promote-latest` path after versioned publication and consumer verification PASS, and only with current-session maintainer authorization.
- `latest` is a mutable closeout alias, not a release source. It must point at an already verified digest with **no rebuild**.
- Never blind-retry an ambiguous publish command or workflow dispatch. Determine whether the first mutation happened before taking any next action.
- Preserve the `release` environment Human approval boundary. AI agents must not bypass required reviewers.
- Side-effecting release operations require explicit maintainer instruction in the current session.
- `prepare-post-sync -Execute` is a mutation boundary. If it has already changed files and later local validation fails, preserve and recover that exact candidate; do **not** rerun `-Execute` merely to regenerate it.
- `verify-public-release-image.yml` is recovery for an already-published image whose post-publish verification did not complete. It is not the normal publication path and must not build, log in, push, or mutate tags.

## Why GHCR Publication Comes Before Git Tag / NuGet

The current `.github/workflows/publish-release-image.yml` requires canonical `refs/heads/main` and binds the requested `source_sha` to the workflow run's `GITHUB_SHA`. Therefore the normal image publication path can publish only the exact current `main` workflow commit.

For a stable full release, freeze `main` and dispatch the image workflow first while that identity is still current. Once that workflow is dispatched, GitHub pins its source commit for the run. After the image is publicly verified, create the immutable Git release tag at the same `releaseCommitSha`, then publish NuGet from that tag.

This order minimizes the risk of publishing a Git tag/NuGet package first and then discovering that `main` advanced so the normal image workflow can no longer publish the same source identity.

## Phase 0 — Explore Current Authority

Before editing or publishing, inspect and report:

- Target issue body, comments, checklist, scope, and non-goals.
- Current `main` SHA.
- Actual local clone path and `origin` URL.
- Local branch, HEAD, worktree state, ahead/behind/divergence.
- Current versions in:
  - `Amane.Mailer.Contracts.csproj`.
  - `docs/api/openapi.yaml`.
  - latest `CHANGELOG.md` release entry.
  - relevant `docs/releases/` record.
- Current release workflows and approval boundaries:
  - `.github/workflows/publish-contracts.yml`.
  - `.github/workflows/publish-release-image.yml`.
  - `.github/workflows/verify-public-release-image.yml`.
  - `.github/workflows/promote-release-latest.yml` (digest-preserving `latest` alias only).
- Existing target version in Git tags, GitHub Releases, GHCR tags, and NuGet.

For local Git operations, disable pagers and prefer non-interactive commands.

```text
GIT_PAGER=cat
PAGER=cat
```

Safe synchronization is fast-forward-only. Do not use `reset --hard`, rebase, force push, or discard local changes to make a release appear clean.

### Canonical read-only status (RO-1)

Reconstruct current release state with the repository client. `Version` is required and is never inferred. `status` is observation-only: it does not update refs, dispatch workflows, or publish.

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\release.ps1 status -Version X.Y.Z
```

Stdout is a stable `KEY=VALUE` summary. Diagnostics go to stderr. `MUTATION_PERFORMED=FALSE` on every `status` path. Treat `CONFLICT` / `INCOMPLETE` and `NEXT_ACTION=STOP` as a halt before any release mutation.

Self-test (fixture-backed; does not use live GitHub / GHCR / NuGet as pass/fail):

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\release-client-self-test.ps1
```

The self-test must also be independent of the caller repository's live `release/current-public.json` value. Tests of current-public observation must use controlled fixtures for predecessor and target authority states. Advancing a legitimate post-sync working tree from the predecessor version to `X.Y.Z` must not make the self-test fail merely because the live authority value changed.

RO-3 implements read-only `status`, `preflight`, and `verify`. M-1 adds guarded mutation commands that require explicit `-Execute`.

### Canonical read-only preflight (RO-2)

`preflight` is the first-mutation gate. Both `-Version` and `-ReleaseCommitSha` are required; the client never infers source SHA. The command is observation-only: it does not update refs, dispatch workflows, approve environment gates, or publish.

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\release.ps1 preflight `
  -Version X.Y.Z `
  -ReleaseCommitSha <40-lowercase-hex>
```

Stdout is a stable `KEY=VALUE` contract. Diagnostics go to stderr. Every path prints:

```text
HUMAN_AUTHORIZATION_REQUIRED=TRUE
MUTATION_PERFORMED=FALSE
```

`TECHNICAL_READINESS=READY` is not Human authorization. READY requires source binding, version preparation, all public collisions ABSENT, canonical workflow semantic identity, and no existing source-bound publish workflow runs. Deterministic mismatch is `FAIL`. Missing required observation is `INCOMPLETE`. Aggregation is `FAIL` over `INCOMPLETE` over `PASS`. STOP is reported in stdout; it is not a CLI crash (`exit 0` once the contract is generated).

Do not treat an existing matching Git tag, GHCR tag, NuGet package, GitHub Release, or source-bound publish run as READY. Already-applied / recovery decisions belong to later mutation slices.

### Canonical read-only verify (RO-3)

`verify` is the final cross-artifact identity gate for a **published** full release. Both `-Version` and `-ReleaseCommitSha` are required; the client never infers source SHA. The command is observation-only.

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\release.ps1 verify `
  -Version X.Y.Z `
  -ReleaseCommitSha <40-lowercase-hex>
```

Stdout is a stable `KEY=VALUE` contract. Diagnostics go to stderr. Every path prints `MUTATION_PERFORMED=FALSE`.

Required published identities (Phase 9):

| Check | Required identity |
|---|---|
| Git tag | `vX.Y.Z -> releaseCommitSha` |
| Contracts source | `<Version>X.Y.Z</Version>` at `releaseCommitSha` |
| OpenAPI | `info.version == X.Y.Z` at `releaseCommitSha` |
| NuGet package | `Amane.Mailer.Contracts X.Y.Z` public |
| NuGet source | nuspec `<repository commit>` == `releaseCommitSha` |
| GHCR version tag | `vX.Y.Z -> publicDigest` |
| GHCR SHA tag | `sha-<releaseCommitSha> -> same publicDigest` |
| OCI labels | version `X.Y.Z`, revision == `releaseCommitSha` |
| GitHub Release | tag `vX.Y.Z`, non-draft/non-prerelease |
| Release record | `PUBLISHED` at `releaseCommitSha`; recorded digest/commit facts match public observations |

Semantics:

```text
verified absence of required artifact     -> ABSENT (FAIL)
exact expected identity                   -> EXACT_MATCH
identity contradiction                    -> CONFLICT (FAIL)
network/auth/rate-limit/5xx/parse/tool    -> INCOMPLETE
```

Aggregation is `FAIL` over `INCOMPLETE` over `PASS`. Transport/auth failure is never treated as ABSENT. `VERIFY_RESULT=PASS` means all required identities verified; it is not Human authorization to mutate.

Before post-sync, the release record may intentionally still be `PENDING / NOT YET PUBLISHED`. In that state, a read-only `verify` can be used diagnostically, but it is **not** the final completion gate: if every public artifact is exact and the only failure is `RELEASE_RECORD` because the GitHub-visible record is still PENDING, preserve that as a sequencing condition, complete consumer verification / `latest` promotion, and continue to post-sync. Do not weaken `verify`, fabricate a PUBLISHED record early, or treat any other failure as acceptable. The final canonical `VERIFY_RESULT=PASS` must occur after the post-sync PUBLISHED record is merged and visible on GitHub.

### Guarded mutation commands (M-1)

M-1 adds four mutation boundaries aligned with the release runbook. Every mutation command requires explicit maintainer authorization in the current session **and** the client `-Execute` switch. Without `-Execute`, the command observes guards only: `MUTATION_ATTEMPTED=FALSE` and no executor calls occur.

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\release.ps1 publish-image `
  -Version X.Y.Z `
  -ReleaseCommitSha <40-lowercase-hex> `
  -Execute

powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\release.ps1 create-tag `
  -Version X.Y.Z `
  -ReleaseCommitSha <40-lowercase-hex> `
  -Execute

powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\release.ps1 publish-nuget `
  -Version X.Y.Z `
  -ReleaseCommitSha <40-lowercase-hex> `
  -Execute

powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\release.ps1 create-github-release `
  -Version X.Y.Z `
  -ReleaseCommitSha <40-lowercase-hex> `
  -ReleaseNotesPath docs/releases/vX.Y.Z.md `
  -Execute
```

Guard states (Fresh observation immediately before any write):

```text
ABSENT        -> eligible for the one intended mutation
EXACT_MATCH   -> ALREADY_APPLIED; do not repeat
CONFLICT      -> STOP; no mutation
INCOMPLETE    -> STOP; no mutation
```

Mutation results:

```text
NOT_ATTEMPTED
APPLIED
ALREADY_APPLIED
CONFLICT
INCOMPLETE
AMBIGUOUS_AFTER_ATTEMPT
```

Every mutation path reports `MUTATION_ATTEMPTED` and `MUTATION_PERFORMED` as `TRUE`, `FALSE`, or `UNKNOWN`. Never claim `FALSE` when the outcome is actually unknown.

Command-specific guards:

| Command | Prerequisites | Target must be ABSENT | Additional guards |
|---|---|---|---|
| `publish-image` | RO-2-equivalent preflight PASS | GHCR | no matching source-bound image publish workflow run |
| `create-tag` | GHCR exact at `ReleaseCommitSha` | Git tag `vX.Y.Z` | read-back `vX.Y.Z^{commit} == ReleaseCommitSha` after attempt |
| `publish-nuget` | GHCR + Git tag exact | NuGet package | no matching source-bound NuGet publish workflow run; dispatch from ref `vX.Y.Z` |
| `create-github-release` | GHCR + Git tag + NuGet exact | GitHub Release | explicit `-ReleaseNotesPath` file; read-back non-draft/non-prerelease release **and** Git tag `vX.Y.Z^{commit} == ReleaseCommitSha` |

GitHub Release creation requires an existing verified release tag. The client uses `gh release create --verify-tag`. The client must never allow GitHub CLI to synthesize a missing release tag.

Matching workflow runs are not permission to redispatch. If a source/version-bound publish run already exists, return `ALREADY_APPLIED` and inspect instead of blind retry.

Self-test uses injectable fake executors and fake command runners only. It must never dispatch workflows, create refs/releases, or publish packages/images.

Production executors are wired automatically when `-Execute` is supplied on the CLI path. They invoke `gh` / `git` through an injectable argv-based command runner; self-test substitutes a fake runner to assert exact command composition without live mutation.

### Canonical release command sequence

Use this order for a full service release. Do not substitute ad-hoc shell for these boundaries unless the maintainer explicitly authorizes an exception in the current session.

```text
status
preflight
publish-image
create-tag
publish-nuget
create-github-release
public identity diagnostic / consumer verification
promote-latest
prepare-post-sync
merge post-sync authority
final verify
develop fast-forward sync
```

Each command requires explicit `-Version X.Y.Z`. Mutation and post-sync write commands additionally require `-ReleaseCommitSha` and `-Execute` for any file or external mutation. `promote-latest` also requires the already verified digest. Final `verify` is read-only and must run against GitHub-visible post-sync authority.

### Current public release authority (A-1)

The machine-readable **current public release** (not the next release candidate) lives at:

```text
release/current-public.json
```

It drives release smoke drift checking and the deterministic post-sync follower set. Version preparation for the next release must **not** advance this authority before full publication is verified.

### Deterministic post-release sync (`prepare-post-sync`)

After public artifacts and required consumer verification are exact, and `latest` has been promoted when required, synchronize current-public followers locally:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\release.ps1 prepare-post-sync `
  -Version X.Y.Z `
  -ReleaseCommitSha <40-lowercase-hex>

powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\release.ps1 prepare-post-sync `
  -Version X.Y.Z `
  -ReleaseCommitSha <40-lowercase-hex> `
  -Execute
```

Without `-Execute`: `MUTATION_ATTEMPTED=FALSE` and zero file writes. With `-Execute`: updates `release/current-public.json`, README / SECURITY / release smoke docs and scripts / compose defaults, and promotes `docs/releases/vX.Y.Z.md` from PENDING to PUBLISHED using externally observed facts only.

Preconditions (Fresh, fail-closed):

- explicit `Version` and `ReleaseCommitSha`
- canonical repository identity and clean worktree
- public cross-artifact identity is exact; a pre-post-sync canonical `verify` may differ only because the release record is still intentionally PENDING
- required consumer verification is PASS
- `latest` equals the verified versioned digest when `latest` promotion is part of the release
- target release record exists
- current-public authority is either the preceding public release or already exact to the requested target
- no mixed/ambiguous follower state

`prepare-post-sync` never commits, pushes, or opens a PR. After `-Execute`, require local validation before any commit:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\release-client-self-test.ps1
node scripts/check-release-smoke-tag-drift.mjs
git diff --check
```

The self-test must remain fixture-backed and must not compare the caller repository's live current-public version against a hard-coded predecessor value.

Post-sync mutation results mirror M-1: `NOT_ATTEMPTED`, `APPLIED`, `ALREADY_APPLIED`, `CONFLICT`, `INCOMPLETE`.

### Exactly-once post-sync recovery

Treat an `APPLIED` `prepare-post-sync -Execute` result as an exactly-once mutation candidate for that recovery attempt. If file mutation has occurred but a later local validation gate fails:

1. **Stop before commit, push, or PR.** Do not rerun `prepare-post-sync -Execute` to try to obtain a cleaner result.
2. Preserve the dirty post-sync working tree as read-only evidence.
3. Before any cleanup, reset, rebase, or reconstruction, export an exact patch and a cryptographic checksum. Record the changed-file inventory and the original post-sync base SHA.
4. Diagnose the failing gate. If the defect is in release tooling or validation, fix that defect in a separate clean branch / PR. Do not include the dirty post-sync candidate in the tooling-fix PR.
5. After the tooling fix is reviewed and merged, create a fresh isolated clone from the corrected `main` and reconstruct the already-generated post-sync candidate from the preserved patch. Use normal `git apply` when exact; a bounded three-way application is acceptable only when the semantic candidate remains the same and conflicts are reviewed.
6. Apply only explicitly reviewed bounded corrections required by the tooling defect. Do not add unrelated "while here" edits.
7. Re-run the local self-test, drift check, and `git diff --check`. Require the expected changed-file set and no unexpected files before commit/push/PR.
8. Record `POST_SYNC_EXECUTE_COUNT=1` and `POST_SYNC_REEXECUTE=FALSE` (or the equivalent actual count/state) in recovery evidence.

The preserved patch is recovery evidence, not new release authority. The frozen `releaseCommitSha` remains unchanged, and no public artifact should be republished to recover a repository-only post-sync validation failure.

### Retry / recovery matrix

| Operation | Policy |
|---|---|
| `status` / `preflight` / `verify` | Repeatable read-only |
| Normal PR CI | Rerun when appropriate |
| Corrected PR HEAD | Prefer a new CI run for the new head |
| `publish-image` dispatch | No blind retry; inspect matching workflow run first |
| `publish-nuget` dispatch | No blind retry; inspect matching workflow run first |
| `create-tag` | Fresh remote tag read-back before any retry decision |
| `create-github-release` | Fresh release + tag read-back before any retry decision |
| `promote-latest` | No blind redispatch; read back `latest` digest and matching run first |
| `prepare-post-sync` before mutation | Dry-run/read-only checks are repeatable |
| `prepare-post-sync -Execute` after files changed | Preserve the exact candidate; if validation fails, recover from checksummed patch after tooling repair instead of rerunning `-Execute` |

### Exploration Gate

Stop before version preparation if:

- The intended version already exists in any authoritative public artifact and this is not a documented recovery check.
- The local repository identity is ambiguous.
- `main` or local state is diverged.
- The intended release scope is unclear.
- The release would mix different source identities under one version.

## Phase 1 — Version Preparation

A full service release must first create a normal reviewed version-preparation PR.
Use the canonical release client; do not treat this phase as a public release.

```text
Fresh authority
  -> prepare-version dry-run
  -> review plan
  -> prepare-version -Execute
  -> Human/Agent reviewed CHANGELOG prose
  -> local validation
  -> reviewed version-preparation PR
```

### Canonical command

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\release.ps1 prepare-version `
  -Version X.Y.Z

powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\release.ps1 prepare-version `
  -Version X.Y.Z `
  -Execute
```

`-Version` is mandatory and is never inferred. Without `-Execute`, the command performs
zero file writes, zero Git/GitHub mutations, and zero public artifact mutations, and
prints a deterministic plan/result summary.

With `-Execute`, only the machine-owned Version Preparation files may change:

- `src/Amane.Mailer.Contracts/Amane.Mailer.Contracts.csproj` -> `X.Y.Z`
- `docs/api/openapi.yaml` -> `X.Y.Z`
- `docs/releases/vX.Y.Z.md` -> deterministic **PENDING / NOT YET PUBLISHED** scaffold

Hard boundaries for `prepare-version`:

```text
prepare-version != public release
prepare-version does not advance current-public
prepare-version does not fabricate CHANGELOG
prepare-version does not publish anything
```

Specifically, `prepare-version` must **not**:

- update `release/current-public.json`
- update README / SECURITY / release-smoke current-public followers
- invent CHANGELOG release-scope prose
- commit, push, create branches/PRs, dispatch workflows, create tags
- publish GHCR / NuGet / GitHub Release, or promote `latest`

### CHANGELOG boundary

`CHANGELOG.md` release-scope prose remains a Human/Agent reviewed input. The command
reports that a reviewed `## [X.Y.Z]` entry is required before Version Preparation
acceptance. Do not auto-generate or copy historical release prose into a new entry.

### PENDING release record

The PENDING release record must not invent final digests, artifact IDs, workflow run
IDs, NuGet availability, GitHub Release URLs, or publication timestamps. Use explicit
`PENDING`, `NOT YET PUBLISHED`, or equivalent placeholders until facts are observed.
NuGet evidence may later record a verifier `NUGET_PUBLIC_OBSERVED_AT_UTC` observation;
that timestamp is when the canonical verifier observed the public package, not a
fabricated NuGet service indexing time.

### Version Alignment Gate

Before the version-prep PR can merge:

- Contracts project version == `X.Y.Z`.
- OpenAPI `info.version` == `X.Y.Z`.
- CHANGELOG contains the reviewed `X.Y.Z` release entry.
- PENDING release record exists and remains free of fabricated public identities.
- `release/current-public.json` and governed followers still describe the predecessor
  public authority.
- No unrelated product change was pulled into the release PR unless the issue
  explicitly includes it.

Run the smallest relevant validation, then broaden:

```powershell
dotnet restore Amane.Mailer.slnx --locked-mode
dotnet build Amane.Mailer.slnx -c Release --no-restore
dotnet test Amane.Mailer.slnx -c Release --no-build --verbosity minimal
node scripts/validate-openapi.mjs docs/api/openapi.yaml
node scripts/check-implementation-status.mjs
```

Run additional release/version checks already present in the repository when applicable.
Do not weaken or skip existing CI to make version preparation pass.

## Phase 2 — Merge and Freeze Release Authority

After the version-prep PR is reviewed and merged to `main`:

1. Fresh-fetch GitHub `main`.
2. Fresh-fetch the local repository.
3. Require local `main == origin/main == GitHub main`.
4. Require a clean worktree.
5. Freeze that exact SHA as `releaseCommitSha`.
6. Record the freeze in the release issue before publication.

From this point, treat `releaseCommitSha` as immutable release authority for the planned publication.

### Main-advance rule before the first public artifact

Because the current image workflow requires `source_sha == GITHUB_SHA` on canonical `main`, `main` must still equal `releaseCommitSha` at image dispatch.

If `main` advances before Phase 4:

- Stop before publication.
- Do not publish from the old frozen SHA through an ad-hoc path.
- Inspect the new `main` changes and re-run version/source alignment checks.
- The maintainer may explicitly rebind the still-unpublished `X.Y.Z` release to the new `main` SHA only if no public artifact for `X.Y.Z` exists and the new SHA is acceptable release content.
- Record the new authority in the issue before continuing.

After any public artifact for `X.Y.Z` exists, do not rebind source under the same version.

## Phase 3 — Final Read-Only Preflight

Immediately before the first release mutation, re-check:

### Source

- GitHub `main == releaseCommitSha`.
- local `main == origin/main == releaseCommitSha`.
- worktree is clean.
- version alignment at `releaseCommitSha` is still `X.Y.Z`.

### Collision guards

Require absence of:

- Git tag `vX.Y.Z`.
- GitHub Release `vX.Y.Z`.
- GHCR version tag `vX.Y.Z`.
- GHCR immutable tag `sha-<releaseCommitSha>`.
- NuGet `Amane.Mailer.Contracts X.Y.Z`.

Do not treat network, authentication, permission, or rate-limit failures as proof of absence.

### Workflow identity

Confirm current workflows retain expected guards:

- `publish-contracts.yml` requires a release tag ref, validates SemVer, and requires project version == tag version before packing.
- `publish-release-image.yml` runs from canonical `main`, binds `source_sha` to the workflow commit, performs build/smoke/reproducibility, publishes the smoke-tested OCI digest, then performs public verification.
- `verify-public-release-image.yml` is read-only recovery and has no package-write publication path.
- Publish jobs remain bound to the `release` environment where configured.

If any collision or identity check is ambiguous, stop before mutation.

## Phase 4 — Publish GHCR Image Exactly Once

This is the first public artifact in the normal full-release sequence because it has the strongest coupling to the current `main` commit.

Use `.github/workflows/publish-release-image.yml` from canonical `main` with:

```text
source_sha=<releaseCommitSha>
release_version=X.Y.Z
```

Before dispatch, fresh-confirm `main == releaseCommitSha`. If the workflow's `source_sha == GITHUB_SHA` guard would fail, stop and resolve authority instead of weakening the guard.

### Dispatch rule

- Dispatch once.
- If command output is ambiguous, do not dispatch again. Locate the matching workflow run read-only.
- Require the `release` environment Human approval when GitHub requests it.
- Track one run ID and one attempt for the normal path.

### Normal path

The primary workflow must complete:

1. canonical main guard.
2. exact source checkout.
3. release-image contract self-test.
4. deterministic `linux/amd64` build.
5. `--help`, `/healthz`, `/readyz` smoke.
6. no-cache rebuild with identical OCI digest.
7. GHCR login only in the publish job.
8. publish the tested digest to:
   - `vX.Y.Z`.
   - `sha-<releaseCommitSha>`.
9. store publication input evidence.
10. run public verification without `packages: write`.
11. verify version/SHA tag equality, public `linux/amd64` pull, OCI labels, digest `--help`.
12. store `release-publication-evidence.json` and `public-consumer-verification.json`.

Record:

- workflow run ID/attempt.
- public OCI digest.
- version tag.
- immutable SHA tag.
- publication artifact ID/name.
- final evidence artifact ID/name.

### Recovery

If the image publish step succeeded but post-publish verification failed:

- Do not republish the same version.
- Do not overwrite either tag.
- Do not start a new build.
- Use `verify-public-release-image.yml` only after binding the original publish run ID, source SHA, release version, and expected digest.

If it is unclear whether publication happened, inspect GHCR and workflow evidence read-only before doing anything else.

If an error occurred before any public mutation and recovery might be safe, do not retry automatically. Require an explicit maintainer decision after proving the mutation state.

After GHCR publication succeeds, `releaseCommitSha` is permanently bound to `X.Y.Z` for this release. Do not change source identity under the same version.

## Phase 5 — Create the Release Tag

After public image verification succeeds, create the immutable Git release tag at the exact same source SHA.

Create `vX.Y.Z` targeting exactly `releaseCommitSha`. Prefer an annotated release tag when following the existing project convention.

Before pushing the tag, require explicit maintainer authorization in the current session.

After tag creation/push:

- Read it back from GitHub.
- Verify `vX.Y.Z^{commit} == releaseCommitSha`.
- Record tag object/target identity when available.

If the tag points at the wrong commit, stop. Do not move, delete, or recreate the same public release tag automatically. Preserve evidence and let the maintainer choose the incident/recovery path.

## Phase 6 — Publish NuGet Exactly Once

Use `.github/workflows/publish-contracts.yml` from the `vX.Y.Z` tag ref.

The workflow itself must verify:

- event ref is a release tag.
- tag is valid SemVer.
- checked-out revision == tag target == event commit.
- project `<Version>` == tag-derived package version.
- restore/audit/build/contracts tests pass.
- `.nupkg` and `.snupkg` are produced.

### Dispatch rule

- Dispatch once.
- If command output is ambiguous, do not dispatch again. Locate the matching workflow run read-only.
- Require the `release` environment Human approval when GitHub requests it.
- Track one run ID and one attempt unless the maintainer explicitly authorizes recovery after a proven pre-publication infrastructure failure.

After success, record:

- workflow run ID.
- package version.
- release tag.
- revision.
- package and symbol publish result.

Then verify public NuGet indexing/availability. NuGet immutability means a wrong package version cannot be replaced.

If GHCR `X.Y.Z` exists but tag or NuGet publication cannot be completed against the same `releaseCommitSha`, treat the version as a partial-release incident. Do not change source identity or republish the image under the same version to hide the mismatch.

## Phase 7 — Create GitHub Release

Create GitHub Release `vX.Y.Z` only after the Git tag, NuGet package, GHCR image, and primary/recovery public verification facts are known.

Use `docs/ops/release-notes-checklist.md` as the content checklist. At minimum include:

- release tag and target commit.
- GHCR version and immutable tags.
- public OCI digest.
- supported platform(s).
- smoke and public-verification result.
- OCI source/revision/version identity.
- NuGet package/version and public URL.
- symbol package status.
- .NET SDK/release baseline when relevant.
- important operational limitations and migration/backup guidance when relevant.
- link to `docs/releases/vX.Y.Z.md`.

Do not claim multi-arch, attestations, assets, migrations, or verification that were not actually produced for this release.

## Phase 7B — Promote `latest` (closeout alias)

After versioned GHCR / Git tag / NuGet / GitHub Release publication and clean-consumer verification PASS, promote GHCR `latest` to the already verified release digest.

`latest` is intentionally mutable and is **not** a release source. Do not rebuild. Do not rebind version or SHA tags. Do not use the qualified-candidate OCI pipeline as the full-release `latest` path.

Canonical client:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\release.ps1 promote-latest `
  -Version X.Y.Z `
  -ReleaseCommitSha <releaseCommitSha> `
  -ExpectedDigest sha256:<64-hex>
```

Dry-run (no `-Execute`) performs zero mutation. Mutation requires explicit Human authorization plus `-Execute`, which dispatches `.github/workflows/promote-release-latest.yml` from canonical `main`.

Closeout order:

```text
consumer verification PASS
  -> promote-latest
  -> latest consumer verification
  -> prepare-post-sync
  -> merge post-sync authority
  -> final verify
  -> develop fast-forward sync
```

The workflow copies `ghcr.io/kooiei-in4a/amane-mailer@ExpectedDigest` to `:latest` with pinned crane (digest-preserving alias). Tooling workflow commit and frozen release source SHA must not be conflated.

## Phase 8 — Post-Release Documentation Sync

After public artifacts exist, `latest` (when required) matches the verified digest, and required consumer verification is PASS, run deterministic local sync:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\release.ps1 prepare-post-sync `
  -Version X.Y.Z `
  -ReleaseCommitSha <releaseCommitSha> `
  -Execute
```

Then run the local validation gate (`release-client-self-test`, `check-release-smoke-tag-drift.mjs`, `git diff --check`) before commit.

The command updates at minimum:

- `release/current-public.json` current-public authority
- `docs/releases/vX.Y.Z.md` PENDING -> PUBLISHED with observed public facts only
- README / README.en / SECURITY / release smoke docs, scripts, and compose defaults aligned to the new current public release

At minimum (manual checklist after sync):

- Record exact tag target, GHCR digest/tags, workflow run IDs, evidence artifact IDs, NuGet package/status, GitHub Release URL/ID, platform, smoke/verification result, `latest` promotion result, and known limitations where not already filled by `prepare-post-sync`.
- Confirm CHANGELOG wording still matches what was actually released.
- Keep historical OCI-only or failed release attempts accurate; do not rewrite history to look cleaner.

If local validation fails **after** `prepare-post-sync -Execute` changed files, follow the exactly-once recovery procedure above. Do not rerun `-Execute` simply because the candidate has not yet been committed.

This post-release documentation commit is **not** the release source and must not move the already-published Git tag.

Use a normal PR for post-release documentation unless the maintainer explicitly authorizes another path.

## Phase 9 — Final Cross-Artifact Verification

After the post-sync PR is reviewed and merged, Fresh-check GitHub-visible `main` and run canonical `verify` against that merged authority. Before declaring the release complete, verify all applicable identities:

| Artifact | Required identity |
|---|---|
| Git tag | `vX.Y.Z -> releaseCommitSha` |
| Contracts source | `<Version>X.Y.Z</Version>` at `releaseCommitSha` |
| OpenAPI | `info.version == X.Y.Z` at `releaseCommitSha` |
| NuGet | `Amane.Mailer.Contracts X.Y.Z`, SourceLink revision == `releaseCommitSha` |
| GHCR version tag | `vX.Y.Z -> publicDigest` |
| GHCR immutable tag | `sha-<releaseCommitSha> -> publicDigest` |
| GHCR `latest` | same `publicDigest` when latest promotion is in scope |
| OCI labels | version `X.Y.Z`, revision/source bound to `releaseCommitSha` |
| GitHub Release | tag `vX.Y.Z`, notes match public facts |
| Current-public authority | `release/current-public.json` == `X.Y.Z` / `vX.Y.Z` / `docs/releases/vX.Y.Z.md` |
| Release record | PUBLISHED evidence matches public facts |

Require `VERIFY_RESULT=PASS` and `MUTATION_PERFORMED=FALSE`. Any mismatch is a release incident, not a documentation typo to hand-wave away. Stop, preserve evidence, and let the maintainer choose the next version/recovery path.

### Final `main` / `develop` branch closeout

Only after final canonical `verify` PASS:

1. Fresh-read both remote branch SHAs.
2. Require final `main` CI PASS for the exact post-sync merge SHA.
3. Test whether current `develop` is an ancestor of final `main` (`git merge-base --is-ancestor <develop> <main>` or equivalent) and confirm the merge-base equals `develop`.
4. If `develop` contains commits not present on final `main`, set `DEVELOP_DIVERGED=TRUE` and STOP. Do not reset, rebase, force-push, or synthesize a merge merely to close the release.
5. If fast-forward is possible, synchronize `develop` to final `main` with a **non-force fast-forward only** using the repository's permitted branch-update path.
6. Fresh-read both branches and require `develop == main`.
7. Require the `develop` CI run for that exact SHA to complete successfully. Do not blind-rerun/redispatch to manufacture a green closeout.
8. Record final `main` SHA, final `develop` SHA, both CI run IDs/conclusions, frozen `releaseCommitSha`, public digest, canonical verify PASS, current-public version, and safety invariants in the release issue before closing it.

The final repository SHA is tooling/documentation closeout authority; it does not replace or rebind the frozen public `releaseCommitSha`.

## Timing Record

Track both preparation and publication so release cost can be measured without conflating human handoff latency with build time.

Recommended timestamps:

```text
T0 RELEASE_REQUEST_ACCEPTED
T1 VERSION_PREP_MERGED
T2 RELEASE_AUTHORITY_FROZEN
T3 OCI_WORKFLOW_DISPATCHED
T4 OCI_JOB_STARTED
T5 OCI_PUBLISHED
T6 PUBLIC_VERIFICATION_COMPLETE
T7 TAG_PUBLISHED
T8 NUGET_PUBLISHED
T9 GITHUB_RELEASE_CREATED
T10 POST_RELEASE_SYNC_MERGED
T11 RELEASE_COMPLETE
```

Report at least:

- `T0 -> T11`: full wall-clock release operation.
- `T2 -> T9`: frozen-source to public full-release completion.
- `T3 -> T6`: OCI Actions path.
- Human approval / queue delay separately from runner execution.
- Version-prep and post-release documentation time separately.

Do not optimize the release platform just because total wall-clock is high. First identify whether the dominant cost is human handoff, CI queue, build/reproducibility, registry publication, NuGet indexing, or documentation closeout.

## Final Result Template

Use a compact final record like this in the release issue:

```yaml
FULL_RELEASE:
  RESULT: SUCCESS | STOP | FAILED | PARTIAL
  VERSION: X.Y.Z
  RELEASE_COMMIT_SHA:
  FINAL_MAIN_SHA:
  FINAL_DEVELOP_SHA:

  OCI:
    WORKFLOW_RUN_ID:
    DIGEST:
    VERSION_TAG:
    SHA_TAG:
    LATEST_DIGEST:
    PUBLIC_VERIFY: PASS | FAIL | NOT_RUN
    PUBLICATION_ARTIFACT_ID:
    EVIDENCE_ARTIFACT_ID:

  GIT:
    TAG: vX.Y.Z
    TAG_TARGET_SHA:

  NUGET:
    PACKAGE: Amane.Mailer.Contracts
    VERSION: X.Y.Z
    WORKFLOW_RUN_ID:
    PACKAGE_AVAILABLE:
    SYMBOL_PACKAGE_STATUS:
    REPOSITORY_REVISION:

  GITHUB_RELEASE:
    ID:
    URL:

  DOCS:
    CURRENT_PUBLIC:
    RELEASE_RECORD: docs/releases/vX.Y.Z.md
    STATUS: PUBLISHED | PENDING

  VERIFY:
    CANONICAL_VERIFY: PASS | FAIL | INCOMPLETE
    MUTATION_PERFORMED: false

  BRANCH_CLOSEOUT:
    MAIN_CI_RUN:
    MAIN_CI: PASS | FAIL
    DEVELOP_SYNC: FAST_FORWARD | STOP
    DEVELOP_CI_RUN:
    DEVELOP_CI: PASS | FAIL

  SAFETY:
    SAME_VERSION_REPUBLISH: false
    TAG_OVERWRITE: false
    SOURCE_REBIND: false
    LATEST_REBUILD: false
    LATEST_REPROMOTION: false
    POST_SYNC_REEXECUTE: false
    WORKFLOW_REDISPATCH: false

  TIMING:
    FROZEN_TO_PUBLIC_COMPLETE:
    OCI_ACTIONS_PATH:
    FULL_WALL_CLOCK:

  STOP_REASON:
```

## Completion Gate

A full service release is complete only when:

- One immutable release source SHA is recorded.
- GHCR version/SHA tags point to the verified digest built from that SHA.
- Git tag targets that SHA.
- Contracts and OpenAPI versions match the release.
- NuGet package and symbols are published/verified as required from that tag/SHA.
- Public image verification and consumer verification exist.
- GitHub Release exists with truthful notes.
- `latest` equals the verified versioned digest when latest promotion is in scope; no rebuild was used.
- `release/current-public.json` and governed followers are synchronized to `X.Y.Z`.
- `docs/releases/vX.Y.Z.md` contains PUBLISHED evidence without fabricated facts.
- Final canonical `verify` passes against GitHub-visible post-sync authority with no mutation.
- Final `main` CI passes on the post-sync merge SHA.
- `develop` is safely fast-forwarded to final `main`, the two remote SHAs are equal, and exact-SHA `develop` CI passes.
- No same-version republish, tag overwrite, source rebind, unapproved `latest`, post-sync re-execution, blind workflow redispatch, or approval bypass occurred.
- Timing and any deviations are recorded in the release issue.

If any of these is intentionally out of scope, the release issue and final result must say **PARTIAL / explicitly scoped**, not imply a full service release.
