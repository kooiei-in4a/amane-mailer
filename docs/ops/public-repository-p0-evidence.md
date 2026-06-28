# Public repository security summary

Consumer-facing summary of security verification for the public `amane-mailer`
repository. This document records value-free conclusions only. It does not
contain credential values, deploy secrets, or maintainer operational evidence.

Last reviewed: 2026-06-29 JST

## Repository secret scan

Status: reviewed on 2026-06-29 JST.

- Tool: `gitleaks 8.30.1` with repository `.gitleaks.toml`
- Tool runtime: `ghcr.io/gitleaks/gitleaks:v8.30.1`
  (`sha256:c00b6bd0aeb3071cbcb79009cb16a60dd9e0a7c60e2be9ab65d25e6bc8abbb7f`)
- Command:
  `docker run --rm -v ${PWD}:/repo ghcr.io/gitleaks/gitleaks:v8.30.1 detect --source=/repo --config=/repo/.gitleaks.toml --log-opts="--all" --redact --no-banner --no-color`
- Public git history: no leaks found with project configuration. The scan
  reported `77 commits scanned`; `git rev-list --all --count` reported 106
  commits total and `git rev-list --all --no-merges --count` reported 77
  non-merge commits.
- Target refs after `git fetch --all --tags --prune`: root commit
  `a699a151d8471e58264abecb25e68479d9f4ee64`; `origin/main` and
  `origin/develop` at `6aec9452bec31c478836fc7c09ad59e892358dc2`; tags
  `v0.1.0` and `v0.1.1` present.
- Working tree: no leaks found with project configuration via `--no-git`; Docker
  could not read local `.vs/.../FileContentIndex/*.vsidx` IDE index files and
  skipped them.

Conclusion: the public repository content and history contain no recorded real
credential values.

## Internal repository history

Status: confirmation-limited on 2026-06-29 JST.

- Former internal repository: `kooiei-in4a/amane-mailer-internal`
- Maintainer-provided premise from
  [#94](https://github.com/kooiei-in4a/amane-mailer/issues/94): the repository
  has been deleted.
- If no pre-deletion full-history scan evidence was captured, the deleted
  internal repository history is not independently verifiable from the public
  repository.

Conclusion: this document does not claim that the deleted internal repository
history was scanned. Public evidence is limited to the public repository scan,
published artifact checks, and maintainer confirmation items below.

## Credential rotation and publish credentials

Status: inventory reviewed on 2026-06-29 JST without recording secret values.

| Area | Publicly verifiable observation | Rotation disposition | Remaining maintainer confirmation |
| --- | --- | --- | --- |
| GitHub Actions repository secrets | `gh secret list --repo kooiei-in4a/amane-mailer` returned no repository secrets. | Not applicable for GitHub Actions repository secrets; no long-lived credential is visible there. | Confirm no required publish or deploy credential is stored outside GitHub Actions secret inventory. |
| GitHub `release` environment secrets | `gh secret list --repo kooiei-in4a/amane-mailer --env release` returned no environment secrets. | Not applicable for GitHub Actions environment secrets; no long-lived credential is visible there. | Confirm the release environment does not rely on hidden out-of-band long-lived credentials. |
| GitHub Actions variables | Repository and `release` environment variable lists were empty. | Not applicable for publish credentials. | None for publish credentials. |
| GHCR image publish | `.github/workflows/publish-image.yml` uses `GITHUB_TOKEN` with `contents: read` and `packages: write`; no repository secret is needed. | Not applicable for repository-stored publish credentials; publish uses the job-scoped `GITHUB_TOKEN`. | Confirm deploy hosts use only intended read-only pull credentials when GHCR authentication is required. |
| NuGet Contracts publish | `.github/workflows/publish-contracts.yml` uses `contents: read`, `id-token: write`, `environment: release`, and `NuGet/login` for GitHub Actions OIDC. No long-lived NuGet API key is visible in repository or environment secrets. | Not applicable for GitHub-stored NuGet API keys; maintainer confirmation required for nuget.org-side Trusted Publishing scope and any legacy keys. | In nuget.org maintainer UI, confirm Trusted Publishing / generated API key scope is limited to `Amane.Mailer.Contracts`, package owner / collaborator settings are expected, and no legacy unscoped or glob/version-only key remains active. If a mismatch is found, track it as follow-up from [#95](https://github.com/kooiei-in4a/amane-mailer/issues/95) before treating the item as complete. |
| Runtime tenant tokens | Public examples use placeholders or the documented local-only sample token. | Maintainer confirmation required. Public repository evidence cannot determine deployed token rotation status. | Confirm deployed `MAIL_SERVICE_TOKEN_*` values were rotated where public/private repository exposure risk existed. |
| ACS connection string | No ACS connection string is present in public repository secrets or docs. | Maintainer confirmation required. Public repository evidence cannot determine deployed ACS credential rotation status. | Confirm deployed `ACS_CONNECTION_STRING` values were rotated where public/private repository exposure risk existed. |
| Deploy secrets | No deploy secret values are stored in this public repository. | Maintainer confirmation required. Public repository evidence cannot determine external deploy secret rotation status. | Confirm external deploy hosts, backup remotes, and operator secret stores were reviewed and rotated as needed. |

## Release artifact consistency

Status: verified on 2026-06-26 JST for `v0.1.0`.

- GHCR image `ghcr.io/kooiei-in4a/amane-mailer:v0.1.0`, immutable
  `sha-<git-sha>` tag, release record digest, runtime manifest digest,
  attestation manifest digest, and OCI source/revision labels are consistent.
  The initial `v0.1.0` image did not set an OCI version label.
- NuGet package `Amane.Mailer.Contracts` `0.1.0` SourceLink commit matches the
  release tag commit

Reference: [docs/releases/v0.1.0.md](../releases/v0.1.0.md)

Consumer verification procedure:
[docs/ops/release-artifact-verification.en.md](release-artifact-verification.en.md)

## Release image smoke

Status: passed on 2026-06-27 JST for `ghcr.io/kooiei-in4a/amane-mailer:v0.1.0`
(digest `sha256:b0e513663df2be1df6045b8bff39d1fcd93536cae98287b91f07cfc7b8700677`).

- Clean-state smoke via [`scripts/release-smoke.sh`](../../scripts/release-smoke.sh):
  8 checks passed, 0 failed (`/healthz`, `/readyz`, valid POST, Mailpit delivery,
  idempotent repost, conflict, invalid token, invalid `source_service`)
- Environment class: Windows 11 host, Docker Desktop, `linux/amd64` container runtime

Reference: [docs/releases/v0.1.0.md](../releases/v0.1.0.md) (full pass/fail table),
[release image smoke runbook](release-image-smoke.en.md),
[GitHub Release v0.1.0](https://github.com/kooiei-in4a/amane-mailer/releases/tag/v0.1.0).

## NuGet publishing

Status: reviewed on 2026-06-29 JST.

- Public NuGet search API lists package owner `kooiei-in4a` for
  `Amane.Mailer.Contracts`.
- `publish-contracts.yml` permissions are `contents: read` and
  `id-token: write`; the job runs in GitHub Environment `release`.
- GitHub Environment `release` exists with required reviewer protection and
  custom deployment policies for `main` and `v*`.
- `Amane.Mailer.Contracts` publish uses `NuGet/login` and a job-scoped
  `NUGET_API_KEY` output from the OIDC login step. Repository and `release`
  environment secret inventories are empty, so no long-lived NuGet API key is
  visible in GitHub Actions secrets.
- The nuget.org maintainer UI is still the source of truth for Trusted
  Publishing configuration, exact package scope, collaborators, and legacy API
  key revocation. Those settings are not fully exposed by public package APIs.
- Symbol packages:
  - `v0.1.0`: not verified at release time; on 2026-06-29 JST the NuGet
    Gallery symbol download endpoint returned a `.snupkg` containing
    `lib/net8.0/Amane.Mailer.Contracts.pdb` (25,983 bytes).
  - `v0.1.1`: workflow summary recorded `.snupkg` generation and push; on
    2026-06-29 JST the NuGet Gallery symbol download endpoint returned a
    `.snupkg` containing `lib/net8.0/Amane.Mailer.Contracts.pdb` (26,073 bytes).
  - `https://api.nuget.org/v3-flatcontainer/.../*.snupkg` returned 404 for both
    versions while `.nupkg` flat-container URLs returned 200. Use the NuGet
    Gallery symbol package endpoint or debugger/NuGet Package Explorer checks
    for symbol evidence instead of treating flat-container `.snupkg` 404 as the
    final symbols result.
  - Symbol server indexing / SourceLink debugging availability was not verified
    in this pass.
- Post-push symbol endpoint verification is currently a manual release evidence
  step, not an automated `publish-contracts.yml` gate.
- Related symbol follow-up history:
  [#66](https://github.com/kooiei-in4a/amane-mailer/issues/66) and
  [#97](https://github.com/kooiei-in4a/amane-mailer/issues/97).

## Main branch protection

Status: reviewed on 2026-06-29 JST.

- GitHub classic branch protection API returned `Branch not protected`; `main`
  is protected by repository ruleset `main protection` (`id: 18124512`).
- Ruleset enforcement: `active`; target: `refs/heads/main`.
- Pull request rule: `required_approving_review_count: 0`,
  `required_review_thread_resolution: true`, code owner review not required,
  last-push approval not required.
- Required status checks:
  - `Restore, build, and test`
  - `Native AOT publish smoke`
  - `Docker build smoke`
  - `OpenAPI validation`
  - `Analyze (actions)`
  - `Analyze (csharp)`
  - `Analyze (javascript-typescript)`
  - `Local compose fresh data dir`
- Additional rules: non-fast-forward block, deletion block, required signatures.
- Latest inspected `main` CI run (`28327793417`) completed successfully. Code
  scanning alerts were fixed or dismissed at review time.

The solo-maintainer review policy for keeping review count 0 is documented in
[branch and CI workflow](branch-and-ci-workflow.en.md).

## CodeQL disposition

| Rule | Disposition | Notes |
| --- | --- | --- |
| `cs/user-controlled-bypass` | False positive, dismissed | Request body size is enforced during read, not only from `Content-Length`. |
| `cs/log-forging` | True positive, fixed ([#48](https://github.com/kooiei-in4a/amane-mailer/issues/48)) | Admin audit logging sanitizes control characters before stdout output. |

## Scope

This summary is intended for OSS consumers. Maintainer-only verification
evidence is not published here.
