# Contributing to Amane Mailer

Thank you for your interest in contributing.

## Getting started

1. Fork the repository, branch from `develop`, and use `feature/<topic>` or
   `fix/<topic>` for your work branch name.
2. Install prerequisites: .NET SDK (see `global.json`), Docker Desktop.
3. Verify the build and tests pass locally:

```powershell
dotnet restore Amane.Mailer.slnx --locked-mode
dotnet format whitespace Amane.Mailer.slnx --verify-no-changes
dotnet build Amane.Mailer.slnx -c Release --no-restore
dotnet test Amane.Mailer.slnx -c Release --no-build --verbosity minimal
```

Formatter and staged analyzer gates are documented in
[Code quality gates](docs/ops/code-quality-gates.en.md). Analyzer severities for
`src/**` participate in the Release build (see `.editorconfig`).

When changing NuGet dependencies or lockfiles, also run the vulnerability audit
(same command as schedule / publish CI):

```powershell
node scripts/nuget-vulnerability-audit.mjs --skip-restore
```

See [NuGet vulnerability audit](docs/ops/nuget-vulnerability-audit.en.md) for
failure remediation and allowlist policy.

4. Open a pull request to `develop` describing your change and the motivation
   behind it.

## Source-of-truth and validation matrix

Use the repository authority below before changing a value or behavior. An
issue defines the contribution scope and acceptance criteria; it does not
replace these sources. Where an area is a coordinated set, update and validate
the set in the same change instead of treating one synchronized artifact as a
new authority.

| Area | Canonical source | Synchronized or derived artifacts | Required validation | Do not edit directly |
|------|------------------|-----------------------------------|---------------------|----------------------|
| Current public release | [`release/current-public.json`](release/current-public.json) for the published version, tag, platform, and release-record path | Current-version text in README, setup guides, `SECURITY.md`, `ROADMAP.md`, and release-smoke defaults | `node scripts/check-release-smoke-tag-drift.mjs` | Do not hand-edit this authority, change a follower first, or use a historical `docs/releases/` record to select the current release. The guarded post-release sync owns coordinated updates. |
| .NET SDK version | [`global.json`](global.json) | Local/CI SDK selection and version references in contributor-facing docs | `dotnet --version` (must resolve to `sdk.version` in `global.json`) | Do not hard-code a second current SDK version in README, `AGENTS.md`, or this guide. |
| OpenAPI / HTTP contracts | [`src/Amane.Mailer.Contracts/`](src/Amane.Mailer.Contracts/) DTOs, constants, and payload-hash contract ([ADR 0012](docs/adr/0012-mail-via-mailer-microservice.md)) | Runtime behavior, [`docs/api/openapi.yaml`](docs/api/openapi.yaml), related tests/test vectors, and Python/TypeScript SDK builders and validation | The affected tests and restore/build/test commands above; `node scripts/validate-openapi.mjs docs/api/openapi.yaml`; `node scripts/check-contract-drift.mjs`; `node scripts/check-mail-request-field-inventory.mjs` | Do not change OpenAPI, runtime DTO behavior, or an SDK contract surface alone. OpenAPI is the synchronized consumer-facing schema, not the code-level authority. |
| Runtime / configuration | Runtime code under [`src/Amane.Mailer/`](src/Amane.Mailer/), including `Configuration/`, plus tenant JSON schemas under [`config/mailer/`](config/mailer/) | Configuration examples, deploy templates, service-spec text, and operational guidance | The restore/build/test commands above; for tenant configuration, `MAIL_SERVICE_TOKEN=local-mail-service-token scripts/validate-tenant-config.sh config/mailer/tenants.example.json` | Do not change only an example or document to claim runtime validation behavior. Do not commit deployment-specific tenant files or secrets. |
| Implementation status | [`docs/implementation-status.json`](docs/implementation-status.json) | Status summaries and links in ADRs or other documentation | `node scripts/check-implementation-status.mjs`; `node scripts/check-implementation-status.mjs --self-test` | Do not add a second dated/current status table to an ADR. ADRs preserve decisions; the manifest owns current tracked-feature status. |
| Release preparation and evidence metadata | The coordinated release identity and lifecycle defined by [`docs/agent-workflows/release.md`](docs/agent-workflows/release.md): Contracts project version, OpenAPI version, reviewed `CHANGELOG.md` entry, and the matching `docs/releases/vX.Y.Z.md` record | Git tag, GHCR image and labels, NuGet package, GitHub Release, then `release/current-public.json` and its followers after verified publication | `powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\release-client-self-test.ps1`; the OpenAPI and current-public checks above | Do not hand-create publication evidence, advance `current-public` during version preparation, or edit machine-owned release fields outside the guarded `release.ps1` workflow. Release/publish operations are maintainer-only. |
| Architecture decisions | Accepted records under [`docs/adr/`](docs/adr/) | Runtime, service specification, tests, and runbooks implementing a decision | Run the focused tests for the affected behavior and the restore/build/test commands above; run the implementation-status checks when tracked behavior changes | Do not rewrite an accepted ADR to make current status appear different. Record a superseding decision and keep current status in the manifest. |
| Setup and current operational docs | [`docs/ops/setup-guide.md`](docs/ops/setup-guide.md) and [`docs/ops/setup-guide.en.md`](docs/ops/setup-guide.en.md) for setup path selection, order, and safety boundaries; their linked runbooks own detailed procedures | README entry points and extracted/candidate setup material | Review the Japanese and English guides together, resolve every changed relative link, run `git diff --check`, and run the current-public check above when current-release text is involved | Do not turn README, candidate extracts, or copied runbook prose into a second setup authority. Current-release values still come from `release/current-public.json`. |

## Branch strategy

Work flows through `develop` and lands on `main` in release-sized batches:

```
feature/**, fix/**  → (PR) → develop  → (PR, release-gate CI) → main
```

After each `main` merge, maintainers sync `main` back into `develop` manually
(`git merge origin/main` on `develop`). See
[Branch strategy and CI weighting](docs/ops/branch-and-ci-workflow.en.md) for
the full flow, CI tiers, and branch protection notes.

## CI weighting

CI runs lighter checks on feature branches and full checks before release:

| Trigger | Checks |
|---------|--------|
| Push to `feature/**` / `fix/**` | Restore, whitespace formatter verify, build, test |
| Push to `develop` or PR to `develop` | Above + OpenAPI validation |
| PR to `main` | Release-gate CI (Native AOT, amd64 Docker, compose smoke, OpenAPI) |
| Push to `main` | Final CI (above + arm64 Docker) |

Native AOT failures may first appear on a PR to `main`; arm64 Docker failures
may first appear on the post-merge `main` push. That keeps release PR feedback
faster while still checking the final `main` commit. Details:
[docs/ops/branch-and-ci-workflow.en.md](docs/ops/branch-and-ci-workflow.en.md).

## Reporting issues

Use GitHub Issues. For security vulnerabilities, see [Security](#security) below.

## Pull requests

- Keep PRs focused on a single concern.
- Include or update tests for behaviour changes.
- Update documentation when adding or changing features.
- When a PR changes tracked feature behavior, update `docs/implementation-status.json`
  and confirm the PR template Implementation Status checklist. CI checks manifest
  format only; do not duplicate current status prose in ADRs.

## Security

See [SECURITY.md](SECURITY.md) for vulnerability reporting and
secret-handling guidelines.

## License

By contributing, you agree that your contributions will be licensed under the
[MIT License](LICENSE) that covers this project.
