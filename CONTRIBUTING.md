# Contributing to Amane Mailer

Thank you for your interest in contributing.

## Getting started

1. Fork the repository, branch from `develop`, and use `feature/<topic>` or
   `fix/<topic>` for your work branch name.
2. Install prerequisites: .NET SDK (see `global.json`), Docker Desktop.
3. Verify the build and tests pass locally:

```powershell
dotnet restore Amane.Mailer.slnx --locked-mode
dotnet build Amane.Mailer.slnx -c Release --no-restore
dotnet test Amane.Mailer.slnx -c Release --no-build --verbosity minimal
```

4. Open a pull request to `develop` describing your change and the motivation
   behind it.

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
| Push to `feature/**` / `fix/**` | Restore, build, test |
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

## Security

See [SECURITY.md](SECURITY.md) for vulnerability reporting and
secret-handling guidelines.

## License

By contributing, you agree that your contributions will be licensed under the
[MIT License](LICENSE) that covers this project.
